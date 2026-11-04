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
public sealed class PortalRefundHandler(ILogger<PortalRefundHandler> log, Microsoft.Extensions.Options.IOptions<Infrastructure.PortalOptions> portalOptions, Fulfilment.FulfilmentProjector? projector = null) : IProjectionHandler
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
        var (money, reserved) = await MoneyAsync(c, ct);
        // The projection is ISOLATED (a savepoint): whatever it does, even fail, the money part above commits (M5 review H2).
        // A refund is projected only when WE reserved it now: a duplicate refund id, or one the money side refused, changes nothing the buyer sees (review M1).
        if (projector is not null && money.Status != Data.InboundStatus.Parked) await projector.ApplyIsolatedAsync(c, refundMoneyMoved: reserved, ct);
        return money;
    }

    private async Task<(HandlerOutcome Outcome, bool Reserved)> MoneyAsync(ProjectionContext c, CancellationToken ct)
    {
        var reserved = new ReservedFlag();
        var outcome = await MoneyCoreAsync(c, reserved, ct);
        return (outcome, reserved.Value);
    }

    private sealed class ReservedFlag { public bool Value; }

    private async Task<HandlerOutcome> MoneyCoreAsync(ProjectionContext c, ReservedFlag reserved, CancellationToken ct)
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

        PortalRefund request;
        if (c.Event.Type == Issued)
        {
            if (Str(data, "refundId") is not { Length: > 0 and <= 100 } rid || Num(data, "amountCents") is not { } total || total <= 0 || (Num(data, "shippingCents") ?? 0) is var shipping && (shipping < 0 || shipping > total))
                return HandlerOutcome.Skipped("invalid refund payload");
            var lines = ParseLines(data, out var lineSum);
            // amountCents is the sum of the lines (tax included) plus shipping (portal answer to C9) plus the shipping tax when the portal sends it (C8). A payload that does not add up is not acted on: money moves only on consistent data.
            var shippingTax = Num(data, "shippingTaxCents") ?? 0;   // C8 (brief N8): the amount is lines (with their tax) + shipping + shipping tax
            if (shippingTax < 0 || shippingTax > MaxPortalCents || total > MaxPortalCents || lines is null || (lines.Count > 0 && Sum3(lineSum, shipping, shippingTax) != total)) { log.LogError("Portal refund.issued for order {Number} is inconsistent (lines do not add up to the amount); nothing was reserved", order.Number); return HandlerOutcome.Skipped("inconsistent refund amounts"); }
            request = new PortalRefund(order.Id, group.Id, total, shipping, lines.Select(l => new PortalRefundLine(l.Id, l.Quantity)).ToList(), Num(data, "taxCents"), false, false, "portal:" + rid, Str(data, "reason") ?? "portal_refund", rid, Num(data, "shippingTaxCents"));
        }
        else
        {
            if (Num(data, "refundDueCents") is not { } due || due <= 0 || due > MaxPortalCents) return HandlerOutcome.Skipped("no refund due");
            // A full cancel returns TotalCents minus what was already returned (tax and shipping included); a partial remainder cancel (partial:true) the cancelled units' line amounts, tax included, no shipping.
            var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            var lines = ParseLines(data, out var cancelLineSum);
            if (lines is null) return HandlerOutcome.Skipped("invalid cancellation lines");
            // A partial remainder cancel refunds the cancelled units, so it must NAME them, and their amounts (tax included) must add up to what is due; anything else is not acted on (review R6).
            if (partial && (lines.Count == 0 || (cancelLineSum > 0 && cancelLineSum != due))) { log.LogError("Portal order.cancelled (partial) for order {Number} is inconsistent (lines {Lines}, amounts {Sum} vs due {Due}); nothing was reserved", order.Number, lines.Count, cancelLineSum, due); return HandlerOutcome.Skipped("inconsistent partial cancellation"); }
            // R3: events can arrive out of order. A full cancel's due amount is TotalCents minus what the portal has already returned (its own figure, `previouslyRefundedCents`, once it sends it): if
            // that is more than we have applied for this seller order, an earlier refund.issued is still on its way and the cancel must NOT run first (it would refund everything left and the
            // earlier refund would then be refused: the buyer short, the units wrong). It is parked and replayed by the maintenance loop; a log error says so once it has waited too long.
            if (!partial)
            {
                var portalReturned = Num(data, "previouslyRefundedCents") ?? group.TotalCents - due;
                var applied = await c.Db.OrderGroups.AsNoTracking().Where(x => x.Id == group.Id).Select(x => x.RefundedCents).FirstAsync(ct);
                if (portalReturned > applied)
                {
                    if (c.Time.GetUtcNow() - c.Event.CreatedAt > TimeSpan.FromMinutes(30))
                        log.LogError("Portal order.cancelled for order {Number} has waited more than 30 minutes for earlier refunds ({Applied} of {Expected} cents applied); a person should look", order.Number, applied, portalReturned);
                    return HandlerOutcome.Parked("order", order.Number, "earlier refunds not applied yet");
                }
            }
            request = new PortalRefund(order.Id, group.Id, due, 0, lines.Select(l => new PortalRefundLine(l.Id, l.Quantity)).ToList(), Num(data, "taxCents"), !partial, true, "cancelled:" + c.Event.Id, "order_cancelled", null, Num(data, "shippingTaxCents"));
        }
        switch (await refunds.ReservePortalAsync(request, perGroupShipping: portalOptions.Value.PerGroupShipping, ct))
        {
            case ReserveOutcome.Reserved: reserved.Value = true; return HandlerOutcome.Applied();
            case ReserveOutcome.Duplicate: return HandlerOutcome.Applied();
            case ReserveOutcome.ExceedsPaid:
                // The portal asks for more than the seller order still holds (or names a line it does not have). Money never moves beyond what was paid: a bug alarm for a person, not a retry loop.
                log.LogError("Portal {Type} for order {Number} exceeds what is left to refund on that seller order; nothing was reserved", c.Event.Type, order.Number);
                return HandlerOutcome.Skipped("exceeds what was paid");
            default:
                return HandlerOutcome.Skipped("order not paid");
        }
    }

    /// <summary><c>lines: [{ marketplaceLineId, quantity, amountCents? }]</c>. Null when malformed; empty when absent.</summary>
    /// <summary>No money figure from the portal is believed above this (a hundred million dollars): every product of two such figures still fits a long, so the proration arithmetic cannot wrap.</summary>
    public const long MaxPortalCents = 10_000_000_000;

    private static long Sum3(long a, long b, long c)
    {
        try { return checked(a + b + c); }
        catch (OverflowException) { return -1; }   // never equal to a positive amount
    }

    private static List<(string Id, int Quantity)>? ParseLines(JsonElement data, out long amountSum)
    {
        amountSum = 0;
        var result = new List<(string, int)>();
        if (!data.TryGetProperty("lines", out var arr) || arr.ValueKind == JsonValueKind.Null) return result;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 200) return null;
        foreach (var l in arr.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object) return null;   // a non-object element is a malformed payload, not an exception
            if (Str(l, "marketplaceLineId") is not { Length: > 0 and <= 100 } id || Num(l, "quantity") is not { } q || q <= 0 || q > 10_000) return null;
            if (l.TryGetProperty("amountCents", out var a)) { if (a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out var amount) || amount < 0) return null; try { amountSum = checked(amountSum + amount); } catch (OverflowException) { return null; } }
            result.Add((id, (int)q));
        }
        return result;
    }

    private static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
}

