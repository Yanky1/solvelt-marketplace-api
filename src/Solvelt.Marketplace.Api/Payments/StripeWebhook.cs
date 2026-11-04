using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Payments;

public static class StripeWebhookEndpoint
{
    public const string Route = "/api/stripe/webhooks";
    public const int MaxBodyBytes = 262144;

    public static IEndpointRouteBuilder MapStripeWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, Handle).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Verified with <c>Stripe:WebhookSecret</c> (its own key) over the raw bytes BEFORE anything is parsed; idempotent on the event id; order-independent (handlers read state,
    /// not the order events arrive in). A failure is a 5xx so Stripe retries for days.
    /// </summary>
    private static async Task<IResult> Handle(HttpContext ctx, IOptions<StripeOptions> options, TimeProvider time, StripeEventProcessor processor)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return Problems.Json(503, "webhook_not_configured", "The webhook receiver is not configured.");
        if (ctx.Request.ContentLength > MaxBodyBytes) return Problems.Json(413, "too_large", "Request body too large.");
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBodyBytes) return Problems.Json(413, "too_large", "Request body too large.");
        }
        var body = ms.ToArray();
        if (!StripeSignature.Verify(ctx.Request.Headers["Stripe-Signature"].ToString(), body, secret, time.GetUtcNow()))
            return Problems.Json(400, "bad_signature", "The signature is not valid.");
        StripeEnvelope? evt;
        try { evt = StripeEnvelope.Parse(body); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { evt = null; }
        if (evt is null) return Problems.Json(400, "invalid_event", "The event could not be read.");
        var outcome = await processor.ProcessAsync(evt, ctx.RequestAborted);
        return Results.Json(new { received = true, status = outcome.ToString().ToLowerInvariant() });
    }
}

/// <summary>The fields we read from an event; the rest of the payload is never stored (it can carry card details).</summary>
public sealed record StripeEnvelope(string Id, string Type, long Created, JsonElement Object)
{
    public static StripeEnvelope Parse(ReadOnlySpan<byte> body)
    {
        using var doc = JsonDocument.Parse(body.ToArray());
        var r = doc.RootElement;
        var id = r.GetProperty("id").GetString();
        var type = r.GetProperty("type").GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || string.IsNullOrWhiteSpace(type) || type.Length > 100) throw new InvalidOperationException("bad event");
        return new StripeEnvelope(id, type, r.TryGetProperty("created", out var c) && c.TryGetInt64(out var cl) ? cl : 0, r.GetProperty("data").GetProperty("object").Clone());
    }

    public string? Str(string name) => Object.ValueKind == JsonValueKind.Object && Object.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public long? Long(string name) => Object.ValueKind == JsonValueKind.Object && Object.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    public string? Meta(string name) => Object.ValueKind == JsonValueKind.Object && Object.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum StripeOutcome { Applied, Ignored, Unmatched, Duplicate }

/// <summary>The event-id insert and the handler's changes commit in ONE transaction: a failure rolls both back and answers 5xx, so Stripe's retry sees a clean slate.</summary>
public sealed class StripeEventProcessor(MarketplaceDb db, OutboxWriter outbox, RefundService refunds, Solvelt.Marketplace.Api.Services.BookingPaymentEvents bookingEvents, Microsoft.Extensions.Options.IOptions<Infrastructure.PortalOptions> portalOptions, TimeProvider time, ILogger<StripeEventProcessor> log)
{
    public async Task<StripeOutcome> ProcessAsync(StripeEnvelope evt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = new StripeEvent { Id = evt.Id, Type = evt.Type, StripeCreated = evt.Created, ReceivedAt = time.GetUtcNow().ToUniversalTime(), Outcome = "ignored", ObjectId = evt.Str("id") is { Length: <= 100 } oid ? oid : null };
        db.StripeEvents.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.StripeEvents.AsNoTracking().AnyAsync(e => e.Id == evt.Id, ct)) return StripeOutcome.Duplicate;
            throw;
        }
        var outcome = evt.Type switch
        {
            "payment_intent.succeeded" => await SucceededAsync(evt, ct),
            "payment_intent.payment_failed" => await FailedAsync(evt, ct),
            "charge.refunded" => await ChargeRefundedAsync(evt, ct),
            "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed" or "charge.dispute.funds_withdrawn" or "charge.dispute.funds_reinstated" => await DisputeAsync(evt, ct),
            _ => StripeOutcome.Ignored,
        };
        row.Outcome = outcome.ToString().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
        return outcome;
    }

    /// <summary>
    /// Our order for an intent: both ids must agree (the intent id we STORED and the order number we put in its metadata), never one alone and never adopted from the event.
    /// An order that is not stored yet, or has no intent stored yet, is a young race (the checkout request is about to save it): a 5xx makes Stripe send the event again. An event older than
    /// <see cref="NotYetKnownGrace"/> that still matches nothing never will: it is acknowledged and alarmed (a person looks), instead of looping 5xx for Stripe's three days.
    /// </summary>
    private async Task<MarketplaceOrder?> FindOrderAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("id");
        var number = evt.Meta("marketplace_order_id");
        if (intentId is null || number is null) return null;
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Number == number, ct);
        if (order is null || order.PaymentIntentId is null)
        {
            if (time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(evt.Created) < NotYetKnownGrace) throw new StripeOrderNotYetKnownException();
            log.LogError("A Stripe {Type} names order {Number}, which is {What}; the event is older than {Grace} minutes, so it is acknowledged and needs a person", evt.Type, number, order is null ? "unknown (purged?)" : "stored without an intent", NotYetKnownGrace.TotalMinutes);
            return null;
        }
        return order.PaymentIntentId == intentId ? order : null;
    }

    public static readonly TimeSpan NotYetKnownGrace = TimeSpan.FromMinutes(10);

    private async Task MailBuyerAsync(Guid accountId, string subject, string body, string dedupe, CancellationToken ct)
    {
        var email = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
        if (email is not null) outbox.EnqueueEmail(new EmailMessage(email, subject, body), "mail:" + dedupe);
    }

    private async Task<StripeOutcome> SucceededAsync(StripeEnvelope evt, CancellationToken ct)
    {
        if (Solvelt.Marketplace.Api.Services.BookingPaymentEvents.IsBookingIntent(evt)) return await bookingEvents.SucceededAsync(evt, ct);   // booking intents (M4-08); the order code below is untouched
        var order = await FindOrderAsync(evt, ct);
        if (order is null) return StripeOutcome.Unmatched;
        var amount = evt.Long("amount_received") ?? evt.Long("amount");
        var now = time.GetUtcNow().ToUniversalTime();
        // Paid is reached once, and only from the pending order: that is the one the buyer was shown and the cart's one payable order.
        var paid = await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.PendingPayment)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid).SetProperty(o => o.PaidAt, now).SetProperty(o => o.PaidCents, amount ?? 0).SetProperty(o => o.ActiveKey, (string?)null).SetProperty(o => o.UpdatedAt, now), ct);
        if (paid == 0)
        {
            // Money arrived for an order we had closed (superseded by a newer checkout, or expired): never fulfilled, and not silently kept either. It is refunded in full, automatically,
            // and the buyer is told (decision M3-18). Anything else (already paid, placed, failed) is a repeat and changes nothing.
            var status = await db.Orders.AsNoTracking().Where(o => o.Id == order.Id).Select(o => o.Status).FirstAsync(ct);
            var code = status switch { OrderStatus.Superseded => "superseded_payment", OrderStatus.Expired => "expired_payment", _ => null };
            if (code is null) return StripeOutcome.Ignored;
            var late = await db.Orders.Where(o => o.Id == order.Id && o.Status == status)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Failed).SetProperty(o => o.FailureCode, code).SetProperty(o => o.PaidAt, now).SetProperty(o => o.PaidCents, amount ?? 0).SetProperty(o => o.ActiveKey, (string?)null).SetProperty(o => o.UpdatedAt, now), ct);
            if (late == 0) return StripeOutcome.Ignored;
            log.LogWarning("A payment arrived for order {Number}, which was {Status}; it is being refunded in full", order.Number, status);
            if (amount is > 0)
            {
                await refunds.ReserveAsync(order.Id, null, amount.Value, 0, $"fail:{order.Number}", code, false, null, ct);
                await MailBuyerAsync(order.AccountId, "Your payment for order " + order.Number + " is being refunded",
                    "You completed a payment for an older version of your checkout (your cart or choices changed afterwards), so we did not place that order. The full amount is being refunded to your card (it can take a few days to show). Your latest checkout is unaffected.\n", $"late:{order.Number}", ct);
            }
            return StripeOutcome.Applied;
        }
        if (amount != order.TotalCents || !string.Equals(evt.Str("currency"), order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Stripe took a different amount than the order we priced: never fulfil it. Fail, give back what was taken, tell the buyer.
            log.LogError("Payment for order {Number} does not match its total (received {Received}, expected {Expected})", order.Number, amount, order.TotalCents);
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Failed).SetProperty(o => o.FailureCode, "amount_mismatch"), ct);
            if (amount is > 0)
            {
                await refunds.ReserveAsync(order.Id, null, amount.Value, 0, $"fail:{order.Number}", "amount_mismatch", false, null, ct);
                await MailBuyerAsync(order.AccountId, "We couldn't place your order " + order.Number, "The amount charged did not match your order, so we did not place it. The full amount is being refunded to your card (it can take a few days to show). Please try again.\n", $"mismatch:{order.Number}", ct);
            }
            return StripeOutcome.Applied;
        }
        outbox.Enqueue(OrderIngestHandler.Type, new OrderIngestHandler.Payload(order.Id), $"ingest:{order.Number}");
        // The cart has been bought: remove exactly the lines this order took (anything added since stays).
        if (order.CartId is { } cart)
        {
            var variants = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == order.Id).Select(l => l.VariantId).ToListAsync(ct);
            await db.CartLines.Where(l => l.CartId == cart && variants.Contains(l.VariantId)).ExecuteDeleteAsync(ct);
        }
        return StripeOutcome.Applied;
    }

    /// <summary>
    /// <c>charge.refunded</c>: our own refunds are reserved BEFORE Stripe is called, so Stripe's cumulative <c>amount_refunded</c> can never exceed what we reserved. When it does, someone
    /// refunded in Stripe's dashboard: the money is gone, so it is recorded against the cap (an <c>external</c> row, idempotent per amount) and logged for a person; the portal cannot be
    /// told which seller order it belonged to.
    /// </summary>
    private async Task<StripeOutcome> ChargeRefundedAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("payment_intent");
        var refunded = evt.Long("amount_refunded");
        if (intentId is null || refunded is null) return StripeOutcome.Unmatched;
        if (await bookingEvents.OwnsIntentAsync(intentId, ct)) return await bookingEvents.ChargeRefundedAsync(evt, ct);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.PaymentIntentId == intentId, ct);
        if (order is null) return StripeOutcome.Unmatched;
        // The order's row lock (a no-op write) serialises parallel charge.refunded events: each then reads the counter the one before it left, so the recorded amounts add up to Stripe's
        // cumulative figure instead of each counting from a stale read (300 then 500 cumulative was recorded as 800).
        await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents), ct);
        var current = await db.Orders.AsNoTracking().Where(o => o.Id == order.Id).Select(o => o.RefundedCents).FirstAsync(ct);
        if (refunded <= current) return StripeOutcome.Ignored;   // ours (reserved before Stripe is called) or already recorded
        var delta = refunded.Value - current;
        var now = time.GetUtcNow().ToUniversalTime();
        db.OrderRefunds.Add(new OrderRefund { Id = Guid.NewGuid(), Key = $"external:{order.Number}:{refunded}", OrderId = order.Id, AmountCents = delta, Reason = "external", Status = RefundStatus.Succeeded, CreatedAt = now, UpdatedAt = now });
        await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, refunded.Value), ct);
        log.LogError("Order {Number} was refunded {Delta} cents outside this system (Stripe dashboard); it is recorded and needs a person to tell the seller", order.Number, delta);
        return StripeOutcome.Applied;
    }

    /// <summary>
    /// <c>charge.dispute.*</c> becomes <c>dispute.created|updated|closed</c> for the portal, per seller order of the payment (matched by our stored intent id). The dispute amount is
    /// allocated over the seller orders by what each sold (items + shipping: tax is not seller money, PAYMENTS.md §2), largest remainder, so the parts add up exactly. Events are
    /// queued through our outbox with our own ids; the portal dedupes on them and orders by status. Inquiries (<c>warning_*</c> except closed) move no money and are the portal's own
    /// webhook's business.
    /// </summary>
    private async Task<StripeOutcome> DisputeAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("payment_intent");
        var disputeId = evt.Str("id");
        var amount = evt.Long("amount");
        var status = evt.Str("status");
        if (intentId is null || disputeId is null || amount is null || status is null) return StripeOutcome.Unmatched;
        if (await bookingEvents.OwnsIntentAsync(intentId, ct)) return await bookingEvents.DisputeAsync(evt, ct);   // a booking's intent (M4-15); orders below are untouched
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstOrDefaultAsync(o => o.PaymentIntentId == intentId, ct);
        if (order is null) return StripeOutcome.Unmatched;
        var portalStatus = status switch
        {
            "needs_response" => "needs_response", "under_review" => "under_review", "won" => "won", "lost" => "lost", "warning_closed" => "warning_closed", _ => null,
        };
        if (portalStatus is null) return StripeOutcome.Ignored;
        if (order.Status == OrderStatus.Failed) return StripeOutcome.Ignored;   // never placed and already refunded: nothing at the portal to tell
        if (order.Groups.Any(g => g.SellerOrderNumber is null)) throw new StripeOrderNotYetKnownException();   // not ingested yet: Stripe retries
        var type = evt.Type == "charge.dispute.created" ? PaymentEventTypes.DisputeCreated : portalStatus is "won" or "lost" or "warning_closed" ? PaymentEventTypes.DisputeClosed : PaymentEventTypes.DisputeUpdated;
        // Our own order guard (webhooks arrive in any order): an event older than the last one sent for this dispute is dropped, and nothing follows a closed dispute.
        var state = await db.DisputeStates.FirstOrDefaultAsync(d => d.DisputeId == disputeId, ct);
        if (state is not null && (state.Closed || evt.Created < state.LastCreated)) return StripeOutcome.Ignored;
        var closed = portalStatus is "won" or "lost" or "warning_closed";
        if (state is null) db.DisputeStates.Add(new DisputeState { DisputeId = disputeId, LastCreated = evt.Created, Closed = closed });
        else { state.LastCreated = evt.Created; state.Closed = closed; }

        var groups = order.Groups.OrderByDescending(g => g.TotalCents).ThenBy(g => g.Id).ToList();
        var share = Allocate(amount.Value * groups.Sum(g => g.ItemsCents + g.ShippingCents) / Math.Max(1, order.TotalCents), groups.Select(g => g.ItemsCents + g.ShippingCents).ToList());
        DateTimeOffset? due = evt.Object.TryGetProperty("evidence_details", out var ed) && ed.ValueKind == JsonValueKind.Object && ed.TryGetProperty("due_by", out var db2) && db2.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        var createdAt = evt.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(evt.Created) : (DateTimeOffset?)null;
        // Until the portal merges C10 (a dispute keyed per seller order), a multi-seller dispute must not go out as several events with the same dispute id: only the first would book money.
        // They are queued but held (due in ten years) and released when Portal:PerSellerOrderDisputes is turned on (OrderMaintenance). Single-seller disputes go out at once.
        TimeSpan? hold = groups.Count > 1 && !portalOptions.Value.PerSellerOrderDisputes ? TimeSpan.FromDays(3650) : null;
        for (var i = 0; i < groups.Count; i++)
            outbox.EnqueuePaymentEvent($"dispute:{evt.Id}:{groups[i].SellerOrderNumber}", type,
                new DisputeData(disputeId, order.Number, groups[i].SellerOrderNumber, null, share[i], portalStatus, i == 0 ? null : 0, order.Currency, evt.Str("reason"), due, createdAt), hold);   // the dispute fee: portal default, once
        return StripeOutcome.Applied;
    }

    /// <summary>Splits <paramref name="total"/> in proportion to <paramref name="weights"/>, exactly (largest remainder; ties go to the earlier entry).</summary>
    public static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        var result = new long[weights.Count];
        var sum = weights.Sum();
        if (total <= 0 || sum <= 0) return result;
        var rema = new (long Rem, int I)[weights.Count];
        long given = 0;
        for (var i = 0; i < weights.Count; i++) { result[i] = total * weights[i] / sum; given += result[i]; rema[i] = (total * weights[i] % sum, i); }
        foreach (var (_, i) in rema.OrderByDescending(r => r.Rem).ThenBy(r => r.I).Take((int)(total - given))) result[i]++;
        return result;
    }

    /// <summary>A failed attempt on an intent is not final (the buyer may try another card): only the decline code is kept for the page, and never on an order that is already paid.</summary>
    private async Task<StripeOutcome> FailedAsync(StripeEnvelope evt, CancellationToken ct)
    {
        if (Solvelt.Marketplace.Api.Services.BookingPaymentEvents.IsBookingIntent(evt)) return await bookingEvents.FailedAsync(evt, ct);
        var order = await FindOrderAsync(evt, ct);
        if (order is null) return StripeOutcome.Unmatched;
        var code = evt.Object.TryGetProperty("last_payment_error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? new string(c.GetString()!.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(60).ToArray()) : "payment_failed";
        await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.PendingPayment).ExecuteUpdateAsync(s => s.SetProperty(o => o.FailureCode, code), ct);
        return StripeOutcome.Applied;
    }
}

/// <summary>An event named one of our orders that is not stored yet. Surfaces as a 5xx so Stripe sends it again.</summary>
public sealed class StripeOrderNotYetKnownException : Exception;
