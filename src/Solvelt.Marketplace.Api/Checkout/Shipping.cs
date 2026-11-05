using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Checkout;

public sealed class ShippingOptions
{
    /// <summary>Business days in transit by speed name; an unknown speed uses <see cref="DefaultTransitDays"/>.</summary>
    public Dictionary<string, int> TransitDays { get; set; } = new() { ["standard"] = 5, ["expedited"] = 2, ["overnight"] = 1 };
    public int DefaultTransitDays { get; set; } = 5;
    public int SettingsCacheSeconds { get; set; } = 300;
}

public sealed record DeliveryEstimate(string Earliest, string Latest);
public sealed record ShippingOption(string Speed, long PriceCents, bool Free, DeliveryEstimate Estimate);
/// <summary>A speed the seller offers that cannot be priced for this group right now (<c>weight_unknown</c>: a weight-based speed while any line has no known weight, decision O-15).</summary>
public sealed record UnavailableSpeed(string Speed, string Reason);
public sealed record ShippingOptionsResult(IReadOnlyList<ShippingOption> Options, IReadOnlyList<UnavailableSpeed> Unavailable);

/// <summary>Raised when the portal cannot answer a checkout-time question (the buyer is told to try again; nothing is guessed).</summary>
public sealed class ShippingUnavailableException : Exception;

public static class ShippingRates
{
    /// <summary>
    /// Options for one (seller, brand) group (CONTRACT.md §5): the default profile, the zone that lists the destination country, each enabled speed priced flat or as
    /// <c>baseCents + ceil(weightGrams / 1000) x perKgCents</c>, and free when the <b>items subtotal of this group</b> reaches the profile's threshold.
    /// </summary>
    public static ShippingOptionsResult Options(ShippingSettings s, string country, long itemsSubtotalCents, long? weightGrams, DateOnly today, ShippingOptions o)
    {
        var profile = s.Profiles.FirstOrDefault(p => p.IsDefault) ?? s.Profiles.FirstOrDefault();
        var zone = profile?.Zones.FirstOrDefault(z => z.Countries.Any(c => string.Equals(c, country, StringComparison.OrdinalIgnoreCase)));
        if (profile is null || zone is null) return new([], []);
        var free = profile.FreeShippingThresholdCents is { } t && itemsSubtotalCents >= t;
        var result = new List<ShippingOption>();
        var unavailable = new List<UnavailableSpeed>();
        // only speeds the portal's ingest accepts (standard, expedited, overnight; C8) are offered: a speed it would refuse with a 422 must never be sold (decision M3-37)
        foreach (var sp in zone.Speeds.Where(x => x.Enabled && x.Speed is not null && IngestMapping.Speeds.Contains(x.Speed.Trim().ToLowerInvariant())))
        {
            // Decision O-15: a weight-based price needs a KNOWN weight for every line of the group. Counting an unknown weight as 0 would under-charge,
            // so the speed is unavailable (flat speeds and local delivery are unaffected), unless the group ships free: then the weight does not matter.
            if (sp.Method == "weight" && weightGrams is not > 0 && !free) { unavailable.Add(new UnavailableSpeed(sp.Speed, "weight_unknown")); continue; }
            long? price = sp.Method switch
            {
                "flat" => sp.FlatCents,
                "weight" => free ? 0 : sp.BaseCents is { } b && sp.PerKgCents is { } k ? WeightPrice(b, k, weightGrams!.Value) : null,   // a free group needs no weight (review C4)
                _ => null,
            };
            if (price is not { } p || p < 0 || p > Carts.CartLimits.MaxTotalCents) continue;   // a speed we cannot price (or whose fee is absurd: portal data is bounded, review K5) is not offered
            var transit = o.TransitDays.GetValueOrDefault(sp.Speed.ToLowerInvariant(), o.DefaultTransitDays);
            var first = AddBusinessDays(today, Math.Max(0, s.HandlingDays) + transit);
            var last = AddBusinessDays(first, transit >= 3 ? 2 : 1);
            result.Add(new ShippingOption(sp.Speed, free ? 0 : p, free, new DeliveryEstimate(first.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd"))));
        }
        return new(result.OrderBy(r => r.PriceCents).ThenBy(r => r.Estimate.Earliest, StringComparer.Ordinal).ToList(), unavailable);
    }

    /// <summary>base + ceil(kg) x per kg in checked arithmetic: an overflow (absurd portal settings) is "cannot be priced", never a wrapped number.</summary>
    private static long? WeightPrice(long baseCents, long perKgCents, long grams)
    {
        try { return checked(baseCents + (grams + 999) / 1000 * perKgCents); }
        catch (OverflowException) { return null; }
    }

    public static DateOnly AddBusinessDays(DateOnly d, int days)
    {
        while (days > 0) { d = d.AddDays(1); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--; }
        return d;
    }

    /// <summary>
    /// Weight of a listing from its <c>weight</c> attribute ("550 g", "1.5 kg", "12 oz", "2 lb"; rounded up). <b>null when unknown</b> (missing, unparseable, zero or negative): the
    /// caller must not count it as 0 (decisions M3-05, O-15). Once the catalog feed carries an explicit <c>weightGrams</c> (contract request C7) it is preferred and this stays the fallback.
    /// </summary>
    public static long? Grams(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute)) return null;
        var parts = attribute.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !decimal.TryParse(parts[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0 || n > 1_000_000) return null;
        var factor = parts[1].ToLowerInvariant() switch { "g" => 1m, "kg" => 1000m, "oz" => 28.349523125m, "lb" or "lbs" => 453.59237m, _ => 0m };
        return factor == 0m ? null : (long)Math.Ceiling(n * factor);
    }
}

/// <summary>
/// Portal shipping settings, cached at most <see cref="ShippingOptions.SettingsCacheSeconds"/> (5 min) per (seller, brand) and fetched once for a burst of
/// concurrent checkouts (decision M1-11). A failed answer is never cached and drops the entry (invalidate on error).
/// </summary>
public sealed class ShippingSettingsCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<ShippingOptions> options, ILogger<ShippingSettingsCache>? log = null)
{
    private sealed record Entry(ShippingSettings Settings, DateTimeOffset FetchedAt);
    private readonly ConcurrentDictionary<(Guid, Guid), Entry> Cache = new();
    private readonly ConcurrentDictionary<(Guid, Guid), SemaphoreSlim> Locks = new();

    public async Task<ShippingSettings> GetAsync(Guid sellerId, Guid brandId, CancellationToken ct)
    {
        var key = (sellerId, brandId);
        var ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.SettingsCacheSeconds, 0, 300));
        if (Fresh(key, ttl) is { } hit) return hit;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(key, ttl) is { } again) return again;
            PortalResult<ShippingSettings> r;
            using var scope = scopes.CreateScope();
            var portal = scope.ServiceProvider.GetRequiredService<ISignedPortalClient>();
            try { r = await portal.GetShippingSettingsAsync(sellerId, brandId, ct); }
            catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException)
            {
                Cache.TryRemove(key, out _);
                // the buyer sees "delivery options are unavailable"; the log says why (never the URL or a secret): a checkout that cannot quote is nearly always one of these settings (M8-12)
                log?.LogError("The portal's shipping settings could not be read ({Kind}). Check Portal:BaseUrl (scheme, host, port) and that the portal is reachable from this server", ex.GetType().Name);
                throw new ShippingUnavailableException();
            }
            if (!r.IsSuccess || r.Value is null)
            {
                Cache.TryRemove(key, out _);
                log?.LogError("The portal answered {Status} to the shipping settings call.{Hint}", r.Status, r.Status switch
                {
                    401 => " Its signature check refused ours: Portal:InboundSecret must equal the portal's Orders:InboundSecret, and this server's clock must be within 5 minutes of the portal's.",
                    403 or 404 => " Check Portal:BaseUrl: it may point at something that is not the portal's API.",
                    429 => " The portal is rate limiting this server.",
                    >= 500 => " The portal is failing.",
                    _ => "",
                });
                throw new ShippingUnavailableException();
            }
            Cache[key] = new Entry(r.Value, time.GetUtcNow());
            return r.Value;
        }
        finally { gate.Release(); }
    }

    private ShippingSettings? Fresh((Guid, Guid) key, TimeSpan ttl)
        => Cache.TryGetValue(key, out var e) && time.GetUtcNow() - e.FetchedAt < ttl ? e.Settings : null;

}
