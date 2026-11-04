using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public sealed record BrandCard(Guid Id, string Slug, string Name);
public sealed record SellerCard(Guid Id, string DisplayName);
public sealed record CategoryCard(Guid Id, string Slug, string Name);
public sealed record ImageCard(Guid Id, int Width, int Height, string Alt);
public sealed record CategoryNode(Guid Id, string Slug, string Name, int ListingCount, IReadOnlyList<CategoryNode> Children);
public sealed record BrandItem(Guid Id, string Slug, string Name, SellerCard Seller, Guid? LogoImageId, int ListingCount);
public sealed record ListingCardDto(Guid Id, string Slug, string Type, string Title, string Summary, long PriceFromCents, long PriceToCents,
    long? CompareAtFromCents, string Currency, bool Available, BrandCard Brand, SellerCard Seller, CategoryCard Category, ImageCard? Image);
public sealed record Suggestion(string Kind, string Text, string Slug);
public sealed record SuggestResponse(string Query, IReadOnlyList<Suggestion> Suggestions);
public sealed record ListingsPage(IReadOnlyList<ListingCardDto> Items, int Page, int PageSize, int Total, int TotalPages, string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ListingFacets? Facets);
public sealed record VariantItem(Guid Id, string Name, long PriceCents, long? CompareAtCents, bool Available);
public sealed record AttributeItem(string Key, string Value);
public sealed record FaqItem(string Question, string Answer);
public sealed record ListingDetailDto(Guid Id, string Slug, string Type, string Title, string Summary, string Description, long PriceFromCents, long PriceToCents,
    long? CompareAtFromCents, string Currency, bool Available, BrandCard Brand, SellerCard Seller, IReadOnlyList<CategoryCard> CategoryPath,
    IReadOnlyList<VariantItem> Variants, IReadOnlyList<ImageCard> Images, IReadOnlyList<AttributeItem> Attributes, IReadOnlyList<FaqItem> Faqs,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Public, anonymous catalog reads (docs/API.md). Only visible listings of active sellers are ever returned; everything else is a 404.</summary>
public static class CatalogEndpoints
{
    public const int DefaultPageSize = 24, MaxPageSize = 100, MaxBrands = 500;
    public const int MaxSuggestions = 10, DefaultSuggestions = 8, MinSuggestChars = 2;

    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/catalog").RequireRateLimiting(RateLimitPolicies.Public);
        g.AddEndpointFilter(async (ctx, next) =>
        {
            // 200 answers may be cached for a few seconds (an unlist must reach buyers at once: docs/API.md); anything else never
            ctx.HttpContext.Response.OnStarting(static state =>
            {
                var res = (HttpResponse)state;
                if (!res.Headers.ContainsKey("Cache-Control")) res.Headers.CacheControl = res.StatusCode == 200 ? "public, max-age=5" : "no-store";
                return Task.CompletedTask;
            }, ctx.HttpContext.Response);
            return await next(ctx);
        });
        g.MapGet("/categories", Categories);
        g.MapGet("/brands", Brands);
        g.MapGet("/listings", Listings);
        g.MapGet("/suggest", Suggest);
        g.MapGet("/listings/{slugOrId}", Listing);
        g.MapStorefronts();
        return app;
    }

    /// <summary>Visible = last known portal state Active, not unlisted, seller active (CONTRACT.md §2).</summary>
    internal static IQueryable<CatalogListing> Visible(MarketplaceDb db)
        => db.CatalogListings.AsNoTracking().Where(l => l.Visible && db.CatalogSellers.Any(s => s.Id == l.SellerId && s.Active));

    private static async Task<IResult> Categories(MarketplaceDb db, CategoryMap map, CancellationToken ct)
    {
        var categories = await map.AllAsync(ct);
        var direct = await Visible(db).GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return Results.Json(BuildTree(categories, direct));
    }

    /// <summary>The tree, roots first, with the counts of the given direct counts rolled up; categories whose subtree has none are left out.</summary>
    internal static List<CategoryNode> BuildTree(List<CatalogCategory> categories, Dictionary<Guid, int> direct)
    {
        var byParent = categories.ToLookup(c => c.ParentId);
        CategoryNode? Build(CatalogCategory c, HashSet<Guid> path)
        {
            if (!path.Add(c.Id) || path.Count > CategoryRules.MaxDepth + 2) return null;   // a cycle in portal data must not loop forever, and a tree deeper than the sync allows is not walked (JSON has a depth limit too)
            var kids = byParent[c.Id].Select(k => Build(k, path)).Where(k => k is not null).Cast<CategoryNode>().Where(k => k.ListingCount > 0).ToList();
            path.Remove(c.Id);
            return new CategoryNode(c.Id, c.Slug, c.Name, direct.GetValueOrDefault(c.Id) + kids.Sum(k => k.ListingCount), kids);
        }
        var ids = categories.Select(c => c.Id).ToHashSet();
        return categories.Where(c => c.ParentId is null || !ids.Contains(c.ParentId.Value)).Select(c => Build(c, [])).Where(n => n is not null && n.ListingCount > 0).Cast<CategoryNode>().ToList();
    }

    private static async Task<IResult> Brands(MarketplaceDb db, string? sellerId, CancellationToken ct)
    {
        Guid? seller = null;
        if (sellerId is not null) { if (!Guid.TryParse(sellerId, out var sg)) return Problems.BadRequest("invalid_query", "sellerId must be an id."); seller = sg; }
        var counts = await Visible(db).Where(l => seller == null || l.SellerId == seller).GroupBy(l => l.BrandId)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var brands = await db.CatalogBrands.AsNoTracking().Where(b => counts.Keys.Contains(b.Id)).OrderBy(b => b.Name).Take(MaxBrands).ToListAsync(ct);
        var sellers = await db.CatalogSellers.AsNoTracking().Where(s => brands.Select(b => b.SellerId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return Results.Json(brands.Select(b => new BrandItem(b.Id, b.Slug, b.Name,
            new SellerCard(b.SellerId, sellers.GetValueOrDefault(b.SellerId)?.DisplayName ?? ""), b.LogoImageId, counts[b.Id])));
    }

    private static async Task<IResult> Listings(HttpRequest request, MarketplaceDb db, ISearchIndex index, CategoryMap map, CancellationToken ct)
    {
        var (query, error) = await ListingQuery.ParseAsync(request.Query, db, index, map, ct);
        if (query is null) return error!;
        var visible = Visible(db);
        var filtered = query.Apply(db, visible);
        var total = await filtered.CountAsync(ct);
        var (rows, next) = query.Sort == "relevance" && query.Searching ? await RelevanceSlice(filtered, query, ct) : await Slice(filtered, query, ct);
        var items = await ToCards(db, rows, map, ct);
        var facets = query.WantFacets ? await query.FacetsAsync(db, visible, map, ct) : null;
        return Results.Json(new ListingsPage(items, query.Cursor is null ? query.Page : 0, query.PageSize, total, (int)Math.Ceiling(total / (double)query.PageSize), next, facets));
    }

    /// <summary>One page in a database order, plus the cursor of the next one. Fetches one extra row to know whether there is a next page.</summary>
    private static async Task<(List<CatalogListing> Rows, string? Next)> Slice(IQueryable<CatalogListing> q, ListingQuery o, CancellationToken ct)
    {
        var sort = o.Sort == "relevance" ? "newest" : o.Sort;      // relevance without a query is the newest order
        if (o.Cursor is { } c) q = After(q, sort, c);
        q = sort switch
        {
            "price_asc" => q.OrderBy(l => l.PriceFromCents).ThenBy(l => l.Id),
            "price_desc" => q.OrderByDescending(l => l.PriceFromCents).ThenBy(l => l.Id),
            "title" => q.OrderBy(l => l.TitleSort).ThenBy(l => l.Id),
            _ => q.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id),    // newest; best_selling is a placeholder for it until M3 records sales
        };
        var skip = o.Cursor is null ? (o.Page - 1) * o.PageSize : 0;
        var rows = await q.Skip(skip).Take(o.PageSize + 1).ToListAsync(ct);
        var more = rows.Count > o.PageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        return (rows, more ? new ListingCursor(o.Sort, ListingCursor.KeyOf(sort, rows[^1]), rows[^1].Id).Encode() : null);
    }

    private static IQueryable<CatalogListing> After(IQueryable<CatalogListing> q, string sort, ListingCursor c)
    {
        var id = c.Id;
        switch (sort)
        {
            case "price_asc": { var p = long.Parse(c.Key, CultureInfo.InvariantCulture); return q.Where(l => l.PriceFromCents > p || (l.PriceFromCents == p && l.Id.CompareTo(id) > 0)); }
            case "price_desc": { var p = long.Parse(c.Key, CultureInfo.InvariantCulture); return q.Where(l => l.PriceFromCents < p || (l.PriceFromCents == p && l.Id.CompareTo(id) > 0)); }
            case "title": { var t = c.Key; return q.Where(l => l.TitleSort.CompareTo(t) > 0 || (l.TitleSort == t && l.Id.CompareTo(id) > 0)); }
            default: { var t = new DateTimeOffset(long.Parse(c.Key, CultureInfo.InvariantCulture), TimeSpan.Zero); return q.Where(l => l.CreatedAt < t || (l.CreatedAt == t && l.Id.CompareTo(id) > 0)); }
        }
    }

    /// <summary>Relevance order lives in memory (at most <see cref="SearchTerms.MaxMatches"/> ids): rank the filtered ids, cut at the cursor or page, then load only that page.</summary>
    private static async Task<(List<CatalogListing> Rows, string? Next)> RelevanceSlice(IQueryable<CatalogListing> filtered, ListingQuery o, CancellationToken ct)
    {
        var score = o.Matches.ToDictionary(m => m.ListingId, m => m.Score);
        IEnumerable<Guid> ranked = (await filtered.Select(l => l.Id).ToListAsync(ct)).OrderByDescending(id => score[id]).ThenBy(id => id);
        if (o.Cursor is { } c)
        {
            var cs = double.Parse(c.Key, CultureInfo.InvariantCulture);
            ranked = ranked.Where(id => score[id] < cs || (score[id] == cs && id.CompareTo(c.Id) > 0));
        }
        else ranked = ranked.Skip((o.Page - 1) * o.PageSize);
        var window = ranked.Take(o.PageSize + 1).ToList();
        var more = window.Count > o.PageSize;
        var pageIds = window.Take(o.PageSize).ToList();
        var rows = (await filtered.Where(l => pageIds.Contains(l.Id)).ToListAsync(ct)).OrderBy(l => pageIds.IndexOf(l.Id)).ToList();
        return (rows, more && rows.Count > 0 ? new ListingCursor("relevance", ListingCursor.KeyOf("relevance", rows[^1], score[rows[^1].Id]), rows[^1].Id).Encode() : null);
    }

    /// <summary>
    /// A listing that was shown and has been unlisted, archived or removed is 410 (permanent), so search engines drop it. A listing whose seller is
    /// suspended, a draft, a stub we only heard an event about, and anything unknown are all 404 with no hint they exist.
    /// </summary>
    private static async Task<bool> IsGone(MarketplaceDb db, string slugOrId, CancellationToken ct)
    {
        var q = db.CatalogListings.AsNoTracking().Where(l => !l.IsStub && !l.Visible && db.CatalogSellers.Any(s => s.Id == l.SellerId && s.Active));
        q = Guid.TryParse(slugOrId, out var id) ? q.Where(l => l.Id == id) : q.Where(l => l.Slug == slugOrId);
        return await q.AnyAsync(l => (l.UnlistedVersion != null && l.UnlistedVersion >= l.Version) || l.Status == "Archived" || l.Status == "Removed", ct);
    }

    private static async Task<IResult> Listing(MarketplaceDb db, CategoryMap map, string slugOrId, CancellationToken ct)
    {
        var q = Visible(db).Include(l => l.Variants).Include(l => l.Images).Include(l => l.Attributes).Include(l => l.Faqs).AsSplitQuery();
        var l = Guid.TryParse(slugOrId, out var id) ? await q.FirstOrDefaultAsync(x => x.Id == id, ct) : await q.FirstOrDefaultAsync(x => x.Slug == slugOrId, ct);
        if (l is null) return await IsGone(db, slugOrId, ct) ? Problems.Gone("This listing is no longer available.") : Problems.NotFound("Listing not found.");
        var brand = await db.CatalogBrands.AsNoTracking().FirstAsync(b => b.Id == l.BrandId, ct);
        var seller = await db.CatalogSellers.AsNoTracking().FirstAsync(s => s.Id == l.SellerId, ct);
        var path = (await map.PathAsync(l.CategoryId, ct)).Select(c => new CategoryCard(c.Id, c.Slug, c.Name)).ToList();
        return Results.Json(new ListingDetailDto(l.Id, l.Slug, l.Type, l.Title, l.Summary, l.Description, l.PriceFromCents, l.PriceToCents, l.CompareAtFromCents, l.Currency, l.Available,
            new BrandCard(brand.Id, brand.Slug, brand.Name), new SellerCard(seller.Id, seller.DisplayName), path,
            l.Variants.OrderBy(v => v.SortOrder).Select(v => new VariantItem(v.Id, v.Name, v.PriceCents, v.CompareAtCents, v.Available)).ToList(),
            l.Images.OrderBy(i => i.SortOrder).Select(i => new ImageCard(i.Id, i.Width, i.Height, i.Alt)).ToList(),
            l.Attributes.OrderBy(a => a.SortOrder).Select(a => new AttributeItem(a.Key, a.Value)).ToList(),
            l.Faqs.OrderBy(f => f.SortOrder).Select(f => new FaqItem(f.Question, f.Answer)).ToList(), l.CreatedAt, l.UpdatedAt));
    }

    /// <summary>Autocomplete: listing titles from the search index, plus brands and categories whose name starts with the text. Nothing below two characters.</summary>
    private static async Task<IResult> Suggest([FromQuery(Name = "q")] string? search, string? limit, ISearchIndex index, MarketplaceDb db, CategoryMap map, CancellationToken ct)
    {
        if (search is { Length: > SearchTerms.MaxQueryLength }) return Problems.BadRequest("invalid_query", $"q must be at most {SearchTerms.MaxQueryLength} characters.");
        if (!TryInt(limit, DefaultSuggestions, 1, MaxSuggestions, out var max)) return Problems.BadRequest("invalid_query", $"limit must be a whole number from 1 to {MaxSuggestions}.");
        var terms = SearchTerms.From(search);
        var words = SearchText.Normalize(search);
        if (words.Length < MinSuggestChars) return Results.Json(new SuggestResponse(search ?? "", []));

        var matches = await index.SearchAsync(db, terms, max, ct);
        var ids = matches.Select(m => m.ListingId).ToList();
        var titles = (await Visible(db).Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.Title, l.Slug }).ToListAsync(ct)).ToDictionary(l => l.Id);
        var result = matches.Where(m => titles.ContainsKey(m.ListingId)).Select(m => new Suggestion("listing", titles[m.ListingId].Title, titles[m.ListingId].Slug)).ToList();

        var last = terms[^1];
        bool StartsAWord(string name) => SearchText.Normalize(name).Split(' ').Any(w => w.StartsWith(last, StringComparison.Ordinal));
        var visibleBrandIds = await Visible(db).Select(l => l.BrandId).Distinct().ToListAsync(ct);
        var brands = (await db.CatalogBrands.AsNoTracking().Where(b => visibleBrandIds.Contains(b.Id)).OrderBy(b => b.Name).ToListAsync(ct)).Where(b => StartsAWord(b.Name)).Take(3).ToList();
        var visibleCategoryIds = (await Visible(db).Select(l => l.CategoryId).Distinct().ToListAsync(ct)).ToHashSet();
        var allCategories = await map.AllAsync(ct);
        var shown = new HashSet<Guid>();    // a category is worth suggesting when it, or something below it, has a visible listing
        var byId = allCategories.ToDictionary(c => c.Id);
        foreach (var id in visibleCategoryIds)
            for (var c = byId.GetValueOrDefault(id); c is not null && shown.Add(c.Id); c = c.ParentId is { } p ? byId.GetValueOrDefault(p) : null) { }
        var categories = allCategories.Where(c => shown.Contains(c.Id) && StartsAWord(c.Name)).Take(3).ToList();
        result.AddRange(brands.Select(b => new Suggestion("brand", b.Name, b.Slug)));
        result.AddRange(categories.Select(c => new Suggestion("category", c.Name, c.Slug)));
        return Results.Json(new SuggestResponse(search!, result.Take(max).ToList()));
    }

    private static async Task<List<ListingCardDto>> ToCards(MarketplaceDb db, List<CatalogListing> rows, CategoryMap map, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var brands = await db.CatalogBrands.AsNoTracking().Where(b => rows.Select(r => r.BrandId).Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var sellers = await db.CatalogSellers.AsNoTracking().Where(s => rows.Select(r => r.SellerId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var categories = await map.ByIdAsync(ct);
        var firstImages = (await db.CatalogImages.AsNoTracking().Where(i => ids.Contains(i.ListingId) && i.SortOrder == 0).ToListAsync(ct)).ToDictionary(i => i.ListingId);
        return rows.Select(l =>
        {
            var b = brands.GetValueOrDefault(l.BrandId); var s = sellers.GetValueOrDefault(l.SellerId); var c = categories.GetValueOrDefault(l.CategoryId);
            var img = firstImages.GetValueOrDefault(l.Id);
            return new ListingCardDto(l.Id, l.Slug, l.Type, l.Title, l.Summary, l.PriceFromCents, l.PriceToCents, l.CompareAtFromCents, l.Currency, l.Available,
                new BrandCard(l.BrandId, b?.Slug ?? "", b?.Name ?? ""), new SellerCard(l.SellerId, s?.DisplayName ?? ""), new CategoryCard(l.CategoryId, c?.Slug ?? "", c?.Name ?? ""),
                img is null ? null : new ImageCard(img.Id, img.Width, img.Height, img.Alt));
        }).ToList();
    }

    internal static bool TryInt(string? raw, int fallback, int min, int max, out int value)
    {
        value = fallback;
        if (raw is null) return true;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }
}
