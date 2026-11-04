using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// The two money moves every "this booking will not happen" path shares (decision M4-15): void a payment the portal never recorded as a sale and give ALL of it back, and cancel a payment
/// intent that must not stay payable. One implementation, so no path can report a sale for a booking we show as over, or leave an intent open behind a closed one.
/// </summary>
public static class BookingMoney
{
    /// <summary>
    /// Captured but never reported to the portal means the portal has no sale to refund, so the refund must not be reported either: the payment becomes Void and what is left of it is
    /// reserved for Stripe in full (no report). False when the payment was not in that state (already reported, already void): nothing was changed. A reservation that cannot be made
    /// (the cap, a chargeback) is logged as an alert: the money is then still with us and needs a person.
    /// </summary>
    public static async Task<bool> VoidCapturedAsync(MarketplaceDb db, BookingRefundService refunds, ILogger log, BookingPayment pay, string code, DateTimeOffset now, CancellationToken ct)
    {
        var voided = await db.BookingPayments.Where(p => p.Id == pay.Id && p.Status == BookingPaymentStatus.Captured && !p.ReportedToPortal)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Void).SetProperty(p => p.FailureCode, code).SetProperty(p => p.UpdatedAt, now), ct);
        if (voided == 0) return false;
        var left = await db.BookingPayments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.CapturedCents - p.RefundedCents).FirstAsync(ct);
        if (left > 0)
        {
            var outcome = await refunds.ReserveAsync(pay.Id, left, $"void:{pay.Key}", code, false, ct);
            if (outcome is BookingReserveOutcome.ExceedsCaptured or BookingReserveOutcome.NotCaptured)
                log.LogError("ALERT: the {Code} refund of booking payment {Key} could not be reserved ({Outcome}); the money is still held and needs a person", code, pay.Key, outcome);
        }
        return true;
    }

    /// <summary>Queues cancellation (M3's retried <c>stripe.cancel</c>) of this booking's still-pending intents, optionally only one kind and never the one for <paramref name="keepAmount"/>.</summary>
    public static async Task QueueCancelPendingIntentsAsync(MarketplaceDb db, OutboxWriter outbox, Guid bookingId, string? kind, long? keepAmount, CancellationToken ct)
    {
        var intents = await db.BookingPayments.AsNoTracking()
            .Where(p => p.BookingId == bookingId && p.Status == BookingPaymentStatus.Pending && p.IntentId != null && (kind == null || p.Kind == kind) && (keepAmount == null || p.AmountCents != keepAmount))
            .Select(p => p.IntentId!).ToListAsync(ct);
        foreach (var intent in intents)
        {
            var key = StripeCancelHandler.DedupeKey(intent);
            if (!await outbox.ExistsAsync(key, ct)) outbox.Enqueue(StripeCancelHandler.Type, new StripeCancelHandler.Payload(intent), key);
        }
    }
}

/// <summary>
/// The portal confirmed a booking we had already closed (a hold that lapsed while the confirm was retrying): cancel it there, so the provider's calendar does not keep a booking nobody will
/// attend and nobody paid for. Retried by the outbox; the portal answering "already over" is success.
/// </summary>
public sealed class BookingLateCancelHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public const string Type = "portal.booking-cancel-late";
    public sealed record Payload(string MarketplaceBookingId);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrWhiteSpace(p.MarketplaceBookingId)) return OutboxResult.Dead("invalid_payload");
        PortalResult<PortalBookingSummary> r;
        try { r = await portal.CancelBookingAsync(p.MarketplaceBookingId, new BookingCancelBody("cancel-late-" + p.MarketplaceBookingId, "payment_not_completed"), ct); }
        catch (PortalUnavailableException) { return OutboxResult.Retry("portal_unavailable"); }
        catch (PortalNotConfiguredException) { return OutboxResult.Retry("portal_not_configured"); }
        if (r.Status is 404 or 409) return OutboxResult.Delivered();
        return PortalOutcome.From(r);
    }
}
