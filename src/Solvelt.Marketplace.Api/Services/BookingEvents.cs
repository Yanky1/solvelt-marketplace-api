using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// Projects the portal's <c>booking.*</c> events onto our booking row (M4; decision M4-03). ONE handler for these types (registry rule). Matching is on OUR
/// marketplace booking id AND the portal booking id (<see cref="EventRefs.Matches(BookingRef?, string, Guid)"/>), never on a number or a time. Every write goes through
/// <see cref="ProjectionContext.AdvanceAsync"/>: ordered by the portal's time and the event id, whatever the arrival order; terminal states (Declined, Expired, Cancelled*, Completed,
/// NoShow) are absorbing (O-10). Facts that do not change the status (a proposed time, a reschedule request, an extra-charge request, a provider change) have their own field
/// group, so a late one is not lost behind a newer status event, yet none of them can reopen a finished booking.
/// What this handler does NOT do: money (refunds and fees are decided by the payment slice from the values stored here) and messages (<c>message.sent</c> is M5's).
/// </summary>
public sealed class BookingEventHandler(ILogger<BookingEventHandler> log) : IProjectionHandler
{
    public const string Requested = "booking.requested", Confirmed = "booking.confirmed", Declined = "booking.declined", TimeProposed = "booking.time_proposed",
        Rescheduled = "booking.rescheduled", RescheduleRequested = "booking.reschedule_requested", RescheduleDecided = "booking.reschedule_decided", Cancelled = "booking.cancelled",
        Started = "booking.started", Completed = "booking.completed", ExtraCharge = "booking.additional_charge_requested", NoShow = "booking.no_show",
        ProviderChanged = "booking.provider_changed", HoldExpired = "booking.hold_expired", Reminder = "booking.reminder";

    public IReadOnlyCollection<string> EventTypes { get; } =
        [Requested, Confirmed, Declined, TimeProposed, Rescheduled, RescheduleRequested, RescheduleDecided, Cancelled, Started, Completed, ExtraCharge, NoShow, ProviderChanged, HoldExpired, Reminder];

    public const string Aggregate = "booking";

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var data = c.Event.Data;
        var bref = EventRefs.Booking(data);
        if (bref is null) return HandlerOutcome.Skipped("booking reference missing");
        var b = await c.Db.Bookings.FirstOrDefaultAsync(x => x.MarketplaceBookingId == bref.MarketplaceBookingId, ct);
        if (b is null) return HandlerOutcome.Skipped("unknown marketplace booking");
        // Our row exists from before the hold call; its portal id arrives with the portal's answer. An event that beats the answer waits (replayed when the id is stored).
        if (b.PortalBookingId is null) return HandlerOutcome.Parked(Aggregate, b.MarketplaceBookingId, "portal booking id not stored yet");
        if (!EventRefs.Matches(bref, b.MarketplaceBookingId, b.PortalBookingId.Value)) return HandlerOutcome.Skipped("portal booking id does not match");

        // A finished row stays finished whatever order the events arrive in AND whoever finished it: the buyer's cancel, the upkeep and the confirm handler set a terminal status from a portal
        // ANSWER, which the projection store never sees, so the handler checks the row itself. Only a repeat of the same end (the cancellation event that triggers the refund) and the extra-charge
        // request that follows completion are let through (decision M4-15).
        if (BookingStatus.IsTerminal(b.Status) && !(Target(c.Event.Type, data) == b.Status || (c.Event.Type == ExtraCharge && b.Status == BookingStatus.Completed)))
            return HandlerOutcome.Skipped("the booking is finished");

        var now = c.Time.GetUtcNow().ToUniversalTime();
        var key = b.MarketplaceBookingId;
        async Task<bool> Step(string group, string? status = null, string? terminal = null)
        {
            if (!await c.AdvanceAsync(Aggregate, key, group, terminal, ct)) return false;
            if (status is not null) { b.Status = status; if (!BookingStatuses.RevealsMeetingLink(status)) b.OnlineInstructions = null; }
            b.UpdatedAt = now; b.Version++;
            return true;
        }
        void Times()
        {
            if (Time(data, "startAt") is { } s) b.StartAt = s;
            if (Time(data, "endAt") is { } e) b.EndAt = e;
            if (Money(data, "totalCents") is { } t) b.TotalCents = t;
            if (b.StartAt is { } st && b.EndAt is { } en && en > st) b.DurationMinutes = (int)Math.Min(24 * 60, (en - st).TotalMinutes);
        }
        HandlerOutcome Done(bool ok) => ok ? HandlerOutcome.Applied() : HandlerOutcome.Skipped("older, or the booking is finished");

        switch (c.Event.Type)
        {
            case Requested:
                if (!await Step("status", BookingStatus.Requested)) return Done(false);
                Times(); b.ApprovalDeadline = Time(data, "approvalDeadline") ?? b.ApprovalDeadline; b.HoldExpiresAt = null;
                break;
            case Confirmed:
                if (!await Step("status", BookingStatus.Confirmed)) return Done(false);
                Times(); b.HoldExpiresAt = null; b.ApprovalDeadline = null; b.ProposedStartAt = null;
                // The meeting link is revealed to the CONFIRMED buyer only; it is kept for an online booking and never logged.
                b.OnlineInstructions = b.Location == "online" ? Cap(Str(data, "onlineInstructions"), 2000) : null;
                break;
            case Declined:
                if (!await Step("status", BookingStatus.Declined, "declined")) return Done(false);
                b.ProposedStartAt = null; b.HoldExpiresAt = null; b.ApprovalDeadline = null; ClearPending(b);
                await RefundAsync(c, b, long.MaxValue, "declined", ct);       // the buyer paid up front: a declined request returns everything that is left
                await BookingMoney.QueueCancelPendingIntentsAsync(c.Db, new OutboxWriter(c.Db, c.Time), b.Id, null, null, ct);
                break;
            case HoldExpired:
                if (!await Step("status", BookingStatus.Expired, "expired")) return Done(false);
                b.HoldExpiresAt = null; ClearPending(b);
                // An intent behind a lapsed hold must not stay payable (a payment that still lands is refunded, but not offering it is better).
                await BookingMoney.QueueCancelPendingIntentsAsync(c.Db, new OutboxWriter(c.Db, c.Time), b.Id, null, null, ct);
                break;
            case TimeProposed:
                if (!await Step("proposal")) return Done(false);
                b.ProposedStartAt = Time(data, "proposedStartAt");
                break;
            case Rescheduled:
                if (!await Step("status", BookingStatus.Rescheduled)) return Done(false);
                Times();
                b.RequiresBuyerConsent = Bool(data, "requiresBuyerConsent");
                if (string.Equals(Str(data, "initiatedBy"), "buyer", StringComparison.OrdinalIgnoreCase)) b.RescheduleCount++;
                b.RescheduleRequestedStartAt = null;
                break;
            case RescheduleRequested:
                if (!await Step("reschedule")) return Done(false);
                b.RescheduleRequestedStartAt = Time(data, "requestedStartAt");
                break;
            case RescheduleDecided:
                if (!await Step("reschedule")) return Done(false);
                b.RescheduleRequestedStartAt = null;
                break;
            case Cancelled:
            {
                var byBuyer = !string.Equals(Str(data, "cancelledBy"), "seller", StringComparison.OrdinalIgnoreCase);
                if (!await Step("status", byBuyer ? BookingStatus.CancelledByBuyer : BookingStatus.CancelledBySeller, byBuyer ? "cancelled_by_buyer" : "cancelled_by_seller")) return Done(false);
                // A seller cancellation never carries a fee (SERVICES.md §4). The fee and refund are the portal's numbers; the payment slice charges and refunds exactly these.
                b.CancellationFeeCents = byBuyer ? Money(data, "cancellationFeeCents") ?? 0 : 0;
                b.RefundDueCents = Math.Clamp(Money(data, "refundDueCents") ?? 0, 0, Math.Max(b.TotalCents, 0));
                b.HoldExpiresAt = null; ClearPending(b);
                // Money back: a seller cancellation returns everything; a buyer's returns what the portal says is due (total minus its policy fee). The fee stays inside the
                // original payment, so no second charge exists (decision M4-09).
                await RefundAsync(c, b, byBuyer ? Money(data, "refundDueCents") ?? Math.Max(0, b.TotalCents - b.CancellationFeeCents) : b.TotalCents, "cancelled", ct);
                await BookingMoney.QueueCancelPendingIntentsAsync(c.Db, new OutboxWriter(c.Db, c.Time), b.Id, null, null, ct);
                break;
            }
            case Started:
                if (!await Step("status", BookingStatus.InProgress)) return Done(false);
                b.RescheduleRequestedStartAt = null;
                break;
            case Completed:
                if (!await Step("status", BookingStatus.Completed, "completed")) return Done(false);
                ClearPending(b);
                break;
            case NoShow:
                if (!await Step("status", BookingStatus.NoShow, "no_show")) return Done(false);
                b.NoShowFeeCents = Math.Max(0, Money(data, "noShowFeeCents") ?? 0);
                b.RefundDueCents = Math.Clamp(Money(data, "refundDueCents") ?? 0, 0, Math.Max(b.TotalCents, 0));
                ClearPending(b);
                await RefundAsync(c, b, Money(data, "refundDueCents") ?? Math.Max(0, b.TotalCents - b.NoShowFeeCents), "no_show", ct);
                break;
            case ExtraCharge:
                // A REQUEST: nothing is charged here. The buyer consents (or declines) on the booking page; only then does the payment slice take the money.
                // The portal raises this when hourly work is completed, so it is a completion fact: it may follow booking.completed (same state again is allowed by the terminal rules) and
                // is refused after a cancellation, a decline, an expiry or a no-show (a different terminal state).
                if (!await Step("charge", terminal: "completed")) return Done(false);
                if (b.ExtraChargeState is ExtraChargeStates.Paid) break;      // an amount already paid is never replaced by a later request
                b.AdditionalChargeCents = Math.Max(0, Money(data, "amountCents") ?? 0);
                b.ExtraChargeState = b.AdditionalChargeCents > 0 ? ExtraChargeStates.Requested : null;   // a changed amount needs a new consent
                // An intent opened for an earlier amount must not stay payable next to the new request.
                await BookingMoney.QueueCancelPendingIntentsAsync(c.Db, new OutboxWriter(c.Db, c.Time), b.Id, PaymentKinds.ExtraCharge, b.AdditionalChargeCents, ct);
                break;
            case Reminder:
                // The buyer's reminder is OUR job (SERVICES.md §11), sent through M5's preference-aware notifier. Only for a live booking whose start still equals the reminded time (a moved or
                // cancelled booking must not remind about the old time); the notifier dedupes on the event id.
                if (b.Status is not (BookingStatus.Confirmed or BookingStatus.Rescheduled) || Time(data, "startAt") is not { } at || b.StartAt is not { } now0 || Math.Abs((at - now0).TotalMinutes) > 1)
                    return HandlerOutcome.Skipped("stale or not confirmed");
                break;
            case ProviderChanged:
                if (!await Step("provider")) return Done(false);
                if (Guid.TryParse(Str(data, "providerId"), out var pid)) b.ProviderId = pid;
                break;
            default:
                return HandlerOutcome.Skipped("not a booking event we project");
        }
        Notify(c, b, data);
        return HandlerOutcome.Applied();
    }

    /// <summary>Reserves a refund of at most what is left of the booking's captured payment, reported to the portal as <c>refund.processed</c> (it recorded the sale). Idempotent per booking and reason.</summary>
    private async Task RefundAsync(ProjectionContext c, Booking b, long wanted, string reason, CancellationToken ct)
    {
        var outbox = new OutboxWriter(c.Db, c.Time);
        var refunds = new BookingRefundService(c.Db, outbox, c.Time);
        var now = c.Time.GetUtcNow().ToUniversalTime();
        // Two tries: a confirm racing this event may report the sale between our read and our update, which turns "unreported" into "reported" under us.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pay = await c.Db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.BookingId == b.Id && p.Kind == "booking" && p.Status == BookingPaymentStatus.Captured, ct);
            if (pay is null) return;
            if (!pay.ReportedToPortal)
            {
                // The portal never recorded this sale (the confirm is still retrying, or failed): there is nothing to refund AS a sale, so the refund must not be reported as one. Everything is
                // voided and given back instead, and a late confirm finds the payment void (decision M4-15).
                if (await BookingMoney.VoidCapturedAsync(c.Db, refunds, log, pay, reason + "_unreported", now, ct)) return;
                continue;                                                                  // it was reported meanwhile: refund it as the sale it now is
            }
            var amount = Math.Min(wanted, pay.CapturedCents - pay.RefundedCents);
            if (amount <= 0) return;
            // Less what a chargeback holds; the rest waits and is reserved when the dispute is decided (M4-17). Never silent: an unreservable amount is an ALERT.
            await refunds.ReserveOrDeferAsync(pay.Id, amount, $"{reason}:{b.MarketplaceBookingId}", reason, true, log, ct);
            return;
        }
    }

    /// <summary>
    /// Tells the buyer through M5's notifier (in-app always, email per their preferences), inside this event's transaction and with the event id as the once-only key. Only what the buyer did not just do
    /// themselves: their own cancel or reschedule is not news to them.
    /// </summary>
    private static void Notify(ProjectionContext c, Booking b, JsonElement data)
    {
        string? kind = c.Event.Type switch
        {
            Confirmed => NotificationKinds.BookingConfirmed,
            Declined => NotificationKinds.BookingDeclined,
            TimeProposed => NotificationKinds.BookingTimeProposed,
            Rescheduled when !string.Equals(Str(data, "initiatedBy"), "buyer", StringComparison.OrdinalIgnoreCase) => NotificationKinds.BookingRescheduled,
            Cancelled when string.Equals(Str(data, "cancelledBy"), "seller", StringComparison.OrdinalIgnoreCase) => NotificationKinds.BookingCancelled,
            Completed => NotificationKinds.BookingCompleted,
            Reminder => NotificationKinds.BookingReminder,
            ExtraCharge => NotificationKinds.BookingExtraChargeRequested,
            NoShow => NotificationKinds.BookingNoShow,
            _ => null,
        };
        if (kind is null) return;
        Notifications.Notifier.AddBooking(c.Db, new OutboxWriter(c.Db, c.Time), c.Time, b.AccountId, kind, c.Event.Id, b.Number ?? "", b.MarketplaceBookingId,
            new { kind = Str(data, "kind"), startAt = b.StartAt, startsAt = b.StartAt });
    }

    /// <summary>The status an event moves a booking TO, or null for facts that do not change the status.</summary>
    private static string? Target(string type, JsonElement data) => type switch
    {
        Requested => BookingStatus.Requested, Confirmed => BookingStatus.Confirmed, Rescheduled => BookingStatus.Rescheduled, Started => BookingStatus.InProgress,
        Declined => BookingStatus.Declined, HoldExpired => BookingStatus.Expired, Completed => BookingStatus.Completed, NoShow => BookingStatus.NoShow,
        Cancelled => string.Equals(Str(data, "cancelledBy"), "seller", StringComparison.OrdinalIgnoreCase) ? BookingStatus.CancelledBySeller : BookingStatus.CancelledByBuyer,
        _ => null,
    };

    private static void ClearPending(Booking b) { b.RequiresBuyerConsent = false; b.RescheduleRequestedStartAt = null; if (b.ExtraChargeState is ExtraChargeStates.Requested or ExtraChargeStates.Consented) b.ExtraChargeState = null; }

    private static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Cap(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
    private static bool Bool(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Time(JsonElement d, string n)
        => Str(d, n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    /// <summary>A whole number of cents; fractions, strings and absurd values are ignored (never rounded into money).</summary>
    private static long? Money(JsonElement d, string n)
        => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l is >= 0 and <= 100_000_000_00 ? l : null;
}
