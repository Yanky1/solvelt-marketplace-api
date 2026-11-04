using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// The portal asks us to move money (CONTRACT.md §3): <c>refund.issued</c> (the seller refunded) and <c>order.cancelled</c> with <c>refundDueCents</c>. The seller order is matched by OUR
/// marketplace order id AND the portal's seller order id, both equal to what we stored at ingest (<see cref="EventRefs.Matches(OrderRef?, string, Guid)"/>). The refund is reserved
/// under its unique key (the portal refund id, or the cancelling event) inside the webhook transaction; Stripe is called from the outbox, then <c>refund.processed</c> goes back.
/// An event for an order whose ingest has not stored the seller orders yet is parked and re-applied when it does.
/// </summary>
public sealed class PortalRefundHandler(ILogger<PortalRefundHandler> log) : IProjectionHandler
{
    public const string Issued = "refund.issued", Cancelled = "order.cancelled";
    public IReadOnlyCollection<string> EventTypes { get; } = [Issued, Cancelled];

    /// <summary>
    /// One handler per event type (the registry enforces it), so the buyer-facing projection of these two types (M5, decision M5-02) runs from here, AFTER the money part:
    /// a parked event is parked untouched (the projection must not run twice), anything else is projected exactly once (the event id is unique).
    /// The outcome stays the money part's, so M3's behaviour is unchanged.
    /// </summary>
    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var money = await MoneyAsync(c, ct);
        if (money.Status != Data.InboundStatus.Parked) await Fulfilment.FulfilmentProjector.ApplyAsync(c, ct, refundMoneyMoved: money.Status == Data.InboundStatus.Applied);
        return money;
    }

    private async Task<HandlerOutcome> MoneyAsync(ProjectionContext c, CancellationToken ct)
    {
        // Handlers are singletons; the request's own scoped context (the webhook transaction) is what the reservation must run in.
        var refunds = new RefundService(c.Db, new Outbox.OutboxWriter(c.Db, c.Time), c.Time);
        var data = c.Event.Data;
        var oref = EventRefs.Order(data);
        if (oref is null) return HandlerOutcome.Skipped("order reference missing");
        var order = await c.Db.Orders.AsNoTracking().Include(o => o.Groups).FirstOrDefaultAsync(o => o.Number == oref.MarketplaceOrderId, ct);
        if (order is null) return HandlerOutcome.Skipped("unknown marketplace order");     // not one of ours
        var group = order.Groups.FirstOrDefault(g => g.SellerOrderId is not null && EventRefs.Matches(oref, order.Number, g.SellerOrderId.Value));
        if (group is null)
            // the seller orders are not stored yet (ingest in flight): wait for them. A seller order id that never matches any group expires as parked (M1-43).
            return order.Groups.Any(g => g.SellerOrderId is null) ? HandlerOutcome.Parked("order", order.Number, "seller orders not stored yet") : HandlerOutcome.Skipped("no such seller order on this order");

        long items, shipping;
        string key, reason;
        bool clamp;
        string? portalRefundId = null;
        if (c.Event.Type == Issued)
        {
            if (Str(data, "refundId") is not { Length: > 0 and <= 100 } rid || Num(data, "amountCents") is not { } a || a < 0 || (Num(data, "shippingCents") ?? 0) is var sh && sh < 0 || a + sh <= 0)
                return HandlerOutcome.Skipped("invalid refund payload");
            (items, shipping, key, reason, clamp, portalRefundId) = (a, sh, "portal:" + rid, Str(data, "reason") ?? "portal_refund", false, rid);
        }
        else
        {
            if (Num(data, "refundDueCents") is not { } due || due <= 0) return HandlerOutcome.Skipped("no refund due");
            // The shipping part of a cancellation is not stated: a full cancel returns the seller order's shipping too, a partial remainder cancel none of it.
            var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            shipping = partial ? 0 : Math.Min(group.ShippingCents - group.RefundedNetCents.ClampAtLeast(0), due).ClampAtLeast(0);
            (items, key, reason, clamp) = (due - shipping, "cancelled:" + c.Event.Id, "order_cancelled", true);
        }
        switch (await refunds.ReserveGroupAsync(order.Id, group.Id, items, shipping, clamp, key, reason, portalRefundId, ct))
        {
            case ReserveOutcome.Reserved or ReserveOutcome.Duplicate: return HandlerOutcome.Applied();
            case ReserveOutcome.ExceedsPaid:
                // The portal asks for more than the seller order still holds. Money never moves beyond what was paid: this is a bug alarm for a person, not a retry.
                log.LogError("Portal {Type} for order {Number} exceeds what is left to refund on that seller order; nothing was reserved", c.Event.Type, order.Number);
                return HandlerOutcome.Skipped("exceeds what was paid");
            default:
                return HandlerOutcome.Skipped("order not paid");
        }
    }

    private static string? Str(JsonElement d, string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement d, string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
}

internal static class LongExtensions
{
    public static long ClampAtLeast(this long v, long min) => v < min ? min : v;
}
