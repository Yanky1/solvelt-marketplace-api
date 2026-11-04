using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Fulfilment;

public sealed record ActionResult(int Status, object? Body = null, string? Code = null, string? Message = null, IReadOnlyDictionary<string, string>? Errors = null)
{
    public static ActionResult NotFound() => new(404, Code: "not_found", Message: "Not found.");
    public static ActionResult Conflict(string code, string message) => new(409, Code: code, Message: message);
    public static ActionResult Invalid(string field, string message) => new(422, Errors: new Dictionary<string, string> { [field] = message });
}

public sealed record ReturnLineInput(string? MarketplaceLineId, int Quantity);

/// <summary>
/// What the buyer can DO with an order (M5): ask to cancel, ask for a return, confirm delivery, refresh a return label link. Every action is idempotent and stored BEFORE the portal call:
/// the row (with OUR id: <c>requestId</c>, <c>returnId</c>) and its outbox message commit in one transaction, the outbox delivers to the portal with retries. Another buyer's order is a 404.
/// </summary>
public sealed class BuyerOrderActions(MarketplaceDb db, OutboxWriter outbox, ISignedPortalClient portal, TimeProvider time)
{
    public static readonly string[] CancelReasons = ["changed_mind", "ordered_by_mistake", "found_better_price", "delivery_too_slow", "other"];
    public static readonly string[] ReturnReasons = ["damaged", "not_as_described", "wrong_item", "no_longer_needed", "other"];
    public const string CancelType = "portal.cancel-request", ReturnType = "portal.return-request", DeliveryType = "portal.delivery-confirmation";
    public sealed record IdPayload(string Id);

    private static readonly string[] Cancellable = [FulfilmentStatus.New, FulfilmentStatus.Accepted, FulfilmentStatus.OnHold, FulfilmentStatus.PartiallyShipped];
    private static readonly string[] Returnable = [FulfilmentStatus.Shipped, FulfilmentStatus.PartiallyShipped, FulfilmentStatus.Delivered];

    /// <summary>The buyer's own order and the seller order they mean. With several seller orders the caller must name one (<c>sellerOrderNumber</c>).</summary>
    private async Task<(MarketplaceOrder? Order, OrderGroup? Group, string State, ActionResult? Error)> LoadAsync(BuyerPrincipal buyer, string number, string? sellerOrderNumber, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Groups).Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(x => x.Number == number && x.AccountId == buyer.AccountId, ct);
        if (o is null) return (null, null, "", ActionResult.NotFound());
        var groups = o.Groups.Where(g => g.SellerOrderId is not null).ToList();
        var g = sellerOrderNumber is null ? (groups.Count == 1 ? groups[0] : null) : groups.FirstOrDefault(x => x.SellerOrderNumber == sellerOrderNumber);
        if (g is null)
            return (o, null, "", sellerOrderNumber is null && groups.Count > 1 ? ActionResult.Invalid("sellerOrderNumber", "Choose which seller order you mean.") : ActionResult.NotFound());
        if (o.Status != OrderStatus.Placed) return (o, g, "", ActionResult.Conflict("order_not_ready", "This order is not placed yet."));
        var state = (await db.SellerOrderStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == g.Id, ct))?.Status ?? FulfilmentStatus.New;
        return (o, g, state, null);
    }

    public async Task<ActionResult> CancelAsync(BuyerPrincipal buyer, string number, string? sellerOrderNumber, string? reason, string? note, CancellationToken ct)
    {
        if (reason is null || !CancelReasons.Contains(reason)) return ActionResult.Invalid("reason", "Choose a reason.");
        if (note is { Length: > 500 }) return ActionResult.Invalid("note", "Keep the note under 500 characters.");
        var (o, g, state, err) = await LoadAsync(buyer, number, sellerOrderNumber, ct);
        if (err is not null) return err;
        var open = await db.OrderCancelRequests.AsNoTracking().FirstOrDefaultAsync(r => r.GroupId == g!.Id && r.OpenKey != null, ct);
        if (open is not null) return Accepted(open);   // idempotent: a second click (or a parallel one) is the same request
        if (!Cancellable.Contains(state)) return ActionResult.Conflict("not_cancellable", "This order can't be cancelled any more.");

        var now = time.GetUtcNow().ToUniversalTime();
        var req = new OrderCancelRequest
        {
            Id = Guid.NewGuid(), RequestId = "CR-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(), OrderId = o!.Id, GroupId = g!.Id, AccountId = buyer.AccountId,
            Reason = reason, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), Status = CancelRequestStatus.Pending, OpenKey = g.Id.ToString("N"), CreatedAt = now, UpdatedAt = now,
        };
        db.OrderCancelRequests.Add(req);
        outbox.Enqueue(CancelType, new IdPayload(req.RequestId), "cancel-request:" + req.RequestId);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // lost the race on the one-open-request key: answer with the winner's request
            var winner = await db.OrderCancelRequests.AsNoTracking().FirstOrDefaultAsync(r => r.GroupId == g.Id && r.OpenKey != null, ct);
            if (winner is null) throw;
            return Accepted(winner);
        }
        return Accepted(req);
    }

    private static ActionResult Accepted(OrderCancelRequest r) => new(202, new { requestId = r.RequestId, status = r.Status });

    public async Task<ActionResult> RequestReturnAsync(BuyerPrincipal buyer, string number, string? sellerOrderNumber, string? reason, string? comment,
        IReadOnlyList<ReturnLineInput>? input, string? clientKey, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (reason is null || !ReturnReasons.Contains(reason)) errors["reason"] = "Choose a reason.";
        if (comment is { Length: > 1000 }) errors["comment"] = "Keep the comment under 1000 characters.";
        if (input is null or { Count: 0 } or { Count: > 50 }) errors["lines"] = "Choose the items to return.";
        else if (input.Any(l => l.Quantity < 1 || string.IsNullOrWhiteSpace(l.MarketplaceLineId)) || input.Select(l => l.MarketplaceLineId).Distinct().Count() != input.Count) errors["lines"] = "Each item once, with a quantity of at least 1.";
        if (clientKey is { Length: > 64 }) errors["idempotencyKey"] = "At most 64 characters.";
        if (errors.Count > 0) return new ActionResult(422, Errors: errors);

        if (clientKey is not null && await db.OrderReturns.AsNoTracking().FirstOrDefaultAsync(r => r.AccountId == buyer.AccountId && r.ClientKey == clientKey, ct) is { } seen)
            return new ActionResult(202, new { returnId = seen.ReturnId, status = seen.Status });
        var (o, g, state, err) = await LoadAsync(buyer, number, sellerOrderNumber, ct);
        if (err is not null) return err;
        if (!Returnable.Contains(state)) return ActionResult.Conflict("not_returnable", "Items can be returned once they have shipped.");
        var lines = o!.Lines.Where(l => l.GroupId == g!.Id).ToList();
        var chosen = new List<(OrderLine Line, int Qty)>();
        foreach (var l in input!)
        {
            var line = lines.FirstOrDefault(x => x.MarketplaceLineId == l.MarketplaceLineId);
            if (line is null) return ActionResult.Invalid("lines", "That item is not on this seller order.");   // another order's line id never matches
            chosen.Add((line, l.Quantity));
        }

        var now = time.GetUtcNow().ToUniversalTime();
        var ret = new OrderReturn
        {
            Id = Guid.NewGuid(), ReturnId = "RT-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(), OrderId = o.Id, GroupId = g!.Id, AccountId = buyer.AccountId,
            Reason = reason!, Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), Status = ReturnStatus.Pending, ClientKey = clientKey, CreatedAt = now, UpdatedAt = now,
            Lines = chosen.Select(c => new OrderReturnLine { LineId = c.Line.Id, MarketplaceLineId = c.Line.MarketplaceLineId, Quantity = c.Qty, Reserved = true }).ToList(),
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The cap (shipped - returned - refunded, minus what other open returns hold) is enforced HERE, in one conditional UPDATE per line: parallel requests cannot both take the last unit.
        foreach (var (line, qty) in chosen)
        {
            var id = line.Id; var q = qty;
            var taken = await db.OrderLineFulfilments.Where(f => f.LineId == id && f.ShippedQty - f.ReturnedQty - f.RefundedQty - f.ReturnPendingQty >= q)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ReturnPendingQty, f => f.ReturnPendingQty + q), ct);
            if (taken == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                var f = await db.OrderLineFulfilments.AsNoTracking().FirstOrDefaultAsync(x => x.LineId == id, ct);
                var left = f is null ? 0 : Math.Max(0, f.ShippedQty - f.ReturnedQty - f.RefundedQty - f.ReturnPendingQty);
                return new ActionResult(422, Code: "quantity_exceeded", Message: $"You can return at most {left} of \"{line.Title}\".", Errors: new Dictionary<string, string> { ["lines"] = $"At most {left} of \"{line.Title}\" can be returned." });
            }
        }
        db.OrderReturns.Add(ret);
        outbox.Enqueue(ReturnType, new IdPayload(ret.ReturnId), "return-request:" + ret.ReturnId);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(CancellationToken.None);   // gives the reserved units back
            db.ChangeTracker.Clear();
            if (clientKey is not null && await db.OrderReturns.AsNoTracking().FirstOrDefaultAsync(r => r.AccountId == buyer.AccountId && r.ClientKey == clientKey, ct) is { } winner)
                return new ActionResult(202, new { returnId = winner.ReturnId, status = winner.Status });
            throw;
        }
        return new ActionResult(202, new { returnId = ret.ReturnId, status = ret.Status });
    }

    /// <summary>
    /// "I received it". The time is OUR clock (the portal stamps its own as well; the body has no <c>deliveredAt</c> and a client value is never read). The claim is one conditional UPDATE,
    /// so any number of parallel clicks queue exactly one portal call.
    /// </summary>
    public async Task<ActionResult> ConfirmDeliveryAsync(BuyerPrincipal buyer, string number, string? sellerOrderNumber, CancellationToken ct)
    {
        var (o, g, state, err) = await LoadAsync(buyer, number, sellerOrderNumber, ct);
        if (err is not null) return err;
        var now = time.GetUtcNow().ToUniversalTime();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.SellerOrderStates.Where(s => s.GroupId == g!.Id && s.BuyerConfirmedAt == null && s.Status == FulfilmentStatus.Shipped)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BuyerConfirmedAt, (DateTimeOffset?)now).SetProperty(x => x.UpdatedAt, now), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(CancellationToken.None);
            var s = await db.SellerOrderStates.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == g!.Id, ct);
            if (s is { BuyerConfirmedAt: not null } || s?.Status == FulfilmentStatus.Delivered) return new ActionResult(200, new { status = "confirmed" });   // idempotent
            return ActionResult.Conflict("not_shipped", "This seller order has not shipped yet.");
        }
        outbox.Enqueue(DeliveryType, new IdPayload(g!.Id.ToString("N")), "delivery-confirmation:" + g.Id.ToString("N") + ":" + Guid.NewGuid().ToString("N")[..12]);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ActionResult(202, new { status = "confirming" });
    }

    /// <summary>A fresh label link for an approved return (the first one expires after about 24 h). A short synchronous call, inside the portal client's 30 s deadline.</summary>
    public async Task<ActionResult> RefreshLabelAsync(BuyerPrincipal buyer, string number, string returnId, CancellationToken ct)
    {
        var ret = await (from r in db.OrderReturns join o in db.Orders on r.OrderId equals o.Id
                         where r.ReturnId == returnId && o.Number == number && o.AccountId == buyer.AccountId && r.AccountId == buyer.AccountId select r).FirstOrDefaultAsync(ct);
        if (ret is null) return ActionResult.NotFound();
        if (ret.Status != ReturnStatus.Approved) return ActionResult.Conflict("no_label", "A label is available once the seller approves the return.");
        PortalResult<ReturnLabelLink> res;
        try { res = await portal.GetReturnLabelLinkAsync(ret.ReturnId, ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return new ActionResult(503, Code: "portal_unavailable", Message: "Try again in a moment."); }
        if (res.Status == 404) return ActionResult.Conflict("no_label", "There is no label for this return yet.");
        var url = FulfilmentProjector.SafeUrl(res.Value?.LabelUrl);
        if (!res.IsSuccess || url is null || res.Value?.LabelUrlExpiresAt is not { } exp) return new ActionResult(503, Code: "portal_unavailable", Message: "Try again in a moment.");
        ret.LabelUrl = url; ret.LabelUrlExpiresAt = exp.ToUniversalTime();
        ret.LabelCarrier = FulfilmentProjector.Cap(res.Value.Carrier, 100) ?? ret.LabelCarrier;
        ret.LabelTrackingNumber = FulfilmentProjector.Cap(res.Value.TrackingNumber, 100) ?? ret.LabelTrackingNumber;
        ret.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new ActionResult(200, new { labelUrl = ret.LabelUrl, labelUrlExpiresAt = ret.LabelUrlExpiresAt });
    }
}

// ---------------------------------------------------------------- outbox handlers (the portal calls)

/// <summary>Reads the per-seller-order answer of a buyer event: <c>{ orders: [{ number, result, message? }] }</c> (ORDERS.md §3.3).</summary>
internal static class BuyerEventAnswer
{
    public static (string Result, string? Message) For(JsonElement? body, string? sellerOrderNumber)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty("orders", out var os) || os.ValueKind != JsonValueKind.Array) return ("", null);
        foreach (var o in os.EnumerateArray())
            if (sellerOrderNumber is null || FulfilmentProjector.Str(o, "number") == sellerOrderNumber)
                return (FulfilmentProjector.Str(o, "result") ?? "", FulfilmentProjector.Cap(FulfilmentProjector.Str(o, "message"), 500));
        return ("", null);
    }

    public static string? IdOf(OutboxMessage m)
    {
        try { return m.Payload is null ? null : JsonSerializer.Deserialize<BuyerOrderActions.IdPayload>(m.Payload, PortalJson.Options)?.Id; }
        catch (JsonException) { return null; }
    }
}

public sealed class CancelRequestOutboxHandler(MarketplaceDb db, ISignedPortalClient portal, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [BuyerOrderActions.CancelType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (BuyerEventAnswer.IdOf(message) is not { } rid) return OutboxResult.Dead("invalid_payload");
        var req = await db.OrderCancelRequests.FirstOrDefaultAsync(r => r.RequestId == rid, ct);
        if (req is null) return OutboxResult.Dead("request_missing");
        if (req.Status != CancelRequestStatus.Pending) return OutboxResult.Delivered();   // already sent
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstAsync(o => o.Id == req.OrderId, ct);
        var group = order.Groups.First(g => g.Id == req.GroupId);
        PortalResult<JsonElement> res;
        try { res = await portal.RequestCancellationAsync(order.Number, new CancelRequestBody(req.RequestId, req.Reason, req.Note, group.SellerOrderNumber), ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
        var now = time.GetUtcNow().ToUniversalTime();
        if (!res.IsSuccess)
        {
            var outcome = PortalOutcome.From(res);
            if (outcome.Outcome == OutboxOutcome.Dead) { req.Status = CancelRequestStatus.Failed; req.OpenKey = null; req.UpdatedAt = now; await db.SaveChangesAsync(ct); }
            return outcome;
        }
        var (result, note) = BuyerEventAnswer.For(res.Value, group.SellerOrderNumber);
        switch (result)
        {
            case "requested" or "duplicate": req.Status = CancelRequestStatus.Requested; break;
            case "already_cancelled": req.Status = CancelRequestStatus.AlreadyCancelled; req.OpenKey = null; break;
            case "auto_rejected": req.Status = CancelRequestStatus.Rejected; req.OpenKey = null; req.DecisionNote = note ?? "This order has already shipped."; break;
            default: return OutboxResult.Retry("unexpected_answer");   // "retry" (not stored) or something we don't know: the same requestId is safe to send again
        }
        req.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return OutboxResult.Delivered();
    }
}

public sealed class ReturnRequestOutboxHandler(MarketplaceDb db, ISignedPortalClient portal, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [BuyerOrderActions.ReturnType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (BuyerEventAnswer.IdOf(message) is not { } rid) return OutboxResult.Dead("invalid_payload");
        var ret = await db.OrderReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.ReturnId == rid, ct);
        if (ret is null) return OutboxResult.Dead("return_missing");
        if (ret.Status != ReturnStatus.Pending) return OutboxResult.Delivered();
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstAsync(o => o.Id == ret.OrderId, ct);
        var group = order.Groups.First(g => g.Id == ret.GroupId);
        var body = new ReturnRequestBody(ret.ReturnId, ret.Reason, ret.Comment, group.SellerOrderNumber, ret.Lines.Select(l => new ReturnLineRequest(l.MarketplaceLineId, l.Quantity)).ToList());
        PortalResult<JsonElement> res;
        try { res = await portal.RequestReturnAsync(order.Number, body, ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
        var now = time.GetUtcNow().ToUniversalTime();
        if (!res.IsSuccess)
        {
            var outcome = PortalOutcome.From(res);
            if (outcome.Outcome == OutboxOutcome.Dead) { ret.Status = ReturnStatus.Failed; ret.UpdatedAt = now; await FulfilmentLines.ReleaseAsync(db, ret, received: false, ct); await db.SaveChangesAsync(ct); }
            return outcome;
        }
        var (result, note) = BuyerEventAnswer.For(res.Value, group.SellerOrderNumber);
        switch (result)
        {
            case "requested" or "duplicate": ret.Status = ReturnStatus.Requested; break;
            case "rejected":
                ret.Status = ReturnStatus.Rejected; ret.DecisionNote = note;
                await FulfilmentLines.ReleaseAsync(db, ret, received: false, ct);   // the units are free for another request
                break;
            default: return OutboxResult.Retry("unexpected_answer");
        }
        ret.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return OutboxResult.Delivered();
    }
}

public sealed class DeliveryConfirmationOutboxHandler(MarketplaceDb db, ISignedPortalClient portal, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [BuyerOrderActions.DeliveryType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (BuyerEventAnswer.IdOf(message) is not { } gid || !Guid.TryParseExact(gid, "N", out var groupId)) return OutboxResult.Dead("invalid_payload");
        var group = await db.OrderGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return OutboxResult.Dead("group_missing");
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == group.OrderId, ct);
        PortalResult<JsonElement> res;
        // No deliveredAt: the portal stamps its own server time (DeliveryConfirmationBody has no such field).
        try { res = await portal.ConfirmDeliveryAsync(order.Number, new DeliveryConfirmationBody(group.SellerOrderNumber), ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
        var rejected = !res.IsSuccess ? PortalOutcome.From(res).Outcome == OutboxOutcome.Dead : BuyerEventAnswer.For(res.Value, group.SellerOrderNumber).Result == "rejected";
        if (rejected)
        {
            // the portal refused (for example a return is open): let the buyer see "not confirmed" and try again later
            await db.SellerOrderStates.Where(s => s.GroupId == groupId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BuyerConfirmedAt, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, time.GetUtcNow()), ct);
            return OutboxResult.Delivered();
        }
        return res.IsSuccess ? OutboxResult.Delivered() : PortalOutcome.From(res);
    }
}
