using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Staff;

public static class StaffOpsEndpoints
{
    public static IEndpointRouteBuilder MapStaffOps(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup();

        g.MapGet("/orders", FindOrder).StaffCan(StaffPermissions.OrdersView);
        g.MapGet("/buyers/{id:guid}/orders", BuyerOrders).StaffCan(StaffPermissions.OrdersView);
        g.MapPost("/orders/{id:guid}/refunds/{refundId:guid}/retry", RetryRefund).StaffCan(StaffPermissions.RefundsRetry);
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

    /// <summary><c>GET /orders?number=…|id=…&amp;reason=…</c>: one order, masked; the Stripe block only with <c>payments.view</c>. A lookup is an action: reason required, audited by order id.</summary>
    private static async Task<IResult> FindOrder(HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, string? number, Guid? id, string? reason, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, reason, stepUp: false) is { } refused) return refused;
        if ((number is null) == (id is null)) return Problems.BadRequest("one_key", "Give exactly one of number or id.");
        var found = await ops.FindOrderAsync(id, number, ct);
        await ops.AuditAsync(ctx.Staff(), found is null ? "order.lookup_miss" : "order.lookup", found is null ? null : "order", found?.ToString("D"), StaffReason.Clean(reason), ct);
        return found is { } f && await ops.OrderViewAsync(f, ctx.Staff().Can(StaffPermissions.PaymentsView), ct) is { } view ? Results.Json(view) : Problems.NotFound();
    }

    private static async Task<IResult> BuyerOrders(Guid id, HttpContext ctx, StaffOpsService ops, TimeProvider time, IOptions<StaffOptions> o, string? reason, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, reason, stepUp: false) is { } refused) return refused;
        await ops.AuditAsync(ctx.Staff(), "buyer.orders", "buyer", id.ToString("D"), StaffReason.Clean(reason), ct);
        return Results.Json(await ops.BuyerOrdersAsync(id, ct));
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
