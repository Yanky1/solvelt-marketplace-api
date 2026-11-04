using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

public sealed record BookingCheckoutBody(IReadOnlyList<IntakeAnswerBody>? Intake, string? Phone);
public sealed record BookingCheckoutResult(BookingView Booking, CheckoutPayment Payment);
public enum BookingCheckoutStatus { Ok, NotFound, NotPayable, Expired, Invalid, AlreadyPaid, AmountTooSmall, PaymentsUnavailable, PaymentRejected }

/// <summary>
/// Paying for a booking (decision M4-08). The buyer holds a slot, then pays: <c>POST /bookings/{id}/checkout</c> stores the intake answers and creates the ONE Stripe intent of
/// this booking for exactly the portal's total (the body has no amount). Money first, then the portal: when Stripe says the intent succeeded, the hold is confirmed at the portal
/// (outbox, retried) and only then is <c>payment.captured</c> (kind <c>booking</c>) sent. If the booking cannot be honoured (the hold is gone, the portal refuses, the amount
/// is not the one we priced) the payment is refunded in full and never reported as a sale.
/// </summary>
public sealed class BookingPaymentService(MarketplaceDb db, IPaymentProvider payments, IOptions<StripeOptions> stripe, TimeProvider time, ILogger<BookingPaymentService> log)
{
    public static string PaymentKey(string marketplaceBookingId) => $"pay:{marketplaceBookingId}:booking";

    public static Dictionary<string, string> Validate(BookingCheckoutBody? b)
    {
        var e = new Dictionary<string, string>();
        if (b is null) return e;
        if (b.Intake is { } a && (a.Count > 30 || a.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 60 || x.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) || (x.Answer ?? "").Length > 2000)))
            e["intake"] = "Check your answers to the provider's questions.";
        if (b.Phone is { } p && (p.Trim().Length > 40 || p.Any(ch => !(char.IsAsciiDigit(ch) || ch is '+' or '-' or '(' or ')' or ' ' or '.')))) e["phone"] = "Enter a valid phone number.";
        return e;
    }

    public async Task<(BookingCheckoutStatus Status, BookingCheckoutResult? Result, IReadOnlyDictionary<string, string>? Errors)> CheckoutAsync(BuyerPrincipal buyer, Guid bookingId, BookingCheckoutBody? body, BookingService bookings, CancellationToken ct)
    {
        var errors = Validate(body);
        if (errors.Count > 0) return (BookingCheckoutStatus.Invalid, null, errors);
        var now = time.GetUtcNow().ToUniversalTime();
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == bookingId && x.AccountId == buyer.AccountId && x.Status != BookingStatus.Failed, ct);
        if (b is null) return (BookingCheckoutStatus.NotFound, null, null);
        var key = PaymentKey(b.MarketplaceBookingId);
        var existing = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);
        if (existing is { Status: BookingPaymentStatus.Captured }) return (BookingCheckoutStatus.AlreadyPaid, null, null);
        if (b.Status != BookingStatus.Hold || b.PortalBookingId is null) return (BookingCheckoutStatus.NotPayable, null, null);
        if (b.HoldExpiresAt is not { } until || until <= now) return (BookingCheckoutStatus.Expired, null, null);
        if (b.TotalCents < 50) return (BookingCheckoutStatus.AmountTooSmall, null, null);   // Stripe's minimum charge

        // The intake answers and the phone are what the portal receives at confirmation; they are stored before any money moves.
        var phone = body?.Phone is { } ph && ph.Trim().Length > 0 ? ph.Trim() : b.Phone;
        var intake = body?.Intake is { Count: > 0 } ? JsonSerializer.Serialize(body.Intake, PortalJson.Options) : b.IntakeJson;
        var stored = await db.Bookings.Where(x => x.Id == b.Id && x.Status == BookingStatus.Hold)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Phone, phone).SetProperty(x => x.IntakeJson, intake).SetProperty(x => x.UpdatedAt, now), ct);
        if (stored == 0) return (BookingCheckoutStatus.NotPayable, null, null);

        var pay = existing;
        if (pay is null)
        {
            db.BookingPayments.Add(new BookingPayment { Id = Guid.NewGuid(), Key = key, BookingId = b.Id, Kind = PaymentKinds.Booking, Currency = b.Currency, AmountCents = b.TotalCents, Status = BookingPaymentStatus.Pending, CreatedAt = now, UpdatedAt = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // a parallel checkout inserted it first: it is the same payment
            pay = await db.BookingPayments.AsNoTracking().FirstAsync(p => p.Key == key, ct);
        }
        if (pay.Status != BookingPaymentStatus.Pending) return (BookingCheckoutStatus.NotPayable, null, null);
        if (pay.AmountCents != b.TotalCents) return (BookingCheckoutStatus.NotPayable, null, null);   // the portal's price moved since the intent: never charge a different amount than the one shown
        try
        {
            var intent = await payments.CreateIntentAsync(pay.AmountCents, pay.Currency, "pi:" + key,
                new Dictionary<string, string> { ["marketplace_booking_id"] = b.MarketplaceBookingId, ["kind"] = PaymentKinds.Booking }, ct);
            await db.BookingPayments.Where(p => p.Id == pay.Id && p.IntentId == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.IntentId, intent.Id), ct);
            var intentStored = await db.BookingPayments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.IntentId).FirstAsync(ct);
            if (intentStored != intent.Id)
            {
                log.LogError("Booking {Mid} has two different payment intents (stored {Stored}, returned {Returned})", b.MarketplaceBookingId, intentStored, intent.Id);
                return (BookingCheckoutStatus.PaymentsUnavailable, null, null);
            }
            var view = bookings.ToView(await db.Bookings.AsNoTracking().FirstAsync(x => x.Id == b.Id, ct), now);
            return (BookingCheckoutStatus.Ok, new BookingCheckoutResult(view, new CheckoutPayment("stripe", intent.ClientSecret, stripe.Value.PublishableKey)), null);
        }
        catch (PaymentsUnavailableException) { return (BookingCheckoutStatus.PaymentsUnavailable, null, null); }
        catch (PaymentRejectedException ex) { log.LogWarning("Payment provider refused booking {Mid}: {Code}", b.MarketplaceBookingId, ex.Code); return (BookingCheckoutStatus.PaymentRejected, null, null); }
    }
}

public static class ExtraChargeStates
{
    public const string Requested = "requested", Consented = "consented", Declined = "declined", Paid = "paid";
}

public enum ExtraChargeStatus { Ok, NotFound, NotRequested, AmountTooSmall, PaymentsUnavailable, PaymentRejected, AlreadyPaid }

/// <summary>
/// An additional charge (hourly work that ran over, SERVICES.md §4) is a REQUEST from the provider. Nothing is charged until the buyer accepts here (decision M4-06). The amount is the one the
/// portal sent (<c>booking.additional_charge_requested</c>), never a client value, and it is the cap: the payment is one intent per request amount, and a payment that no longer fits under the
/// portal's cap when it lands is refunded instead of reported (the portal would refuse it: <c>exceeds_booking</c>).
/// </summary>
public sealed class ExtraChargeService(MarketplaceDb db, IPaymentProvider payments, IOptions<StripeOptions> stripe, TimeProvider time, ILogger<ExtraChargeService> log)
{
    public static string PaymentKey(string mid, long amount) => $"pay:{mid}:extra:{amount}";
    private static readonly string[] Payable = [BookingStatus.Confirmed, BookingStatus.Rescheduled, BookingStatus.InProgress, BookingStatus.Completed];

    public async Task<(ExtraChargeStatus Status, CheckoutPayment? Payment, long AmountCents)> AcceptAsync(Guid accountId, Guid bookingId, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId && x.AccountId == accountId && x.Status != BookingStatus.Failed, ct);
        if (b is null) return (ExtraChargeStatus.NotFound, null, 0);
        if (b.ExtraChargeState == ExtraChargeStates.Paid) return (ExtraChargeStatus.AlreadyPaid, null, b.AdditionalChargeCents);
        if (b.ExtraChargeState is not (ExtraChargeStates.Requested or ExtraChargeStates.Consented) || b.AdditionalChargeCents <= 0 || !Payable.Contains(b.Status)) return (ExtraChargeStatus.NotRequested, null, 0);
        if (b.AdditionalChargeCents < 50) return (ExtraChargeStatus.AmountTooSmall, null, b.AdditionalChargeCents);
        var now = time.GetUtcNow().ToUniversalTime();
        var amount = b.AdditionalChargeCents;
        var key = PaymentKey(b.MarketplaceBookingId, amount);
        // Consent is recorded with the amount the buyer saw; if a newer request changed the amount meanwhile the row no longer matches and nothing is charged.
        var consented = await db.Bookings.Where(x => x.Id == b.Id && x.AdditionalChargeCents == amount && (x.ExtraChargeState == ExtraChargeStates.Requested || x.ExtraChargeState == ExtraChargeStates.Consented))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraChargeState, ExtraChargeStates.Consented).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (consented == 0) return (ExtraChargeStatus.NotRequested, null, 0);
        var pay = await db.BookingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);
        if (pay is null)
        {
            db.BookingPayments.Add(new BookingPayment { Id = Guid.NewGuid(), Key = key, BookingId = b.Id, Kind = PaymentKinds.ExtraCharge, Currency = b.Currency, AmountCents = amount, Status = BookingPaymentStatus.Pending, CreatedAt = now, UpdatedAt = now });
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            pay = await db.BookingPayments.AsNoTracking().FirstAsync(p => p.Key == key, ct);
        }
        if (pay.Status != BookingPaymentStatus.Pending) return (ExtraChargeStatus.AlreadyPaid, null, amount);
        try
        {
            var intent = await payments.CreateIntentAsync(amount, pay.Currency, "pi:" + key, new Dictionary<string, string> { ["marketplace_booking_id"] = b.MarketplaceBookingId, ["kind"] = PaymentKinds.ExtraCharge }, ct);
            await db.BookingPayments.Where(p => p.Id == pay.Id && p.IntentId == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.IntentId, intent.Id), ct);
            var stored = await db.BookingPayments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.IntentId).FirstAsync(ct);
            if (stored != intent.Id) { log.LogError("Extra charge of booking {Mid} has two different intents", b.MarketplaceBookingId); return (ExtraChargeStatus.PaymentsUnavailable, null, amount); }
            return (ExtraChargeStatus.Ok, new CheckoutPayment("stripe", intent.ClientSecret, stripe.Value.PublishableKey), amount);
        }
        catch (PaymentsUnavailableException) { return (ExtraChargeStatus.PaymentsUnavailable, null, amount); }
        catch (PaymentRejectedException ex) { log.LogWarning("Payment provider refused an extra charge: {Code}", ex.Code); return (ExtraChargeStatus.PaymentRejected, null, amount); }
    }

    /// <summary>The buyer says no. Nothing is charged and nothing is sent; a later request from the provider can ask again.</summary>
    public async Task<ExtraChargeStatus> DeclineAsync(Guid accountId, Guid bookingId, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId && x.AccountId == accountId && x.Status != BookingStatus.Failed, ct);
        if (b is null) return ExtraChargeStatus.NotFound;
        var n = await db.Bookings.Where(x => x.Id == b.Id && (x.ExtraChargeState == ExtraChargeStates.Requested || x.ExtraChargeState == ExtraChargeStates.Consented))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraChargeState, ExtraChargeStates.Declined).SetProperty(x => x.UpdatedAt, time.GetUtcNow()).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return n == 1 ? ExtraChargeStatus.Ok : ExtraChargeStatus.NotRequested;
    }
}

public enum BookingReserveOutcome { Reserved, Duplicate, ExceedsCaptured, NotCaptured }

/// <summary>
/// The only way a booking payment's money leaves: reserve against the cap (<c>RefundedCents + amount &lt;= CapturedCents</c>, a conditional update) together with the unique refund key and the outbox
/// message that calls Stripe, so refunds can never exceed what was captured however many events, retries or parallel requests arrive (same rule as <c>RefundService</c> for orders).
/// </summary>
public sealed class BookingRefundService(MarketplaceDb db, OutboxWriter outbox, TimeProvider time)
{
    public const string StripeRefundType = "stripe.booking-refund";
    public sealed record RefundPayload(string Key);

    public async Task<BookingReserveOutcome> ReserveAsync(Guid paymentId, long amountCents, string key, string reason, bool reportToPortal, CancellationToken ct)
    {
        if (amountCents <= 0) throw new ArgumentOutOfRangeException(nameof(amountCents));
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.BookingRefunds.AnyAsync(r => r.Key == key, ct)) return BookingReserveOutcome.Duplicate;
        var pay = await db.BookingPayments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => new { p.BookingId, p.CapturedCents }).FirstOrDefaultAsync(ct);
        if (pay is null || pay.CapturedCents <= 0) return BookingReserveOutcome.NotCaptured;
        var n = await db.BookingPayments.Where(p => p.Id == paymentId && p.RefundedCents + amountCents <= p.CapturedCents)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RefundedCents, p => p.RefundedCents + amountCents), ct);
        if (n == 0) return BookingReserveOutcome.ExceedsCaptured;
        var now = time.GetUtcNow().ToUniversalTime();
        db.BookingRefunds.Add(new BookingRefund { Id = Guid.NewGuid(), Key = key, PaymentId = paymentId, BookingId = pay.BookingId, AmountCents = amountCents, Reason = reason, Status = RefundStatus.Reserved, ReportToPortal = reportToPortal, CreatedAt = now, UpdatedAt = now });
        outbox.Enqueue(StripeRefundType, new RefundPayload(key), $"refund:{key}");
        if (own) { await db.SaveChangesAsync(ct); await tx!.CommitAsync(ct); }
        return BookingReserveOutcome.Reserved;
    }
}

/// <summary>Calls Stripe for a reserved booking refund (idempotent on the refund key); reports <c>refund.processed</c> with <c>marketplaceBookingId</c> only when it reverses a sale the portal knows.</summary>
public sealed class BookingStripeRefundHandler(MarketplaceDb db, IPaymentProvider payments, OutboxWriter outbox, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [BookingRefundService.StripeRefundType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        BookingRefundService.RefundPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<BookingRefundService.RefundPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.Key)) return OutboxResult.Dead("invalid_payload");
        var refund = await db.BookingRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.Key == p.Key, ct);
        if (refund is null) return OutboxResult.Dead("refund_missing");
        if (refund.Status != RefundStatus.Reserved) return OutboxResult.Delivered();
        var pay = await db.BookingPayments.AsNoTracking().FirstAsync(x => x.Id == refund.PaymentId, ct);
        var mid = await db.Bookings.AsNoTracking().Where(b => b.Id == pay.BookingId).Select(b => b.MarketplaceBookingId).FirstAsync(ct);
        if (pay.IntentId is null) return OutboxResult.Dead("no_payment_intent");
        RefundResult result;
        try { result = await payments.RefundAsync(pay.IntentId, refund.AmountCents, "refund:" + refund.Key, new Dictionary<string, string> { ["marketplace_booking_id"] = mid, ["refund_key"] = refund.Key }, ct); }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.BookingRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Rejected).SetProperty(r => r.UpdatedAt, time.GetUtcNow()), ct);
            if (changed == 1) await db.BookingPayments.Where(x => x.Id == pay.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents - refund.AmountCents), ct);
            await tx.CommitAsync(ct);
            return OutboxResult.Dead("stripe_" + ex.Code);
        }
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = time.GetUtcNow().ToUniversalTime();
            var changed = await db.BookingRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Succeeded).SetProperty(r => r.StripeRefundId, result.Id).SetProperty(r => r.UpdatedAt, now), ct);
            if (changed == 1)
            {
                if (refund.ReportToPortal)
                    outbox.EnqueuePaymentEvent($"refund-processed:{refund.Key}", PaymentEventTypes.RefundProcessed, new RefundProcessedData(result.Id, null, null, mid, refund.AmountCents, null, pay.Currency, refund.Reason));
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return OutboxResult.Delivered();
    }
}
