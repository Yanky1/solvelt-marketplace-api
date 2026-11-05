using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Lists;

public sealed record ListCardImage(Guid Id, int Width, int Height, string Alt);
public sealed record ListCard(Guid Id, string Slug, string Type, string Title, long PriceFromCents, long PriceToCents, string Currency, bool Available, string BrandName, string SellerName, ListCardImage? Image);

/// <summary>The small listing card used by wishlists and recently viewed. Only listings that are visible on the storefront now (CONTRACT.md §2) are returned: a hidden one simply drops out.</summary>
public static class ListCards
{
    public static async Task<Dictionary<Guid, ListCard>> LoadAsync(MarketplaceDb db, IReadOnlyCollection<Guid> listingIds, CancellationToken ct)
    {
        if (listingIds.Count == 0) return [];
        var rows = await CatalogEndpoints.Visible(db).Where(l => listingIds.Contains(l.Id)).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var brandIds = rows.Select(r => r.BrandId).Distinct().ToList();
        var sellerIds = rows.Select(r => r.SellerId).Distinct().ToList();
        var brands = await db.CatalogBrands.AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var sellers = await db.CatalogSellers.AsNoTracking().Where(s => sellerIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        var images = (await db.CatalogImages.AsNoTracking().Where(i => ids.Contains(i.ListingId) && i.SortOrder == 0).ToListAsync(ct)).ToDictionary(i => i.ListingId);
        return rows.ToDictionary(l => l.Id, l =>
        {
            var img = images.GetValueOrDefault(l.Id);
            return new ListCard(l.Id, l.Slug, l.Type, l.Title, l.PriceFromCents, l.PriceToCents, l.Currency, l.Available, brands.GetValueOrDefault(l.BrandId, ""), sellers.GetValueOrDefault(l.SellerId, ""),
                img is null ? null : new ListCardImage(img.Id, img.Width, img.Height, img.Alt));
        });
    }
}
