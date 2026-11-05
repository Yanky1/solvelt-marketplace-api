using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// Booking upkeep (decision M4-05). (1) A hold whose portal answer never arrived is looked up with <c>GET /bookings/{mid}</c>: found means the answer was lost, so it is applied
/// (and the events that were parked behind it replayed); a 404 means the portal never got it, so it is closed as failed; an outage leaves it for the next run, and after a day it is
/// closed as unresolved (a hold lasts 10 minutes, so a late answer would be useless). (2) A hold that should have expired (the portal's <c>booking.hold_expired</c> event is overdue)
/// is re-read the same way, so a lost event cannot keep a booking "held" forever. (3) Retention: personal data is wiped <c>Bookings:RedactAfterDays</c> (30, as the portal does,
/// SERVICES.md §9) after a booking ended, and a day after a hold attempt failed; money, ids and the policy numbers stay.
/// </summary>
public sealed class BookingMaintenance(MarketplaceDb db, ISignedPortalClient portal, BookingService bookings, BuyerQuoteService quotes, BookingRefundService refunds, BookingsAccountEraser eraser, PortalEventProcessor events, TimeProvider time, IOptions<BookingOptions> options, ILogger<BookingMaintenance> log)
{
    public const int Batch = 50;
    public sealed record Result(int Resolved, int Failed, int Redacted, int Cancelled = 0, int Released = 0, int Erased = 0);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        int resolved = 0, failed = 0;

        var stalePending = now.AddSeconds(-Math.Clamp(options.Value.PendingResolveAfterSeconds, 5, 3600));
        var overdueHold = now.AddMinutes(-2);
        var ids = await db.Bookings.AsNoTracking()
            .Where(b => (b.Status == BookingStatus.HoldPending && b.UpdatedAt <= stalePending) || (b.Status == BookingStatus.Hold && b.HoldExpiresAt != null && b.HoldExpiresAt <= overdueHold))
            .OrderBy(b => b.UpdatedAt).Select(b => b.Id).Take(Batch).ToListAsync(ct);
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var row = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (row is null || (row.Status != BookingStatus.HoldPending && row.Status != BookingStatus.Hold)) continue;
            PortalResult<PortalBookingSummary> r;
            try { r = await portal.GetBookingAsync(row.MarketplaceBookingId, ct); }
            catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException)
            { log.LogWarning("Booking lookup unavailable: {ExceptionType}", ex.GetType().Name); break; }   // the portal is down: stop, try again next run
            if (r.IsSuccess && r.Value is { } s && string.Equals(s.MarketplaceBookingId, row.MarketplaceBookingId, StringComparison.Ordinal) && (s.ServiceId is null || s.ServiceId == row.ListingId))
            {
                await bookings.ApplySummaryAsync(row, s, now, ct);
                if (row.Status == BookingStatus.Expired) await QuoteReopen.ReopenAsync(db, row, now, ct);        // an overdue quote hold read as lapsed (portal C13)
                await events.ReplayParkedAsync(BookingEventHandler.Aggregate, row.MarketplaceBookingId, ct);
                resolved++;
            }
            else if (r.Status == 404 && row.Status == BookingStatus.HoldPending)
            {
                await Fail(row, "not_found", now, ct); failed++;
            }
            else if (row.Status == BookingStatus.HoldPending && row.CreatedAt <= now.AddDays(-1))
            {
                await Fail(row, "unresolved", now, ct); failed++;
            }
        }
        db.ChangeTracker.Clear();
        var cancelled = await CancelUnpaidQuoteBookingsAsync(now, ct);
        await ResolveRequestingQuotesAsync(now, ct);
        return new Result(resolved, failed, await RedactAsync(now, ct), cancelled, await ReleaseDeferredAsync(ct), await EraseDeletedAccountsAsync(ct));
    }

    /// <summary>
    /// A booking made by accepting a quote is confirmed by the portal at once. If it is not paid by <c>PaymentDueAt</c> it is cancelled at the portal (reason <c>payment_not_completed</c>, request id derived from our
    /// booking id), so a provider's calendar is never blocked by someone who did not pay. A booking that has a captured payment is left alone (its timer is cleared when the payment is reported).
    /// </summary>
    private async Task<int> CancelUnpaidQuoteBookingsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ids = await db.Bookings.AsNoTracking().Where(b => b.PaymentDueAt != null && b.PaymentDueAt <= now && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Requested || b.Status == BookingStatus.Rescheduled))
            .OrderBy(b => b.PaymentDueAt).Select(b => b.Id).Take(Batch).ToListAsync(ct);
        var n = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var row = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (row is null || row.PaymentDueAt is null) continue;
            if (await db.BookingPayments.AnyAsync(p => p.BookingId == id && p.Kind == "booking" && p.Status == BookingPaymentStatus.Captured, ct)) continue;   // paid: the confirm handler clears the timer
            PortalResult<PortalBookingSummary> r;
            try { r = await portal.CancelBookingAsync(row.MarketplaceBookingId, new BookingCancelBody("cancel-unpaid-" + row.MarketplaceBookingId, "payment_not_completed"), ct); }
            catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException) { break; }
            if (r.IsSuccess && r.Value is { } s && string.Equals(s.MarketplaceBookingId, row.MarketplaceBookingId, StringComparison.Ordinal))
            {
                await bookings.ApplySummaryAsync(row, s, now, ct);
                await db.Bookings.Where(b => b.Id == id).ExecuteUpdateAsync(b => b.SetProperty(x => x.PaymentDueAt, (DateTimeOffset?)null), ct);
                n++;
            }
            else if (r.Status == 409 || r.Status == 404) await db.Bookings.Where(b => b.Id == id).ExecuteUpdateAsync(b => b.SetProperty(x => x.PaymentDueAt, (DateTimeOffset?)null), ct);   // already over at the portal: stop asking
        }
        db.ChangeTracker.Clear();
        return n;
    }

    /// <summary>A quote request whose portal answer never came: sent again (idempotent on our quote id at the portal), or closed as failed after a day.</summary>
    private async Task ResolveRequestingQuotesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stale = now.AddSeconds(-Math.Clamp(options.Value.PendingResolveAfterSeconds, 5, 3600));
        var rows = await db.Quotes.AsNoTracking().Where(q => q.Status == QuoteStatus.Requesting && q.UpdatedAt <= stale).OrderBy(q => q.UpdatedAt).Take(Batch).ToListAsync(ct);
        foreach (var q in rows)
        {
            db.ChangeTracker.Clear();
            var who = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == q.AccountId && a.DeletedAt == null).Select(a => new { a.DisplayName, a.RelayEmail }).FirstOrDefaultAsync(ct);
            if (who is null) { await db.Quotes.Where(x => x.Id == q.Id && x.Status == QuoteStatus.Requesting).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Failed).SetProperty(x => x.FailureCode, "account_deleted").SetProperty(x => x.UpdatedAt, now), ct); continue; }
            var r = await quotes.SendRequestAsync(q, who.RelayEmail, who.DisplayName, false, ct);
            if (r.Status == QuoteOutcome.Unavailable && q.CreatedAt <= now.AddDays(-1))
                await db.Quotes.Where(x => x.Id == q.Id && x.Status == QuoteStatus.Requesting).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Failed).SetProperty(x => x.FailureCode, "unresolved").SetProperty(x => x.UpdatedAt, now), ct);
        }
        db.ChangeTracker.Clear();
    }

    private async Task Fail(Booking row, string code, DateTimeOffset now, CancellationToken ct)
    {
        var v = row.Version;
        await db.Bookings.Where(b => b.Id == row.Id && b.Version == v && b.Status == BookingStatus.HoldPending)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Failed).SetProperty(b => b.FailureCode, code).SetProperty(b => b.UpdatedAt, now).SetProperty(b => b.Version, v + 1), ct);
    }

    /// <summary>
    /// Deferred refunds (owed, held behind a chargeback) whose payment no longer has a dispute holding money are released: normally the dispute decision does it, this catches one whose release raced a
    /// refund and found nothing (decision M4-22). Idempotent: a released row is no longer deferred.
    /// </summary>
    public async Task<int> ReleaseDeferredAsync(CancellationToken ct)
    {
        var ids = await db.BookingRefunds.AsNoTracking().Where(r => r.Status == RefundStatus.Deferred && db.BookingPayments.Any(p => p.Id == r.PaymentId && p.DisputedCents == 0))
            .Select(r => r.PaymentId).Distinct().Take(Batch).ToListAsync(ct);
        foreach (var id in ids) { db.ChangeTracker.Clear(); await refunds.ReleaseDeferredAsync(id, ct); }
        return ids.Count;
    }

    /// <summary>
    /// A deleted account's bookings that were still open when it was deleted keep their data until they finish (decision O-23); once finished they are wiped here, without waiting for the
    /// retention days. Idempotent: a redacted row is no longer selected.
    /// </summary>
    public async Task<int> EraseDeletedAccountsAsync(CancellationToken ct)
    {
        string[] bookingDone = [.. BookingStatus.Terminal], quoteDone = [.. QuoteStatus.Terminal];
        var ids = await db.BuyerAccounts.AsNoTracking().Where(a => a.DeletedAt != null && (
                db.Bookings.Any(b => b.AccountId == a.Id && b.RedactedAt == null && bookingDone.Contains(b.Status)) ||
                db.Quotes.Any(q => q.AccountId == a.Id && q.RedactedAt == null && quoteDone.Contains(q.Status))))
            .Select(a => a.Id).Take(Batch).ToListAsync(ct);
        foreach (var id in ids) await eraser.EraseAsync(id, ct);
        return ids.Count;
    }

    /// <summary>Wipes the personal data of finished bookings and of failed hold attempts; idempotent (a redacted row is skipped).</summary>
    public async Task<int> RedactAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ended = now.AddDays(-Math.Clamp(options.Value.RedactAfterDays, 1, 3650));
        var failedBefore = now.AddDays(-1);
        string[] terminal = [.. BookingStatus.Terminal];
        string[] quoteTerminal = [.. QuoteStatus.Terminal];
        var quotesRedacted = await db.Quotes.Where(q => q.RedactedAt == null && ((q.Status == QuoteStatus.Failed && q.UpdatedAt <= failedBefore) || (q.Status != QuoteStatus.Failed && quoteTerminal.Contains(q.Status) && q.UpdatedAt <= ended)))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Details, "").SetProperty(q => q.PreferredTimesJson, (string?)null).SetProperty(q => q.Note, (string?)null).SetProperty(q => q.DeclineReason, (string?)null)
                .SetProperty(q => q.AddrName, (string?)null).SetProperty(q => q.AddrLine1, (string?)null).SetProperty(q => q.AddrLine2, (string?)null).SetProperty(q => q.AddrCity, (string?)null).SetProperty(q => q.AddrRegion, (string?)null)
                .SetProperty(q => q.AddrPostalCode, (string?)null).SetProperty(q => q.AddrCountry, (string?)null).SetProperty(q => q.AddrLat, (double?)null).SetProperty(q => q.AddrLng, (double?)null).SetProperty(q => q.Phone, (string?)null)
                .SetProperty(q => q.RedactedAt, now), ct);
        return quotesRedacted + await db.Bookings.Where(b => b.RedactedAt == null && (
                (b.Status == BookingStatus.Failed && b.UpdatedAt <= failedBefore) ||
                (b.Status != BookingStatus.Failed && terminal.Contains(b.Status) && b.UpdatedAt <= ended)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.AddrName, (string?)null).SetProperty(b => b.AddrLine1, (string?)null).SetProperty(b => b.AddrLine2, (string?)null)
                .SetProperty(b => b.AddrCity, (string?)null).SetProperty(b => b.AddrRegion, (string?)null).SetProperty(b => b.AddrPostalCode, (string?)null)
                .SetProperty(b => b.AddrCountry, (string?)null).SetProperty(b => b.AddrLat, (double?)null).SetProperty(b => b.AddrLng, (double?)null)
                .SetProperty(b => b.Phone, (string?)null).SetProperty(b => b.IntakeJson, (string?)null).SetProperty(b => b.OnlineInstructions, (string?)null)
                .SetProperty(b => b.RedactedAt, now), ct);
    }
}

public sealed class BookingWorker(IServiceScopeFactory scopes, ILogger<BookingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Booking maintenance failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
