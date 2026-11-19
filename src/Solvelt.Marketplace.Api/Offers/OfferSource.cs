using System.Globalization;
using System.Text.Json;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Offers;

public sealed record OfferGiftDoc(int MinQuantity, Guid ProductId, int Quantity, string Title, bool Available);

/// <summary>One offer document of the portal feed (C18.2). Money is <c>long</c> cents.</summary>
public sealed record OfferSnapshot(Guid OfferId, long Version, long FeedVersion, Guid SellerId, Guid BrandId, string Name, string Currency, string Scope,
    IReadOnlyList<Guid> ProductIds, IReadOnlyList<Guid> CategoryIds, IReadOnlyList<OfferTier> Tiers, IReadOnlyList<OfferGiftDoc> Gifts,
    DateTimeOffset? ActiveFrom, DateTimeOffset? ActiveTo, bool Demo, DateTimeOffset UpdatedAt);

/// <summary>An offer that stopped applying (<c>ended</c>, <c>paused</c> or <c>deleted</c>). <see cref="Version"/> is null for a deleted one (gone for good).</summary>
public sealed record OfferTombstone(Guid OfferId, long? Version, string Reason);

public sealed record OfferPage(IReadOnlyList<OfferSnapshot> Items, IReadOnlyList<OfferTombstone> Tombstones, string? NextCursor, bool HasMore);

/// <summary>Where offers come from: the portal's signed feed (C18.2), a fixture for tests and development, or nothing.</summary>
public interface IOfferSource
{
    Task<OfferPage> GetOffersAsync(string? since, int limit, CancellationToken ct);
    /// <summary>One offer, or null when the portal does not have it (never active, or gone).</summary>
    Task<OfferSnapshot?> GetOfferAsync(Guid id, CancellationToken ct);
}

public sealed class EmptyOfferSource : IOfferSource
{
    public Task<OfferPage> GetOffersAsync(string? since, int limit, CancellationToken ct) => Task.FromResult(new OfferPage([], [], null, false));
    public Task<OfferSnapshot?> GetOfferAsync(Guid id, CancellationToken ct) => Task.FromResult<OfferSnapshot?>(null);
}

/// <summary>An in-memory feed for tests and development: <see cref="Upsert"/> writes a new version, <see cref="End"/> a tombstone; ordering is by feed version like the portal's cursor.</summary>
public sealed class FixtureOfferSource : IOfferSource
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (OfferSnapshot? Doc, OfferTombstone? Dead, long Feed)> _items = [];
    private long _feed;

    public OfferSnapshot Upsert(OfferSnapshot offer)
    {
        lock (_gate)
        {
            var version = _items.TryGetValue(offer.OfferId, out var old) ? Math.Max(old.Doc?.Version ?? 0, old.Dead?.Version ?? 0) + 1 : Math.Max(1, offer.Version);
            var doc = offer with { Version = version, FeedVersion = ++_feed };
            _items[offer.OfferId] = (doc, null, _feed);
            return doc;
        }
    }

    public void End(Guid offerId, string reason = "ended")
    {
        lock (_gate)
        {
            var v = _items.TryGetValue(offerId, out var old) ? old.Doc?.Version ?? old.Dead?.Version : null;
            _items[offerId] = (null, new OfferTombstone(offerId, reason == "deleted" ? null : v, reason), ++_feed);
        }
    }

    public Task<OfferPage> GetOffersAsync(string? since, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var after = long.TryParse(since, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var page = _items.Values.Where(i => i.Feed > after).OrderBy(i => i.Feed).Take(Math.Clamp(limit, 1, 200)).ToList();
            var more = _items.Values.Count(i => i.Feed > after) > page.Count;
            return Task.FromResult(new OfferPage(page.Where(i => i.Doc is not null).Select(i => i.Doc!).ToList(), page.Where(i => i.Dead is not null).Select(i => i.Dead!).ToList(),
                page.Count == 0 ? since : page[^1].Feed.ToString(CultureInfo.InvariantCulture), more));
        }
    }

    public Task<OfferSnapshot?> GetOfferAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_items.TryGetValue(id, out var i) ? i.Doc : null);
    }
}

/// <summary>The portal's offer feed (C18.2) over the signed client. The one file that reads its documents; an unreadable document is skipped (logged by id and version only), never blocks the page.</summary>
public sealed class HttpOfferSource(ISignedPortalClient portal, ILogger<HttpOfferSource> log) : IOfferSource
{
    private const int MaxTiers = 20, MaxGifts = 10, MaxIds = 2000;
    private const long MaxCents = 100_000_000;

    public async Task<OfferPage> GetOffersAsync(string? since, int limit, CancellationToken ct)
    {
        var r = await portal.GetCatalogOffersAsync(since, limit, ct);
        if (r.Status is < 200 or >= 300 || r.Value.ValueKind != JsonValueKind.Object) throw new PortalUnavailableException($"The portal offer feed answered {r.Status}.");
        var items = new List<OfferSnapshot>(); var dead = new List<OfferTombstone>();
        if (r.Value.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                if (e.TryGetProperty("listed", out var l) && l.ValueKind == JsonValueKind.False)
                {
                    if (Guid_(e, "offerId") is { } id) dead.Add(new OfferTombstone(id, Long(e, "version"), Str(e, "reason") ?? "ended"));
                    continue;
                }
                var doc = Parse(e);
                if (doc is not null) items.Add(doc); else log.LogWarning("An offer document could not be read and was skipped ({Id} version {Version}).", Str(e, "offerId"), Long(e, "version"));
            }
        var next = r.Value.TryGetProperty("nextCursor", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new OfferPage(items, dead, next, r.Value.TryGetProperty("hasMore", out var h) && h.ValueKind == JsonValueKind.True);
    }

    public async Task<OfferSnapshot?> GetOfferAsync(Guid id, CancellationToken ct)
    {
        var r = await portal.GetCatalogOfferAsync(id, ct);
        if (r.Status == 404) return null;
        if (r.Status is < 200 or >= 300 || r.Value.ValueKind != JsonValueKind.Object) throw new PortalUnavailableException($"The portal offer call answered {r.Status}.");
        if (r.Value.TryGetProperty("listed", out var l) && l.ValueKind == JsonValueKind.False) return null;
        return Parse(r.Value);
    }

    internal static OfferSnapshot? Parse(JsonElement e)
    {
        if (Guid_(e, "offerId") is not { } id || Long(e, "version") is not { } version || version < 1 || Guid_(e, "sellerId") is not { } seller || Guid_(e, "brandId") is not { } brand) return null;
        if (Str(e, "status") is { } status && status != "active") return null;
        var scope = e.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object ? sc : default;
        var kind = Str(scope, "kind");
        if (kind is not ("products" or "category" or "all")) return null;
        var products = Guids(scope, "productIds"); var categories = Guids(scope, "categoryIds");
        if (products.Count > MaxIds || categories.Count > MaxIds || (kind == "products" && products.Count == 0) || (kind == "category" && categories.Count == 0)) return null;
        var tiers = new List<OfferTier>();
        if (e.TryGetProperty("tiers", out var ta) && ta.ValueKind == JsonValueKind.Array)
            foreach (var t in ta.EnumerateArray())
            {
                if (Long(t, "minQuantity") is not { } mq || mq < 1 || mq > 100000 || Long(t, "unitPriceCents") is not { } up || up < 0 || up > MaxCents) return null;
                tiers.Add(new OfferTier((int)mq, up));
            }
        if (tiers.Count > MaxTiers || tiers.Select(t => t.MinQuantity).Distinct().Count() != tiers.Count) return null;
        var gifts = new List<OfferGiftDoc>();
        if (e.TryGetProperty("gifts", out var ga) && ga.ValueKind == JsonValueKind.Array)
            foreach (var g in ga.EnumerateArray())
            {
                if (Long(g, "minQuantity") is not { } mq || mq < 1 || mq > 100000 || Guid_(g, "productId") is not { } gp || Long(g, "quantity") is not { } q || q < 1 || q > 100) return null;
                gifts.Add(new OfferGiftDoc((int)mq, gp, (int)q, (Str(g, "title") ?? "").Trim() is { Length: > 0 } title ? title[..Math.Min(title.Length, 120)] : "Free gift", !g.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False));
            }
        if (gifts.Count > MaxGifts || (tiers.Count == 0 && gifts.Count == 0)) return null;
        var name = (Str(e, "name") ?? "").Trim();
        return new OfferSnapshot(id, version, Long(e, "feedVersion") ?? 0, seller, brand, name.Length == 0 ? "Quantity offer" : name[..Math.Min(name.Length, 200)], (Str(e, "currency") ?? "USD").ToUpperInvariant(), kind,
            products, categories, tiers.OrderBy(t => t.MinQuantity).ToList(), gifts, Time(e, "activeFrom"), Time(e, "activeTo"), e.TryGetProperty("demo", out var d) && d.ValueKind == JsonValueKind.True,
            Time(e, "updatedAt") ?? DateTimeOffset.UnixEpoch);
    }

    private static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
    private static Guid? Guid_(JsonElement e, string n) => Str(e, n) is { } s && Guid.TryParse(s, out var g) ? g : null;
    private static DateTimeOffset? Time(JsonElement e, string n) => Str(e, n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    private static List<Guid> Guids(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out _)).Select(x => Guid.Parse(x.GetString()!)).ToList() : [];
}
