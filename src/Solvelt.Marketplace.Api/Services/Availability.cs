using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

public sealed class ServicesOptions
{
    /// <summary>How long one availability answer is reused (CONTRACT.md §4: at most 30 s; the portal's bucket is per service for all shoppers).</summary>
    public int AvailabilityCacheSeconds { get; set; } = 30;
    public int MaxWindowDays { get; set; } = 62;
    public int DefaultWindowDays { get; set; } = 14;
    /// <summary>Upper bound on cached answers; expired entries are dropped first, then the oldest.</summary>
    public int MaxCacheEntries { get; set; } = 2000;
    /// <summary>
    /// Portal availability calls the marketplace itself allows per service per minute, counted over cache MISSES only (a single-flight burst is one call). The portal's own limit is 600 per service for
    /// ALL shoppers (SERVICES.md §6); this stays well below it so that no mix of queries, from any number of addresses, can push a service into 429s for everyone (decision M4-14). When it is spent the buyer
    /// gets <c>503 availability_unavailable</c> and the next minute works again.
    /// </summary>
    public int AvailabilityPortalCallsPerMinute { get; set; } = 300;
}

/// <summary>The buyer-facing availability request after normalisation. Everything that changes the portal's answer is in the key; nothing else is.</summary>
public sealed record AvailabilityRequest(Guid ServiceId, DateTimeOffset From, DateTimeOffset To, string Location, string? PostalCode, Guid? ProviderId,
    double? Lat, double? Lng, int? Minutes, IReadOnlyList<string> AddOns)
{
    public string Key => string.Join("|", ServiceId.ToString("N"), From.UtcTicks, To.UtcTicks, Location, PostalCode ?? "", ProviderId?.ToString("N") ?? "",
        Lat?.ToString("F2", CultureInfo.InvariantCulture) ?? "", Lng?.ToString("F2", CultureInfo.InvariantCulture) ?? "", Minutes?.ToString(CultureInfo.InvariantCulture) ?? "", string.Join(",", AddOns));
}

/// <summary>Result of an availability read: either the (cached) portal answer or a buyer-safe error.</summary>
public sealed record AvailabilityOutcome(ServiceAvailability? Value, int Status, string? Code, string? Message, DateTimeOffset? FetchedAt)
{
    public bool IsSuccess => Value is not null;
}

/// <summary>
/// Portal availability, cached at most <see cref="ServicesOptions.AvailabilityCacheSeconds"/> and fetched <b>once</b> for any burst of identical concurrent requests
/// (single-flight). The portal's availability rate limit is per service for all shoppers (600/min), so the marketplace is the only place that can protect it.
/// The in-flight fetch runs on its own cancellation scope: one shopper closing the tab never cancels the answer the others are waiting for. Failures are
/// shared with the callers that were already waiting but never cached, so the next request tries the portal again.
/// </summary>
public sealed class AvailabilityCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<ServicesOptions> options)
{
    private sealed record Entry(AvailabilityOutcome Outcome, DateTimeOffset At);
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<AvailabilityOutcome>>> _inflight = new();
    public int PortalCalls;

    // One token bucket per service, filled to AvailabilityPortalCallsPerMinute and refilled continuously over a minute (decision M4-14).
    private sealed class Bucket { public double Tokens; public DateTimeOffset At; }
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    private bool TryTakeToken(Guid serviceId)
    {
        var cap = Math.Clamp(options.Value.AvailabilityPortalCallsPerMinute, 1, 600);
        var now = time.GetUtcNow();
        var b = _buckets.GetOrAdd(serviceId, _ => new Bucket { Tokens = cap, At = now });
        lock (b)
        {
            var elapsed = Math.Max(0, (now - b.At).TotalSeconds);
            b.Tokens = Math.Min(cap, b.Tokens + elapsed * cap / 60.0);
            b.At = now;
            if (b.Tokens < 1) return false;
            b.Tokens -= 1;
            return true;
        }
    }

    public async Task<AvailabilityOutcome> GetAsync(AvailabilityRequest r, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.AvailabilityCacheSeconds, 0, 30));
        var key = r.Key;
        if (_cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < ttl) return hit.Outcome;

        var flight = _inflight.GetOrAdd(key, _ => new Lazy<Task<AvailabilityOutcome>>(() => FetchAsync(key, r, ttl), LazyThreadSafetyMode.ExecutionAndPublication));
        return await flight.Value.WaitAsync(ct);
    }

    private async Task<AvailabilityOutcome> FetchAsync(string key, AvailabilityRequest r, TimeSpan ttl)
    {
        try
        {
            // A re-check after joining: a flight that just finished may have filled the cache between our first look and GetOrAdd.
            if (_cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < ttl) return hit.Outcome;
            // Only a real portal call costs a token: cache hits and joined in-flight calls are free, so ordinary shopping never meets this limit, while a stream of ever-new queries cannot spend the
            // portal's 600 per minute for everyone (it was possible to mint a new cache key per request by varying the postal code, coordinates, window or minutes).
            if (!TryTakeToken(r.ServiceId)) return Fail(503, "availability_unavailable", "Availability can't be loaded right now. Try again in a moment.");
            Interlocked.Increment(ref PortalCalls);
            var outcome = await CallPortalAsync(r);
            if (outcome.IsSuccess && ttl > TimeSpan.Zero) { Trim(); _cache[key] = new Entry(outcome, time.GetUtcNow()); }
            return outcome;
        }
        finally { _inflight.TryRemove(key, out _); }
    }

    private async Task<AvailabilityOutcome> CallPortalAsync(AvailabilityRequest r)
    {
        using var scope = scopes.CreateScope();
        var portal = scope.ServiceProvider.GetRequiredService<ISignedPortalClient>();
        PortalResult<ServiceAvailability> res;
        try
        {
            // No caller token on purpose (see the class summary); the client's own total deadline (O-08, 30 s) bounds this.
            res = await portal.GetServiceAvailabilityAsync(new ServiceAvailabilityQuery(r.ServiceId, r.From, r.To, r.Location, r.PostalCode, r.ProviderId, r.Lat, r.Lng, r.Minutes,
                r.AddOns.Count == 0 ? null : r.AddOns), CancellationToken.None);
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException)
        {
            return Fail(503, "availability_unavailable", "Availability can't be loaded right now. Try again in a moment.");
        }
        if (res.IsSuccess && res.Value is { } v)
            return new AvailabilityOutcome(Normalise(v), 200, null, null, time.GetUtcNow());
        return res.Status switch
        {
            404 => Fail(404, "not_found", "This service is not available."),
            422 => Fail(422, res.Error?.Code is { Length: > 0 and <= 40 } c && c is "outside_service_area" or "location_not_offered" or "duration" or "add_on" or "provider_required" ? res.Error!.Code! : "invalid_request",
                res.Error?.Code switch
                {
                    "outside_service_area" => "This provider doesn't serve that area.",
                    "location_not_offered" => "That location option isn't offered for this service.",
                    "duration" => "Choose a different duration.",
                    "add_on" => "One of the add-ons isn't available.",
                    "provider_required" => "Choose a provider.",
                    _ => "Some choices need attention.",
                }),
            429 => Fail(503, "availability_unavailable", "Availability can't be loaded right now. Try again in a moment."),
            _ => Fail(503, "availability_unavailable", "Availability can't be loaded right now. Try again in a moment."),
        };
    }

    private static AvailabilityOutcome Fail(int status, string code, string message) => new(null, status, code, message, null);

    /// <summary>Ordered, de-duplicated and trimmed to what the buyer needs; the portal's slot list is the only source of truth for start times.</summary>
    private static ServiceAvailability Normalise(ServiceAvailability v) => v with
    {
        // One slot per (start, end); a start listed twice (one entry per provider, say) keeps every provider.
        Slots = v.Slots.Where(s => s.EndAt > s.StartAt).GroupBy(s => (s.StartAt, s.EndAt))
            .Select(g => new AvailabilitySlot(g.Key.StartAt, g.Key.EndAt, g.SelectMany(x => x.ProviderIds ?? []).Distinct().Order().ToList()))
            .OrderBy(s => s.StartAt).ThenBy(s => s.EndAt).ToList(),
    };

    private void Trim()
    {
        var max = Math.Clamp(options.Value.MaxCacheEntries, 10, 50_000);
        if (_cache.Count < max) return;
        var now = time.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.AvailabilityCacheSeconds, 0, 30));
        foreach (var kv in _cache.Where(k => now - k.Value.At >= ttl).ToList()) _cache.TryRemove(kv.Key, out _);
        if (_cache.Count < max) return;
        foreach (var kv in _cache.OrderBy(k => k.Value.At).Take(Math.Max(1, max / 10)).ToList()) _cache.TryRemove(kv.Key, out _);
    }
}

/// <summary>The catalog's own visibility rule (listing Active, not unlisted, SELLER active: CONTRACT.md §2) narrowed to services. Every public service endpoint uses it.</summary>
public static class ServiceListings
{
    public static IQueryable<CatalogListing> Visible(MarketplaceDb db) => Catalog.CatalogEndpoints.Visible(db).Where(l => l.Type == ListingTypes.Service);
}

public static class ServiceEndpoints
{
    private static readonly string[] Locations = ["online", "customer", "seller"];

    public static IEndpointRouteBuilder MapServices(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/services").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("/{id:guid}/availability", Availability);
        g.MapGet("/{id:guid}/options", ServiceOptionsEndpoint.Get);
        return app;
    }

    /// <summary>
    /// <c>GET /api/v1/services/{id}/availability?from&amp;to&amp;location&amp;postalCode&amp;lat&amp;lng&amp;providerId&amp;minutes&amp;addOns</c>. Public (browsing never needs an account), only for
    /// visible service listings. The window is snapped to whole UTC days (so many shoppers share one cache key) and is at most 62 days; the start is never in the past.
    /// Slots are UTC instants; <c>providers[].timeZone</c> is the provider's zone for display. Nothing the client sends decides a price or a time: a booking
    /// re-validates the slot at the portal.
    /// </summary>
    private static async Task<IResult> Availability(Guid id, HttpContext http, MarketplaceDb db, AvailabilityCache cache, TimeProvider time, IOptions<ServicesOptions> options, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";   // every answer but a 200 (a 404, 422 or 503 must never be cached for the next shopper)
        var q = http.Request.Query;
        var errors = new Dictionary<string, string>();
        var o = options.Value;
        var now = time.GetUtcNow();
        static DateTimeOffset Day(DateTimeOffset d) => new(d.UtcDateTime.Date, TimeSpan.Zero);

        var location = q["location"].ToString().Trim().ToLowerInvariant();
        if (!Locations.Contains(location)) errors["location"] = "Choose online, at your place or at the provider's.";

        DateTimeOffset from = Day(now), to = from.AddDays(o.DefaultWindowDays);
        if (q.ContainsKey("from") && !TryInstant(q["from"].ToString(), out from)) errors["from"] = "Use a date like 2026-12-09.";
        if (q.ContainsKey("to") && !TryInstant(q["to"].ToString(), out to)) errors["to"] = "Use a date like 2026-12-10.";
        // Far-future dates are refused BEFORE any date arithmetic (adding a window to 9999-12-30 overflows).
        var horizon = now.AddDays(400);
        if (errors.Count == 0 && from > horizon) errors["from"] = "Choose a date within the next year.";
        if (errors.Count == 0 && !q.ContainsKey("to") && q.ContainsKey("from")) to = Day(from).AddDays(o.DefaultWindowDays);
        if (errors.Count == 0 && to > horizon) errors["to"] = "Choose a date within the next year.";
        if (errors.Count == 0)
        {
            from = Day(from); if (to != Day(to)) to = Day(to).AddDays(1); else to = Day(to);
            if (from < Day(now)) from = Day(now);
            if (to <= from) errors["to"] = "The end must be after the start.";
            else if (to - from > TimeSpan.FromDays(o.MaxWindowDays)) errors["to"] = $"Ask for at most {o.MaxWindowDays} days at a time.";
        }

        string? postal = null;
        if (q.ContainsKey("postalCode"))
        {
            postal = q["postalCode"].ToString().Trim().ToUpperInvariant();
            if (postal.Length is 0 or > 12 || postal.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-'))) errors["postalCode"] = "Enter a valid postal code.";
        }
        double? lat = null, lng = null;
        if (q.ContainsKey("lat") != q.ContainsKey("lng")) errors["lat"] = "Send both lat and lng.";
        else if (q.ContainsKey("lat"))
        {
            if (!double.TryParse(q["lat"], NumberStyles.Float, CultureInfo.InvariantCulture, out var la) || la is < -90 or > 90 || double.IsNaN(la)) errors["lat"] = "Invalid latitude.";
            else if (!double.TryParse(q["lng"], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) || lo is < -180 or > 180 || double.IsNaN(lo)) errors["lng"] = "Invalid longitude.";
            else { lat = Math.Round(la, 2); lng = Math.Round(lo, 2); }   // about 1.1 km: the postal code decides the service area, and probing coordinates can only mint a handful of cache keys; the booking itself re-validates the exact address
        }
        Guid? provider = null;
        if (q.ContainsKey("providerId")) { if (Guid.TryParse(q["providerId"], out var p)) provider = p; else errors["providerId"] = "Invalid provider."; }
        int? minutes = null;
        if (q.ContainsKey("minutes")) { if (int.TryParse(q["minutes"], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 24 * 60) minutes = m; else errors["minutes"] = "Invalid duration."; }
        var addOns = new List<string>();
        if (q.ContainsKey("addOns"))
        {
            addOns = q["addOns"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (addOns.Count > 20 || addOns.Any(a => a.Length > 100 || a.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))) errors["addOns"] = "Invalid add-ons.";
        }
        if (errors.Count > 0) return Problems.Validation(errors);

        var isService = await ServiceListings.Visible(db).AnyAsync(l => l.Id == id, ct);
        if (!isService) return Problems.NotFound("This service is not available.");

        var outcome = await cache.GetAsync(new AvailabilityRequest(id, from, to, location, postal, provider, lat, lng, minutes, addOns), ct);
        if (!outcome.IsSuccess) return Problems.Json(outcome.Status, outcome.Code!, outcome.Message!);
        var v = outcome.Value!;
        http.Response.Headers.CacheControl = "public, max-age=15";
        return Results.Json(new
        {
            durationMinutes = v.DurationMinutes, priceCents = v.PriceCents, providers = v.Providers, slots = v.Slots,
            from, to, fetchedAt = outcome.FetchedAt,
        });
    }

    private static bool TryInstant(string s, out DateTimeOffset d)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d);
}
