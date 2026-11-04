using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

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
public sealed class StripeEventProcessor(MarketplaceDb db, OutboxWriter outbox, RefundService refunds, TimeProvider time, ILogger<StripeEventProcessor> log)
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
            _ => StripeOutcome.Ignored,
        };
        row.Outcome = outcome.ToString().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
        return outcome;
    }

    /// <summary>Our order for an intent: both ids must agree (the intent id we stored AND the order number we put in its metadata), never one alone.</summary>
    private async Task<MarketplaceOrder?> FindOrderAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("id");
        var number = evt.Meta("marketplace_order_id");
        if (intentId is null || number is null) return null;
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Number == number, ct);
        if (order is null) throw new StripeOrderNotYetKnownException();   // ours by its metadata but not saved yet: 5xx, Stripe retries
        if (order.PaymentIntentId is null)
        {
            // The webhook beat the checkout request that stores the intent id (a payment within milliseconds): adopt it once.
            await db.Orders.Where(o => o.Id == order.Id && o.PaymentIntentId == null).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentIntentId, intentId), ct);
            order.PaymentIntentId = intentId;
        }
        return order.PaymentIntentId == intentId ? order : null;
    }

    private async Task<StripeOutcome> SucceededAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var order = await FindOrderAsync(evt, ct);
        if (order is null) return StripeOutcome.Unmatched;
        var amount = evt.Long("amount_received") ?? evt.Long("amount");
        var now = time.GetUtcNow().ToUniversalTime();
        // Paid is reached once, from any pending-like state: also from superseded or expired, because the money WAS taken.
        var paid = await db.Orders.Where(o => o.Id == order.Id && (o.Status == OrderStatus.PendingPayment || o.Status == OrderStatus.Superseded || o.Status == OrderStatus.Expired))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Paid).SetProperty(o => o.PaidAt, now).SetProperty(o => o.PaidCents, amount ?? 0).SetProperty(o => o.ActiveKey, (string?)null).SetProperty(o => o.UpdatedAt, now), ct);
        if (paid == 0) return StripeOutcome.Ignored;   // already paid, placed or failed: a second event changes nothing
        if (amount != order.TotalCents || !string.Equals(evt.Str("currency"), order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Stripe took a different amount than the order we priced: never fulfil it. Fail and give back what was taken.
            log.LogError("Payment for order {Number} does not match its total (received {Received}, expected {Expected})", order.Number, amount, order.TotalCents);
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Failed).SetProperty(o => o.FailureCode, "amount_mismatch"), ct);
            if (amount is > 0) await refunds.ReserveAsync(order.Id, null, amount.Value, 0, $"fail:{order.Number}", "amount_mismatch", false, null, ct);
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

    /// <summary>A failed attempt on an intent is not final (the buyer may try another card): only the decline code is kept for the page, and never on an order that is already paid.</summary>
    private async Task<StripeOutcome> FailedAsync(StripeEnvelope evt, CancellationToken ct)
    {
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
