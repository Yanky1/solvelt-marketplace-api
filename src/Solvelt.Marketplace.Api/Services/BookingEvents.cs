using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
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
public sealed class BookingEventHandler(IOptions<MarketplaceOptions> marketplace) : IProjectionHandler
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
                break;
            case HoldExpired:
                if (!await Step("status", BookingStatus.Expired, "expired")) return Done(false);
                b.HoldExpiresAt = null; ClearPending(b);
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
                break;
            case Reminder:
            {
                // The buyer's reminder is OUR job (SERVICES.md §11). Only for a live booking whose time still is the one the reminder was scheduled for (a moved or cancelled booking
                // must not remind about the old time); one mail per event id (the receiver already refuses a repeated event).
                if (b.Status is not (BookingStatus.Confirmed or BookingStatus.Rescheduled) || Time(data, "startAt") is not { } at || b.StartAt is not { } now0 || Math.Abs((at - now0).TotalMinutes) > 1)
                    return HandlerOutcome.Skipped("stale or not confirmed");
                var email = await c.Db.BuyerAccounts.AsNoTracking().Where(a => a.Id == b.AccountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
                if (email is null) return HandlerOutcome.Skipped("no recipient");
                var title = await c.Db.CatalogListings.AsNoTracking().Where(l => l.Id == b.ListingId).Select(l => l.Title).FirstOrDefaultAsync(ct) ?? "your booking";
                var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
                var when = Str(data, "kind") == "hour_before" ? "in about an hour" : "tomorrow";
                new OutboxWriter(c.Db, c.Time).EnqueueEmail(new EmailMessage(email, "Reminder: your booking starts " + when,
                    $"Hello,\n\nA reminder that \"{title}\" starts {when}: {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC.\n\nSee the details, reschedule or cancel: {baseUrl}/account/bookings/{b.Id}\n"), "mail:reminder:" + c.Event.Id);
                break;
            }
            case ProviderChanged:
                if (!await Step("provider")) return Done(false);
                if (Guid.TryParse(Str(data, "providerId"), out var pid)) b.ProviderId = pid;
                break;
            default:
                return HandlerOutcome.Skipped("not a booking event we project");
        }
        return HandlerOutcome.Applied();
    }

    /// <summary>Reserves a refund of at most what is left of the booking's captured payment, reported to the portal as <c>refund.processed</c> (it recorded the sale). Idempotent per booking and reason.</summary>
    private static async Task RefundAsync(ProjectionContext c, Booking b, long wanted, string reason, CancellationToken ct)
    {
        var pay = await c.Db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.BookingId == b.Id && p.Kind == "booking" && p.Status == BookingPaymentStatus.Captured, ct);
        if (pay is null) return;
        var amount = Math.Min(wanted, pay.CapturedCents - pay.RefundedCents);
        if (amount <= 0) return;
        await new BookingRefundService(c.Db, new OutboxWriter(c.Db, c.Time), c.Time).ReserveAsync(pay.Id, amount, $"{reason}:{b.MarketplaceBookingId}", reason, true, ct);
    }

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
