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

/// <summary>Raised when the portal cannot answer a checkout-time question (the buyer is told to try again; nothing is guessed).</summary>
public sealed class ShippingUnavailableException : Exception;

public static class ShippingRates
{
    /// <summary>
    /// Options for one (seller, brand) group (CONTRACT.md §5): the default profile, the zone that lists the destination country, each enabled speed priced flat or as
    /// <c>baseCents + ceil(weightGrams / 1000) x perKgCents</c>, and free when the <b>items subtotal of this group</b> reaches the profile's threshold.
    /// </summary>
    public static IReadOnlyList<ShippingOption> Options(ShippingSettings s, string country, long itemsSubtotalCents, long weightGrams, DateOnly today, ShippingOptions o)
    {
        var profile = s.Profiles.FirstOrDefault(p => p.IsDefault) ?? s.Profiles.FirstOrDefault();
        var zone = profile?.Zones.FirstOrDefault(z => z.Countries.Any(c => string.Equals(c, country, StringComparison.OrdinalIgnoreCase)));
        if (profile is null || zone is null) return [];
        var free = profile.FreeShippingThresholdCents is { } t && itemsSubtotalCents >= t;
        var result = new List<ShippingOption>();
        foreach (var sp in zone.Speeds.Where(x => x.Enabled))
        {
            long? price = sp.Method switch
            {
                "flat" => sp.FlatCents,
                "weight" => sp.BaseCents is { } b && sp.PerKgCents is { } k ? b + (weightGrams + 999) / 1000 * k : null,
                _ => null,
            };
            if (price is not { } p || p < 0) continue;   // a speed we cannot price is not offered
            var transit = o.TransitDays.GetValueOrDefault(sp.Speed.ToLowerInvariant(), o.DefaultTransitDays);
            var first = AddBusinessDays(today, Math.Max(0, s.HandlingDays) + transit);
            var last = AddBusinessDays(first, transit >= 3 ? 2 : 1);
            result.Add(new ShippingOption(sp.Speed, free ? 0 : p, free, new DeliveryEstimate(first.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd"))));
        }
        return result.OrderBy(r => r.PriceCents).ThenBy(r => r.Estimate.Earliest, StringComparer.Ordinal).ToList();
    }

    public static DateOnly AddBusinessDays(DateOnly d, int days)
    {
        while (days > 0) { d = d.AddDays(1); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--; }
        return d;
    }

    /// <summary>Weight of a listing from its <c>weight</c> attribute ("550 g", "1.5 kg", "12 oz", "2 lb"); unknown is 0 (decision M3-05).</summary>
    public static long Grams(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute)) return 0;
        var parts = attribute.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !decimal.TryParse(parts[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1_000_000) return 0;
        var factor = parts[1].ToLowerInvariant() switch { "g" => 1m, "kg" => 1000m, "oz" => 28.349523125m, "lb" or "lbs" => 453.59237m, _ => 0m };
        return (long)Math.Ceiling(n * factor);
    }
}

/// <summary>
/// Portal shipping settings, cached at most <see cref="ShippingOptions.SettingsCacheSeconds"/> (5 min) per (seller, brand) and fetched once for a burst of
/// concurrent checkouts (decision M1-11). A failed answer is never cached and drops the entry (invalidate on error).
/// </summary>
public sealed class ShippingSettingsCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<ShippingOptions> options)
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
            catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException) { Cache.TryRemove(key, out _); throw new ShippingUnavailableException(); }
            if (!r.IsSuccess || r.Value is null) { Cache.TryRemove(key, out _); throw new ShippingUnavailableException(); }
            Cache[key] = new Entry(r.Value, time.GetUtcNow());
            return r.Value;
        }
        finally { gate.Release(); }
    }

    private ShippingSettings? Fresh((Guid, Guid) key, TimeSpan ttl)
        => Cache.TryGetValue(key, out var e) && time.GetUtcNow() - e.FetchedAt < ttl ? e.Settings : null;

}
