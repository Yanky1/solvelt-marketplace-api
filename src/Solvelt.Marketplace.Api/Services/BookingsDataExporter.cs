using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// M4's part of "download my data" (M5's <see cref="IAccountDataExporter"/>, decision M4-28): the buyer's bookings (time, place, what they paid, fees, the cancellation policy the provider froze, the
/// address and phone they gave, their answers), the payments and refunds behind them, and their quote requests. NOT in it: anything of the provider's or the portal's (ids, assignment, notes), idempotency
/// keys and hashes, Stripe ids and secrets, and the meeting link of a booking that is not confirmed. A booking already redacted shows its remaining facts (the personal fields are empty).
/// </summary>
public sealed class BookingsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "bookings";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var bookings = await db.Bookings.AsNoTracking().Where(b => b.AccountId == accountId && b.Status != BookingStatus.Failed && b.Status != BookingStatus.HoldPending).OrderBy(b => b.CreatedAt).ToListAsync(ct);
        var ids = bookings.Select(b => b.Id).ToList();
        var payments = await db.BookingPayments.AsNoTracking().Where(p => ids.Contains(p.BookingId)).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var refunds = await db.BookingRefunds.AsNoTracking().Where(r => ids.Contains(r.BookingId) && r.Status != RefundStatus.Deferred).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var quotes = await db.Quotes.AsNoTracking().Where(q => q.AccountId == accountId && q.Status != QuoteStatus.Failed && q.Status != QuoteStatus.Requesting).OrderBy(q => q.CreatedAt).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => bookings.Select(b => b.ListingId).Concat(quotes.Select(q => q.ListingId)).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        return new
        {
            bookings = bookings.Select(b => new
            {
                number = b.Number, service = titles.GetValueOrDefault(b.ListingId), status = b.Status, location = b.Location, startAt = b.StartAt, endAt = b.EndAt, durationMinutes = b.DurationMinutes, createdAt = b.CreatedAt,
                money = new { b.Currency, b.PriceCents, b.AddOnsCents, b.TravelFeeCents, b.TotalCents, b.CancellationFeeCents, b.RefundDueCents, b.NoShowFeeCents, additionalChargeCents = b.AdditionalChargeCents, additionalChargeState = b.ExtraChargeState },
                policy = b.PolicyCancelFreeHours is null && b.PolicyCancelFeePercent is null && b.PolicyNoShowFeePercent is null && b.PolicyMaxReschedules is null ? null
                    : new { cancelFreeHours = b.PolicyCancelFreeHours, cancelFeePercent = b.PolicyCancelFeePercent, noShowFeePercent = b.PolicyNoShowFeePercent, maxReschedules = b.PolicyMaxReschedules },
                address = b.AddrLine1 is null ? null : new { name = b.AddrName, line1 = b.AddrLine1, line2 = b.AddrLine2, city = b.AddrCity, region = b.AddrRegion, postalCode = b.AddrPostalCode, country = b.AddrCountry },
                phone = b.Phone, intakeAnswers = Intake(b.IntakeJson), meetingLink = BookingStatuses.RevealsMeetingLink(b.Status) ? b.OnlineInstructions : null, redacted = b.RedactedAt is not null,
                payments = payments.Where(p => p.BookingId == b.Id).Select(p => new { kind = p.Kind, p.Currency, amountCents = p.AmountCents, status = p.Status, capturedCents = p.CapturedCents, refundedCents = p.RefundedCents, at = p.CapturedAt ?? p.CreatedAt }),
                refunds = refunds.Where(r => r.BookingId == b.Id).Select(r => new { amountCents = r.AmountCents, reason = r.Reason, status = r.Status, at = r.CreatedAt }),
            }),
            quoteRequests = quotes.Select(q => new
            {
                service = titles.GetValueOrDefault(q.ListingId), status = q.Status, location = q.Location, details = q.Details, preferredTimes = Times(q.PreferredTimesJson), createdAt = q.CreatedAt,
                priceCents = q.PriceCents, durationMinutes = q.DurationMinutes, currency = q.Currency, offeredTimes = Times(q.SlotsJson), validUntil = q.ExpiresAt, providerNote = q.Note, declineReason = q.DeclineReason,
                address = q.AddrLine1 is null ? null : new { name = q.AddrName, line1 = q.AddrLine1, line2 = q.AddrLine2, city = q.AddrCity, region = q.AddrRegion, postalCode = q.AddrPostalCode, country = q.AddrCountry }, phone = q.Phone, redacted = q.RedactedAt is not null,
            }),
        };
    }

    private static object? Intake(string? json) { try { return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json); } catch (JsonException) { return null; } }
    private static IReadOnlyList<DateTimeOffset> Times(string? json) { try { return json is null ? [] : JsonSerializer.Deserialize<List<DateTimeOffset>>(json) ?? []; } catch (JsonException) { return []; } }
}
