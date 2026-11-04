using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// Projects the portal's seller-order events onto what the buyer sees (M5). Matching is by OUR marketplace order id AND the portal seller order id
/// (<see cref="EventRefs.Matches(OrderRef?, string, Guid)"/>), never by tracking number. Two kinds of write:
/// status transitions go through <see cref="ProjectionContext.AdvanceAsync"/> (ordered by the portal's time + event id, terminal states never reopened);
/// shipment facts (a parcel exists, was delivered, was voided) and line counters are monotonic and additive, so they apply even when the status step is skipped.
/// The aggregate key is per SELLER order (<c>MP-…/&lt;sellerOrderId&gt;</c>): cancelling one seller order never blocks another.
/// </summary>
public sealed class FulfilmentProjector(Microsoft.Extensions.Options.IOptions<Infrastructure.PortalOptions> portal, ILogger<FulfilmentProjector> log)
{
    public const string Accepted = "order.accepted", Held = "order.held", Resumed = "order.resumed", Shipped = "order.shipped", Tracking = "order.tracking_updated",
        Delivered = "order.delivered", Cancelled = "order.cancelled", ShipmentDelivered = "shipment.delivered", ShipmentVoided = "shipment.voided",
        CancellationDecided = "cancellation.decided", ReturnDecided = "return.decided", ReturnReceived = "return.received", LabelCreated = "return.label_created",
        AttemptFailed = "delivery.attempt_failed", MethodChanged = "order.delivery_method_changed", RefundIssued = "refund.issued";

    /// <summary>The types <see cref="FulfilmentHandler"/> owns. <c>order.cancelled</c> and <c>refund.issued</c> belong to M3's money handler, which calls <see cref="ApplyAsync"/> itself (one handler per type).</summary>
    public static readonly string[] OwnTypes = [Accepted, Held, Resumed, Shipped, Tracking, Delivered, ShipmentDelivered, ShipmentVoided, CancellationDecided, ReturnDecided, ReturnReceived, LabelCreated, AttemptFailed, MethodChanged];

    /// <summary>
    /// Runs <see cref="ApplyAsync"/> for an event whose MONEY part (M3) is already in this transaction, in a savepoint: if the projection fails for any reason, only the projection is rolled back
    /// (and its tracked changes dropped), the money part still commits and the event is acked. The failure is logged by type only (payloads can hold personal data).
    /// </summary>
    public async Task ApplyIsolatedAsync(ProjectionContext c, bool refundMoneyMoved, CancellationToken ct)
    {
        var tx = c.Db.Database.CurrentTransaction;
        if (tx is null || !tx.SupportsSavepoints) { await ApplyAsync(c, ct, refundMoneyMoved); return; }
        await c.Db.SaveChangesAsync(ct);   // the money part is flushed first: nothing after this point can undo it
        var tracked = c.Db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);   // what the money part (and the caller) hold
        const string name = "m5_projection";
        await tx.CreateSavepointAsync(name, ct);
        try
        {
            await ApplyAsync(c, ct, refundMoneyMoved);
            await c.Db.SaveChangesAsync(ct);   // errors surface here, inside the savepoint
            await tx.ReleaseSavepointAsync(name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackToSavepointAsync(name, CancellationToken.None);
            // The database is back at the money part, but the change tracker is not: entities the projection added, or changed and SAVED before it failed, still look current. Everything the
            // projection created is dropped; what was tracked before is re-read, so nothing stale is carried into the caller's own later SaveChanges (review P5).
            foreach (var e in c.Db.ChangeTracker.Entries().ToList())
            {
                if (!tracked.Contains(e.Entity)) { e.State = Microsoft.EntityFrameworkCore.EntityState.Detached; continue; }
                try { e.State = Microsoft.EntityFrameworkCore.EntityState.Unchanged; await e.ReloadAsync(CancellationToken.None); }
                catch (Exception) { e.State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
            }
            // and the projection is retried later from the stored event (the money part is not touched): one message per event
            await new OutboxWriter(c.Db, c.Time).TryEnqueueAsync(ReprojectHandler.Type, new { eventId = c.Event.Id, refundMoneyMoved }, "m5.reproject:" + c.Event.Id, TimeSpan.FromMinutes(1), CancellationToken.None);
            log.LogError("The order projection of a {Type} event failed ({ExceptionType}); the money part was kept and the projection is queued again", c.Event.Type, ex.GetType().FullName);
        }
    }

    public static string AggregateKey(string marketplaceOrderId, Guid sellerOrderId) => $"{marketplaceOrderId}/{sellerOrderId:D}";

    /// <param name="refundMoneyMoved">False when M3's money part refused a <c>refund.issued</c> (over the cap, unpaid): then the buyer is not told about a refund that did not happen.</param>
    public async Task<HandlerOutcome> ApplyAsync(ProjectionContext c, CancellationToken ct, bool refundMoneyMoved = true)
    {
        if (c.Event.Type == RefundIssued && !refundMoneyMoved) return HandlerOutcome.Skipped("no money moved");   // counts and the 'refunded' state follow only a refund we really reserved
        var data = c.Event.Data;
        var oref = EventRefs.Order(data);
        if (oref is null) return HandlerOutcome.Skipped("order reference missing");
        var order = await c.Db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Number == oref.MarketplaceOrderId, ct);
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
        // The buyer's notification (and its email, queued in the same transaction), once per event and kind.
        // Side facts (returns, labels, tracking, attempts) have their OWN aggregates: an order's terminal state (delivered) must not freeze them, the portal allows Delivered -> ReturnRequested.
        Task<bool> Side(string sub, string fieldGroup) => c.AdvanceAsync("order-side", $"{key}/{sub}", fieldGroup, null, ct);
        void Tell(string kind, object? parameters) => Notifier.Add(c.Db, new OutboxWriter(c.Db, c.Time), c.Time, order, group, kind, c.Event.Id, parameters);

        switch (c.Event.Type)
        {
            case Accepted:
            {
                var ok = await Step("status", FulfilmentStatus.Accepted);
                if (ok) { state.AcceptedAt = Time(data, "acceptedAt") ?? now; Tell(NotificationKinds.OrderAccepted, null); }
                return Done(ok, "older or terminal");
            }
            case Held: return Done(await Step("status", FulfilmentStatus.OnHold), "older or terminal");
            case Resumed: return Done(await Step("status", FulfilmentStatus.FromPortal(Str(data, "status")) is { } s && s != FulfilmentStatus.OnHold ? s : FulfilmentStatus.Accepted), "older or terminal");
            case Shipped:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                // A shipped event only FILLS what is empty: an older one arriving late must not overwrite newer tracking data.
                sh.Carrier ??= Cap(Str(data, "carrier"), 100);
                sh.Service ??= Cap(Str(data, "service"), 100);
                sh.TrackingNumber ??= Cap(Str(data, "trackingNumber"), 100);
                sh.TrackingUrl ??= SafeUrl(Str(data, "trackingUrl"));
                if (string.Equals(Str(data, "carrier"), "Local", StringComparison.OrdinalIgnoreCase) || Str(data, "method") == "local") sh.Method = "local";
                sh.ShippedAt ??= Time(data, "shippedAt") ?? now;
                sh.UpdatedAt = now;
                if (data.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                    {
                        var mlid = Str(l, "marketplaceLineId");
                        if (mlid is null || lines.All(x => x.MarketplaceLineId != mlid) || Int(l, "quantity") is not { } qty || qty <= 0) continue;
                        var existing = await c.Db.OrderShipmentLines.FindAsync([sh.Id, mlid], ct);
                        if (existing is null) c.Db.OrderShipmentLines.Add(new OrderShipmentLine { ShipmentId = sh.Id, MarketplaceLineId = mlid, Quantity = qty });
                        else existing.Quantity = qty;
                    }
                await c.Db.SaveChangesAsync(ct);
                await RecomputeShippedAsync(c.Db, group.Id, lines, ct);
                var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
                state.ShippedAt ??= sh.ShippedAt;   // a fact, even when the status step is skipped (shipped arriving after delivered)
                if (await Step("status", partial ? FulfilmentStatus.PartiallyShipped : FulfilmentStatus.Shipped)) { Tell(NotificationKinds.OrderShipped, new { carrier = sh.Carrier, partial }); }
                return HandlerOutcome.Applied();   // the parcel itself is recorded even when the status step was skipped
            }
            case Tracking:
            {
                if (Str(data, "kind") == "return") return HandlerOutcome.Skipped("return parcel");   // the return's own tracking is not a forward shipment
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                if (sh.DeliveredAt is not null) return HandlerOutcome.Skipped("shipment already delivered");   // a late in-transit scan after delivery
                if (!await Side("ship:" + sh.PortalShipmentId, "tracking")) return HandlerOutcome.Skipped("older");
                var wasOut = sh.TrackingStatus == "out_for_delivery";
                sh.Carrier = Cap(Str(data, "carrier"), 100) ?? sh.Carrier;
                sh.TrackingNumber = Cap(Str(data, "trackingNumber"), 100) ?? sh.TrackingNumber;
                sh.TrackingUrl = SafeUrl(Str(data, "trackingUrl")) ?? sh.TrackingUrl;
                if (Str(data, "method") == "local") sh.Method = "local";
                if (Str(data, "status") is { } st) { sh.TrackingStatus = Cap(st, 40); sh.TrackingStatusDetail = Cap(Str(data, "statusDetail"), 200); }
                if (data.TryGetProperty("eta", out var eta))
                {
                    if (eta.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(eta.GetString(), out var e)) sh.Eta = e.ToUniversalTime();
                    else if (eta.ValueKind == JsonValueKind.Object)
                    {
                        sh.Eta = Time(eta, "estimatedAt") ?? sh.Eta;
                        sh.EtaWindowStart = Time(eta, "windowStart") ?? sh.EtaWindowStart;
                        sh.EtaWindowEnd = Time(eta, "windowEnd") ?? sh.EtaWindowEnd;
                    }
                }
                sh.DriverFirstName = Cap(Str(data, "driverFirstName"), 60) ?? sh.DriverFirstName;
                if (data.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array) sh.TrackingEventsJson = TimelineJson(evs) ?? sh.TrackingEventsJson;
                // The position exists only while the buyer's stop is next. Anything else (another status, or an event without one) clears it: it is never carried over.
                if (Str(data, "status") == "out_for_delivery" && state.Status is not (FulfilmentStatus.Delivered or FulfilmentStatus.Cancelled or FulfilmentStatus.Refunded) && data.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object
                    && Dbl(pos, "lat") is { } lat && Dbl(pos, "lng") is { } lng
                    && lat is >= -90 and <= 90 && lng is >= -180 and <= 180)
                { sh.PositionLat = Math.Round(lat, 3); sh.PositionLng = Math.Round(lng, 3); sh.PositionAt = Time(data, "occurredAt") is { } oc && oc <= now ? oc : now; }
                else { sh.PositionLat = sh.PositionLng = null; sh.PositionAt = null; }
                sh.UpdatedAt = now;
                if (!wasOut && sh.TrackingStatus == "out_for_delivery") Tell(NotificationKinds.OutForDelivery, null);   // once, not on every position update
                return HandlerOutcome.Applied();
            }
            case ShipmentDelivered:
            {
                if (Str(data, "kind") == "return") return HandlerOutcome.Skipped("return parcel");
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                sh.DeliveredAt ??= Time(data, "deliveredAt") ?? now;
                sh.PositionLat = sh.PositionLng = null; sh.PositionAt = null;   // delivered: no more live position
                sh.Placement = Cap(Str(data, "placement"), 40) ?? sh.Placement;
                if (data.TryGetProperty("proof", out var proof) && proof.ValueKind == JsonValueKind.Object && ProofPaths(proof) is { Count: > 0 } paths)
                {
                    // links are short-lived (the portal says expiresInMinutes, 60 by default): kept no longer than that, counted from now (our clock, never the event's)
                    var minutes = Int(proof, "expiresInMinutes") is { } m ? Math.Clamp(m, 1, 120) : 60;
                    sh.ProofJson = JsonSerializer.Serialize(paths);
                    sh.ProofExpiresAt = now.AddMinutes(minutes);
                }
                sh.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case ShipmentVoided:
            {
                var sh = await ShipmentAsync(c, group, Str(data, "shipmentId"));
                if (sh is null) return HandlerOutcome.Skipped("shipment id missing");
                sh.VoidedAt ??= now;
                sh.PositionLat = sh.PositionLng = null; sh.PositionAt = null;
                sh.UpdatedAt = now;
                await c.Db.SaveChangesAsync(ct);
                await RecomputeShippedAsync(c.Db, group.Id, lines, ct);
                return HandlerOutcome.Applied();
            }
            case Delivered:
            {
                var ok = await Step("status", FulfilmentStatus.Delivered, TerminalStates.Delivered);
                if (ok) await ClearPositionsAsync(c.Db, group.Id, ct);
                if (ok) { state.DeliveredAt = Time(data, "deliveredAt") ?? now; state.DeliveredBy = Str(data, "by") is "seller" or "buyer" ? Str(data, "by") : null; Tell(NotificationKinds.OrderDelivered, null); }
                return Done(ok, "older or terminal");
            }
            case Cancelled:
            {
                var partial = data.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
                if (partial)
                {
                    await AddCountsAsync(c.Db, data, lines, (f, q) => f.CancelledQty += q, ct);
                    return HandlerOutcome.Applied();
                }
                var ok = await Step("status", FulfilmentStatus.Cancelled, TerminalStates.Cancelled);
                if (ok) await ClearPositionsAsync(c.Db, group.Id, ct);
                if (ok)
                {
                    state.CancelledAt = now;
                    state.CancelledBy = Cap(Str(data, "cancelledBy"), 30);
                    state.CancelReason = Cap(Str(data, "reason"), 100);
                    var open = await c.Db.OrderCancelRequests.Where(r => r.GroupId == group.Id && r.OpenKey != null).ToListAsync(ct);
                    foreach (var r in open) { r.Status = CancelRequestStatus.Approved; r.OpenKey = null; r.UpdatedAt = now; }
                    Tell(NotificationKinds.OrderCancelled, null);
                }
                return Done(ok, "older or terminal");
            }
            case CancellationDecided:
            {
                if (!await Side("cancellation", "decision")) return HandlerOutcome.Skipped("older");
                var rid = Str(data, "requestId");
                var req = rid is null ? null : await c.Db.OrderCancelRequests.FirstOrDefaultAsync(r => r.RequestId == rid && r.GroupId == group.Id, ct);
                if (req is null) return HandlerOutcome.Skipped("unknown cancel request");
                if (Str(data, "decision") == "rejected") { req.Status = CancelRequestStatus.Rejected; req.OpenKey = null; req.DecisionNote = Cap(Str(data, "note"), 500); req.UpdatedAt = now; Tell(NotificationKinds.CancellationRejected, null); }
                return HandlerOutcome.Applied();
            }
            case ReturnDecided or ReturnReceived or LabelCreated:
                return await ReturnEventAsync(c, group, key, lines, now, Tell, ct);
            case AttemptFailed:
            {
                if (!await Side("attempts", "n")) return HandlerOutcome.Skipped("older");
                var attempt = Int(data, "attempt") ?? state.DeliveryAttemptsFailed + 1;
                state.DeliveryAttemptsFailed = Math.Max(state.DeliveryAttemptsFailed, Math.Clamp(attempt, 0, 100));   // a number, not a counter: replays cannot inflate it
                state.LastAttemptFailedAt = Time(data, "occurredAt") ?? now;
                Tell(NotificationKinds.DeliveryAttemptFailed, null);
                state.LastAttemptFailedReason = Str(data, "reason") is "no_one_home" or "address_issue" or "refused" or "unsafe" ? Str(data, "reason") : null;
                state.UpdatedAt = now;
                return HandlerOutcome.Applied();
            }
            case MethodChanged:
            {
                if (!await Side("method", "m")) return HandlerOutcome.Skipped("older");
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
                if (full && await Step("status", FulfilmentStatus.Refunded, TerminalStates.Refunded)) await ClearPositionsAsync(c.Db, group.Id, ct);
                if (refundMoneyMoved) Tell(NotificationKinds.RefundIssued, new { amountCents = (Long(data, "amountCents") ?? 0) + (Long(data, "shippingCents") ?? 0), full });
                return HandlerOutcome.Applied();
            }
            default: return HandlerOutcome.Skipped("not a fulfilment event");
        }
    }

    private async Task<HandlerOutcome> ReturnEventAsync(ProjectionContext c, OrderGroup group, string key, List<OrderLine> lines, DateTimeOffset now, Action<string, object?> tell, CancellationToken ct)
    {
        var data = c.Event.Data;
        var rid = Str(data, "returnId");
        var ret = rid is null ? null : await c.Db.OrderReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.ReturnId == rid && r.GroupId == group.Id, ct);
        if (ret is null) return HandlerOutcome.Skipped("unknown return");   // matched on OUR return id AND the seller order, so another seller's event cannot touch it
        // the return has its own aggregate: it goes on after the order is delivered (Delivered -> ReturnRequested is legal at the portal)
        if (!await c.AdvanceAsync("order-side", $"{key}/return:{ret.ReturnId}", c.Event.Type == LabelCreated ? "label" : "return", null, ct)) return HandlerOutcome.Skipped("older");
        switch (c.Event.Type)
        {
            case ReturnDecided when Str(data, "decision") == "approved" && ret.Status is ReturnStatus.Pending or ReturnStatus.Requested:
                ret.Status = ReturnStatus.Approved; ret.DecisionNote = Cap(Str(data, "note"), 500); tell(NotificationKinds.ReturnApproved, null); break;
            case ReturnDecided when Str(data, "decision") == "rejected" && ret.Status is ReturnStatus.Pending or ReturnStatus.Requested or ReturnStatus.Approved:
                ret.Status = ReturnStatus.Rejected; ret.DecisionNote = Cap(Str(data, "note"), 500);
                await FulfilmentLines.ReleaseAsync(c.Db, ret, received: false, ct);
                tell(NotificationKinds.ReturnRejected, null);
                break;
            case ReturnReceived when ret.Status is not ReturnStatus.Received and not ReturnStatus.Refunded and not ReturnStatus.Rejected:
                ret.Status = ReturnStatus.Received;
                await FulfilmentLines.ReleaseAsync(c.Db, ret, received: true, ct);
                tell(NotificationKinds.ReturnReceived, null);
                break;
            case LabelCreated:
                var url = LabelLinks.Valid(Str(data, "labelUrl"), LabelLinks.Origin(portal.Value), now);
                var exp = Time(data, "labelUrlExpiresAt");
                if (url is null || exp is null || exp <= now) break;   // never keep a link that is dead or not on the portal's origin
                ret.LabelUrl = url; ret.LabelUrlExpiresAt = exp;
                ret.LabelCarrier = Cap(Str(data, "carrier"), 100); ret.LabelTrackingNumber = Cap(Str(data, "trackingNumber"), 100); ret.LabelTrackingUrl = SafeUrl(Str(data, "trackingUrl"));
                tell(NotificationKinds.ReturnLabelReady, null);
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
            if (line is null || Int(l, "quantity") is not { } qty || qty <= 0) continue;
            var f = await db.OrderLineFulfilments.FindAsync([line.Id], ct);
            if (f is not null) add(f, Math.Min(qty, line.Quantity));
        }
    }

    /// <summary>A terminal seller order has no live position: it is cleared from every shipment of the group (M5 review P2).</summary>
    internal static async Task ClearPositionsAsync(MarketplaceDb db, Guid groupId, CancellationToken ct)
    {
        var sh = await db.OrderShipments.Where(x => x.GroupId == groupId && x.PositionAt != null).ToListAsync(ct);
        foreach (var x in sh) { x.PositionLat = x.PositionLng = null; x.PositionAt = null; }
    }

    public const string ProofPrefix = "/api/public/delivery-proof/";

    /// <summary>The portal-relative path+query of each proof link (photos, then the signature). Anything that is not under the proof path of the portal is dropped.</summary>
    internal static List<string> ProofPaths(JsonElement proof)
    {
        var raw = new List<string>();
        if (proof.TryGetProperty("photoUrls", out var ph) && ph.ValueKind == JsonValueKind.Array) raw.AddRange(ph.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(6));
        if (Str(proof, "signatureUrl") is { } sig) raw.Add(sig);
        var paths = new List<string>();
        foreach (var u in raw)
        {
            // A portal-relative link ("/api/...") is kept as it is: on Linux Uri.TryCreate parses it as an absolute file URI and PathAndQuery
            // would mangle it. Anything else must be an absolute http(s) URL, of which only the path and query are kept (M5 review P1).
            string pq;
            if (u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal)) pq = u;
            else if (Uri.TryCreate(u, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp)) pq = abs.PathAndQuery;
            else continue;
            if (pq.StartsWith(ProofPrefix, StringComparison.Ordinal) && pq.Length <= 600 && !pq.Contains("..") && !pq.Contains('#')) paths.Add(pq);
        }
        return paths;
    }

    /// <summary>The carrier timeline, bounded: at most 30 entries, texts capped, times parsed; null when nothing usable.</summary>
    internal static string? TimelineJson(JsonElement events)
    {
        var list = new List<object>();
        foreach (var e in events.EnumerateArray().Take(30))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            list.Add(new { status = Cap(Str(e, "status"), 40), detail = Cap(Str(e, "detail"), 200), location = Cap(Str(e, "location"), 100), at = Time(e, "at") });
        }
        var json = JsonSerializer.Serialize(list);
        return list.Count == 0 || json.Length > 8000 ? null : json;
    }

    /// <summary>Numbers are read only when the value IS a number: <c>TryGetInt32</c> on a string would throw and fail the whole webhook (M5 review H2).</summary>
    internal static int? Int(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;
    internal static long? Long(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
    internal static double? Dbl(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : null;

    internal static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static string? Cap(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
    internal static DateTimeOffset? Time(JsonElement d, string n) => Str(d, n) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    /// <summary>Only absolute https links are kept (a buyer's browser follows them): never javascript: or data:.</summary>
    internal static string? SafeUrl(string? s) => s is { Length: > 0 and <= 1000 } && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? s : null;
}

/// <summary>The handler for the fulfilment event types nobody else owns. A failure of the projection of ONE event is isolated in a savepoint (see <see cref="FulfilmentProjectorGuard"/>) only when it runs inside M3's money handler; here the event is simply retried.</summary>
public sealed class FulfilmentHandler(FulfilmentProjector projector) : IProjectionHandler
{
    public IReadOnlyCollection<string> EventTypes { get; } = FulfilmentProjector.OwnTypes;
    public Task<HandlerOutcome> HandleAsync(ProjectionContext context, CancellationToken ct) => projector.ApplyAsync(context, ct);
}

/// <summary>
/// Portal-given label links: only the portal's own public origin, only the label path, only while alive (<c>exp</c> in the link and the stated expiry). A link anywhere else is never
/// stored or shown (it would be a phishing target in the buyer's own account page).
/// </summary>
public static class LabelLinks
{
    public const string PathPrefix = "/api/v1/shipping/public/labels/";

    public static string? Origin(Infrastructure.PortalOptions o)
        => Uri.TryCreate(string.IsNullOrWhiteSpace(o.PublicBaseUrl) ? o.BaseUrl : o.PublicBaseUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;

    public static string? Valid(string? url, string? origin, DateTimeOffset now)
    {
        if (url is not { Length: > 0 and <= 1000 } || origin is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (!string.Equals(u.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase) || u.Scheme != Uri.UriSchemeHttps && !origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)) return null;
        if (!u.AbsolutePath.StartsWith(PathPrefix, StringComparison.Ordinal) || u.AbsolutePath.Contains("..") || !string.IsNullOrEmpty(u.Fragment)) return null;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        return long.TryParse(q["exp"], out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp) > now ? url : null;
    }
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

/// <summary>
/// Re-runs the projection of a stored portal event whose first attempt failed inside the savepoint of <see cref="FulfilmentProjector.ApplyIsolatedAsync"/> (the money part had committed). The
/// event is read from the inbox (its raw body is kept until retention), applied in its own transaction through the same ordering rules, so an event that is now out of date is skipped, never
/// forced. A failure retries with the outbox's backoff (review P5).
/// </summary>
public sealed class ReprojectHandler(MarketplaceDb db, FulfilmentProjector projector, ProjectionStore store, TimeProvider time) : IOutboxHandler
{
    public const string Type = "m5.reproject";
    public IReadOnlyCollection<string> Types { get; } = [Type];
    private sealed record Payload(string? EventId, bool RefundMoneyMoved = true);

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p?.EventId is null) return OutboxResult.Dead("invalid_payload");
        var row = await db.PortalInboundEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == p.EventId, ct);
        if (row?.PayloadJson is null) return OutboxResult.Dead("event_gone");   // the raw body was purged: nothing to replay
        if (!PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes(row.PayloadJson), out var evt, out _) || evt is null) return OutboxResult.Dead("invalid_event");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var outcome = await projector.ApplyAsync(new ProjectionContext(db, evt with { CreatedAt = row.OrderingAt }, store, time), ct, p.RefundMoneyMoved);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
        return OutboxResult.Delivered();   // applied, skipped (older/terminal) or parked: all are final answers for this message
    }
}
