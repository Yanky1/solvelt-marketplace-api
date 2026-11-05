using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

public enum ActionStatus { Ok, NotFound, InvalidState, Invalid, Refused, Unavailable }
public sealed record ActionResult(ActionStatus Status, BookingView? Booking = null, int HttpStatus = 0, string? Code = null, IReadOnlyDictionary<string, string>? Errors = null);
/// <summary>What cancelling now would cost. <see cref="FeeKnown"/> is false until the portal tells us the policy: we never guess a fee. The portal's own answer is final.</summary>
public sealed record CancelPreview(bool FeeKnown, long? FeeCents, long? RefundCents, long TotalCents, bool WithinFreeWindow, DateTimeOffset? FreeUntil, string Currency);

/// <summary>
/// What the buyer can do to their own booking (cancel, ask to move it, answer a proposed time). Every action is one portal call that is idempotent on our booking id and a request id
/// that WE derive (never a client value), made while the buyer waits (30 s deadline, O-08); money follows from the portal's own events (decision M4-09). The booking is always looked
/// up by (our id, account): another buyer's booking is a 404.
/// </summary>
public sealed class BookingActions(MarketplaceDb db, ISignedPortalClient portal, BookingService bookings, TimeProvider time)
{
    private static readonly string[] Cancellable = [BookingStatus.Hold, BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled];

    private Task<Booking?> Own(Guid accountId, Guid id, CancellationToken ct)
        => db.Bookings.FirstOrDefaultAsync(b => b.Id == id && b.AccountId == accountId && b.Status != BookingStatus.Failed && b.Status != BookingStatus.HoldPending, ct);

    /// <summary>Fee preview: only for a Confirmed/Rescheduled booking (a hold or a pending request is free), only when the portal told us the policy.</summary>
    public async Task<CancelPreview?> PreviewAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        var b = await Own(accountId, id, ct);
        if (b is null) return null;
        var now = time.GetUtcNow();
        if (b.Status is BookingStatus.Hold or BookingStatus.Requested) return new CancelPreview(true, 0, b.TotalCents, b.TotalCents, true, null, b.Currency);
        if (b.PolicyCancelFreeHours is not { } free || b.PolicyCancelFeePercent is not { } pct || b.StartAt is not { } start) return new CancelPreview(false, null, null, b.TotalCents, false, null, b.Currency);
        var until = start.AddHours(-free);
        var inWindow = now <= until;
        // The portal's rule: round-half-up(total x percent / 100), only less than cancelFreeHours before the start. Seller-rescheduled bookings are free for the buyer.
        var fee = inWindow || b.RequiresBuyerConsent ? 0 : (long)Math.Round(b.TotalCents * (decimal)Math.Clamp(pct, 0, 100) / 100m, 0, MidpointRounding.AwayFromZero);
        return new CancelPreview(true, fee, b.TotalCents - fee, b.TotalCents, inWindow, until, b.Currency);
    }

    public async Task<ActionResult> CancelAsync(BuyerPrincipal buyer, Guid id, string? reason, CancellationToken ct)
    {
        if (reason is { Length: > 500 }) return Invalid("reason", "Use at most 500 characters.");
        var b = await Own(buyer.AccountId, id, ct);
        if (b is null) return new ActionResult(ActionStatus.NotFound);
        if (b.Status == BookingStatus.CancelledByBuyer) return new ActionResult(ActionStatus.Ok, bookings.ToView(b, time.GetUtcNow()));   // a repeated tap is the same cancellation
        if (!Cancellable.Contains(b.Status) || b.PortalBookingId is null) return new ActionResult(ActionStatus.InvalidState, HttpStatus: 409, Code: "invalid_state");
        return await Call(b, () => portal.CancelBookingAsync(b.MarketplaceBookingId, new BookingCancelBody("cancel-" + b.MarketplaceBookingId, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), ct), ct);
    }

    public async Task<ActionResult> RescheduleAsync(BuyerPrincipal buyer, Guid id, DateTimeOffset? newStart, string requestKey, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (newStart is not { } ns || ns <= now || ns > now.AddDays(400)) return Invalid("newStart", "Choose one of the available times.");
        var b = await Own(buyer.AccountId, id, ct);
        if (b is null) return new ActionResult(ActionStatus.NotFound);
        if (b.Status is not (BookingStatus.Confirmed or BookingStatus.Rescheduled) || b.PortalBookingId is null) return new ActionResult(ActionStatus.InvalidState, HttpStatus: 409, Code: "invalid_state");
        // The request id is derived from OUR booking id and the buyer's Idempotency-Key: a retry is the same request, and a different key is a different one.
        return await Call(b, () => portal.RescheduleBookingAsync(b.MarketplaceBookingId, new BookingRescheduleBody($"resched-{b.MarketplaceBookingId}-{requestKey}", ns.ToUniversalTime()), ct), ct);
    }

    public async Task<ActionResult> RespondToProposalAsync(BuyerPrincipal buyer, Guid id, bool? accept, CancellationToken ct)
    {
        if (accept is not { } a) return Invalid("accept", "Say whether you accept the proposed time.");
        var b = await Own(buyer.AccountId, id, ct);
        if (b is null) return new ActionResult(ActionStatus.NotFound);
        if (b.Status != BookingStatus.Requested || b.ProposedStartAt is null || b.PortalBookingId is null) return new ActionResult(ActionStatus.InvalidState, HttpStatus: 409, Code: "no_proposal");
        return await Call(b, () => portal.RespondToProposalAsync(b.MarketplaceBookingId, new BookingProposalBody(a), ct), ct);
    }

    /// <summary>The buyer saw the provider's new time (a seller reschedule): clears the flag. Cancelling instead is the other choice and is free.</summary>
    public async Task<ActionResult> AcknowledgeRescheduleAsync(BuyerPrincipal buyer, Guid id, CancellationToken ct)
    {
        var b = await Own(buyer.AccountId, id, ct);
        if (b is null) return new ActionResult(ActionStatus.NotFound);
        if (!b.RequiresBuyerConsent) return new ActionResult(ActionStatus.InvalidState, HttpStatus: 409, Code: "nothing_to_accept");
        await db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiresBuyerConsent, false).SetProperty(x => x.UpdatedAt, time.GetUtcNow()).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return new ActionResult(ActionStatus.Ok, await bookings.GetAsync(buyer.AccountId, id, ct));
    }

    private static ActionResult Invalid(string field, string message) => new(ActionStatus.Invalid, Errors: new Dictionary<string, string> { [field] = message });

    private async Task<ActionResult> Call(Booking b, Func<Task<PortalResult<PortalBookingSummary>>> send, CancellationToken ct)
    {
        PortalResult<PortalBookingSummary> r;
        try { r = await send(); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException)
        { return new ActionResult(ActionStatus.Unavailable); }
        if (r.IsSuccess && r.Value is { } s && string.Equals(s.MarketplaceBookingId, b.MarketplaceBookingId, StringComparison.Ordinal))
        {
            await bookings.ApplySummaryAsync(b, s, time.GetUtcNow().ToUniversalTime(), ct);
            var fresh = await db.Bookings.AsNoTracking().FirstAsync(x => x.Id == b.Id, ct);
            return new ActionResult(ActionStatus.Ok, bookings.ToView(fresh, time.GetUtcNow()));
        }
        if (r.Retryable || r.Status is 408 or 429 or >= 500 or 401 or 413) return new ActionResult(ActionStatus.Unavailable);
        var code = new string((r.Error?.Code ?? "refused").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(40).ToArray());
        return r.Status switch
        {
            404 => new ActionResult(ActionStatus.NotFound),
            409 => new ActionResult(ActionStatus.Refused, HttpStatus: 409, Code: code.Length == 0 ? "conflict" : code),
            _ => new ActionResult(ActionStatus.Refused, HttpStatus: 422, Code: code.Length == 0 ? "refused" : code),
        };
    }
}

public sealed record CancelBody(string? Reason);
public sealed record RescheduleBody(DateTimeOffset? NewStart);
public sealed record ProposalBody(bool? Accept);

public static class BookingActionEndpoints
{
    public static RouteGroupBuilder MapBookingActions(this RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/cancel-preview", async (Guid id, HttpContext ctx, BookingActions a, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "private, no-store";
            return await a.PreviewAsync(ctx.Buyer().AccountId, id, ct) is { } p ? Results.Json(p) : Problems.NotFound();
        });
        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelBody? body, HttpContext ctx, BookingActions a, CancellationToken ct) => Reply(ctx, await a.CancelAsync(ctx.Buyer(), id, body?.Reason, ct))).AddEndpointFilter<Demo.DemoBookingGuard>();
        g.MapPost("/{id:guid}/reschedule", async (Guid id, RescheduleBody? body, HttpContext ctx, BookingActions a, CancellationToken ct) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (key.Length is < 8 or > 60 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return Problems.Validation(new Dictionary<string, string> { ["Idempotency-Key"] = "Send an Idempotency-Key header (8 to 60 letters, digits, - _)." });
            return Reply(ctx, await a.RescheduleAsync(ctx.Buyer(), id, body?.NewStart, key, ct));
        }).AddEndpointFilter<Demo.DemoBookingGuard>();
        g.MapPost("/{id:guid}/proposal", async (Guid id, ProposalBody? body, HttpContext ctx, BookingActions a, CancellationToken ct) => Reply(ctx, await a.RespondToProposalAsync(ctx.Buyer(), id, body?.Accept, ct))).AddEndpointFilter<Demo.DemoBookingGuard>();
        g.MapPost("/{id:guid}/acknowledge-reschedule", async (Guid id, HttpContext ctx, BookingActions a, CancellationToken ct) => Reply(ctx, await a.AcknowledgeRescheduleAsync(ctx.Buyer(), id, ct))).AddEndpointFilter<Demo.DemoBookingGuard>();
        return g;
    }

    private static IResult Reply(HttpContext ctx, ActionResult r)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        return r.Status switch
        {
            ActionStatus.Ok => Results.Json(new { booking = r.Booking }),
            ActionStatus.NotFound => Problems.NotFound(),
            ActionStatus.Invalid => Problems.Validation(r.Errors!),
            ActionStatus.InvalidState or ActionStatus.Refused => Problems.Json(r.HttpStatus, r.Code!, Message(r.Code)),
            _ => Problems.Json(503, "booking_unavailable", "We couldn't reach the provider's calendar right now. Please try again in a moment."),
        };
    }

    private static string Message(string? code) => code switch
    {
        "invalid_state" => "That isn't possible for this booking in its current state.",
        "no_proposal" => "There is no proposed time to answer.",
        "nothing_to_accept" => "There is nothing to accept.",
        "slot_unavailable" => "That time is no longer available. Please choose another.",
        "reschedule_limit" => "This booking can't be moved again.",
        "too_late" => "It's too late to move this booking.",
        "request_expired" => "This request has expired.",
        "version_conflict" => "The booking just changed. Please reload and try again.",
        _ => "That couldn't be done. Please check your choice and try again.",
    };
}
