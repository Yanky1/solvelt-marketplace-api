using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// An event of an order arrived with <c>predecessorDead: true</c> (portal C14/N12): an earlier event of that seller order was given up on, so our view may have a gap. The portal's own view
/// of the order is read (<c>GET /orders/{mid}</c>, C16 / N13) and the FULFILMENT state is brought up to it: counters only ever go UP, a seller order's status only moves forward (never out of a
/// terminal state), missing shipments are added. Money is never touched here (M3 compares refunds and alerts). Parked events are replayed afterwards. A portal that cannot answer yet (404,
/// 405, 501) is logged as an error (the alert) and the message is done: nothing is retried against a portal that does not have the endpoint. Decision M5-29.
/// </summary>
public sealed class OrderReconcileHandler(MarketplaceDb db, ISignedPortalClient portal, PortalEventProcessor events, TimeProvider time, ILogger<OrderReconcileHandler> log) : IOutboxHandler
{
    public const string Type = "portal.order-reconcile";
    public sealed record Payload(string MarketplaceOrderId);
    public static string DedupeKey(string eventId) => "order-reconcile:" + eventId;
    public IReadOnlyCollection<string> Types { get; } = [Type];

    private static readonly string[] Forward = [FulfilmentStatus.New, FulfilmentStatus.Accepted, FulfilmentStatus.OnHold, FulfilmentStatus.PartiallyShipped, FulfilmentStatus.Shipped, FulfilmentStatus.Delivered];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrWhiteSpace(p.MarketplaceOrderId)) return OutboxResult.Dead("invalid_payload");
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Number == p.MarketplaceOrderId, ct);
        if (order is null) return OutboxResult.Delivered();
        PortalResult<PortalOrderState> r;
        try { r = await portal.GetOrderStateAsync(p.MarketplaceOrderId, ct); }
        catch (PortalUnavailableException) { return OutboxResult.Retry("portal_unavailable"); }
        catch (PortalNotConfiguredException) { return OutboxResult.Retry("portal_not_configured"); }
        if (r.Status is 404 or 405 or 501)
        {
            log.LogError("Order reconcile: the portal cannot show order state yet (HTTP {Status}); a predecessor event of an order was lost and its fulfilment state may have a gap", r.Status);   // the alert; no ids, no payload
            return OutboxResult.Delivered();
        }
        if (!r.IsSuccess || r.Value is not { } state) return PortalOutcome.From(r);
        if (!string.Equals(state.MarketplaceOrderId, order.Number, StringComparison.Ordinal)) return OutboxResult.Dead("order_mismatch");

        var now = time.GetUtcNow().ToUniversalTime();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var so in state.SellerOrders ?? [])
        {
            var group = order.Groups.FirstOrDefault(g => g.SellerOrderId == so.SellerOrderId);
            if (group is null) continue;                                                           // a seller order we do not know: nothing to apply it to
            var lines = order.Lines.Where(l => l.GroupId == group.Id).ToList();
            await FulfilmentLines.EnsureAsync(db, lines, ct);
            foreach (var pl in so.Lines ?? [])
            {
                var line = lines.FirstOrDefault(l => l.MarketplaceLineId == pl.MarketplaceLineId);
                if (line is null) continue;
                var f = await db.OrderLineFulfilments.FindAsync([line.Id], ct);
                if (f is null) continue;
                f.ShippedQty = Math.Max(f.ShippedQty, Math.Clamp(pl.ShippedQty, 0, line.Quantity));            // counters never go down
                f.CancelledQty = Math.Max(f.CancelledQty, Math.Clamp(pl.CancelledQty, 0, line.Quantity));
                f.RefundedQty = Math.Max(f.RefundedQty, Math.Clamp(pl.RefundedQty, 0, line.Quantity));
                f.ReturnedQty = Math.Max(f.ReturnedQty, Math.Clamp(pl.ReturnedQty, 0, line.Quantity));
            }
            var st = await db.SellerOrderStates.FindAsync([group.Id], ct);
            if (st is null) { st = new SellerOrderState { GroupId = group.Id, OrderId = order.Id, UpdatedAt = now }; db.SellerOrderStates.Add(st); }
            var portalStatus = FulfilmentStatus.FromPortal(so.Status);
            if (portalStatus is not null && !IsTerminal(st.Status) && IsAhead(portalStatus, st.Status)) { st.Status = portalStatus; st.UpdatedAt = now; }
            foreach (var ps in so.Shipments ?? [])
            {
                if (string.IsNullOrWhiteSpace(ps.ShipmentId) || ps.ShipmentId.Length > 100) continue;
                var sh = await db.OrderShipments.FirstOrDefaultAsync(x => x.GroupId == group.Id && x.PortalShipmentId == ps.ShipmentId, ct);
                if (sh is null) { sh = new OrderShipment { Id = Guid.NewGuid(), GroupId = group.Id, PortalShipmentId = ps.ShipmentId, UpdatedAt = now }; db.OrderShipments.Add(sh); }
                if (string.Equals(ps.Method, "local", StringComparison.OrdinalIgnoreCase)) sh.Method = "local";
                sh.Carrier ??= FulfilmentProjector.Cap(ps.Carrier, 100);
                sh.TrackingNumber ??= FulfilmentProjector.Cap(ps.TrackingNumber, 100);
                sh.ShippedAt ??= ps.ShippedAt is { } sa && sa <= now ? sa : null;
                sh.DeliveredAt ??= ps.DeliveredAt is { } da && da <= now ? da : null;
                sh.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        await events.ReplayParkedAsync("order", p.MarketplaceOrderId, ct);
        return OutboxResult.Delivered();
    }

    private static bool IsTerminal(string s) => s is FulfilmentStatus.Cancelled or FulfilmentStatus.Refunded;
    private static int Rank(string s) => Array.IndexOf(Forward, s);
    /// <summary>A forward move, or into a terminal state (cancelled / refunded win from any non-terminal state; delivered is a forward state).</summary>
    private static bool IsAhead(string portal, string ours) => IsTerminal(portal) || (Rank(portal) >= 0 && Rank(portal) > Rank(ours));
}
