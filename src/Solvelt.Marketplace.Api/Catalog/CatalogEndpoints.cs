using System.Globalization;
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
public sealed record ListingsPage(IReadOnlyList<ListingCardDto> Items, int Page, int PageSize, int Total, int TotalPages);
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
    private static readonly string[] Sorts = ["newest", "price_asc", "price_desc", "title"];

    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/catalog").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("/categories", Categories);
        g.MapGet("/brands", Brands);
        g.MapGet("/listings", Listings);
        g.MapGet("/listings/{slugOrId}", Listing);
        return app;
    }

    /// <summary>Visible = last known portal state Active, not unlisted, seller active (CONTRACT.md §2).</summary>
    private static IQueryable<CatalogListing> Visible(MarketplaceDb db)
        => db.CatalogListings.AsNoTracking().Where(l => l.Visible && db.CatalogSellers.Any(s => s.Id == l.SellerId && s.Active));

    private static async Task<IResult> Categories(MarketplaceDb db, CancellationToken ct)
    {
        var categories = await db.CatalogCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var direct = await Visible(db).GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var byParent = categories.ToLookup(c => c.ParentId);
        CategoryNode? Build(CatalogCategory c, HashSet<Guid> path)
        {
            if (!path.Add(c.Id)) return null;   // a cycle in portal data must not loop forever
            var kids = byParent[c.Id].Select(k => Build(k, path)).Where(k => k is not null).Cast<CategoryNode>().Where(k => k.ListingCount > 0).ToList();
            path.Remove(c.Id);
            return new CategoryNode(c.Id, c.Slug, c.Name, direct.GetValueOrDefault(c.Id) + kids.Sum(k => k.ListingCount), kids);
        }
        var ids = categories.Select(c => c.Id).ToHashSet();
        var roots = categories.Where(c => c.ParentId is null || !ids.Contains(c.ParentId.Value)).Select(c => Build(c, [])).Where(n => n is not null && n.ListingCount > 0).ToList();
        return Results.Json(roots);
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

    private static async Task<IResult> Listings(MarketplaceDb db, string? category, string? brand, string? seller, string? type, string? sort, string? page, string? pageSize, CancellationToken ct)
    {
        if (type is not null && !ListingTypes.IsValid(type)) return Problems.BadRequest("invalid_query", "type must be 'product' or 'service'.");
        sort ??= "newest";
        if (!Sorts.Contains(sort)) return Problems.BadRequest("invalid_query", $"sort must be one of {string.Join(", ", Sorts)}.");
        if (!TryInt(page, 1, 1, 100_000, out var pageNo)) return Problems.BadRequest("invalid_query", "page must be a whole number from 1.");
        if (!TryInt(pageSize, DefaultPageSize, 1, MaxPageSize, out var size)) return Problems.BadRequest("invalid_query", $"pageSize must be a whole number from 1 to {MaxPageSize}.");

        Guid? sellerId = null;
        if (seller is not null) { if (!Guid.TryParse(seller, out var sg)) return Problems.BadRequest("invalid_query", "seller must be an id."); sellerId = sg; }

        var q = Visible(db);
        if (type is not null) q = q.Where(l => l.Type == type);
        if (sellerId is not null) q = q.Where(l => l.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(brand))
        {
            var brandId = Guid.TryParse(brand, out var bg) ? bg : await db.CatalogBrands.Where(b => b.Slug == brand).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            q = q.Where(l => l.BrandId == brandId);
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            var all = await db.CatalogCategories.AsNoTracking().ToListAsync(ct);
            var root = Guid.TryParse(category, out var cg) ? all.FirstOrDefault(c => c.Id == cg) : all.FirstOrDefault(c => c.Slug == category);
            var ids = root is null ? [] : Descendants(all, root.Id);
            q = q.Where(l => ids.Contains(l.CategoryId));
        }
        q = sort switch
        {
            "price_asc" => q.OrderBy(l => l.PriceFromCents).ThenBy(l => l.Id),
            "price_desc" => q.OrderByDescending(l => l.PriceFromCents).ThenBy(l => l.Id),
            "title" => q.OrderBy(l => l.TitleSort).ThenBy(l => l.Id),
            _ => q.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id),
        };
        var total = await q.CountAsync(ct);
        var rows = await q.Skip((pageNo - 1) * size).Take(size).ToListAsync(ct);
        var items = await ToCards(db, rows, ct);
        return Results.Json(new ListingsPage(items, pageNo, size, total, (int)Math.Ceiling(total / (double)size)));
    }

    private static async Task<IResult> Listing(MarketplaceDb db, string slugOrId, CancellationToken ct)
    {
        var q = Visible(db).Include(l => l.Variants).Include(l => l.Images).Include(l => l.Attributes).Include(l => l.Faqs).AsSplitQuery();
        var l = Guid.TryParse(slugOrId, out var id) ? await q.FirstOrDefaultAsync(x => x.Id == id, ct) : await q.FirstOrDefaultAsync(x => x.Slug == slugOrId, ct);
        if (l is null) return Problems.NotFound("Listing not found.");
        var brand = await db.CatalogBrands.AsNoTracking().FirstAsync(b => b.Id == l.BrandId, ct);
        var seller = await db.CatalogSellers.AsNoTracking().FirstAsync(s => s.Id == l.SellerId, ct);
        var categories = await db.CatalogCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var path = new List<CategoryCard>();
        for (var c = categories.GetValueOrDefault(l.CategoryId); c is not null && path.Count < 10 && path.All(p => p.Id != c.Id); c = c.ParentId is { } p ? categories.GetValueOrDefault(p) : null)
            path.Insert(0, new CategoryCard(c.Id, c.Slug, c.Name));
        return Results.Json(new ListingDetailDto(l.Id, l.Slug, l.Type, l.Title, l.Summary, l.Description, l.PriceFromCents, l.PriceToCents, l.CompareAtFromCents, l.Currency, l.Available,
            new BrandCard(brand.Id, brand.Slug, brand.Name), new SellerCard(seller.Id, seller.DisplayName), path,
            l.Variants.OrderBy(v => v.SortOrder).Select(v => new VariantItem(v.Id, v.Name, v.PriceCents, v.CompareAtCents, v.Available)).ToList(),
            l.Images.OrderBy(i => i.SortOrder).Select(i => new ImageCard(i.Id, i.Width, i.Height, i.Alt)).ToList(),
            l.Attributes.OrderBy(a => a.SortOrder).Select(a => new AttributeItem(a.Key, a.Value)).ToList(),
            l.Faqs.OrderBy(f => f.SortOrder).Select(f => new FaqItem(f.Question, f.Answer)).ToList(), l.CreatedAt, l.UpdatedAt));
    }

    private static async Task<List<ListingCardDto>> ToCards(MarketplaceDb db, List<CatalogListing> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var brands = await db.CatalogBrands.AsNoTracking().Where(b => rows.Select(r => r.BrandId).Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var sellers = await db.CatalogSellers.AsNoTracking().Where(s => rows.Select(r => r.SellerId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var categories = await db.CatalogCategories.AsNoTracking().Where(c => rows.Select(r => r.CategoryId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
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

    private static List<Guid> Descendants(List<CatalogCategory> all, Guid root)
    {
        var result = new List<Guid> { root };
        for (var i = 0; i < result.Count && result.Count <= all.Count; i++)
            foreach (var child in all.Where(c => c.ParentId == result[i] && !result.Contains(c.Id))) result.Add(child.Id);
        return result;
    }

    private static bool TryInt(string? raw, int fallback, int min, int max, out int value)
    {
        value = fallback;
        if (raw is null) return true;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }
}
