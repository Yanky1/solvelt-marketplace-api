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
    /// One handler per event type (the registry enforces it), so the buyer-facing projection of these two types (M5, decision M5-02) runs from here, AFTER the money part. Two rules
    /// (orchestrator review of M5): the money part must commit whatever the projection does, so the projection runs inside a SAVEPOINT and any error it throws is caught, logged
    /// (type only) and rolled back with the changes it made, never taking the reservation down with it; and it runs only when money moved (the refund was reserved), or for a cancellation
    /// that has nothing left to refund (its status is still real), never for a repeat or a refusal. The outcome stays the money part's.
    /// </summary>
    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var (money, project) = await MoneyAsync(c, ct);
        if (project && money.Status != Data.InboundStatus.Parked) await ProjectIsolatedAsync(c, ct);
        return money;
    }

    private Task ProjectIsolatedAsync(ProjectionContext c, CancellationToken ct) => RunIsolatedAsync(c, Fulfilment.FulfilmentProjector.ApplyAsync, log, ct);

    /// <summary>Runs <paramref name="projection"/> inside a savepoint of the webhook transaction; an error rolls back what it did and is logged, the rest of the transaction (the money) is untouched.</summary>
    internal static async Task RunIsolatedAsync(ProjectionContext c, Func<ProjectionContext, CancellationToken, Task> projection, ILogger log, CancellationToken ct)
    {
        var tx = c.Db.Database.CurrentTransaction;
        if (tx is null) { await projection(c, ct); return; }
        await c.Db.SaveChangesAsync(ct);                       // the money part is flushed first: it is what the savepoint protects
        const string name = "fulfilment_projection";
        await tx.CreateSavepointAsync(name, ct);
        try { await projection(c, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackToSavepointAsync(name, ct);
            foreach (var e in c.Db.ChangeTracker.Entries().Where(e => e.Entity is not Data.PortalInboundEvent && e.State != Microsoft.EntityFrameworkCore.EntityState.Unchanged).ToList())
                e.State = Microsoft.EntityFrameworkCore.EntityState.Detached;   // what the projection changed in memory must not be saved later
            log.LogError("The buyer-facing projection of portal event {Type} failed ({ExceptionType}); the refund was kept and the projection was skipped", c.Event.Type, ex.GetType().Name);
        }
    }

    private async Task<(HandlerOutcome Outcome, bool Project)> MoneyAsync(ProjectionContext c, CancellationToken ct)
    {
        // Handlers are singletons; the request's own scoped context (the webhook transaction) is what the reservation must run in.
        var refunds = new RefundService(c.Db, new Outbox.OutboxWriter(c.Db, c.Time), c.Time);
        var data = c.Event.Data;
        var oref = EventRefs.Order(data);
        if (oref is null) return (HandlerOutcome.Skipped("order reference missing"), false);
        var order = await c.Db.Orders.AsNoTracking().Include(o => o.Groups).FirstOrDefaultAsync(o => o.Number == oref.MarketplaceOrderId, ct);
        if (order is null) return (HandlerOutcome.Skipped("unknown marketplace order"), false);     // not one of ours
        var group = order.Groups.FirstOrDefault(g => g.SellerOrderId is not null && EventRefs.Matches(oref, order.Number, g.SellerOrderId.Value));
        if (group is null)
            // the seller orders are not stored yet (ingest in flight): wait for them. A seller order id that never matches any group expires as parked (M1-43).
            return (order.Groups.Any(g => g.SellerOrderId is null) ? HandlerOutcome.Parked("order", order.Number, "seller orders not stored yet") : HandlerOutcome.Skipped("no such seller order on this order"), false);

        PortalRefund request;
        if (c.Event.Type == Issued)
        {
            if (Str(data, "refundId") is not { Length: > 0 and <= 100 } rid || Num(data, "amountCents") is not { } total || total <= 0 || (Num(data, "shippingCents") ?? 0) is var shipping && (shipping < 0 || shipping > total))
                return (HandlerOutcome.Skipped("invalid refund payload"), false);
            var lines = ParseLines(data, out var lineSum);
            // amountCents is the sum of the lines (tax included) plus shipping (portal answer to C9). A payload that does not add up is not acted on: money moves only on consistent data.
            if (lines is null || (lines.Count > 0 && lineSum + shipping != total)) { log.LogError("Portal refund.issued for order {Number} is inconsistent (lines do not add up to the amount); nothing was reserved", order.Number); return (HandlerOutcome.Skipped("inconsistent refund amounts"), false); }
            request = new PortalRefund(order.Id, group.Id, total, shipping, lines.Select(l => new PortalRefundLine(l.Id, l.Quantity)).ToList(), Num(data, "taxCents"), false, false, "portal:" + rid, Str(data, "reason") ?? "portal_refund", rid);
        }
        else
        {
            var isCancel = c.Event.Type == Cancelled;
            if (Num(data, "refundDueCents") is not { } due || due <= 0) return (HandlerOutcome.Skipped("no refund due"), isCancel);   // a cancel with nothing to refund is still a cancellation
            // A full cancel returns TotalCents minus what was already returned (tax and shipping included); a partial remainder cancel (partial:true) the cancelled units' line amounts, tax included, no shipping.
            var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            var lines = ParseLines(data, out _);
            if (lines is null) return (HandlerOutcome.Skipped("invalid cancellation lines"), false);
            request = new PortalRefund(order.Id, group.Id, due, 0, lines.Select(l => new PortalRefundLine(l.Id, l.Quantity)).ToList(), Num(data, "taxCents"), !partial, true, "cancelled:" + c.Event.Id, "order_cancelled", null);
        }
        switch (await refunds.ReservePortalAsync(request, perGroupShipping: false, ct))
        {
            case ReserveOutcome.Reserved: return (HandlerOutcome.Applied(), true);
            case ReserveOutcome.Duplicate: return (HandlerOutcome.Applied(), false);
            case ReserveOutcome.ExceedsPaid:
                // The portal asks for more than the seller order still holds (or names a line it does not have). Money never moves beyond what was paid: a bug alarm for a person, not a retry loop.
                log.LogError("Portal {Type} for order {Number} exceeds what is left to refund on that seller order; nothing was reserved", c.Event.Type, order.Number);
                return (HandlerOutcome.Skipped("exceeds what was paid"), c.Event.Type == Cancelled);   // a cancel with nothing left to refund is still a cancellation
            default:
                return (HandlerOutcome.Skipped("order not paid"), false);
        }
    }

    /// <summary><c>lines: [{ marketplaceLineId, quantity, amountCents? }]</c>. Null when malformed; empty when absent.</summary>
    private static List<(string Id, int Quantity)>? ParseLines(JsonElement data, out long amountSum)
    {
        amountSum = 0;
        var result = new List<(string, int)>();
        if (!data.TryGetProperty("lines", out var arr) || arr.ValueKind == JsonValueKind.Null) return result;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 200) return null;
        foreach (var l in arr.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object) return null;
            if (Str(l, "marketplaceLineId") is not { Length: > 0 and <= 100 } id || Num(l, "quantity") is not { } q || q <= 0 || q > 10_000) return null;
            if (l.TryGetProperty("amountCents", out var a)) { if (a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out var amount) || amount < 0) return null; amountSum += amount; }
            result.Add((id, (int)q));
        }
        return result;
    }

    private static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
}

