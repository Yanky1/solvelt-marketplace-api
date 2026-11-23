using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Staff;

public static class StaffOpsEndpoints
{
    public static IEndpointRouteBuilder MapStaffOps(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup();

        g.MapPost("/orders/lookup", FindOrder).StaffCan(StaffPermissions.OrdersView);
        g.MapPost("/buyers/{id:guid}/orders", BuyerOrders).StaffCan(StaffPermissions.OrdersView);
        g.MapPost("/orders/{id:guid}/refunds/{refundId:guid}/retry", RetryRefund).StaffCan(StaffPermissions.RefundsRetry);
        g.MapPost("/orders/{id:guid}/refunds/{refundId:guid}/sola-resolve", ResolveSolaRefund).StaffCan(StaffPermissions.RefundsRetry);
        g.MapPost("/orders/{id:guid}/sola-chargebacks", RecordSolaChargeback).StaffCan(StaffPermissions.RefundsRetry);
        g.MapGet("/ingests", (StaffOpsService s, CancellationToken ct) => s.StuckIngestsAsync(ct).ContinueWith(t => Results.Json(t.Result), ct)).StaffCan(StaffPermissions.IngestsView);
        g.MapPost("/ingests/{orderId:guid}/retry", RetryIngest).StaffCan(StaffPermissions.IngestsRetry);
        g.MapGet("/outbox", (StaffOpsService s, string? status, string? type, int? limit, string? before, CancellationToken ct) => s.OutboxAsync(status, type, limit, before, ct)).StaffCan(StaffPermissions.OutboxView);
        g.MapPost("/outbox/{id:guid}/retry", RetryOutbox).StaffCan(StaffPermissions.OutboxRetry);
        g.MapGet("/dead-letters", (StaffOpsService s, int? limit, CancellationToken ct) => s.DeadLettersAsync(limit, ct).ContinueWith(t => Results.Json(t.Result), ct)).StaffCan(StaffPermissions.DeadLettersView);
        g.MapPost("/dead-letters/retry", RetryDeadLetter).StaffCan(StaffPermissions.DeadLettersRetry);
        g.MapGet("/stripe/failures", (StaffOpsService s, string? before, int? limit, CancellationToken ct) => s.StripeFailuresAsync(before, limit, ct)).StaffCan(StaffPermissions.StripeFailuresView);
        return app;
    }

    public sealed record RetryDeadLetterBody(string? EventId, string? Reason);

    public sealed record OrderLookupBody(string? Number, string? Reason);

    /// <summary><c>POST /orders/lookup { number, reason }</c>: one order, masked; the Stripe block only with <c>payments.view</c>. A body, not a query string (see the buyer lookup); a lookup is an action: reason required, audited by order id, capped per hour.</summary>
    private static async Task<IResult> FindOrder(OrderLookupBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(body!.Number)) return Problems.BadRequest("number_required", "Give the order number.");
        var r = await ops.LookupOrderAsync(ctx.Staff(), body.Number, StaffReason.Clean(body.Reason), ctx.Staff().Can(StaffPermissions.PaymentsView), ct);
        return r.Limited ? Problems.Json(429, "lookup_limit", "Too many lookups this hour.") : r.View is null ? Problems.NotFound() : Results.Json(r.View);
    }

    private static async Task<IResult> BuyerOrders(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var (limited, orders) = await ops.BuyerOrdersAsync(ctx.Staff(), id, StaffReason.Clean(body!.Reason), ct);
        return limited ? Problems.Json(429, "lookup_limit", "Too many lookups this hour.") : Results.Json(orders);
    }

    private static IResult Retried((OpsOutcome Outcome, string? State) r) => r.Outcome switch
    {
        OpsOutcome.Ok => Results.NoContent(),
        OpsOutcome.NotFound => Problems.NotFound(),
        OpsOutcome.AlreadyQueued => Problems.Json(409, "already_queued", "It is already queued and will be tried again by itself."),
        _ => Problems.Json(409, "not_retryable", $"Nothing to retry here (state: {r.State ?? "unknown"})."),
    };

    private static async Task<IResult> RetryRefund(Guid id, Guid refundId, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return Retried(await ops.RetryRefundAsync(ctx.Staff(), id, refundId, StaffReason.Clean(body!.Reason), ct));
    }

    public sealed record SolaResolveBody(string? Outcome, string? RefNum, string? Reason);

    /// <summary><c>POST .../sola-resolve { outcome: "refunded" | "not_refunded", refNum, reason }</c>: a person checked at Sola whether a refund whose answer was lost went through.</summary>
    private static async Task<IResult> ResolveSolaRefund(Guid id, Guid refundId, SolaResolveBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var refunded = body!.Outcome == "refunded";
        if (!refunded && body.Outcome != "not_refunded") return Problems.BadRequest("outcome_invalid", "outcome must be refunded or not_refunded.");
        long? refNum = null;
        if (refunded)
        {
            if (!long.TryParse(body.RefNum, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return Problems.BadRequest("ref_invalid", "Give the refund's xRefNum from Sola (digits only).");
            refNum = n;
        }
        return Retried(await ops.ResolveSolaRefundAsync(ctx.Staff(), id, refundId, refunded, refNum, StaffReason.Clean(body.Reason), ct));
    }

    public sealed record SolaChargebackBody(string? Reference, long? AmountCents, long? FeeCents, string? Status, string? SolaReason, DateTimeOffset? EvidenceDueBy, string? Reason);

    /// <summary><c>POST /orders/{id}/sola-chargebacks</c>: records a Sola chargeback and tells the portal (<c>provider: "sola"</c>, fee always sent). Staff only, a reason is required and audited.</summary>
    private static async Task<IResult> RecordSolaChargeback(Guid id, SolaChargebackBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var reference = body!.Reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > 40 || !reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return Problems.BadRequest("reference_invalid", "reference: the chargeback's reference at Sola, at most 40 letters, digits, - or _.");
        if (body.AmountCents is not > 0) return Problems.BadRequest("amount_invalid", "amountCents must be above zero.");
        if (body.FeeCents is not >= 0) return Problems.BadRequest("fee_required", "feeCents is required (0 or more): the portal charges its own default when it is missing.");
        if (body.Status is null || !SolaMoneyEvents.DisputeStatuses.Contains(body.Status)) return Problems.BadRequest("status_invalid", "status must be one of: " + string.Join(", ", SolaMoneyEvents.DisputeStatuses) + ".");
        var sr = body.SolaReason is { } r && r.Length > 100 ? r[..100] : body.SolaReason;
        var (outcome, detail) = await ops.RecordSolaChargebackAsync(ctx.Staff(), id, new(reference, body.AmountCents.Value, body.FeeCents.Value, body.Status, sr, body.EvidenceDueBy), StaffReason.Clean(body.Reason), ct);
        return outcome switch
        {
            OpsOutcome.Ok => Results.NoContent(),
            OpsOutcome.NotFound => Problems.NotFound(),
            OpsOutcome.AlreadyQueued => Problems.Json(409, "already_recorded", "This status of this chargeback was already recorded."),
            _ => Problems.Json(409, detail ?? "not_recordable", "Not recordable: " + (detail ?? "unknown") + "."),
        };
    }

    private static async Task<IResult> RetryIngest(Guid orderId, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return Retried(await ops.RetryIngestAsync(ctx.Staff(), orderId, StaffReason.Clean(body!.Reason), ct));
    }

    private static async Task<IResult> RetryOutbox(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return Retried((await ops.RetryOutboxAsync(ctx.Staff(), id, StaffReason.Clean(body!.Reason), ct), null));
    }

    private static async Task<IResult> RetryDeadLetter(RetryDeadLetterBody? body, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(body!.EventId) || body.EventId.Length > 100) return Problems.NotFound();
        var (outcome, replayed) = await ops.RetryDeadLetterAsync(ctx.Staff(), body.EventId, StaffReason.Clean(body.Reason), ct);
        return outcome == OpsOutcome.Ok ? Results.Json(new { replayed }) : Retried((outcome, "not parked"));
    }
}
