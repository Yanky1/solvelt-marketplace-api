using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// Projects the portal's seller-order events onto what the buyer sees (M5). Matching is by OUR marketplace order id AND the portal seller order id
/// (<see cref="EventRefs.Matches(OrderRef?, string, Guid)"/>), never by tracking number. Two kinds of write:
/// status transitions go through <see cref="ProjectionContext.AdvanceAsync"/> (ordered by the portal's time + event id, terminal states never reopened);
/// shipment facts (a parcel exists, was delivered, was voided) and line counters are monotonic and additive, so they apply even when the status step is skipped.
/// The aggregate key is per SELLER order (<c>MP-…/&lt;sellerOrderId&gt;</c>): cancelling one seller order never blocks another.
/// </summary>
public static class FulfilmentProjector
{
    public const string Accepted = "order.accepted", Held = "order.held", Resumed = "order.resumed", Shipped = "order.shipped", Tracking = "order.tracking_updated",
        Delivered = "order.delivered", Cancelled = "order.cancelled", ShipmentDelivered = "shipment.delivered", ShipmentVoided = "shipment.voided",
        CancellationDecided = "cancellation.decided", ReturnDecided = "return.decided", ReturnReceived = "return.received", LabelCreated = "return.label_created",
        AttemptFailed = "delivery.attempt_failed", MethodChanged = "order.delivery_method_changed", RefundIssued = "refund.issued";

    /// <summary>The types <see cref="FulfilmentHandler"/> owns. <c>order.cancelled</c> and <c>refund.issued</c> belong to M3's money handler, which calls <see cref="ApplyAsync"/> itself (one handler per type).</summary>
    public static readonly string[] OwnTypes = [Accepted, Held, Resumed, Shipped, Tracking, Delivered, ShipmentDelivered, ShipmentVoided, CancellationDecided, ReturnDecided, ReturnReceived, LabelCreated, AttemptFailed, MethodChanged];

    public static string AggregateKey(string marketplaceOrderId, Guid sellerOrderId) => $"{marketplaceOrderId}/{sellerOrderId:D}";

    public static async Task<HandlerOutcome> ApplyAsync(ProjectionContext c, CancellationToken ct)
    {
        var data = c.Event.Data;
        var oref = EventRefs.Order(data);
        if (oref is null) return HandlerOutcome.Skipped("order reference missing");
        var order = await c.Db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).FirstOrDefaultAsync(o => o.Number == oref.MarketplaceOrderId, ct);
        if (order is null) return HandlerOutcome.Skipped("unknown marketplace order");
        var group = order.Groups.FirstOrDefault(g => g.SellerOrderId is not null && EventRefs.Matches(oref, order.Number, g.SellerOrderId.Value));
        if (group is null)
            return order.Groups.Any(g => g.SellerOrderId is null) ? HandlerOutcome.Parked("order", order.Number, "seller orders not stored yet") : HandlerOutcome.Skipped("no such seller order on this order");

        var key = AggregateKey(order.Number, group.SellerOrderId!.Value);
        var state = await c.Db.SellerOrderStates.FindAsync([group.Id], ct);
        if (state is null)
        {
            state = new SellerOrderState { GroupId = group.Id, OrderId = order.Id, UpdatedAt = c.Time.GetUtcNow() };
            c.Db.SellerOrderStates.Add(state);
        }
        var lines = order.Lines.Where(l => l.GroupId == group.Id).ToList();
        await FulfilmentLines.EnsureAsync(c.Db, lines, ct);
        var now = c.Time.GetUtcNow().ToUniversalTime();

        async Task<bool> Step(string fieldGroup, string status, string? terminal = null)
        {
            if (!await c.AdvanceAsync("order", key, fieldGroup, terminal, ct)) return false;
            state.Status = status;
            state.UpdatedAt = now;
            return true;
        }
        HandlerOutcome Done(bool applied, string why) => applied ? HandlerOutcome.Applied() : HandlerOutcome.Skipped(why);

        switch (c.Event.Type)
        {
            case Accepted:
            {
                var ok = await Step("status", FulfilmentStatus.Accepted);
                if (ok) state.AcceptedAt = Time(data, "acceptedAt") ?? now;
                return Done(ok, "older or terminal");
            }
            case Held: return Done(await Step("status", FulfilmentStatus.OnHold), "older or terminal");
            case Resumed: return Done(await Step("status", FulfilmentStatus.FromPortal(Str(data, "status")) is { } s && s != FulfilmentStatus.OnHold ? s : FulfilmentStatus.Accepted), "older or terminal");
            case Shipped:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                sh.Carrier = Cap(Str(data, "carrier"), 100) ?? sh.Carrier;
                sh.Service = Cap(Str(data, "service"), 100) ?? sh.Service;
                sh.TrackingNumber = Cap(Str(data, "trackingNumber"), 100) ?? sh.TrackingNumber;
                sh.TrackingUrl = SafeUrl(Str(data, "trackingUrl")) ?? sh.TrackingUrl;
                sh.Method = string.Equals(Str(data, "carrier"), "Local", StringComparison.OrdinalIgnoreCase) || Str(data, "method") == "local" ? "local" : sh.Method;
                sh.ShippedAt ??= Time(data, "shippedAt") ?? now;
                sh.UpdatedAt = now;
                if (data.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                    {
                        var mlid = Str(l, "marketplaceLineId");
                        if (mlid is null || lines.All(x => x.MarketplaceLineId != mlid) || !l.TryGetProperty("quantity", out var q) || !q.TryGetInt32(out var qty) || qty <= 0) continue;
                        var existing = await c.Db.OrderShipmentLines.FindAsync([sh.Id, mlid], ct);
                        if (existing is null) c.Db.OrderShipmentLines.Add(new OrderShipmentLine { ShipmentId = sh.Id, MarketplaceLineId = mlid, Quantity = qty });
                        else existing.Quantity = qty;
                    }
                await c.Db.SaveChangesAsync(ct);
                await RecomputeShippedAsync(c.Db, group.Id, lines, ct);
                var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
                if (await Step("status", partial ? FulfilmentStatus.PartiallyShipped : FulfilmentStatus.Shipped)) state.ShippedAt ??= sh.ShippedAt;
                return HandlerOutcome.Applied();   // the parcel itself is recorded even when the status step was skipped
            }
            case Tracking:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                if (!await c.AdvanceAsync("order", key, "tracking:" + sh.PortalShipmentId, null, ct)) return HandlerOutcome.Skipped("older or terminal");
                sh.Carrier = Cap(Str(data, "carrier"), 100) ?? sh.Carrier;
                sh.TrackingNumber = Cap(Str(data, "trackingNumber"), 100) ?? sh.TrackingNumber;
                sh.TrackingUrl = SafeUrl(Str(data, "trackingUrl")) ?? sh.TrackingUrl;
                if (Str(data, "method") == "local") sh.Method = "local";
                if (Str(data, "status") is { } st) { sh.TrackingStatus = Cap(st, 40); sh.TrackingStatusDetail = Cap(Str(data, "statusDetail"), 200); }
                if (data.TryGetProperty("eta", out var eta))
                {
                    if (eta.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(eta.GetString(), out var e)) sh.Eta = e.ToUniversalTime();
                    else if (eta.ValueKind == JsonValueKind.Object && Time(eta, "estimatedAt") is { } est) sh.Eta = est;
                }
                sh.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case ShipmentDelivered:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                sh.DeliveredAt ??= Time(data, "deliveredAt") ?? now;
                sh.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case ShipmentVoided:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                sh.VoidedAt ??= now;
                sh.UpdatedAt = now;
                await c.Db.SaveChangesAsync(ct);
                await RecomputeShippedAsync(c.Db, group.Id, lines, ct);
                return HandlerOutcome.Applied();
            }
            case Delivered:
            {
                var ok = await Step("status", FulfilmentStatus.Delivered, TerminalStates.Delivered);
                if (ok) { state.DeliveredAt = Time(data, "deliveredAt") ?? now; state.DeliveredBy = Str(data, "by") is "seller" or "buyer" ? Str(data, "by") : null; }
                return Done(ok, "older or terminal");
            }
            case Cancelled:
            {
                var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
                if (partial)
                {
                    if (!await c.AdvanceAsync("order", key, "cancel-remainder", null, ct)) return HandlerOutcome.Skipped("older or terminal");
                    await AddCountsAsync(c.Db, data, lines, (f, q) => f.CancelledQty += q, ct);
                    return HandlerOutcome.Applied();
                }
                var ok = await Step("status", FulfilmentStatus.Cancelled, TerminalStates.Cancelled);
                if (ok)
                {
                    state.CancelledAt = now;
                    state.CancelledBy = Cap(Str(data, "cancelledBy"), 30);
                    state.CancelReason = Cap(Str(data, "reason"), 100);
                    var open = await c.Db.OrderCancelRequests.Where(r => r.GroupId == group.Id && r.OpenKey != null).ToListAsync(ct);
                    foreach (var r in open) { r.Status = CancelRequestStatus.Approved; r.OpenKey = null; r.UpdatedAt = now; }
                }
                return Done(ok, "older or terminal");
            }
            case CancellationDecided:
            {
                if (!await c.AdvanceAsync("order", key, "cancellation", null, ct)) return HandlerOutcome.Skipped("older or terminal");
                var rid = Str(data, "requestId");
                var req = rid is null ? null : await c.Db.OrderCancelRequests.FirstOrDefaultAsync(r => r.RequestId == rid && r.GroupId == group.Id, ct);
                if (req is null) return HandlerOutcome.Skipped("unknown cancel request");
                if (Str(data, "decision") == "rejected") { req.Status = CancelRequestStatus.Rejected; req.OpenKey = null; req.DecisionNote = Cap(Str(data, "note"), 500); req.UpdatedAt = now; }
                return HandlerOutcome.Applied();
            }
            case ReturnDecided or ReturnReceived or LabelCreated:
                return await ReturnEventAsync(c, group, key, lines, now, ct);
            case AttemptFailed:
            {
                if (!await c.AdvanceAsync("order", key, "attempts", null, ct)) return HandlerOutcome.Skipped("older or terminal");
                var attempt = data.TryGetProperty("attempt", out var a) && a.TryGetInt32(out var n) ? n : state.DeliveryAttemptsFailed + 1;
                state.DeliveryAttemptsFailed = Math.Max(state.DeliveryAttemptsFailed, Math.Clamp(attempt, 0, 100));   // a number, not a counter: replays cannot inflate it
                state.LastAttemptFailedAt = Time(data, "occurredAt") ?? now;
                state.LastAttemptFailedReason = Str(data, "reason") is "no_one_home" or "address_issue" or "refused" or "unsafe" ? Str(data, "reason") : null;
                state.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case MethodChanged:
            {
                if (!await c.AdvanceAsync("order", key, "method", null, ct)) return HandlerOutcome.Skipped("older or terminal");
                if (Str(data, "method") == "local") state.DeliveryMethodChangedTo = "local";
                state.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case RefundIssued:
            {
                var full = data.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True;
                await AddCountsAsync(c.Db, data, lines, (x, q) => x.RefundedQty += q, ct);
                if (Str(data, "returnId") is { } retId)
                {
                    var ret = await c.Db.OrderReturns.FirstOrDefaultAsync(r => r.ReturnId == retId && r.GroupId == group.Id, ct);
                    if (ret is not null && ret.Status == ReturnStatus.Received) { ret.Status = ReturnStatus.Refunded; ret.UpdatedAt = now; }
                }
                if (full) await Step("status", FulfilmentStatus.Refunded, TerminalStates.Refunded);
                return HandlerOutcome.Applied();
            }
            default: return HandlerOutcome.Skipped("not a fulfilment event");
        }
    }

    private static async Task<HandlerOutcome> ReturnEventAsync(ProjectionContext c, OrderGroup group, string key, List<OrderLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        var data = c.Event.Data;
        var rid = Str(data, "returnId");
        var ret = rid is null ? null : await c.Db.OrderReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.ReturnId == rid && r.GroupId == group.Id, ct);
        if (ret is null) return HandlerOutcome.Skipped("unknown return");   // matched on OUR return id AND the seller order, so another seller's event cannot touch it
        var fieldGroup = (c.Event.Type == LabelCreated ? "label:" : "return:") + ret.ReturnId;
        if (!await c.AdvanceAsync("order", key, fieldGroup, null, ct)) return HandlerOutcome.Skipped("older or terminal");
        switch (c.Event.Type)
        {
            case ReturnDecided when Str(data, "decision") == "approved" && ret.Status is ReturnStatus.Pending or ReturnStatus.Requested:
                ret.Status = ReturnStatus.Approved; ret.DecisionNote = Cap(Str(data, "note"), 500); break;
            case ReturnDecided when Str(data, "decision") == "rejected" && ret.Status is ReturnStatus.Pending or ReturnStatus.Requested or ReturnStatus.Approved:
                ret.Status = ReturnStatus.Rejected; ret.DecisionNote = Cap(Str(data, "note"), 500);
                await FulfilmentLines.ReleaseAsync(c.Db, ret, received: false, ct);
                break;
            case ReturnReceived when ret.Status is not ReturnStatus.Received and not ReturnStatus.Refunded and not ReturnStatus.Rejected:
                ret.Status = ReturnStatus.Received;
                await FulfilmentLines.ReleaseAsync(c.Db, ret, received: true, ct);
                break;
            case LabelCreated:
                var url = SafeUrl(Str(data, "labelUrl"));
                var exp = Time(data, "labelUrlExpiresAt");
                if (url is null || exp is null || exp <= now) break;   // never keep a link that is already dead
                ret.LabelUrl = url; ret.LabelUrlExpiresAt = exp;
                ret.LabelCarrier = Cap(Str(data, "carrier"), 100); ret.LabelTrackingNumber = Cap(Str(data, "trackingNumber"), 100); ret.LabelTrackingUrl = SafeUrl(Str(data, "trackingUrl"));
                break;
        }
        ret.UpdatedAt = now;
        return HandlerOutcome.Applied();
    }

    private static async Task<OrderShipment?> ShipmentAsync(ProjectionContext c, OrderGroup group, string? portalId)
    {
        if (string.IsNullOrWhiteSpace(portalId) || portalId.Length > 100) return null;
        var sh = c.Db.OrderShipments.Local.FirstOrDefault(s => s.GroupId == group.Id && s.PortalShipmentId == portalId)
            ?? await c.Db.OrderShipments.FirstOrDefaultAsync(s => s.GroupId == group.Id && s.PortalShipmentId == portalId);
        if (sh is null)
        {
            sh = new OrderShipment { Id = Guid.NewGuid(), GroupId = group.Id, PortalShipmentId = portalId, UpdatedAt = c.Time.GetUtcNow() };
            c.Db.OrderShipments.Add(sh);
        }
        return sh;
    }

    /// <summary>Shipped units per line = the sum over parcels that are not voided. Recomputed, never incremented, so a replayed event cannot double it.</summary>
    private static async Task RecomputeShippedAsync(MarketplaceDb db, Guid groupId, List<OrderLine> lines, CancellationToken ct)
    {
        var ids = await db.OrderShipments.Where(s => s.GroupId == groupId && s.VoidedAt == null).Select(s => s.Id).ToListAsync(ct);
        var sums = (await db.OrderShipmentLines.Where(l => ids.Contains(l.ShipmentId)).ToListAsync(ct)).GroupBy(l => l.MarketplaceLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var line in lines)
        {
            var f = await db.OrderLineFulfilments.FindAsync([line.Id], ct);
            if (f is not null) f.ShippedQty = Math.Min(line.Quantity, sums.GetValueOrDefault(line.MarketplaceLineId));
        }
    }

    private static async Task AddCountsAsync(MarketplaceDb db, JsonElement data, List<OrderLine> lines, Action<OrderLineFulfilment, int> add, CancellationToken ct)
    {
        if (!data.TryGetProperty("lines", out var ls) || ls.ValueKind != JsonValueKind.Array) return;
        foreach (var l in ls.EnumerateArray())
        {
            var line = lines.FirstOrDefault(x => x.MarketplaceLineId == Str(l, "marketplaceLineId"));
            if (line is null || !l.TryGetProperty("quantity", out var q) || !q.TryGetInt32(out var qty) || qty <= 0) continue;
            var f = await db.OrderLineFulfilments.FindAsync([line.Id], ct);
            if (f is not null) add(f, Math.Min(qty, line.Quantity));
        }
    }

    internal static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static string? Cap(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
    internal static DateTimeOffset? Time(JsonElement d, string n) => Str(d, n) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    /// <summary>Only absolute https links are kept (a buyer's browser follows them): never javascript: or data:.</summary>
    internal static string? SafeUrl(string? s) => s is { Length: > 0 and <= 1000 } && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? s : null;
}

/// <summary>The handler for the fulfilment event types nobody else owns.</summary>
public sealed class FulfilmentHandler : IProjectionHandler
{
    public IReadOnlyCollection<string> EventTypes { get; } = FulfilmentProjector.OwnTypes;
    public Task<HandlerOutcome> HandleAsync(ProjectionContext context, CancellationToken ct) => FulfilmentProjector.ApplyAsync(context, ct);
}

public static class FulfilmentLines
{
    /// <summary>Makes sure every line has its counter row. Two racers inserting the same row conflict on the key: the loser's transaction rolls back and is retried (webhook 5xx) or ignored (buyer action).</summary>
    public static async Task EnsureAsync(MarketplaceDb db, IEnumerable<OrderLine> lines, CancellationToken ct)
    {
        foreach (var l in lines)
            if (await db.OrderLineFulfilments.FindAsync([l.Id], ct) is null)
                db.OrderLineFulfilments.Add(new OrderLineFulfilment { LineId = l.Id });
    }

    /// <summary>Gives back the units a return held. <paramref name="received"/>: they become returned units; otherwise they are free again. Each line is released once (<see cref="OrderReturnLine.Reserved"/>).</summary>
    public static async Task ReleaseAsync(MarketplaceDb db, OrderReturn ret, bool received, CancellationToken ct)
    {
        foreach (var rl in ret.Lines.Where(l => l.Reserved))
        {
            // atomic in the database: the buyer's own reservations run as conditional UPDATEs on the same row
            var q = rl.Quantity;
            if (received)
                await db.OrderLineFulfilments.Where(f => f.LineId == rl.LineId).ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ReturnPendingQty, f => f.ReturnPendingQty >= q ? f.ReturnPendingQty - q : 0).SetProperty(f => f.ReturnedQty, f => f.ReturnedQty + q), ct);
            else
                await db.OrderLineFulfilments.Where(f => f.LineId == rl.LineId).ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ReturnPendingQty, f => f.ReturnPendingQty >= q ? f.ReturnPendingQty - q : 0), ct);
            rl.Reserved = false;
        }
    }
}
