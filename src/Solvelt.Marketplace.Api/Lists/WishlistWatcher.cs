using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Lists;

/// <summary>
/// Price-drop and back-in-stock notifications for wishlist items (M6), raised by the catalog sync inside the same unit of work as the snapshot (the caller saves). Each item keeps the price and the
/// availability the buyer last knew; a visible listing below that price, or available again after being unavailable, is ONE notification (the dedupe key holds the item and the snapshot version)
/// and moves the baseline, so the same change is never announced twice and a price that only went up quietly raises the baseline.
/// </summary>
public static class WishlistWatcher
{
    public static async Task OnListingAppliedAsync(MarketplaceDb db, TimeProvider time, CatalogListingView listing, CancellationToken ct)
    {
        if (!listing.Visible) return;
        var items = await db.WishlistItems.Where(i => i.ListingId == listing.Id).ToListAsync(ct);
        if (items.Count == 0) return;
        var wishlistIds = items.Select(i => i.WishlistId).Distinct().ToList();
        var owners = await db.Wishlists.AsNoTracking().Where(w => wishlistIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.AccountId, ct);
        var outbox = new OutboxWriter(db, time);
        var notified = new HashSet<(Guid Account, string Kind)>();   // one entry per buyer even when the listing is on several of their lists
        foreach (var item in items)
        {
            if (!owners.TryGetValue(item.WishlistId, out var accountId)) continue;
            var sameCurrency = string.Equals(item.BaselineCurrency, listing.Currency, StringComparison.Ordinal);
            var parameters = new Dictionary<string, object?> { ["listingId"] = listing.Id, ["slug"] = listing.Slug, ["title"] = Clip(listing.Title), ["currency"] = listing.Currency, ["priceCents"] = listing.PriceFromCents };
            if (sameCurrency && listing.PriceFromCents < item.BaselinePriceCents && notified.Add((accountId, NotificationKinds.WishlistPriceDrop)))
                Notifier.AddForAccount(db, outbox, time, accountId, NotificationKinds.WishlistPriceDrop, $"wishlist:{listing.Id:N}:{listing.Version}", null, null,
                    parameters.Concat([new KeyValuePair<string, object?>("previousPriceCents", item.BaselinePriceCents)]));
            if (!item.BaselineAvailable && listing.Available && notified.Add((accountId, NotificationKinds.WishlistBackInStock)))
                Notifier.AddForAccount(db, outbox, time, accountId, NotificationKinds.WishlistBackInStock, $"wishlist:{listing.Id:N}:{listing.Version}", null, null, parameters);
            item.BaselinePriceCents = listing.PriceFromCents; item.BaselineCurrency = listing.Currency; item.BaselineAvailable = listing.Available;
        }
    }

    private static string Clip(string s) => s.Length <= 120 ? s : s[..(char.IsHighSurrogate(s[119]) ? 119 : 120)];
}

/// <summary>The fields of a freshly applied listing the watcher needs (kept apart from the entity so the watcher is testable and the sync stays the only writer of listings).</summary>
public readonly record struct CatalogListingView(Guid Id, string Slug, string Title, string Currency, long PriceFromCents, bool Available, bool Visible, long Version);
