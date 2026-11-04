using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public sealed record BrandStorefront(Guid Id, string Slug, string Name, Guid? LogoImageId, SellerCard Seller, int ListingCount, IReadOnlyList<CategoryNode> Categories);
public sealed record SellerStorefront(Guid Id, string DisplayName, int ListingCount, IReadOnlyList<BrandItem> Brands, IReadOnlyList<CategoryNode> Categories);
public sealed record SitemapEntry(string Key, DateTimeOffset LastModified);
public sealed record SitemapPage(string Kind, int Page, int PageSize, int Total, int TotalPages, IReadOnlyList<SitemapEntry> Items);

/// <summary>Brand and seller storefront data and the paged lists the web tier builds sitemaps from (M2). Same visibility rule as every catalog read.</summary>
public static class StorefrontEndpoints
{
    public const int DefaultSitemapPageSize = 1000, MaxSitemapPageSize = 5000;
    public static readonly string[] SitemapKinds = ["listings", "categories", "brands", "sellers"];

    public static RouteGroupBuilder MapStorefronts(this RouteGroupBuilder g)
    {
        g.MapGet("/brands/{slugOrId}", Brand);
        g.MapGet("/sellers/{id}", Seller);
        g.MapGet("/sitemap/{kind}", Sitemap);
        return g;
    }

    /// <summary>
    /// The page with validators: <c>ETag</c> (a hash of what the page says), <c>Last-Modified</c> (its newest entry, to the second), and <c>304</c> when the caller already
    /// holds that version (<c>If-None-Match</c>, else <c>If-Modified-Since</c>). The page itself is cheap to recompute; the point is that a crawler or the web tier's
    /// sitemap cache can revalidate instead of downloading tens of thousands of entries again.
    /// </summary>
    private static IResult Validated(HttpContext http, SitemapPage page)
    {
        var text = $"{page.Kind}|{page.Page}|{page.PageSize}|{page.Total}|" + string.Join(";", page.Items.Select(i => i.Key + "@" + i.LastModified.UtcTicks));
        var etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant() + "\"";
        var req = http.Request.Headers; var res = http.Response.Headers;
        res.ETag = etag;
        DateTimeOffset? last = page.Items.Count == 0 ? null : page.Items.Max(i => i.LastModified);
        if (last is { } l) { l = new DateTimeOffset(l.UtcTicks - l.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero); last = l; res.LastModified = l.ToString("R", CultureInfo.InvariantCulture); }
        var inm = req.IfNoneMatch.ToString();
        if (inm.Length > 0)
        {
            if (inm.Split(',', StringSplitOptions.TrimEntries).Any(t => t == "*" || t == etag || t == "W/" + etag)) return Results.StatusCode(304);
        }
        else if (last is { } lm && DateTimeOffset.TryParseExact(req.IfModifiedSince.ToString(), "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) && lm <= since)
            return Results.StatusCode(304);
        return Results.Json(page);
    }

    private static async Task<List<CategoryNode>> CategoriesOf(CategoryMap map, IQueryable<CatalogListing> listings, CancellationToken ct)
    {
        var rows = await map.AllAsync(ct);
        var direct = await listings.GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return CatalogEndpoints.BuildTree(rows, direct);
    }

    private static async Task<IResult> Brand(MarketplaceDb db, CategoryMap map, string slugOrId, CancellationToken ct)
    {
        if (ListingQuery.HasControl(slugOrId)) return Problems.NotFound("Brand not found.");
        var brand = Guid.TryParse(slugOrId, out var id)
            ? await db.CatalogBrands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            : await db.CatalogBrands.AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slugOrId, ct);
        if (brand is null) return Problems.NotFound("Brand not found.");
        var listings = CatalogEndpoints.Visible(db).Where(l => l.BrandId == brand.Id);
        var count = await listings.CountAsync(ct);
        if (count == 0) return Problems.NotFound("Brand not found.");     // a brand with nothing visible does not exist as far as buyers can tell
        // the seller shown is one that really has these listings: the brand feed's seller when it does, else the seller with the most of them (a brand row can name another seller)
        var bySeller = await listings.GroupBy(l => l.SellerId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        var sellerId = bySeller.Any(x => x.Id == brand.SellerId) ? brand.SellerId : bySeller.OrderByDescending(x => x.N).ThenBy(x => x.Id).First().Id;
        var seller = await db.CatalogSellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return Problems.NotFound("Brand not found.");
        return Results.Json(new BrandStorefront(brand.Id, brand.Slug, brand.Name, brand.LogoImageId, new SellerCard(seller.Id, seller.DisplayName), count, await CategoriesOf(map, listings, ct)));
    }

    private static async Task<IResult> Seller(MarketplaceDb db, CategoryMap map, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var sellerId)) return Problems.NotFound("Seller not found.");
        var seller = await db.CatalogSellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId && s.Active, ct);
        if (seller is null) return Problems.NotFound("Seller not found.");
        var listings = CatalogEndpoints.Visible(db).Where(l => l.SellerId == sellerId);
        var count = await listings.CountAsync(ct);
        if (count == 0) return Problems.NotFound("Seller not found.");
        var counts = await listings.GroupBy(l => l.BrandId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var brands = (await db.CatalogBrands.AsNoTracking().Where(b => counts.Keys.Contains(b.Id)).ToListAsync(ct)).OrderBy(b => b.Name, StringComparer.Ordinal)
            .Select(b => new BrandItem(b.Id, b.Slug, b.Name, new SellerCard(seller.Id, seller.DisplayName), b.LogoImageId, counts[b.Id])).ToList();
        return Results.Json(new SellerStorefront(seller.Id, seller.DisplayName, count, brands, await CategoriesOf(map, listings, ct)));
    }

    /// <summary>
    /// Pages of keys with a last-modified time, for sitemaps. <c>listings</c>: slug and the portal's updated time; <c>categories</c> and
    /// <c>brands</c>: slug and the newest update among their visible listings (a category counts its subtree); <c>sellers</c>: id and the same.
    /// Only entries with at least one visible listing appear. Order is by key, so pages are stable.
    /// </summary>
    private static async Task<IResult> Sitemap(HttpContext http, MarketplaceDb db, CategoryMap map, string kind, string? page, string? pageSize, CancellationToken ct)
    {
        if (ListingQuery.HasControl(kind) || !SitemapKinds.Contains(kind)) return Problems.NotFound();
        if (!CatalogEndpoints.TryInt(page, 1, 1, 100_000, out var pageNo)) return Problems.BadRequest("invalid_query", "page must be a whole number from 1.");
        if (!CatalogEndpoints.TryInt(pageSize, DefaultSitemapPageSize, 1, MaxSitemapPageSize, out var size)) return Problems.BadRequest("invalid_query", $"pageSize must be a whole number from 1 to {MaxSitemapPageSize}.");
        var visible = CatalogEndpoints.Visible(db);
        if (kind == "listings")      // the big one is paged in the database
        {
            var count = await visible.CountAsync(ct);
            var rows = await visible.OrderBy(l => l.Id).Skip((pageNo - 1) * size).Take(size).Select(l => new { l.Slug, l.UpdatedAt }).ToListAsync(ct);
            return Validated(http, new SitemapPage(kind, pageNo, size, count, (int)Math.Ceiling(count / (double)size), rows.Select(r => new SitemapEntry(r.Slug, r.UpdatedAt)).ToList()));
        }
        List<(string Key, Guid Id, DateTimeOffset At)> entries;
        switch (kind)
        {
            case "brands":
            {
                var newest = await visible.GroupBy(l => l.BrandId).Select(g => new { Id = g.Key, At = g.Max(l => l.UpdatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
                // last modified = the newer of the brand row's own change and its newest visible listing (so it does not move backwards when a listing leaves)
                entries = (await db.CatalogBrands.AsNoTracking().Where(b => newest.Keys.Contains(b.Id)).Select(b => new { b.Slug, b.Id, b.UpdatedAt }).ToListAsync(ct))
                    .Select(b => (b.Slug, b.Id, b.UpdatedAt > newest[b.Id] ? b.UpdatedAt : newest[b.Id])).ToList();
                break;
            }
            case "sellers":
            {
                var newest = await visible.GroupBy(l => l.SellerId).Select(g => new { Id = g.Key, At = g.Max(l => l.UpdatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
                entries = newest.Select(x => (x.Key.ToString("D"), x.Key, x.Value)).ToList();
                break;
            }
            default:     // categories: the newest listing anywhere in the subtree
            {
                var direct = await visible.GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, At = g.Max(l => l.UpdatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
                var categories = await map.AllAsync(ct);
                var byId = categories.ToDictionary(c => c.Id);
                var newest = new Dictionary<Guid, DateTimeOffset>();
                foreach (var (id, at) in direct)       // push each leaf time up through its ancestors (a cycle in portal data stops at the first repeat)
                    for (var c = byId.GetValueOrDefault(id); c is not null; c = c.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
                    {
                        if (newest.TryGetValue(c.Id, out var seen) && seen >= at) break;
                        newest[c.Id] = at;
                    }
                // a category's own change (a rename, a move) also counts for every category above it, so a parent is never older than what changed below it
                foreach (var c0 in categories.Where(c => newest.ContainsKey(c.Id)).ToList())
                    for (var c = c0; c is not null; c = c.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
                    {
                        if (newest.TryGetValue(c.Id, out var seen) && seen >= c0.UpdatedAt) { if (c.Id != c0.Id) break; else continue; }
                        newest[c.Id] = c0.UpdatedAt;
                    }
                entries = categories.Where(c => newest.ContainsKey(c.Id)).Select(c => (c.Slug, c.Id, c.UpdatedAt > newest[c.Id] ? c.UpdatedAt : newest[c.Id])).ToList();
                break;
            }
        }
        var ordered = entries.OrderBy(e => e.Id).ToList();       // by id: stable whatever the database collation does with slugs
        var items = ordered.Skip((pageNo - 1) * size).Take(size).Select(e => new SitemapEntry(e.Key, e.At)).ToList();
        return Validated(http, new SitemapPage(kind, pageNo, size, ordered.Count, (int)Math.Ceiling(ordered.Count / (double)size), items));
    }
}
