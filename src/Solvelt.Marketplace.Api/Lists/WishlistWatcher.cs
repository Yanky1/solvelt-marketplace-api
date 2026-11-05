using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Lists;

/// <summary>
/// Price-drop and back-in-stock notifications for wishlist items (M6, decisions M6-02 and M6-03). The catalog sync only QUEUES a <c>wishlist.watch</c> outbox message per applied listing version
/// (<see cref="WishlistWatchQueue"/>, inside the snapshot's own unit of work, so the message exists exactly when the snapshot is saved); this handler does the work later, in its own transactions, ONE BUYER AT A TIME, so a problem with one
/// buyer's notification is logged and skipped and never stops the others or the catalog.
/// <para>
/// What is compared: each item keeps the LOWEST PRICE OF ALL VARIANTS (available or not) and the availability the buyer last knew. A price below that baseline is a price drop; a cheaper variant coming back in
/// stock does not change the lowest price of all variants, so it is not a false drop. An unavailable listing that is available again is a back-in-stock. The baseline always moves to the current figure, but a
/// buyer is told at most once per <see cref="Cooldown"/> per kind and listing (flapping stock cannot send a mail each time). Demo listings and listings that are not on the storefront (seller inactive,
/// unlisted) are skipped and leave the baseline alone. The dedupe key holds the ACCOUNT, the listing and the version: the key is unique over all notifications.
/// </para>
/// </summary>
public sealed class WishlistWatchHandler(MarketplaceDb db, TimeProvider time, ILogger<WishlistWatchHandler> log) : IOutboxHandler
{
    public const string Type = "wishlist.watch";
    public sealed record Payload(Guid ListingId);
    public static readonly TimeSpan Cooldown = TimeSpan.FromDays(7);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : System.Text.Json.JsonSerializer.Deserialize<Payload>(message.Payload, Solvelt.Marketplace.Api.Portal.PortalJson.Options); }
        catch (System.Text.Json.JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var listing = await CatalogEndpoints.Visible(db).Where(l => l.Id == p.ListingId && !l.IsDemoListing)
            .Select(l => new { l.Id, l.Slug, l.Title, l.Currency, l.Available, l.Version }).FirstOrDefaultAsync(ct);
        if (listing is null) return OutboxResult.Delivered();
        var lowest = await LowestPriceAsync(db, listing.Id, ct);
        if (lowest is not { } price) return OutboxResult.Delivered();

        var accounts = await (from i in db.WishlistItems.AsNoTracking() join w in db.Wishlists.AsNoTracking() on i.WishlistId equals w.Id where i.ListingId == listing.Id select w.AccountId).Distinct().ToListAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        foreach (var accountId in accounts)
        {
            try
            {
                var items = await db.WishlistItems.Where(i => i.ListingId == listing.Id && db.Wishlists.Any(w => w.Id == i.WishlistId && w.AccountId == accountId)).ToListAsync(ct);   // tracked: the baseline is saved below
                var sameCurrency = items.All(i => i.BaselineCurrency == listing.Currency);
                var drop = sameCurrency && items.Any(i => price < i.BaselinePriceCents);
                var restock = listing.Available && items.Any(i => !i.BaselineAvailable);
                var parameters = new Dictionary<string, object?> { ["listingId"] = listing.Id, ["slug"] = listing.Slug, ["title"] = Clip(listing.Title), ["currency"] = listing.Currency, ["priceCents"] = price };
                var outbox = new OutboxWriter(db, time);
                var key = $"wishlist:{accountId:N}:{listing.Id:N}:{listing.Version}";
                if (drop && !items.Any(i => i.LastPriceNotifiedAt is { } t && now - t < Cooldown))
                {
                    Notifier.AddForAccount(db, outbox, time, accountId, NotificationKinds.WishlistPriceDrop, key, null, null,
                        parameters.Concat([new KeyValuePair<string, object?>("previousPriceCents", items.Where(i => price < i.BaselinePriceCents).Max(i => i.BaselinePriceCents))]));
                    foreach (var i in items) i.LastPriceNotifiedAt = now;
                }
                if (restock && !items.Any(i => i.LastStockNotifiedAt is { } t && now - t < Cooldown))
                {
                    Notifier.AddForAccount(db, outbox, time, accountId, NotificationKinds.WishlistBackInStock, key, null, null, parameters);
                    foreach (var i in items) i.LastStockNotifiedAt = now;
                }
                foreach (var i in items) { i.BaselinePriceCents = price; i.BaselineCurrency = listing.Currency; i.BaselineAvailable = listing.Available; }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();   // this buyer is skipped (their baseline did not move, so the next version tries again); the others and the catalog are untouched
                log.LogWarning("Wishlist notification skipped for listing {ListingId}: {Type}", listing.Id, ex.GetType().Name);
            }
        }
        return OutboxResult.Delivered();
    }

    /// <summary>The lowest price over ALL variants of the listing (available or not); null when it has none.</summary>
    public static async Task<long?> LowestPriceAsync(MarketplaceDb db, Guid listingId, CancellationToken ct)
        => await db.CatalogVariants.AsNoTracking().Where(v => v.ListingId == listingId).Select(v => (long?)v.PriceCents).MinAsync(ct);

    private static string Clip(string s) => s.Length <= 120 ? s : s[..(char.IsHighSurrogate(s[119]) ? 119 : 120)];
}

/// <summary>
/// Called by the catalog sync while a snapshot is applied: adds the watch message to the SAME unit of work as the snapshot (nothing is written here), so the message exists exactly when the
/// snapshot is saved: the worker can never see it before the new data, and a failed save leaves no used-up key (review MED-1). The key holds the listing version, which the sync applies and saves
/// once (a stale or duplicate snapshot is refused before this runs), so the unique key cannot be hit by a normal run; the existence check covers a replay.
/// </summary>
public static class WishlistWatchQueue
{
    public static async Task QueueAsync(MarketplaceDb db, TimeProvider time, Guid listingId, long version, CancellationToken ct)
    {
        var key = $"wishlist-watch:{listingId:N}:{version}";
        var outbox = new OutboxWriter(db, time);
        if (await outbox.ExistsAsync(key, ct) || db.OutboxMessages.Local.Any(m => m.DedupeKey == key)) return;
        outbox.Enqueue(WishlistWatchHandler.Type, new WishlistWatchHandler.Payload(listingId), key);
    }
}
