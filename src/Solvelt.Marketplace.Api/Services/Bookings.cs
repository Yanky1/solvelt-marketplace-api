using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

public sealed record HoldAddress(string? Name, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, double? Lat, double? Lng);
/// <summary>The hold request. There is deliberately no price, total or end time: the slot's start is the only time the client names, and the portal validates it.</summary>
public sealed record HoldRequest(Guid? ListingId, Guid? ProviderId, DateTimeOffset? Start, int? DurationMinutes, string? Location, IReadOnlyList<string>? AddOnIds, HoldAddress? Address, string? Phone);

public sealed record BookingView(Guid Id, string? Number, Guid ListingId, string Status, string Location, DateTimeOffset? StartAt, DateTimeOffset? EndAt, int DurationMinutes,
    string Currency, long PriceCents, long AddOnsCents, long TravelFeeCents, long TotalCents, DateTimeOffset? HoldExpiresAt, bool HoldExpired, DateTimeOffset? ApprovalDeadline,
    DateTimeOffset? ProposedStartAt, long CancellationFeeCents, long RefundDueCents, long NoShowFeeCents, long AdditionalChargeCents, int RescheduleCount, IReadOnlyList<string> AddOnIds,
    string? OnlineInstructions, string? FailureCode, DateTimeOffset CreatedAt, bool RequiresBuyerConsent = false, DateTimeOffset? RescheduleRequestedStartAt = null, string? ExtraChargeState = null);

public enum HoldStatus { Ok, NotFound, Invalid, IdempotencyConflict, TooManyHolds, SlotUnavailable, PortalRefused, Unavailable }
public sealed record HoldResult(HoldStatus Status, BookingView? Booking = null, bool Created = false, string? Code = null, IReadOnlyDictionary<string, string>? Errors = null);

public sealed class BookingOptions
{
    public int MaxActiveHolds { get; set; } = 3;
    /// <summary>A hold whose portal answer never came (we crashed or the portal was down) is looked up again after this long.</summary>
    public int PendingResolveAfterSeconds { get; set; } = 60;
    /// <summary>Personal data of a finished booking is wiped this long after it ended (the portal does the same, SERVICES.md §9).</summary>
    public int RedactAfterDays { get; set; } = 30;
}

public static class BookingStatuses
{
    /// <summary>The meeting link is revealed only while the buyer's booking is confirmed (decision M4-04).</summary>
    public static bool RevealsMeetingLink(string status) => status is BookingStatus.Confirmed or BookingStatus.Rescheduled or BookingStatus.InProgress;
}

public sealed class BookingService(MarketplaceDb db, ISignedPortalClient portal, Portal.Webhooks.PortalEventProcessor events, TimeProvider time, IOptions<BookingOptions> options, ILogger<BookingService> log)
{
    private static readonly string[] Locations = ["online", "customer", "seller"];
    private const string IdAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewMarketplaceBookingId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder("BK-", 3 + 16);
        foreach (var b in bytes) sb.Append(IdAlphabet[b & 31]);
        return sb.ToString();
    }

    public static string Clean(string? s) => (s ?? "").Trim();

    public static Dictionary<string, string> Validate(HoldRequest? r, DateTimeOffset now)
    {
        var e = new Dictionary<string, string>();
        if (r is null) { e["body"] = "Send a JSON body."; return e; }
        if (r.ListingId is null || r.ListingId == Guid.Empty) e["listingId"] = "Choose a service.";
        var loc = Clean(r.Location).ToLowerInvariant();
        if (!Locations.Contains(loc)) e["location"] = "Choose online, at your place or at the provider's.";
        if (r.Start is not { } s || s <= now || s > now.AddDays(400)) e["start"] = "Choose one of the available times.";
        if (r.DurationMinutes is { } d && (d <= 0 || d > 24 * 60)) e["durationMinutes"] = "Invalid duration.";
        if (r.AddOnIds is { } ids && (ids.Count > 20 || ids.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100 || x.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))))) e["addOnIds"] = "Invalid add-ons.";
        if (r.Phone is { Length: > 40 }) e["phone"] = "Phone number is too long.";
        if (loc == "customer")
        {
            var a = r.Address;
            if (a is null || Clean(a.Line1).Length is 0 or > 200 || Clean(a.City).Length is 0 or > 100 || Clean(a.PostalCode).Length is 0 or > 20 || Clean(a.Country).Length != 2 || Clean(a.Name).Length > 200
                || Clean(a.Line2).Length > 200 || Clean(a.Region).Length > 100)
                e["address"] = "Enter the address where the service takes place.";
            else if ((a.Lat is null) != (a.Lng is null) || a.Lat is < -90 or > 90 || a.Lng is < -180 or > 180) e["address"] = "The map position is not valid.";
        }
        return e;
    }

    /// <summary>Everything the key stands for. The same key for a different slot, service, place or add-on set is a client bug and is refused (never answered with the old hold).</summary>
    public static string HashOf(HoldRequest r)
    {
        var a = r.Address;
        var text = string.Join("|", r.ListingId?.ToString("N"), r.ProviderId?.ToString("N"), r.Start?.UtcTicks.ToString(CultureInfo.InvariantCulture), r.DurationMinutes?.ToString(CultureInfo.InvariantCulture),
            Clean(r.Location).ToLowerInvariant(), string.Join(",", (r.AddOnIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            a is null ? "" : string.Join("~", Clean(a.Name), Clean(a.Line1), Clean(a.Line2), Clean(a.City), Clean(a.Region), Clean(a.PostalCode), Clean(a.Country).ToUpperInvariant(), a.Lat?.ToString("R", CultureInfo.InvariantCulture), a.Lng?.ToString("R", CultureInfo.InvariantCulture)),
            Clean(r.Phone));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Reserves a slot for 10 minutes. Order of work (decision M4-02): validate; find the buyer's own row for this <c>Idempotency-Key</c>; cap the buyer's open holds;
    /// INSERT our row (<c>HoldPending</c>, unique on (account, key)) and only then call the portal with our <c>BK-…</c> id; store the portal's answer. A retry with the same key
    /// reaches the same row and the same portal id, so nothing is created twice; a refusal (slot taken: <b>exactly one buyer wins, the portal decides</b>) marks our row
    /// <c>Failed</c>, which no list shows as held.
    /// </summary>
    public async Task<HoldResult> HoldAsync(BuyerPrincipal buyer, string relayEmail, string idempotencyKey, HoldRequest? request, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var errors = Validate(request, now);
        if (errors.Count > 0) return new HoldResult(HoldStatus.Invalid, Errors: errors);
        var r = request!;
        var listing = await db.CatalogListings.AsNoTracking().Where(l => l.Id == r.ListingId && l.Type == ListingTypes.Service && l.Visible).Select(l => new { l.Id, l.Currency }).FirstOrDefaultAsync(ct);
        if (listing is null) return new HoldResult(HoldStatus.NotFound);

        var hash = HashOf(r);
        var row = await db.Bookings.FirstOrDefaultAsync(b => b.AccountId == buyer.AccountId && b.IdempotencyKey == idempotencyKey, ct);
        var created = false;
        if (row is not null)
        {
            if (row.RequestHash != hash) return new HoldResult(HoldStatus.IdempotencyConflict);
        }
        else
        {
            var open = await db.Bookings.CountAsync(b => b.AccountId == buyer.AccountId && (b.Status == BookingStatus.HoldPending || (b.Status == BookingStatus.Hold && (b.HoldExpiresAt == null || b.HoldExpiresAt > now))), ct);
            if (open >= Math.Clamp(options.Value.MaxActiveHolds, 1, 20)) return new HoldResult(HoldStatus.TooManyHolds);
            var loc = Clean(r.Location).ToLowerInvariant();
            var a = loc == "customer" ? r.Address : null;
            row = new Booking
            {
                Id = Guid.NewGuid(), MarketplaceBookingId = NewMarketplaceBookingId(), AccountId = buyer.AccountId, IdempotencyKey = idempotencyKey, RequestHash = hash, ListingId = listing.Id,
                ProviderId = r.ProviderId, Status = BookingStatus.HoldPending, Location = loc, StartAt = r.Start!.Value.ToUniversalTime(), DurationMinutes = r.DurationMinutes ?? 0, Currency = listing.Currency,
                AddOnIds = string.Join(",", (r.AddOnIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), CreatedAt = now, UpdatedAt = now,
                AddrName = a is null ? null : Clean(a.Name), AddrLine1 = a is null ? null : Clean(a.Line1), AddrLine2 = a is null || Clean(a.Line2).Length == 0 ? null : Clean(a.Line2),
                AddrCity = a is null ? null : Clean(a.City), AddrRegion = a is null || Clean(a.Region).Length == 0 ? null : Clean(a.Region), AddrPostalCode = a is null ? null : Clean(a.PostalCode),
                AddrCountry = a is null ? null : Clean(a.Country).ToUpperInvariant(), AddrLat = a?.Lat, AddrLng = a?.Lng, Phone = Clean(r.Phone) is { Length: > 0 } p ? p : null,
            };
            db.Bookings.Add(row);
            try { await db.SaveChangesAsync(ct); created = true; }
            catch (DbUpdateException)
            {
                // A parallel request with the same key won the unique index: answer with ITS row.
                db.ChangeTracker.Clear();
                row = await db.Bookings.FirstOrDefaultAsync(b => b.AccountId == buyer.AccountId && b.IdempotencyKey == idempotencyKey, ct);
                if (row is null) throw;
                if (row.RequestHash != hash) return new HoldResult(HoldStatus.IdempotencyConflict);
            }
        }

        if (row.Status == BookingStatus.Failed)
        {
            // The same key again after the portal refused it answers what it answered the first time (never a 200 for a hold that does not exist). A new attempt needs a new key.
            var failed = row.FailureCode ?? "refused";
            return failed switch
            {
                "slot_unavailable" or "no_provider" => new HoldResult(HoldStatus.SlotUnavailable, Code: failed),
                "service_not_found" => new HoldResult(HoldStatus.NotFound, Code: failed),
                _ => new HoldResult(HoldStatus.PortalRefused, Code: failed),
            };
        }
        if (row.Status != BookingStatus.HoldPending) return new HoldResult(HoldStatus.Ok, ToView(row, now), created);
        return await CallPortalHoldAsync(row, relayEmail, buyer.DisplayName, created, ct);
    }

    private async Task<HoldResult> CallPortalHoldAsync(Booking row, string relayEmail, string displayName, bool created, CancellationToken ct)
    {
        var addr = row.AddrLine1 is null ? null : new PortalAddress(row.AddrName ?? "", row.AddrLine1, row.AddrLine2, row.AddrCity ?? "", row.AddrRegion, row.AddrPostalCode ?? "", row.AddrCountry ?? "", row.AddrLat, row.AddrLng);
        var body = new BookingRequestBody(row.MarketplaceBookingId, row.ListingId, row.ProviderId, row.StartAt!.Value, row.DurationMinutes > 0 ? row.DurationMinutes : null, row.Location,
            row.AddOnIds.Length == 0 ? [] : row.AddOnIds.Split(','), new PortalBuyer(RelayIdentity.PublicName(displayName), relayEmail, row.Phone), addr);
        PortalResult<BookingResponse> res;
        try { res = await portal.CreateBookingHoldAsync(body, ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Ambiguous: the portal may or may not have the hold. The row stays HoldPending; the same key resumes it, and the sweeper resolves it with GET /bookings/{mid}.
            return new HoldResult(HoldStatus.Unavailable);
        }
        var now = time.GetUtcNow().ToUniversalTime();
        if (res.IsSuccess && res.Value?.Booking is { } sb)
        {
            if (!Matches(row, sb)) { log.LogError("Portal answered a hold with a different booking than asked for ({Mid})", row.MarketplaceBookingId); return new HoldResult(HoldStatus.Unavailable); }
            await ApplySummaryAsync(row, sb, now, ct);
            // Events that beat the answer were parked (their booking had no portal id yet): apply them now, then show the buyer the result.
            // The replay works on its own tracking state, so the row is read again rather than reloaded.
            if (await events.ReplayParkedAsync(BookingEventHandler.Aggregate, row.MarketplaceBookingId, ct) > 0)
                row = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == row.Id, ct);
            return new HoldResult(HoldStatus.Ok, ToView(row, time.GetUtcNow().ToUniversalTime()), created);
        }
        if (res.Retryable || res.Status is 408 or 429 or >= 500) return new HoldResult(HoldStatus.Unavailable);
        var code = Code(res.Error?.Code);
        await SetFailedAsync(row, code, now, ct);
        return code switch
        {
            "slot_unavailable" or "no_provider" => new HoldResult(HoldStatus.SlotUnavailable, Code: code),
            "service_not_found" => new HoldResult(HoldStatus.NotFound, Code: code),
            _ => new HoldResult(HoldStatus.PortalRefused, Code: code),
        };
    }

    private static string Code(string? c)
    {
        var s = string.IsNullOrEmpty(c) ? "refused" : new string(c.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_').Take(60).ToArray());
        return s.Length == 0 ? "refused" : s;
    }

    /// <summary>The summary belongs to THIS booking only when both our id and the service match what we asked for.</summary>
    private static bool Matches(Booking row, PortalBookingSummary s)
        => string.Equals(s.MarketplaceBookingId, row.MarketplaceBookingId, StringComparison.Ordinal) && (s.ServiceId is null || s.ServiceId == row.ListingId);

    private async Task SetFailedAsync(Booking row, string code, DateTimeOffset now, CancellationToken ct)
    {
        var v = row.Version;
        var n = await db.Bookings.Where(b => b.Id == row.Id && b.Version == v && b.Status == BookingStatus.HoldPending)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Failed).SetProperty(b => b.FailureCode, code).SetProperty(b => b.UpdatedAt, now).SetProperty(b => b.Version, v + 1), ct);
        if (n == 1) { row.Status = BookingStatus.Failed; row.FailureCode = code; row.Version = v + 1; }
        else await db.Entry(row).ReloadAsync(ct);
    }

    /// <summary>Copies the portal's authoritative values onto our row. Only ever moves a row forward out of HoldPending/Hold; a terminal row is left alone (decision O-10).</summary>
    /// <summary>True when the summary was stored. False when the row is already finished (a finished booking is never reopened by a portal ANSWER either) or a parallel writer stored a newer state first: callers that must know (the confirm handler) check it.</summary>
    public async Task<bool> ApplySummaryAsync(Booking row, PortalBookingSummary s, DateTimeOffset now, CancellationToken ct)
    {
        if (BookingStatus.IsTerminal(row.Status)) return false;
        var status = BookingStatus.FromPortal(s.Status) ?? row.Status;
        row.PortalBookingId = s.BookingId; row.Number = s.Number; row.Status = status; row.ProviderId = s.ProviderId ?? row.ProviderId;
        row.StartAt = s.StartAt ?? row.StartAt; row.EndAt = s.EndAt ?? row.EndAt; row.DurationMinutes = s.DurationMinutes;
        row.PriceCents = s.PriceCents; row.AddOnsCents = s.AddOnsCents; row.TravelFeeCents = s.TravelFeeCents; row.TotalCents = s.TotalCents;
        row.HoldExpiresAt = s.HoldExpiresAt; row.ApprovalDeadline = s.ApprovalDeadline; row.ProposedStartAt = s.ProposedStartAt; row.RescheduleCount = s.RescheduleCount;
        row.CancellationFeeCents = s.CancellationFeeCents; row.AdditionalChargeCents = s.AdditionalChargeCents;
        if (s.Policy is { } pol) { row.PolicyCancelFreeHours = pol.CancelFreeHours; row.PolicyCancelFeePercent = pol.CancelFeePercent; row.PolicyNoShowFeePercent = pol.NoShowFeePercent; row.PolicyMaxReschedules = pol.MaxReschedules; }
        row.OnlineInstructions = BookingStatuses.RevealsMeetingLink(status) ? Cap(s.OnlineInstructions, 2000) : null;
        row.UpdatedAt = now; row.Version++;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException)
        {
            // A parallel request (same key, or an event) already stored a newer state: keep theirs. Both answers came from the same portal booking.
            foreach (var e in db.ChangeTracker.Entries().ToList()) await e.ReloadAsync(ct);
            return false;
        }
    }

    private static string? Cap(string? s, int max) => string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];

    public BookingView ToView(Booking b, DateTimeOffset now) => new(
        b.Id, b.Number, b.ListingId, b.Status == BookingStatus.HoldPending ? BookingStatus.Hold : b.Status, b.Location, b.StartAt, b.EndAt, b.DurationMinutes, b.Currency, b.PriceCents, b.AddOnsCents,
        b.TravelFeeCents, b.TotalCents, b.HoldExpiresAt, b.Status == BookingStatus.Expired || (b.Status == BookingStatus.Hold && b.HoldExpiresAt is { } h && h <= now), b.ApprovalDeadline, b.ProposedStartAt,
        b.CancellationFeeCents, b.RefundDueCents, b.NoShowFeeCents, b.AdditionalChargeCents, b.RescheduleCount, b.AddOnIds.Length == 0 ? [] : b.AddOnIds.Split(','),
        BookingStatuses.RevealsMeetingLink(b.Status) ? b.OnlineInstructions : null, b.Status == BookingStatus.Failed ? b.FailureCode : null, b.CreatedAt,
        b.RequiresBuyerConsent, b.RescheduleRequestedStartAt, b.ExtraChargeState);

    /// <summary>The buyer's own booking, or null (another buyer's booking is a 404, never a 403).</summary>
    public async Task<BookingView?> GetAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AccountId == accountId && x.Status != BookingStatus.Failed, ct);
        return b is null ? null : ToView(b, time.GetUtcNow());
    }

    public async Task<IReadOnlyList<BookingView>> ListAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.Bookings.AsNoTracking().Where(x => x.AccountId == accountId && x.Status != BookingStatus.HoldPending && x.Status != BookingStatus.Failed)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(b => ToView(b, now) with { OnlineInstructions = null }).ToList();   // the meeting link is only on the detail view
    }
}

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/bookings").RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/holds", Hold);
        g.MapBookingActions();
        g.MapGet("/{id:guid}/calendar.ics", BookingCalendar.Get);
        g.MapPost("/{id:guid}/extra-charge/accept", ExtraAccept);
        g.MapPost("/{id:guid}/extra-charge/decline", ExtraDecline);
        g.MapPost("/{id:guid}/checkout", Checkout);
        g.MapGet("", async (HttpContext ctx, BookingService svc, CancellationToken ct) => Private(ctx, Results.Json(new { bookings = await svc.ListAsync(ctx.Buyer().AccountId, ct) })));
        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, BookingService svc, CancellationToken ct)
            => await svc.GetAsync(ctx.Buyer().AccountId, id, ct) is { } v ? Private(ctx, Results.Json(v)) : Problems.NotFound());
        return app;
    }

    /// <summary>Personalised responses are never cached by a shared cache (and the meeting link never lands in one).</summary>
    private static IResult Private(HttpContext ctx, IResult r) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static async Task<IResult> Hold(HoldRequest? body, HttpContext ctx, BookingService svc, MarketplaceDb db, CancellationToken ct)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 8 or > 100 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            return Problems.Validation(new Dictionary<string, string> { ["Idempotency-Key"] = "Send an Idempotency-Key header (8 to 100 letters, digits, - _ . :)." });
        var buyer = ctx.Buyer();
        var relay = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == buyer.AccountId && a.DeletedAt == null).Select(a => a.RelayEmail).FirstOrDefaultAsync(ct);
        if (relay is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        var r = await svc.HoldAsync(buyer, relay, key, body, ct);
        ctx.Response.Headers.CacheControl = "private, no-store";
        return r.Status switch
        {
            HoldStatus.Ok => Results.Json(new { created = r.Created, booking = r.Booking }, statusCode: r.Created ? 201 : 200),
            HoldStatus.Invalid => Problems.Validation(r.Errors!),
            HoldStatus.NotFound => Problems.NotFound("This service is not available."),
            HoldStatus.IdempotencyConflict => Problems.Json(409, "idempotency_conflict", "That Idempotency-Key was already used for a different request."),
            HoldStatus.TooManyHolds => Problems.Unprocessable("too_many_holds", "You already have several time slots on hold. Finish or let one expire first."),
            HoldStatus.SlotUnavailable => Problems.Json(409, "slot_unavailable", "That time was just taken. Please choose another."),
            HoldStatus.PortalRefused => Problems.Unprocessable(Known(r.Code), Message(r.Code)),
            _ => Problems.Json(503, "booking_unavailable", "We couldn't reserve that time right now. Please try again in a moment."),
        };
    }

    /// <summary>
    /// Stores the intake answers and phone, creates the booking's ONE payment intent for the portal's total (there is no amount in the body) and returns what the browser needs to pay.
    /// Repeating it, or sending it twice in parallel, returns the same intent. The booking is confirmed at the portal only after Stripe reports the payment.
    /// </summary>
    private static async Task<IResult> Checkout(Guid id, BookingCheckoutBody? body, HttpContext ctx, BookingService bookings, BookingPaymentService pay, CancellationToken ct)
    {
        var (status, result, errors) = await pay.CheckoutAsync(ctx.Buyer(), id, body, bookings, ct);
        ctx.Response.Headers.CacheControl = "private, no-store";
        return status switch
        {
            BookingCheckoutStatus.Ok => Results.Json(result),
            BookingCheckoutStatus.Invalid => Problems.Validation(errors!),
            BookingCheckoutStatus.NotFound => Problems.NotFound(),
            BookingCheckoutStatus.Expired => Problems.Json(409, "hold_expired", "Your time slot is no longer held. Please choose a time again."),
            BookingCheckoutStatus.AlreadyPaid => Problems.Json(409, "already_paid", "This booking is already paid."),
            BookingCheckoutStatus.AmountTooSmall => Problems.Unprocessable("amount_too_small", "The total is below the minimum we can charge."),
            BookingCheckoutStatus.PaymentRejected => Problems.Unprocessable("payment_rejected", "The payment provider could not start this payment."),
            BookingCheckoutStatus.PaymentsUnavailable => Problems.Json(503, "payments_unavailable", "Payments are unavailable right now. Please try again in a moment."),
            _ => Problems.Json(409, "not_payable", "This booking can't be paid for right now."),
        };
    }

    /// <summary>The buyer's consent to an additional charge the provider asked for: creates the intent for the amount the portal sent (the body has none). Nothing is charged before this.</summary>
    private static async Task<IResult> ExtraAccept(Guid id, HttpContext ctx, ExtraChargeService extra, CancellationToken ct)
    {
        var (status, payment, amount) = await extra.AcceptAsync(ctx.Buyer().AccountId, id, ct);
        ctx.Response.Headers.CacheControl = "private, no-store";
        return status switch
        {
            ExtraChargeStatus.Ok => Results.Json(new { amountCents = amount, payment }),
            ExtraChargeStatus.NotFound => Problems.NotFound(),
            ExtraChargeStatus.AlreadyPaid => Problems.Json(409, "already_paid", "This charge is already paid."),
            ExtraChargeStatus.AmountTooSmall => Problems.Unprocessable("amount_too_small", "The amount is below the minimum we can charge."),
            ExtraChargeStatus.PaymentRejected => Problems.Unprocessable("payment_rejected", "The payment provider could not start this payment."),
            ExtraChargeStatus.PaymentsUnavailable => Problems.Json(503, "payments_unavailable", "Payments are unavailable right now. Please try again in a moment."),
            _ => Problems.Json(409, "nothing_requested", "There is no additional charge to pay."),
        };
    }

    private static async Task<IResult> ExtraDecline(Guid id, HttpContext ctx, ExtraChargeService extra, CancellationToken ct)
        => await extra.DeclineAsync(ctx.Buyer().AccountId, id, ct) switch
        {
            ExtraChargeStatus.Ok => Results.NoContent(),
            ExtraChargeStatus.NotFound => Problems.NotFound(),
            _ => Problems.Json(409, "nothing_requested", "There is no additional charge to answer."),
        };

    private static readonly string[] KnownCodes = ["outside_service_area", "location_not_offered", "duration", "add_on", "intake_required", "intake_invalid", "provider_required", "quote_required"];
    private static string Known(string? c) => c is not null && KnownCodes.Contains(c) ? c : "invalid_request";
    private static string Message(string? c) => c switch
    {
        "outside_service_area" => "This provider doesn't serve that address.",
        "location_not_offered" => "That location option isn't offered for this service.",
        "duration" => "Choose a different duration.",
        "add_on" => "One of the add-ons isn't available.",
        "provider_required" => "Choose a provider.",
        "quote_required" => "This service needs a quote first.",
        _ => "Some choices need attention.",
    };
}
