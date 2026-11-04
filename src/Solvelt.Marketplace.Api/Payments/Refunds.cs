using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Payments;

public enum ReserveOutcome { Reserved, Duplicate, ExceedsPaid, NotPaid }

/// <summary>
/// The only way money leaves: a refund is first RESERVED against the cap with conditional updates
/// (<c>RefundedCents + amount &lt;= PaidCents</c> on the order, <c>&lt;= TotalCents</c> on the seller order), together with its unique idempotency key and the outbox message that will
/// call Stripe. So refunds can never exceed what was paid, in total or per seller order, however many events, retries or parallel requests arrive.
/// </summary>
public sealed class RefundService(MarketplaceDb db, OutboxWriter outbox, TimeProvider time)
{
    public const string StripeRefundType = "stripe.refund";

    /// <summary>
    /// Reserves and queues. Inside the caller's transaction when there is one (the caller saves and commits); otherwise in a transaction of its own, saved here.
    /// </summary>
    public async Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid? groupId, long amountCents, long shippingCents, string key, string reason, bool reportToPortal, string? portalRefundId, CancellationToken ct)
    {
        if (amountCents <= 0) throw new ArgumentOutOfRangeException(nameof(amountCents));
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.OrderRefunds.AnyAsync(r => r.Key == key, ct)) return ReserveOutcome.Duplicate;
        var paid = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.PaidAt).FirstOrDefaultAsync(ct);
        if (paid is null) return ReserveOutcome.NotPaid;
        var order = await db.Orders.Where(o => o.Id == orderId && o.PaidAt != null && o.RefundedCents + amountCents <= o.PaidCents)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + amountCents), ct);
        if (order == 0) return ReserveOutcome.ExceedsPaid;
        if (groupId is { } gid)
        {
            var g = await db.OrderGroups.Where(x => x.Id == gid && x.OrderId == orderId && x.RefundedCents + amountCents <= x.TotalCents)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents + amountCents), ct);
            if (g == 0)
            {
                await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents - amountCents), ct);
                return ReserveOutcome.ExceedsPaid;
            }
        }
        var now = time.GetUtcNow().ToUniversalTime();
        db.OrderRefunds.Add(new OrderRefund
        {
            Id = Guid.NewGuid(), Key = key, OrderId = orderId, GroupId = groupId, AmountCents = amountCents, ShippingCents = shippingCents, Reason = reason, Status = RefundStatus.Reserved,
            ReportToPortal = reportToPortal, PortalRefundId = portalRefundId, CreatedAt = now, UpdatedAt = now,
        });
        outbox.Enqueue(StripeRefundType, new RefundPayload(key), $"refund:{key}");
        if (own)
        {
            await db.SaveChangesAsync(ct);   // the unique key decides a parallel duplicate: it throws and the transaction (reservation included) rolls back
            await tx!.CommitAsync(ct);
        }
        return ReserveOutcome.Reserved;
    }

    /// <summary>
    /// Reserves a refund the PORTAL asked for on one seller order (<c>refund.issued</c>, a cancellation): <paramref name="itemsCents"/> and <paramref name="shippingCents"/> are what the
    /// portal's ledger counts; the buyer also gets back the tax on them, in proportion (round half up, the last refund of the seller order takes the remainder, so tax is returned
    /// in full and never twice). Stripe is refunded <c>items + shipping + tax</c>; the portal is told only items and shipping. With <paramref name="clamp"/> (cancellations) a
    /// request above what is left on the seller order is cut down to it instead of refused.
    /// </summary>
    public async Task<ReserveOutcome> ReserveGroupAsync(Guid orderId, Guid groupId, long itemsCents, long shippingCents, bool clamp, string key, string reason, string? portalRefundId, CancellationToken ct)
    {
        if (itemsCents < 0 || shippingCents < 0 || itemsCents + shippingCents <= 0) throw new ArgumentOutOfRangeException(nameof(itemsCents));
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.OrderRefunds.AnyAsync(r => r.Key == key, ct)) return ReserveOutcome.Duplicate;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var g = await db.OrderGroups.AsNoTracking().Where(x => x.Id == groupId && x.OrderId == orderId).FirstOrDefaultAsync(ct);
            var paid = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.PaidAt).FirstOrDefaultAsync(ct);
            if (g is null || paid is null) return ReserveOutcome.NotPaid;
            var sellable = g.ItemsCents + g.ShippingCents;
            var net = itemsCents + shippingCents;
            var shipping = shippingCents;
            if (clamp && g.RefundedNetCents + net > sellable) { net = Math.Max(0, sellable - g.RefundedNetCents); shipping = Math.Min(shipping, net); }
            if (net <= 0) return ReserveOutcome.ExceedsPaid;
            if (g.RefundedNetCents + net > sellable) return ReserveOutcome.ExceedsPaid;
            var tax = g.RefundedNetCents + net >= sellable
                ? g.TaxCents - g.RefundedTaxCents                                                                   // the last refund returns the rest of the tax
                : (long)Math.Round((decimal)g.TaxCents * net / sellable, 0, MidpointRounding.AwayFromZero);
            tax = Math.Clamp(tax, 0, g.TaxCents - g.RefundedTaxCents);
            var total = net + tax;
            var seen = g.RefundedNetCents;
            // compare-and-swap on what we read, so two parallel refunds cannot both take "the remainder" of the tax or both fit under the cap
            var won = await db.OrderGroups.Where(x => x.Id == groupId && x.RefundedNetCents == seen && x.RefundedCents + total <= x.TotalCents)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedNetCents, x => x.RefundedNetCents + net).SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents + tax)
                    .SetProperty(x => x.RefundedCents, x => x.RefundedCents + total), ct);
            if (won == 0) continue;   // someone else moved the counters: read again (reads are untracked, so nothing stale is kept)
            var order = await db.Orders.Where(o => o.Id == orderId && o.PaidAt != null && o.RefundedCents + total <= o.PaidCents)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + total), ct);
            if (order == 0)
            {
                await db.OrderGroups.Where(x => x.Id == groupId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedNetCents, x => x.RefundedNetCents - net)
                    .SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents - tax).SetProperty(x => x.RefundedCents, x => x.RefundedCents - total), ct);
                return ReserveOutcome.ExceedsPaid;
            }
            var now = time.GetUtcNow().ToUniversalTime();
            db.OrderRefunds.Add(new OrderRefund
            {
                Id = Guid.NewGuid(), Key = key, OrderId = orderId, GroupId = groupId, AmountCents = total, ShippingCents = shipping, TaxCents = tax, Reason = reason, Status = RefundStatus.Reserved,
                ReportToPortal = true, PortalRefundId = portalRefundId, CreatedAt = now, UpdatedAt = now,
            });
            outbox.Enqueue(StripeRefundType, new RefundPayload(key), $"refund:{key}");
            if (own) { await db.SaveChangesAsync(ct); await tx!.CommitAsync(ct); }
            return ReserveOutcome.Reserved;
        }
        return ReserveOutcome.ExceedsPaid;   // the counters kept moving under us: refuse rather than guess; the portal's event is retried
    }

    public sealed record RefundPayload(string Key);
}

/// <summary>Calls Stripe for a reserved refund (idempotent on the refund key), then tells the portal and the buyer, all in one transaction with the status change.</summary>
public sealed class StripeRefundHandler(MarketplaceDb db, IPaymentProvider payments, OutboxWriter outbox, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [RefundService.StripeRefundType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        RefundService.RefundPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<RefundService.RefundPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.Key)) return OutboxResult.Dead("invalid_payload");
        var refund = await db.OrderRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.Key == p.Key, ct);
        if (refund is null) return OutboxResult.Dead("refund_missing");
        if (refund.Status != RefundStatus.Reserved) return OutboxResult.Delivered();   // already done (a retry after the result was recorded)
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstAsync(o => o.Id == refund.OrderId, ct);
        if (order.PaymentIntentId is null) return OutboxResult.Dead("no_payment_intent");

        RefundResult result;
        try
        {
            result = await payments.RefundAsync(order.PaymentIntentId, refund.AmountCents, "refund:" + refund.Key,
                new Dictionary<string, string> { ["marketplace_order_id"] = order.Number, ["refund_key"] = refund.Key }, ct);
        }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex)
        {
            // Stripe will never do this one (for example it exceeds what was captured): give the reservation back and leave it for a person.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.OrderRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Rejected).SetProperty(r => r.UpdatedAt, time.GetUtcNow()), ct);
            if (changed == 1)
            {
                await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents - refund.AmountCents), ct);
                if (refund.GroupId is { } g)
                {
                    var net = refund.AmountCents - refund.TaxCents;
                    await db.OrderGroups.Where(x => x.Id == g).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents - refund.AmountCents)
                        .SetProperty(x => x.RefundedNetCents, x => x.RefundedNetCents - (refund.ReportToPortal ? net : 0)).SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents - refund.TaxCents), ct);
                }
            }
            await tx.CommitAsync(ct);
            return OutboxResult.Dead("stripe_" + ex.Code);
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = time.GetUtcNow().ToUniversalTime();
            var changed = await db.OrderRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Succeeded).SetProperty(r => r.StripeRefundId, result.Id).SetProperty(r => r.UpdatedAt, now), ct);
            if (changed == 1)
            {
                var group = refund.GroupId is { } gid ? order.Groups.FirstOrDefault(g => g.Id == gid) : null;
                if (refund.ReportToPortal && group?.SellerOrderNumber is { } sellerOrderNumber)
                    outbox.EnqueuePaymentEvent($"refund-processed:{refund.Key}", PaymentEventTypes.RefundProcessed,
                        new RefundProcessedData(result.Id, order.Number, sellerOrderNumber, null, refund.AmountCents - refund.ShippingCents - refund.TaxCents, refund.ShippingCents, order.Currency, refund.Reason));
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return OutboxResult.Delivered();
    }
}

/// <summary>
/// Cancels a PaymentIntent of a closed order (superseded or expired), with retries: an intent that stays payable is the one thing we must never leave behind. A Stripe refusal means it can no
/// longer be cancelled (it already succeeded, or is already cancelled): nothing to do here, the webhook refunds a payment that landed.
/// </summary>
public sealed class StripeCancelHandler(IPaymentProvider payments) : IOutboxHandler
{
    public const string Type = "stripe.cancel";
    public sealed record Payload(string IntentId);
    public static string DedupeKey(string intentId) => "cancel:" + intentId;
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.IntentId)) return OutboxResult.Dead("invalid_payload");
        try { await payments.CancelIntentAsync(p.IntentId, "cancel:" + p.IntentId, ct); }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException) { return OutboxResult.Delivered(); }
        return OutboxResult.Delivered();
    }
}
