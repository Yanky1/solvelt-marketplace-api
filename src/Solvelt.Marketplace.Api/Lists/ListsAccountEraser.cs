using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Lists;

/// <summary>Deleting an account deletes its wishlists (names are free text and can be personal; the share links stop working with them), their items and its recently-viewed list at once. Safe to run again.</summary>
public sealed class ListsAccountEraser(MarketplaceDb db) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var lists = db.Wishlists.Where(w => w.AccountId == accountId);
        await db.WishlistItems.Where(i => lists.Any(w => w.Id == i.WishlistId)).ExecuteDeleteAsync(ct);
        await lists.ExecuteDeleteAsync(ct);
        await db.RecentlyViewed.Where(r => r.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>"Download my data": the buyer's lists with their items, whether each is shared (never the link: only its hash exists), and the recently viewed listings.</summary>
public sealed class WishlistsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "wishlists";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var lists = await db.Wishlists.AsNoTracking().Where(w => w.AccountId == accountId).OrderBy(w => w.CreatedAt).ThenBy(w => w.Id).ToListAsync(ct);
        var ids = lists.Select(l => l.Id).ToList();
        var items = (await db.WishlistItems.AsNoTracking().Where(i => ids.Contains(i.WishlistId)).OrderBy(i => i.AddedAt).ThenBy(i => i.Id).ToListAsync(ct)).ToLookup(i => i.WishlistId);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => db.WishlistItems.Any(i => i.ListingId == l.Id && ids.Contains(i.WishlistId))).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        return new
        {
            wishlists = lists.Select(l => new
            {
                l.Name, l.CreatedAt, l.UpdatedAt, shared = l.ShareKeyHash != null, l.SharedAt,
                items = items[l.Id].Select(i => new { item = titles.GetValueOrDefault(i.ListingId), listingId = i.ListingId, i.AddedAt, priceWhenLastCheckedCents = i.BaselinePriceCents, currency = i.BaselineCurrency, lastPriceDropNotifiedAt = i.LastPriceNotifiedAt, lastBackInStockNotifiedAt = i.LastStockNotifiedAt }),
            }),
        };
    }
}

public sealed class RecentlyViewedDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "recently-viewed";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.RecentlyViewed.AsNoTracking().Where(r => r.AccountId == accountId).OrderByDescending(r => r.ViewedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => db.RecentlyViewed.Any(r => r.ListingId == l.Id && r.AccountId == accountId)).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        return new { recentlyViewed = rows.Select(r => new { item = titles.GetValueOrDefault(r.ListingId), listingId = r.ListingId, viewedAt = r.ViewedAt }) };
    }
}
