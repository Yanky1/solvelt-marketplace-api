using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public sealed record CountItem(string Value, int Count);
public sealed record NamedCount(Guid Id, string Slug, string Name, int Count);
public sealed record SellerCount(Guid Id, string DisplayName, int Count);
public sealed record PriceFacet(long? MinCents, long? MaxCents);
public sealed record ListingFacets(
    IReadOnlyList<CategoryNode> Categories, IReadOnlyList<NamedCount> Brands, IReadOnlyList<SellerCount> Sellers, IReadOnlyList<CountItem> Types,
    int Available, PriceFacet Price, IReadOnlyDictionary<string, IReadOnlyList<CountItem>> Attributes);

/// <summary>What the buyer asked for, parsed and validated; applied to the visible listings by <see cref="Apply"/>.</summary>
public sealed class ListingQuery
{
    public const int MaxAttrPairs = 12, MaxValuesPerKey = 10, MaxValueLength = 60;
    public const long MaxCents = 1_000_000_000_000;
    /// <summary>Attribute keys that get value counts in the facets (kosher certification and the delivery options).</summary>
    public static readonly string[] FacetKeys = ["kosher-certification", "kosher-status", "kosher-for-passover", "delivery"];
    public static readonly string[] DeliveryOptions = ["ships", "local", "at-home", "online"];
    public static readonly string[] Sorts = ["relevance", "newest", "best_selling", "price_asc", "price_desc", "title"];

    public string? Type;
    public Guid? SellerId;
    public bool BrandGiven; public Guid? BrandId;
    public bool CategoryGiven; public List<Guid> CategoryIds = [];
    public long? PriceMin, PriceMax;
    public bool AvailableOnly;
    public Dictionary<string, List<string>> Attrs = new();
    public bool Searching; public IReadOnlyList<SearchMatch> Matches = [];
    public string Sort = "newest";
    public int Page = 1, PageSize = CatalogEndpoints.DefaultPageSize;
    public ListingCursor? Cursor;
    public bool WantFacets;

    public enum Dim { Type, Seller, Brand, Category, Price, Availability, Attribute }

    public static async Task<(ListingQuery? Query, IResult? Error)> ParseAsync(IQueryCollection q, MarketplaceDb db, ISearchIndex index, CategoryMap map, CancellationToken ct)
    {
        IResult Bad(string m) => Problems.BadRequest("invalid_query", m);
        var r = new ListingQuery();
        string? One(string key) => q.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : null;
        foreach (var key in q.Keys)
            if (q[key].Count > 1 && key is not "attr") return (null, Bad($"{key} may be given once."));

        r.Type = One("type");
        if (r.Type is not null && !ListingTypes.IsValid(r.Type)) return (null, Bad("type must be 'product' or 'service'."));

        var search = One("q");
        if (search is { Length: > SearchTerms.MaxQueryLength }) return (null, Bad($"q must be at most {SearchTerms.MaxQueryLength} characters."));
        r.Searching = !string.IsNullOrWhiteSpace(search);
        r.Sort = One("sort") ?? (r.Searching ? "relevance" : "newest");
        if (!Sorts.Contains(r.Sort)) return (null, Bad($"sort must be one of {string.Join(", ", Sorts)}."));
        if (!CatalogEndpoints.TryInt(One("page"), 1, 1, 100_000, out r.Page)) return (null, Bad("page must be a whole number from 1."));
        if (!CatalogEndpoints.TryInt(One("pageSize"), CatalogEndpoints.DefaultPageSize, 1, CatalogEndpoints.MaxPageSize, out r.PageSize))
            return (null, Bad($"pageSize must be a whole number from 1 to {CatalogEndpoints.MaxPageSize}."));

        if (One("seller") is { } seller) { if (!Guid.TryParse(seller, out var sg)) return (null, Bad("seller must be an id.")); r.SellerId = sg; }

        if (!TryCents(One("priceMin"), out r.PriceMin) || !TryCents(One("priceMax"), out r.PriceMax)) return (null, Bad($"priceMin and priceMax must be whole cents from 0 to {MaxCents}."));
        if (r.PriceMin > r.PriceMax) return (null, Bad("priceMin must not be above priceMax."));

        switch (One("available"))
        {
            case null or "": break;
            case "true": r.AvailableOnly = true; break;
            default: return (null, Bad("available must be 'true' (or left out)."));
        }
        switch (One("facets"))
        {
            case null or "" or "false": break;
            case "true": r.WantFacets = true; break;
            default: return (null, Bad("facets must be 'true' or 'false'."));
        }

        // attribute filters: values of one key are alternatives (OR), different keys all have to hold (AND)
        var pairs = new List<(string Key, string Value)>();
        foreach (var raw in q.TryGetValue("attr", out var attrs) ? attrs : StringValues.Empty)
        {
            var parts = (raw ?? "").Split(':', 2);
            if (parts.Length != 2 || parts[0].Length is 0 or > 100 || parts[1].Length is 0 or > MaxValueLength) return (null, Bad("attr must look like key:value."));
            pairs.Add((parts[0], parts[1]));
        }
        foreach (var (param, key) in new[] { ("delivery", "delivery"), ("kosher", "kosher-certification") })
            if (One(param) is { Length: > 0 } list)
                foreach (var v in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (v.Length > MaxValueLength) return (null, Bad($"{param} values must be at most {MaxValueLength} characters."));
                    if (param == "delivery" && !DeliveryOptions.Contains(v)) return (null, Bad($"delivery must be a comma separated list of {string.Join(", ", DeliveryOptions)}."));
                    pairs.Add((key, v));
                }
        if (pairs.Count > MaxAttrPairs) return (null, Bad($"at most {MaxAttrPairs} attribute values may be given."));
        foreach (var g in pairs.GroupBy(p => p.Key))
        {
            var values = g.Select(p => p.Value).Distinct().ToList();
            if (values.Count > MaxValuesPerKey) return (null, Bad($"at most {MaxValuesPerKey} values per attribute."));
            r.Attrs[g.Key] = values;
        }

        if (One("brand") is { } brand && !string.IsNullOrWhiteSpace(brand))
        {
            r.BrandGiven = true;
            r.BrandId = Guid.TryParse(brand, out var bg) ? bg : await db.CatalogBrands.Where(b => b.Slug == brand).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        }
        if (One("category") is { } category && !string.IsNullOrWhiteSpace(category))
        {
            r.CategoryGiven = true;
            var root = await map.FindAsync(category, ct);
            r.CategoryIds = root is null ? [] : await map.DescendantsAsync(root.Id, ct);
        }

        if (r.Searching)
            r.Matches = SearchTerms.From(search) is { Count: > 0 } terms ? await index.SearchAsync(db, terms, SearchTerms.MaxMatches, ct) : [];

        if (One("cursor") is { } cursor)
        {
            if (q.ContainsKey("page")) return (null, Bad("use either page or cursor, not both."));
            r.Cursor = ListingCursor.Decode(cursor);
            if (r.Cursor is null || r.Cursor.Sort != r.Sort || !r.Cursor.KeyIsValid()) return (null, Bad("cursor is not valid for this request."));
        }
        return (r, null);
    }

    private static bool TryCents(string? raw, out long? value)
    {
        value = null;
        if (string.IsNullOrEmpty(raw)) return true;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v > MaxCents) return false;
        value = v;
        return true;
    }

    /// <summary>The visible listings with every filter applied except the dimension named in <paramref name="skip"/> (disjunctive facets: a facet shows what choosing one of its values would give).</summary>
    public IQueryable<CatalogListing> Apply(MarketplaceDb db, IQueryable<CatalogListing> q, Dim? skip = null, string? skipKey = null)
    {
        if (Type is not null && skip != Dim.Type) q = q.Where(l => l.Type == Type);
        if (SellerId is { } s && skip != Dim.Seller) q = q.Where(l => l.SellerId == s);
        if (BrandGiven && skip != Dim.Brand) { var b = BrandId; q = q.Where(l => l.BrandId == b); }
        if (CategoryGiven && skip != Dim.Category) { var ids = CategoryIds; q = q.Where(l => ids.Contains(l.CategoryId)); }
        if (skip != Dim.Price)
        {
            if (PriceMin is { } min) q = q.Where(l => l.PriceToCents >= min);     // some buyable price is at or above the minimum ...
            if (PriceMax is { } max) q = q.Where(l => l.PriceFromCents <= max);   // ... and some is at or below the maximum
        }
        if (AvailableOnly && skip != Dim.Availability) q = q.Where(l => l.Available);
        foreach (var (key, values) in Attrs)
        {
            if (skip == Dim.Attribute && key == skipKey) continue;
            var k = key; var vs = values;
            q = q.Where(l => db.CatalogAttributes.Any(a => a.ListingId == l.Id && a.Key == k && vs.Contains(a.Value)));
        }
        if (Searching) { var ids = Matches.Select(m => m.ListingId).ToList(); q = q.Where(l => ids.Contains(l.Id)); }
        return q;
    }

    public async Task<ListingFacets> FacetsAsync(MarketplaceDb db, IQueryable<CatalogListing> visible, CategoryMap map, CancellationToken ct)
    {
        var categoryRows = await map.AllAsync(ct);
        var direct = await Apply(db, visible, Dim.Category).GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var tree = CatalogEndpoints.BuildTree(categoryRows, direct);

        var brandCounts = await Apply(db, visible, Dim.Brand).GroupBy(l => l.BrandId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var brands = (await db.CatalogBrands.AsNoTracking().Where(b => brandCounts.Keys.Contains(b.Id)).ToListAsync(ct)).OrderBy(b => b.Name, StringComparer.Ordinal)
            .Take(CatalogEndpoints.MaxBrands).Select(b => new NamedCount(b.Id, b.Slug, b.Name, brandCounts[b.Id])).ToList();

        var sellerCounts = await Apply(db, visible, Dim.Seller).GroupBy(l => l.SellerId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var sellers = (await db.CatalogSellers.AsNoTracking().Where(s => sellerCounts.Keys.Contains(s.Id)).ToListAsync(ct)).OrderBy(s => s.DisplayName, StringComparer.Ordinal)
            .Select(s => new SellerCount(s.Id, s.DisplayName, sellerCounts[s.Id])).ToList();

        var types = (await Apply(db, visible, Dim.Type).GroupBy(l => l.Type).Select(g => new { V = g.Key, N = g.Count() }).ToListAsync(ct))
            .OrderBy(x => x.V, StringComparer.Ordinal).Select(x => new CountItem(x.V, x.N)).ToList();

        var available = await Apply(db, visible, Dim.Availability).CountAsync(l => l.Available, ct);
        var priced = Apply(db, visible, Dim.Price);
        var any = await priced.AnyAsync(ct);
        var price = any ? new PriceFacet(await priced.MinAsync(l => l.PriceFromCents, ct), await priced.MaxAsync(l => l.PriceToCents, ct)) : new PriceFacet(null, null);

        var attributes = new Dictionary<string, IReadOnlyList<CountItem>>();
        foreach (var key in FacetKeys)
        {
            var scope = Apply(db, visible, Dim.Attribute, key);
            var k = key;
            var counts = await db.CatalogAttributes.AsNoTracking().Where(a => a.Key == k && scope.Select(l => l.Id).Contains(a.ListingId))
                .GroupBy(a => a.Value).Select(g => new { V = g.Key, N = g.Select(a => a.ListingId).Distinct().Count() }).ToListAsync(ct);
            attributes[key] = counts.OrderByDescending(c => c.N).ThenBy(c => c.V, StringComparer.Ordinal).Select(c => new CountItem(c.V, c.N)).ToList();
        }
        return new ListingFacets(tree, brands, sellers, types, available, price, attributes);
    }
}

/// <summary>An opaque keyset position: the sort it belongs to plus the sort value and id of the last item of the previous page.</summary>
public sealed record ListingCursor(string Sort, string Key, Guid Id)
{
    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { Sort, Key, Id.ToString("D") })).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static ListingCursor? Decode(string text)
    {
        try
        {
            if (text.Length is 0 or > 400) return null;
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(padded));
            if (parts is not { Length: 3 } || !Guid.TryParse(parts[2], out var id) || !ListingQuery.Sorts.Contains(parts[0])) return null;
            return new ListingCursor(parts[0], parts[1], id);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    /// <summary>A cursor is client input: its key must be a value of the right type for its sort, or the keyset query could not be built.</summary>
    public bool KeyIsValid() => Sort switch
    {
        "price_asc" or "price_desc" => long.TryParse(Key, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= ListingQuery.MaxCents,
        "title" => Key.Length <= 300,
        "relevance" => double.TryParse(Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d),
        _ => long.TryParse(Key, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t <= DateTimeOffset.MaxValue.UtcTicks,
    };

    public static string KeyOf(string sort, CatalogListing l, double score = 0) => sort switch
    {
        "price_asc" or "price_desc" => l.PriceFromCents.ToString(CultureInfo.InvariantCulture),
        "title" => l.TitleSort,
        "relevance" => score.ToString("R", CultureInfo.InvariantCulture),
        _ => l.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),    // newest, best_selling
    };
}
