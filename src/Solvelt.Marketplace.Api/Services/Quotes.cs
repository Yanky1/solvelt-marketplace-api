using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

public sealed record QuoteRequestInput(Guid? ListingId, string? Details, string? Location, HoldAddress? Address, string? Phone, IReadOnlyList<DateTimeOffset>? PreferredTimes);
public sealed record QuoteAcceptInput(DateTimeOffset? Start, IReadOnlyList<string>? AddOnIds, IReadOnlyList<IntakeAnswerBody>? Intake, string? Phone);
public sealed record QuoteView(Guid Id, Guid ListingId, string Status, string Location, string? Details, IReadOnlyList<DateTimeOffset> PreferredTimes, string Currency, long? PriceCents,
    int? DurationMinutes, IReadOnlyList<DateTimeOffset> Slots, Guid? ProviderId, DateTimeOffset? ExpiresAt, bool Expired, string? Note, string? DeclineReason, Guid? BookingId, string? FailureCode, DateTimeOffset CreatedAt);

public enum QuoteOutcome { Ok, NotFound, Invalid, IdempotencyConflict, TooMany, Refused, Unavailable }
public sealed record QuoteResult(QuoteOutcome Status, QuoteView? Quote = null, BookingView? Booking = null, bool Created = false, int HttpStatus = 0, string? Code = null, IReadOnlyDictionary<string, string>? Errors = null);

/// <summary>
/// Quote requests (decision M4-13): request, the provider's quote (<c>quote.sent</c>), accept one offered time (creates the booking at the portal), or decline. Same rules as holds: our row exists before
/// the portal is called, every call is idempotent on an id we mint, the buyer's own quote only (another's is 404), and every amount is the portal's.
/// </summary>
public sealed class BuyerQuoteService(MarketplaceDb db, ISignedPortalClient portal, BookingService bookings, PortalEventProcessor events, TimeProvider time, IOptions<BookingOptions> options)
{
    private const string IdAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public static string NewQuoteId() { var b = RandomNumberGenerator.GetBytes(16); var sb = new StringBuilder("QT-"); foreach (var x in b) sb.Append(IdAlphabet[x & 31]); return sb.ToString(); }
    private static string Clean(string? s) => new string((s ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();

    public static Dictionary<string, string> Validate(QuoteRequestInput? r, DateTimeOffset now)
    {
        var e = new Dictionary<string, string>();
        if (r is null) { e["body"] = "Send a JSON body."; return e; }
        if (r.ListingId is null || r.ListingId == Guid.Empty) e["listingId"] = "Choose a service.";
        var loc = Clean(r.Location).ToLowerInvariant();
        if (loc is not ("online" or "customer" or "seller")) e["location"] = "Choose online, at your place or at the provider's.";
        if (Clean(r.Details) is not { Length: >= 10 and <= 2000 }) e["details"] = "Describe the job in 10 to 2000 characters.";
        if (r.Phone is { Length: > 40 }) e["phone"] = "Phone number is too long.";
        if (r.PreferredTimes is { } t && (t.Count > 5 || t.Any(x => x <= now || x > now.AddDays(400)))) e["preferredTimes"] = "Choose up to 5 future times.";
        if (loc == "customer") BookingService.ValidateAddress(r.Address, e);
        return e;
    }

    private static string HashOf(QuoteRequestInput r)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", r.ListingId?.ToString("N"), Clean(r.Location).ToLowerInvariant(), Clean(r.Details), Clean(r.Phone),
            string.Join(",", (r.PreferredTimes ?? []).Select(x => x.UtcTicks)), r.Address is { } a ? string.Join("~", Clean(a.Name), Clean(a.Line1), Clean(a.Line2), Clean(a.City), Clean(a.Region), Clean(a.PostalCode), Clean(a.Country).ToUpperInvariant()) : "")))).ToLowerInvariant();

    public async Task<QuoteResult> RequestAsync(BuyerPrincipal buyer, string relayEmail, string key, QuoteRequestInput? input, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var errors = Validate(input, now);
        if (errors.Count > 0) return new QuoteResult(QuoteOutcome.Invalid, Errors: errors);
        var r = input!;
        var listing = await ServiceListings.Visible(db).Where(l => l.Id == r.ListingId).Select(l => new { l.Id, l.Currency }).FirstOrDefaultAsync(ct);
        if (listing is null) return new QuoteResult(QuoteOutcome.NotFound);
        var hash = HashOf(r);
        var row = await db.Quotes.FirstOrDefaultAsync(q => q.AccountId == buyer.AccountId && q.IdempotencyKey == key, ct);
        var created = false;
        if (row is not null) { if (row.RequestHash != hash) return new QuoteResult(QuoteOutcome.IdempotencyConflict); }
        else
        {
            var open = await db.Quotes.CountAsync(q => q.AccountId == buyer.AccountId && (q.Status == QuoteStatus.Requesting || q.Status == QuoteStatus.Requested || q.Status == QuoteStatus.Quoted), ct);
            if (open >= 10) return new QuoteResult(QuoteOutcome.TooMany);
            var loc = Clean(r.Location).ToLowerInvariant();
            var a = loc == "customer" ? r.Address : null;
            row = new BuyerQuote
            {
                Id = Guid.NewGuid(), MarketplaceQuoteId = NewQuoteId(), AccountId = buyer.AccountId, IdempotencyKey = key, RequestHash = hash, ListingId = listing.Id, Status = QuoteStatus.Requesting, Location = loc,
                Details = Clean(r.Details), PreferredTimesJson = r.PreferredTimes is { Count: > 0 } pt ? JsonSerializer.Serialize(pt.Select(x => x.ToUniversalTime()).ToList()) : null, Currency = listing.Currency,
                Phone = Clean(r.Phone) is { Length: > 0 } ph ? ph : null, CreatedAt = now, UpdatedAt = now,
                AddrName = a is null ? null : Clean(a.Name), AddrLine1 = a is null ? null : Clean(a.Line1), AddrLine2 = a is null || Clean(a.Line2).Length == 0 ? null : Clean(a.Line2), AddrCity = a is null ? null : Clean(a.City),
                AddrRegion = a is null || Clean(a.Region).Length == 0 ? null : Clean(a.Region), AddrPostalCode = a is null ? null : Clean(a.PostalCode), AddrCountry = a is null ? null : Clean(a.Country).ToUpperInvariant(), AddrLat = a?.Lat, AddrLng = a?.Lng,
            };
            db.Quotes.Add(row);
            try { await db.SaveChangesAsync(ct); created = true; }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                row = await db.Quotes.FirstOrDefaultAsync(q => q.AccountId == buyer.AccountId && q.IdempotencyKey == key, ct);
                if (row is null) throw;
                if (row.RequestHash != hash) return new QuoteResult(QuoteOutcome.IdempotencyConflict);
            }
        }
        if (row.Status != QuoteStatus.Requesting) return new QuoteResult(QuoteOutcome.Ok, ToView(row, now), Created: created);
        return await SendRequestAsync(row, relayEmail, buyer.DisplayName, created, ct);
    }

    public async Task<QuoteResult> SendRequestAsync(BuyerQuote row, string relayEmail, string displayName, bool created, CancellationToken ct)
    {
        var addr = row.AddrLine1 is null ? null : new PortalAddress(row.AddrName ?? "", row.AddrLine1, row.AddrLine2, row.AddrCity ?? "", row.AddrRegion, row.AddrPostalCode ?? "", row.AddrCountry ?? "", row.AddrLat, row.AddrLng);
        IReadOnlyList<DateTimeOffset>? times = row.PreferredTimesJson is { } j ? JsonSerializer.Deserialize<List<DateTimeOffset>>(j) : null;
        PortalResult<QuoteCreatedResponse> res;
        try { res = await portal.CreateQuoteRequestAsync(new QuoteRequestBody(row.MarketplaceQuoteId, row.ListingId, new PortalBuyer(RelayIdentity.PublicName(displayName), relayEmail, row.Phone), row.Details, row.Location, addr, times), ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or JsonException) { return new QuoteResult(QuoteOutcome.Unavailable); }
        var now = time.GetUtcNow().ToUniversalTime();
        if (res.IsSuccess && res.Value is { } v && (v.MarketplaceQuoteId is null || v.MarketplaceQuoteId == row.MarketplaceQuoteId))
        {
            var status = v.Status is "Requested" or "Quoted" ? v.Status : QuoteStatus.Requested;
            var n = await db.Quotes.Where(q => q.Id == row.Id && q.Status == QuoteStatus.Requesting).ExecuteUpdateAsync(s => s.SetProperty(q => q.PortalQuoteId, v.QuoteId).SetProperty(q => q.Status, status).SetProperty(q => q.UpdatedAt, now).SetProperty(q => q.Version, q => q.Version + 1), ct);
            db.ChangeTracker.Clear();
            await events.ReplayParkedAsync(QuoteEventHandler.Aggregate, row.MarketplaceQuoteId, ct);
            var fresh = await db.Quotes.AsNoTracking().FirstAsync(q => q.Id == row.Id, ct);
            return new QuoteResult(QuoteOutcome.Ok, ToView(fresh, now), Created: created && n == 1);
        }
        if (res.Retryable || res.Status is 408 or 429 or >= 500) return new QuoteResult(QuoteOutcome.Unavailable);
        var code = Code(res.Error?.Code);
        await db.Quotes.Where(q => q.Id == row.Id && q.Status == QuoteStatus.Requesting).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Failed).SetProperty(q => q.FailureCode, code).SetProperty(q => q.UpdatedAt, now).SetProperty(q => q.Version, q => q.Version + 1), ct);
        return res.Status == 404 ? new QuoteResult(QuoteOutcome.NotFound) : new QuoteResult(QuoteOutcome.Refused, HttpStatus: 422, Code: code);
    }

    private static string Code(string? c) { var s = new string((c ?? "refused").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(60).ToArray()); return s.Length == 0 ? "refused" : s; }

    public QuoteView ToView(BuyerQuote q, DateTimeOffset now)
    {
        static IReadOnlyList<DateTimeOffset> Times(string? j) { try { return j is null ? [] : JsonSerializer.Deserialize<List<DateTimeOffset>>(j) ?? []; } catch (JsonException) { return []; } }
        var status = q.Status == QuoteStatus.Requesting ? QuoteStatus.Requested : q.Status;
        var expired = q.Status == QuoteStatus.Expired || (q.Status == QuoteStatus.Quoted && q.ExpiresAt is { } e && e <= now);
        return new QuoteView(q.Id, q.ListingId, status, q.Location, q.RedactedAt is null ? q.Details : null, Times(q.PreferredTimesJson), q.Currency, q.Status == QuoteStatus.Quoted || q.Status == QuoteStatus.Accepted ? q.PriceCents : null,
            q.DurationMinutes > 0 ? q.DurationMinutes : null, expired ? [] : Times(q.SlotsJson), q.ProviderId, q.ExpiresAt, expired, q.Note, q.DeclineReason, q.BookingId, q.Status == QuoteStatus.Failed ? q.FailureCode : null, q.CreatedAt);
    }

    public async Task<QuoteView?> GetAsync(Guid accountId, Guid id, CancellationToken ct)
        => await db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.AccountId == accountId && q.Status != QuoteStatus.Failed, ct) is { } q ? ToView(q, time.GetUtcNow()) : null;

    public async Task<IReadOnlyList<QuoteView>> ListAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.Quotes.AsNoTracking().Where(q => q.AccountId == accountId && q.Status != QuoteStatus.Failed && q.Status != QuoteStatus.Requesting).OrderByDescending(q => q.CreatedAt).Take(100).ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(q => ToView(q, now)).ToList();
    }

    /// <summary>
    /// Accepts one of the times the provider offered. The portal creates the booking (confirmed, or requested) under its slot lock and the booking must then be PAID within
    /// <c>Bookings:QuotePaymentMinutes</c> or it is cancelled (M4-13). Our booking row exists before the call; the attempt number makes a parallel double click one booking and a retry after a refusal a new attempt.
    /// </summary>
    public async Task<QuoteResult> AcceptAsync(BuyerPrincipal buyer, string relayEmail, Guid quoteId, QuoteAcceptInput? input, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var q = await db.Quotes.FirstOrDefaultAsync(x => x.Id == quoteId && x.AccountId == buyer.AccountId && x.Status != QuoteStatus.Failed && x.Status != QuoteStatus.Requesting, ct);
        if (q is null) return new QuoteResult(QuoteOutcome.NotFound);
        if (q.Status == QuoteStatus.Accepted && q.BookingId is { } done)
            return new QuoteResult(QuoteOutcome.Ok, ToView(q, now), await bookings.GetAsync(buyer.AccountId, done, ct));        // a repeated tap is the same acceptance
        if (q.Status != QuoteStatus.Quoted) return new QuoteResult(QuoteOutcome.Refused, HttpStatus: 409, Code: "quote_not_open");
        if (q.ExpiresAt is { } exp && exp <= now) return new QuoteResult(QuoteOutcome.Refused, HttpStatus: 409, Code: "quote_expired");
        var errors = new Dictionary<string, string>();
        if (input?.Start is not { } start) errors["start"] = "Choose one of the offered times.";
        else if (!Slots(q).Contains(start.ToUniversalTime())) errors["start"] = "That time was not offered.";
        if (input?.AddOnIds is { } a && (a.Count > 20 || a.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100 || x.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))))) errors["addOnIds"] = "Invalid add-ons.";
        if (input?.Intake is { } ia && (ia.Count > 30 || ia.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 60 || (x.Answer ?? "").Length > 2000))) errors["intake"] = "Check your answers.";
        if (errors.Count > 0) return new QuoteResult(QuoteOutcome.Invalid, Errors: errors);

        var attempt = await db.Bookings.CountAsync(b => b.QuoteId == q.Id && b.Status == BookingStatus.Failed, ct) + 1;
        var key = $"quote-accept:{q.Id:N}:{attempt}";
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.AccountId == buyer.AccountId && b.IdempotencyKey == key, ct);
        if (booking is null)
        {
            booking = new Booking
            {
                Id = Guid.NewGuid(), MarketplaceBookingId = BookingService.NewMarketplaceBookingId(), AccountId = buyer.AccountId, IdempotencyKey = key, RequestHash = q.MarketplaceQuoteId, ListingId = q.ListingId, ProviderId = q.ProviderId,
                Status = BookingStatus.HoldPending, Location = q.Location, StartAt = input!.Start!.Value.ToUniversalTime(), DurationMinutes = q.DurationMinutes, Currency = q.Currency, QuoteId = q.Id,
                AddOnIds = string.Join(",", (input.AddOnIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), CreatedAt = now, UpdatedAt = now,
                AddrName = q.AddrName, AddrLine1 = q.AddrLine1, AddrLine2 = q.AddrLine2, AddrCity = q.AddrCity, AddrRegion = q.AddrRegion, AddrPostalCode = q.AddrPostalCode, AddrCountry = q.AddrCountry, AddrLat = q.AddrLat, AddrLng = q.AddrLng,
                Phone = BookingService.Clean(input.Phone) is { Length: > 0 } p ? p : q.Phone, IntakeJson = input.Intake is { Count: > 0 } ? JsonSerializer.Serialize(input.Intake, PortalJson.Options) : null,
            };
            db.Bookings.Add(booking);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();     // a parallel accept inserted the same attempt: it is the same booking
                booking = await db.Bookings.FirstOrDefaultAsync(b => b.AccountId == buyer.AccountId && b.IdempotencyKey == key, ct) ?? throw new InvalidOperationException("quote booking missing");
            }
        }
        if (booking.Status != BookingStatus.HoldPending) return new QuoteResult(QuoteOutcome.Ok, ToView(await db.Quotes.AsNoTracking().FirstAsync(x => x.Id == q.Id, ct), now), bookings.ToView(booking, now));

        var addr = booking.AddrLine1 is null ? null : new PortalAddress(booking.AddrName ?? "", booking.AddrLine1, booking.AddrLine2, booking.AddrCity ?? "", booking.AddrRegion, booking.AddrPostalCode ?? "", booking.AddrCountry ?? "", booking.AddrLat, booking.AddrLng);
        PortalResult<BookingResponse> res;
        try
        {
            res = await portal.AcceptQuoteAsync(q.MarketplaceQuoteId, new QuoteAcceptBody(booking.MarketplaceBookingId, booking.StartAt!.Value, booking.AddOnIds.Length == 0 ? [] : booking.AddOnIds.Split(','),
                new PortalBuyer(RelayIdentity.PublicName(buyer.DisplayName), relayEmail, booking.Phone), input!.Intake), ct);
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or JsonException) { return new QuoteResult(QuoteOutcome.Unavailable); }
        now = time.GetUtcNow().ToUniversalTime();
        if (res.IsSuccess && res.Value?.Booking is { } sb && string.Equals(sb.MarketplaceBookingId, booking.MarketplaceBookingId, StringComparison.Ordinal))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await bookings.ApplySummaryAsync(booking, sb, now, ct);
            var due = now.AddMinutes(Math.Clamp(options.Value.QuotePaymentMinutes, 5, 24 * 60));
            await db.Bookings.Where(b => b.Id == booking.Id && b.PaymentDueAt == null).ExecuteUpdateAsync(s => s.SetProperty(b => b.PaymentDueAt, due), ct);
            await db.Quotes.Where(x => x.Id == q.Id && x.Status == QuoteStatus.Quoted).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Accepted).SetProperty(x => x.BookingId, booking.Id).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            await events.ReplayParkedAsync(BookingEventHandler.Aggregate, booking.MarketplaceBookingId, ct);
            var fresh = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id, ct);
            return new QuoteResult(QuoteOutcome.Ok, ToView(await db.Quotes.AsNoTracking().FirstAsync(x => x.Id == q.Id, ct), now), bookings.ToView(fresh, now), Created: res.Value.Created);
        }
        if (res.Retryable || res.Status is 408 or 429 or >= 500) return new QuoteResult(QuoteOutcome.Unavailable);
        var code = Code(res.Error?.Code);
        await db.Bookings.Where(b => b.Id == booking.Id && b.Status == BookingStatus.HoldPending).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Failed).SetProperty(b => b.FailureCode, code).SetProperty(b => b.UpdatedAt, now).SetProperty(b => b.Version, b => b.Version + 1), ct);
        if (code is "quote_expired") await db.Quotes.Where(x => x.Id == q.Id && x.Status == QuoteStatus.Quoted).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Expired).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return res.Status switch
        {
            404 => new QuoteResult(QuoteOutcome.NotFound),
            409 => new QuoteResult(QuoteOutcome.Refused, HttpStatus: 409, Code: code),
            _ => new QuoteResult(QuoteOutcome.Refused, HttpStatus: 422, Code: code),
        };
    }

    private static IReadOnlyList<DateTimeOffset> Slots(BuyerQuote q)
    {
        try { return q.SlotsJson is null ? [] : (JsonSerializer.Deserialize<List<DateTimeOffset>>(q.SlotsJson) ?? []).Select(x => x.ToUniversalTime()).ToList(); } catch (JsonException) { return []; }
    }

    public async Task<QuoteResult> DeclineAsync(Guid accountId, Guid quoteId, string? reason, CancellationToken ct)
    {
        var q = await db.Quotes.FirstOrDefaultAsync(x => x.Id == quoteId && x.AccountId == accountId && x.Status != QuoteStatus.Failed && x.Status != QuoteStatus.Requesting, ct);
        if (q is null) return new QuoteResult(QuoteOutcome.NotFound);
        if (q.Status == QuoteStatus.Withdrawn) return new QuoteResult(QuoteOutcome.Ok, ToView(q, time.GetUtcNow()));
        if (!QuoteStatus.IsOpen(q.Status)) return new QuoteResult(QuoteOutcome.Refused, HttpStatus: 409, Code: "quote_not_open");
        if (reason is { Length: > 500 }) return new QuoteResult(QuoteOutcome.Invalid, Errors: new Dictionary<string, string> { ["reason"] = "Use at most 500 characters." });
        try
        {
            var res = await portal.DeclineQuoteAsync(q.MarketplaceQuoteId, new QuoteDeclineBody(string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), ct);
            if (!res.IsSuccess)
            {
                if (res.Retryable || res.Status is 408 or 429 or >= 500) return new QuoteResult(QuoteOutcome.Unavailable);
                return new QuoteResult(QuoteOutcome.Refused, HttpStatus: res.Status == 409 ? 409 : 422, Code: Code(res.Error?.Code));
            }
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or JsonException) { return new QuoteResult(QuoteOutcome.Unavailable); }
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Quotes.Where(x => x.Id == q.Id && (x.Status == QuoteStatus.Requested || x.Status == QuoteStatus.Quoted)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Withdrawn).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return new QuoteResult(QuoteOutcome.Ok, ToView(await db.Quotes.AsNoTracking().FirstAsync(x => x.Id == q.Id, ct), now));
    }
}

/// <summary>
/// <c>quote.sent</c>, <c>quote.declined</c>, <c>quote.expired</c> (decision M4-13). Matched on OUR <c>QT-…</c> id AND the portal quote id; an event that beats the request's answer is parked and replayed.
/// A re-sent quote replaces the numbers while the request is open (ordered by the portal's time); Accepted, Declined, Withdrawn and Expired are absorbing.
/// </summary>
public sealed class QuoteEventHandler(IOptions<MarketplaceOptions> marketplace) : IProjectionHandler
{
    public const string Sent = "quote.sent", Declined = "quote.declined", Expired = "quote.expired", Aggregate = "quote";
    public IReadOnlyCollection<string> EventTypes { get; } = [Sent, Declined, Expired];

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var data = c.Event.Data;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("quote", out var qr) || qr.ValueKind != JsonValueKind.Object) return HandlerOutcome.Skipped("quote reference missing");
        var mid = Str(qr, "marketplaceQuoteId");
        if (string.IsNullOrWhiteSpace(mid) || !Guid.TryParse(Str(qr, "quoteId"), out var portalId)) return HandlerOutcome.Skipped("quote reference missing");
        var q = await c.Db.Quotes.FirstOrDefaultAsync(x => x.MarketplaceQuoteId == mid, ct);
        if (q is null) return HandlerOutcome.Skipped("unknown marketplace quote");
        if (q.PortalQuoteId is null) return HandlerOutcome.Parked(Aggregate, q.MarketplaceQuoteId, "portal quote id not stored yet");
        if (q.PortalQuoteId != portalId) return HandlerOutcome.Skipped("portal quote id does not match");
        // The buyer's own accept / withdraw is applied locally (no portal event carries it), so a finished quote must not be reopened by a late provider event.
        if (QuoteStatus.IsTerminal(q.Status)) return HandlerOutcome.Skipped("the quote is finished");
        var now = c.Time.GetUtcNow().ToUniversalTime();
        switch (c.Event.Type)
        {
            case Sent:
            {
                if (!await c.AdvanceAsync(Aggregate, q.MarketplaceQuoteId, "answer", null, ct)) return HandlerOutcome.Skipped("older, or the quote is finished");
                var price = Money(data, "priceCents");
                var dur = data.TryGetProperty("durationMinutes", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dm) && dm is > 0 and <= 24 * 60 ? dm : 0;
                var slots = new List<DateTimeOffset>();
                if (data.TryGetProperty("slots", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    foreach (var s in ss.EnumerateArray().Take(10)) if (s.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) slots.Add(t.ToUniversalTime());
                if (price is null || dur == 0 || slots.Count == 0) return HandlerOutcome.Skipped("incomplete quote");
                q.Status = QuoteStatus.Quoted; q.PriceCents = price.Value; q.DurationMinutes = dur; q.SlotsJson = JsonSerializer.Serialize(slots.Order().ToList());
                q.ProviderId = Guid.TryParse(Str(data, "providerId"), out var pid) ? pid : q.ProviderId; q.ExpiresAt = Time(data, "expiresAt"); q.Note = Cap(Str(data, "note"), 1000);
                q.UpdatedAt = now; q.Version++;
                var email = await c.Db.BuyerAccounts.AsNoTracking().Where(a => a.Id == q.AccountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
                if (email is not null)
                {
                    var title = await c.Db.CatalogListings.AsNoTracking().Where(l => l.Id == q.ListingId).Select(l => l.Title).FirstOrDefaultAsync(ct) ?? "your request";
                    var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
                    new OutboxWriter(c.Db, c.Time).EnqueueEmail(new EmailMessage(email, "You have a quote", $"Hello,\n\nThe provider answered your request for \"{title}\". Review the price and choose a time:\n\n{baseUrl}/account/quotes/{q.Id}\n"), "mail:quote:" + c.Event.Id);
                }
                return HandlerOutcome.Applied();
            }
            case Declined:
                if (!await c.AdvanceAsync(Aggregate, q.MarketplaceQuoteId, "status", "declined", ct)) return HandlerOutcome.Skipped("older, or the quote is finished");
                q.Status = QuoteStatus.Declined; q.DeclineReason = Cap(Str(data, "reason"), 500); q.UpdatedAt = now; q.Version++;
                return HandlerOutcome.Applied();
            case Expired:
                if (!await c.AdvanceAsync(Aggregate, q.MarketplaceQuoteId, "status", "expired", ct)) return HandlerOutcome.Skipped("older, or the quote is finished");
                q.Status = QuoteStatus.Expired; q.UpdatedAt = now; q.Version++;
                return HandlerOutcome.Applied();
            default: return HandlerOutcome.Skipped("not a quote event we project");
        }
    }

    private static string? Str(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Cap(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
    private static DateTimeOffset? Time(JsonElement d, string n) => Str(d, n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    private static long? Money(JsonElement d, string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l is >= 0 and <= 100_000_000_00 ? l : null;
}

public static class QuoteEndpoints
{
    public static IEndpointRouteBuilder MapQuotes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/quotes").RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("", Request);
        g.MapGet("", async (HttpContext ctx, BuyerQuoteService svc, CancellationToken ct) => Private(ctx, Results.Json(new { quotes = await svc.ListAsync(ctx.Buyer().AccountId, ct) })));
        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, BuyerQuoteService svc, CancellationToken ct) => await svc.GetAsync(ctx.Buyer().AccountId, id, ct) is { } v ? Private(ctx, Results.Json(v)) : Problems.NotFound());
        g.MapPost("/{id:guid}/accept", Accept);
        g.MapPost("/{id:guid}/decline", async (Guid id, QuoteDeclineInput? body, HttpContext ctx, BuyerQuoteService svc, CancellationToken ct) => Reply(ctx, await svc.DeclineAsync(ctx.Buyer().AccountId, id, body?.Reason, ct)));
        return app;
    }

    public sealed record QuoteDeclineInput(string? Reason);
    private static IResult Private(HttpContext ctx, IResult r) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static string? KeyOf(HttpContext ctx)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString().Trim();
        return key.Length is >= 8 and <= 100 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':') ? key : null;
    }

    private static async Task<IResult> Request(QuoteRequestInput? body, HttpContext ctx, BuyerQuoteService svc, MarketplaceDb db, CancellationToken ct)
    {
        if (KeyOf(ctx) is not { } key) return Problems.Validation(new Dictionary<string, string> { ["Idempotency-Key"] = "Send an Idempotency-Key header (8 to 100 letters, digits, - _ . :)." });
        var buyer = ctx.Buyer();
        var relay = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == buyer.AccountId && a.DeletedAt == null).Select(a => a.RelayEmail).FirstOrDefaultAsync(ct);
        if (relay is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        return Reply(ctx, await svc.RequestAsync(buyer, relay, key, body, ct));
    }

    private static async Task<IResult> Accept(Guid id, QuoteAcceptInput? body, HttpContext ctx, BuyerQuoteService svc, MarketplaceDb db, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var relay = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == buyer.AccountId && a.DeletedAt == null).Select(a => a.RelayEmail).FirstOrDefaultAsync(ct);
        if (relay is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        return Reply(ctx, await svc.AcceptAsync(buyer, relay, id, body, ct));
    }

    private static IResult Reply(HttpContext ctx, QuoteResult r)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        return r.Status switch
        {
            QuoteOutcome.Ok => Results.Json(new { created = r.Created, quote = r.Quote, booking = r.Booking }, statusCode: r.Created ? 201 : 200),
            QuoteOutcome.NotFound => Problems.NotFound("This quote or service is not available."),
            QuoteOutcome.Invalid => Problems.Validation(r.Errors!),
            QuoteOutcome.IdempotencyConflict => Problems.Json(409, "idempotency_conflict", "That Idempotency-Key was already used for a different request."),
            QuoteOutcome.TooMany => Problems.Unprocessable("too_many_quotes", "You have several open quote requests. Wait for answers or withdraw one first."),
            QuoteOutcome.Refused => Problems.Json(r.HttpStatus, r.Code!, Message(r.Code)),
            _ => Problems.Json(503, "quote_unavailable", "We couldn't reach the provider right now. Please try again in a moment."),
        };
    }

    private static string Message(string? code) => code switch
    {
        "quote_expired" => "This quote has expired. Ask for a new one.",
        "quote_not_open" => "This quote can't be answered any more.",
        "slot_not_offered" => "That time was not part of the quote.",
        "slot_unavailable" => "That time was just taken. Please choose another offered time.",
        "outside_service_area" => "This provider doesn't serve that address.",
        _ => "That couldn't be done. Please check your choices and try again.",
    };
}
