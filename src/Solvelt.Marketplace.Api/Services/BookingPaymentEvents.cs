using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// Stripe events for BOOKING intents (decision M4-08). <see cref="StripeEventProcessor"/> hands over every event whose intent metadata carries <c>marketplace_booking_id</c>; order intents
/// (<c>marketplace_order_id</c>) never reach this class. The payment is found by the intent id WE stored, and the booking by the id in its metadata: both must agree (never adopt an id from the event).
/// </summary>
public sealed class BookingPaymentEvents(MarketplaceDb db, BookingRefundService refunds, OutboxWriter outbox, TimeProvider time, ILogger<BookingPaymentEvents> log)
{
    public static bool IsBookingIntent(StripeEnvelope evt) => evt.Meta("marketplace_booking_id") is not null;

    /// <summary><c>charge.refunded</c> carries no metadata of ours, only the intent id: it is a booking's when a booking payment stores that intent.</summary>
    public Task<bool> OwnsIntentAsync(string intentId, CancellationToken ct) => db.BookingPayments.AsNoTracking().AnyAsync(p => p.IntentId == intentId, ct);

    private async Task<(BookingPayment Pay, Booking Booking)?> FindAsync(StripeEnvelope evt, string? intentId, CancellationToken ct)
    {
        var mid = evt.Meta("marketplace_booking_id");
        if (intentId is null || mid is null) return null;
        var pay = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.IntentId == intentId, ct);
        var booking = pay is null ? null : await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == pay.BookingId, ct);
        if (pay is null || booking is null || booking.MarketplaceBookingId != mid)
        {
            // The checkout request saves the intent id right after creating the intent: a young event may beat it (Stripe retries on a 5xx); an old one never matches.
            if (time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(evt.Created) < StripeEventProcessor.NotYetKnownGrace) throw new StripeOrderNotYetKnownException();
            log.LogError("A Stripe {Type} names a booking payment that does not match anything we stored; acknowledged and needs a person", evt.Type);
            return null;
        }
        return (pay, booking);
    }

    public async Task<StripeOutcome> SucceededAsync(StripeEnvelope evt, CancellationToken ct)
    {
        if (await FindAsync(evt, evt.Str("id"), ct) is not var (pay, booking)) return StripeOutcome.Unmatched;
        var amount = evt.Long("amount_received") ?? evt.Long("amount") ?? 0;
        var now = time.GetUtcNow().ToUniversalTime();
        var mismatch = amount != pay.AmountCents || !string.Equals(evt.Str("currency"), pay.Currency, StringComparison.OrdinalIgnoreCase);
        // A booking that is already over (expired, declined, cancelled ...) cannot take the payment: it is refunded, never captured silently.
        var extra = pay.Kind == PaymentKinds.ExtraCharge;
        // A booking that is already over (expired, declined, cancelled ...) cannot take the payment for it: refunded, never captured silently. An extra charge is paid AFTER the work, so
        // the booking being Completed is normal for it; only the portal's cap matters (it refuses more than it asked for).
        var closed = extra ? booking.Status is BookingStatus.CancelledByBuyer or BookingStatus.CancelledBySeller or BookingStatus.Declined or BookingStatus.Expired or BookingStatus.NoShow : BookingStatus.IsTerminal(booking.Status);
        var overCap = extra && amount > booking.AdditionalChargeCents;
        if (mismatch || closed || overCap || (pay.Kind != PaymentKinds.Booking && !extra))
        {
            var code = mismatch ? "amount_mismatch" : closed ? "booking_closed" : overCap ? "exceeds_request" : "unsupported_kind";
            var voided = await db.BookingPayments.Where(p => p.Id == pay.Id && p.Status == BookingPaymentStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Void).SetProperty(p => p.CapturedCents, amount).SetProperty(p => p.CapturedAt, now).SetProperty(p => p.FailureCode, code).SetProperty(p => p.UpdatedAt, now), ct);
            if (voided == 0) return StripeOutcome.Ignored;
            log.LogWarning("A payment arrived for booking {Mid} that cannot be honoured ({Code}); it is being refunded in full", booking.MarketplaceBookingId, code);
            if (amount > 0)
            {
                await refunds.ReserveAsync(pay.Id, amount, $"void:{pay.Key}", code, false, ct);
                await MailAsync(booking.AccountId, "Your payment for a booking is being refunded",
                    "We could not complete your booking, so the payment is being refunded in full to your card (it can take a few days to show). You have not been charged for a booking.", $"void:{pay.Key}", ct);
            }
            return StripeOutcome.Applied;
        }
        var captured = await db.BookingPayments.Where(p => p.Id == pay.Id && p.Status == BookingPaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Captured).SetProperty(p => p.CapturedCents, amount).SetProperty(p => p.CapturedAt, now).SetProperty(p => p.UpdatedAt, now), ct);
        if (captured == 0) return StripeOutcome.Ignored;     // a repeat
        if (extra)
        {
            // Paid is reached ONCE and only from the consent the buyer gave for exactly this amount: a payment for a superseded request (the provider changed the amount), for a declined one, or for
            // one that was already paid by another intent fails this update and is refunded in full instead of being kept (decision M4-15).
            var paidState = await db.Bookings.Where(b => b.Id == booking.Id && b.ExtraChargeState == ExtraChargeStates.Consented && b.AdditionalChargeCents == amount)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ExtraChargeState, ExtraChargeStates.Paid).SetProperty(b => b.UpdatedAt, now).SetProperty(b => b.Version, b => b.Version + 1), ct);
            if (paidState == 0)
            {
                await db.BookingPayments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Void).SetProperty(p => p.FailureCode, "extra_not_consented").SetProperty(p => p.UpdatedAt, now), ct);
                await refunds.ReserveAsync(pay.Id, amount, $"void:{pay.Key}", "extra_not_consented", false, ct);
                await MailAsync(booking.AccountId, "Your additional payment is being refunded", "The additional charge you paid is no longer the one requested (it was changed, declined or already paid), so the payment is being refunded in full to your card (it can take a few days to show).", $"void:{pay.Key}", ct);
                return StripeOutcome.Applied;
            }
            await db.BookingPayments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ReportedToPortal, true), ct);
            outbox.EnqueuePaymentEvent($"captured:{pay.Key}", PaymentEventTypes.Captured, new PaymentCapturedData(PaymentKinds.ExtraCharge, null, booking.MarketplaceBookingId, null, pay.IntentId!, pay.Currency, amount, now));
            return StripeOutcome.Applied;
        }
        outbox.Enqueue(BookingConfirmHandler.Type, new BookingConfirmHandler.Payload(booking.Id, pay.Id), $"confirm:{booking.MarketplaceBookingId}");
        return StripeOutcome.Applied;
    }

    /// <summary>A declined attempt is not final (another card may work): only the decline code is kept, and never on a captured payment.</summary>
    public async Task<StripeOutcome> FailedAsync(StripeEnvelope evt, CancellationToken ct)
    {
        if (await FindAsync(evt, evt.Str("id"), ct) is not var (pay, _)) return StripeOutcome.Unmatched;
        var code = evt.Object.TryGetProperty("last_payment_error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? new string(c.GetString()!.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(60).ToArray()) : "payment_failed";
        await db.BookingPayments.Where(p => p.Id == pay.Id && p.Status == BookingPaymentStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(p => p.FailureCode, code), ct);
        return StripeOutcome.Applied;
    }

    /// <summary>
    /// A refund made outside this system (the Stripe dashboard). Stripe's <c>amount_refunded</c> is cumulative; what is OURS is what Stripe confirmed for our own refunds (status Succeeded), so
    /// the rest is external. It is recorded with a compare-and-swap on the external total (two events racing record one refund once, and a refund of ours that is merely reserved can no longer hide
    /// one made in the dashboard), counted against the cap, and logged for a person (decision M4-15).
    /// </summary>
    public async Task<StripeOutcome> ChargeRefundedAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("payment_intent");
        var refunded = evt.Long("amount_refunded");
        if (intentId is null || refunded is null) return StripeOutcome.Unmatched;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var pay = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.IntentId == intentId, ct);
            if (pay is null) return StripeOutcome.Unmatched;
            var ours = await db.BookingRefunds.AsNoTracking().Where(r => r.PaymentId == pay.Id && r.Status == RefundStatus.Succeeded).SumAsync(r => (long?)r.AmountCents, ct) ?? 0;
            var delta = refunded.Value - ours - pay.ExternalRefundedCents;
            if (delta <= 0) return StripeOutcome.Ignored;                                            // ours, or already recorded
            var seen = pay.ExternalRefundedCents;
            var won = await db.BookingPayments.Where(p => p.Id == pay.Id && p.ExternalRefundedCents == seen)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExternalRefundedCents, p => p.ExternalRefundedCents + delta).SetProperty(p => p.RefundedCents, p => p.RefundedCents + delta), ct);
            if (won == 1)
            {
                log.LogError("A booking payment was refunded {Delta} cents outside this system; it is recorded against the cap and needs a person to tell the seller", delta);
                return StripeOutcome.Applied;
            }
        }
        throw new InvalidOperationException("booking refund counters kept moving");                     // the event is retried
    }

    /// <summary>
    /// <c>charge.refund.updated</c> for one of OUR booking refunds that Stripe accepted and then failed (the card was closed): the refund is marked failed and its amount goes back under the cap, so it can
    /// be made again, and a person is alerted because the portal may have been told it succeeded. Null when the refund is not a booking refund of ours.
    /// </summary>
    public async Task<StripeOutcome?> RefundFailedAsync(string refundId, CancellationToken ct)
    {
        var row = await db.BookingRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.StripeRefundId == refundId, ct);
        if (row is null) return null;
        var changed = await db.BookingRefunds.Where(r => r.Id == row.Id && r.Status == RefundStatus.Succeeded)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Failed).SetProperty(r => r.UpdatedAt, time.GetUtcNow()), ct);
        if (changed == 0) return StripeOutcome.Ignored;
        await db.BookingPayments.Where(p => p.Id == row.PaymentId).ExecuteUpdateAsync(s => s.SetProperty(p => p.RefundedCents, p => p.RefundedCents - row.AmountCents), ct);
        log.LogError("ALERT: booking refund {Key} ({Amount} cents) FAILED at Stripe after it was accepted; its amount is back under the cap and the portal may have been told it succeeded", row.Key, row.AmountCents);
        return StripeOutcome.Applied;
    }

    /// <summary>
    /// <c>charge.dispute.*</c> for a booking intent: told to the portal with <c>marketplaceBookingId</c> (our own event id, the portal dedupes on it), ordered like the order path (an older event for a
    /// dispute is dropped, nothing follows a closed one), and the amount at stake is held back from refunds while the dispute is open or lost, so we never refund money a chargeback already took.
    /// </summary>
    public async Task<StripeOutcome> DisputeAsync(StripeEnvelope evt, CancellationToken ct)
    {
        var intentId = evt.Str("payment_intent");
        var disputeId = evt.Str("id");
        var amount = evt.Long("amount");
        var status = evt.Str("status");
        if (intentId is null || disputeId is null || amount is null || status is null) return StripeOutcome.Unmatched;
        var pay = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.IntentId == intentId, ct);
        var booking = pay is null ? null : await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == pay.BookingId, ct);
        if (pay is null || booking is null) return StripeOutcome.Unmatched;
        var portalStatus = status switch { "needs_response" => "needs_response", "under_review" => "under_review", "won" => "won", "lost" => "lost", "warning_closed" => "warning_closed", _ => null };
        if (portalStatus is null) return StripeOutcome.Ignored;
        var closed = portalStatus is "won" or "lost" or "warning_closed";
        var state = await db.DisputeStates.FirstOrDefaultAsync(d => d.DisputeId == disputeId, ct);
        if (state is not null && (state.Closed || evt.Created < state.LastCreated)) return StripeOutcome.Ignored;
        if (state is null) db.DisputeStates.Add(new DisputeState { DisputeId = disputeId, LastCreated = evt.Created, Closed = closed });
        else { state.LastCreated = evt.Created; state.Closed = closed; }
        // What a chargeback holds: the amount while it is open or lost; nothing once it is won or was only an inquiry that closed.
        var held = portalStatus is "won" or "warning_closed" ? 0 : Math.Clamp(amount.Value, 0, pay.CapturedCents);
        await db.BookingPayments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisputedCents, held), ct);
        // The portal can only book a dispute against a sale it recorded.
        if (!pay.ReportedToPortal) return StripeOutcome.Applied;
        var type = evt.Type == "charge.dispute.created" ? PaymentEventTypes.DisputeCreated : closed ? PaymentEventTypes.DisputeClosed : PaymentEventTypes.DisputeUpdated;
        DateTimeOffset? due = evt.Object.TryGetProperty("evidence_details", out var ed) && ed.ValueKind == JsonValueKind.Object && ed.TryGetProperty("due_by", out var d2) && d2.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        var createdAt = evt.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(evt.Created) : (DateTimeOffset?)null;
        outbox.EnqueuePaymentEvent($"dispute:{evt.Id}:{booking.MarketplaceBookingId}", type, new DisputeData(disputeId, null, null, booking.MarketplaceBookingId, amount.Value, portalStatus, null, pay.Currency, evt.Str("reason"), due, createdAt));
        return StripeOutcome.Applied;
    }

    private async Task MailAsync(Guid accountId, string subject, string body, string dedupe, CancellationToken ct)
    {
        var email = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
        if (email is not null) outbox.EnqueueEmail(new EmailMessage(email, subject, body), "mail:" + dedupe);
    }
}

/// <summary>
/// After the money is in: confirm the hold at the portal, then report the sale. Retried by the outbox. A definitive refusal (the hold is gone and the slot taken, the intake is
/// invalid, the portal does not know the booking) voids the payment and refunds it in full, never reported to the portal as a sale; the buyer is told. Idempotent: the portal
/// confirm is idempotent on our booking id and the payment event on its own event id.
/// </summary>
public sealed class BookingConfirmHandler(MarketplaceDb db, ISignedPortalClient portal, BookingService bookings, BookingRefundService refunds, OutboxWriter outbox, PortalEventProcessor events, TimeProvider time, ILogger<BookingConfirmHandler> log) : IOutboxHandler
{
    public const string Type = "portal.booking-confirm";
    public sealed record Payload(Guid BookingId, Guid PaymentId);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    private async Task VoidClosedAsync(Booking b, BookingPayment pay, string email, string code, bool cancelAtPortal, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        if (await BookingMoney.VoidCapturedAsync(db, refunds, log, pay, code, now, ct))
        {
            outbox.EnqueueEmail(new EmailMessage(email, "We couldn't confirm your booking",
                "The time you chose could not be confirmed, so your payment is being refunded in full to your card (it can take a few days to show). Please choose another time.\n"), "mail:void:" + pay.Key);
            if (cancelAtPortal) outbox.Enqueue(BookingLateCancelHandler.Type, new BookingLateCancelHandler.Payload(b.MarketplaceBookingId), "late-cancel:" + b.MarketplaceBookingId);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == p.BookingId, ct);
        var pay = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.PaymentId, ct);
        if (b is null || pay is null) return OutboxResult.Dead("booking_missing");
        if (pay.Status != BookingPaymentStatus.Captured || pay.ReportedToPortal) return OutboxResult.Delivered();   // done before, or voided
        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == b.AccountId).Select(a => new { a.DisplayName, a.RelayEmail, Email = a.Email! }).FirstOrDefaultAsync(ct);
        if (account is null) return OutboxResult.Dead("account_missing");

        // A booking we already show as over (the hold lapsed while this message waited, the buyer cancelled) is never confirmed: confirming it could charge the buyer for a booking that
        // is not theirs. The payment is voided and refunded in full instead, and no sale is reported (decision M4-15).
        if (BookingStatus.IsTerminal(b.Status))
        {
            await VoidClosedAsync(b, pay, account.Email, "booking_closed", false, ct);
            return OutboxResult.Delivered();
        }

        var addr = b.AddrLine1 is null ? null : new PortalAddress(b.AddrName ?? "", b.AddrLine1, b.AddrLine2, b.AddrCity ?? "", b.AddrRegion, b.AddrPostalCode ?? "", b.AddrCountry ?? "", b.AddrLat, b.AddrLng);
        IReadOnlyList<IntakeAnswerBody>? intake = null;
        if (b.IntakeJson is { } ij) { try { intake = JsonSerializer.Deserialize<List<IntakeAnswerBody>>(ij, PortalJson.Options); } catch (JsonException) { } }
        PortalResult<PortalBookingSummary> r;
        try { r = await portal.ConfirmBookingAsync(b.MarketplaceBookingId, new BookingConfirmBody(new PortalBuyer(RelayIdentity.PublicName(account.DisplayName), account.RelayEmail, b.Phone), addr, intake), ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or JsonException) { return OutboxResult.Retry("portal_unavailable"); }

        var now = time.GetUtcNow().ToUniversalTime();
        if (r.IsSuccess && r.Value is { } s && string.Equals(s.MarketplaceBookingId, b.MarketplaceBookingId, StringComparison.Ordinal) && (s.ServiceId is null || s.ServiceId == b.ListingId))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Entry(b).ReloadAsync(ct);                                  // the row as it is NOW: an event may have closed it while the portal call was running
            if (!await bookings.ApplySummaryAsync(b, s, now, ct))
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                var fresh = await db.Bookings.AsNoTracking().FirstAsync(x => x.Id == b.Id, ct);
                if (!BookingStatus.IsTerminal(fresh.Status)) return OutboxResult.Retry("booking_changed");      // a parallel writer: the portal confirm is idempotent, try again
                // The portal confirmed a booking that is over on our side: refund everything, report no sale, and cancel it there.
                await VoidClosedAsync(fresh, pay, account.Email, "booking_closed", true, ct);
                return OutboxResult.Delivered();
            }
            var marked = await db.BookingPayments.Where(x => x.Id == pay.Id && !x.ReportedToPortal).ExecuteUpdateAsync(x => x.SetProperty(y => y.ReportedToPortal, true).SetProperty(y => y.UpdatedAt, now), ct);
            if (marked == 1)
            {
                outbox.EnqueuePaymentEvent($"captured:{pay.Key}", PaymentEventTypes.Captured, new PaymentCapturedData(PaymentKinds.Booking, null, b.MarketplaceBookingId, null, pay.IntentId!, pay.Currency, null, pay.CapturedAt));
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            await events.ReplayParkedAsync(BookingEventHandler.Aggregate, b.MarketplaceBookingId, ct);
            return OutboxResult.Delivered();
        }
        // Transient: try again later (never refund on a maybe).
        if (r.Retryable || r.Status is 408 or 429 or >= 500 or 401 or 413) return PortalOutcome.From(r);

        // Definitive refusal: the booking will not happen. Void the payment, refund it in full (not a sale: the portal never recorded one), tell the buyer.
        var code = new string((r.Error?.Code ?? "refused").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(40).ToArray());
        var newStatus = r.Status == 409 ? BookingStatus.Expired : BookingStatus.Failed;
        log.LogWarning("The portal refused to confirm booking {Mid} ({Status} {Code}); refunding the payment", b.MarketplaceBookingId, r.Status, code);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var voided = await db.BookingPayments.Where(x => x.Id == pay.Id && x.Status == BookingPaymentStatus.Captured && !x.ReportedToPortal)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, BookingPaymentStatus.Void).SetProperty(y => y.FailureCode, "confirm_" + code).SetProperty(y => y.UpdatedAt, now), ct);
            if (voided == 1)
            {
                await db.Bookings.Where(x => x.Id == b.Id && x.Status == BookingStatus.Hold)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, newStatus).SetProperty(y => y.FailureCode, "confirm_" + code).SetProperty(y => y.UpdatedAt, now).SetProperty(y => y.Version, y => y.Version + 1), ct);
                await refunds.ReserveAsync(pay.Id, pay.CapturedCents, $"void:{pay.Key}", "confirm_" + code, false, ct);
                outbox.EnqueueEmail(new EmailMessage(account.Email, "We couldn't confirm your booking",
                    "The time you chose could not be confirmed, so your payment is being refunded in full to your card (it can take a few days to show). Please choose another time.\n"), "mail:void:" + pay.Key);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return OutboxResult.Delivered();
    }
}
