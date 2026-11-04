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

    private static async Task<List<CategoryNode>> CategoriesOf(CategoryMap map, IQueryable<CatalogListing> listings, CancellationToken ct)
    {
        var rows = await map.AllAsync(ct);
        var direct = await listings.GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return CatalogEndpoints.BuildTree(rows, direct);
    }

    private static async Task<IResult> Brand(MarketplaceDb db, CategoryMap map, string slugOrId, CancellationToken ct)
    {
        var brand = Guid.TryParse(slugOrId, out var id)
            ? await db.CatalogBrands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            : await db.CatalogBrands.AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slugOrId, ct);
        if (brand is null) return Problems.NotFound("Brand not found.");
        var listings = CatalogEndpoints.Visible(db).Where(l => l.BrandId == brand.Id);
        var count = await listings.CountAsync(ct);
        if (count == 0) return Problems.NotFound("Brand not found.");     // a brand with nothing visible does not exist as far as buyers can tell
        var seller = await db.CatalogSellers.AsNoTracking().FirstAsync(s => s.Id == brand.SellerId, ct);
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
    private static async Task<IResult> Sitemap(MarketplaceDb db, CategoryMap map, string kind, string? page, string? pageSize, CancellationToken ct)
    {
        if (!SitemapKinds.Contains(kind)) return Problems.NotFound();
        if (!CatalogEndpoints.TryInt(page, 1, 1, 100_000, out var pageNo)) return Problems.BadRequest("invalid_query", "page must be a whole number from 1.");
        if (!CatalogEndpoints.TryInt(pageSize, DefaultSitemapPageSize, 1, MaxSitemapPageSize, out var size)) return Problems.BadRequest("invalid_query", $"pageSize must be a whole number from 1 to {MaxSitemapPageSize}.");
        var visible = CatalogEndpoints.Visible(db);
        if (kind == "listings")      // the big one is paged in the database
        {
            var count = await visible.CountAsync(ct);
            var rows = await visible.OrderBy(l => l.Id).Skip((pageNo - 1) * size).Take(size).Select(l => new { l.Slug, l.UpdatedAt }).ToListAsync(ct);
            return Results.Json(new SitemapPage(kind, pageNo, size, count, (int)Math.Ceiling(count / (double)size), rows.Select(r => new SitemapEntry(r.Slug, r.UpdatedAt)).ToList()));
        }
        List<(string Key, Guid Id, DateTimeOffset At)> entries;
        switch (kind)
        {
            case "brands":
            {
                var newest = await visible.GroupBy(l => l.BrandId).Select(g => new { Id = g.Key, At = g.Max(l => l.UpdatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
                entries = (await db.CatalogBrands.AsNoTracking().Where(b => newest.Keys.Contains(b.Id)).Select(b => new { b.Slug, b.Id }).ToListAsync(ct)).Select(b => (b.Slug, b.Id, newest[b.Id])).ToList();
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
                entries = categories.Where(c => newest.ContainsKey(c.Id)).Select(c => (c.Slug, c.Id, newest[c.Id])).ToList();
                break;
            }
        }
        var ordered = entries.OrderBy(e => e.Id).ToList();       // by id: stable whatever the database collation does with slugs
        var items = ordered.Skip((pageNo - 1) * size).Take(size).Select(e => new SitemapEntry(e.Key, e.At)).ToList();
        return Results.Json(new SitemapPage(kind, pageNo, size, ordered.Count, (int)Math.Ceiling(ordered.Count / (double)size), items));
    }
}
