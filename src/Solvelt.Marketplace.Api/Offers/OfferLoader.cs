using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Offers;

/// <summary>An offer ready for <see cref="OfferPricing"/>, with the display name and which gifts can be shipped (decision Q-03).</summary>
public sealed record LoadedOffer(OfferDef Def, string Name, IReadOnlyDictionary<Guid, bool> GiftAvailable);

/// <summary>
/// Reads the offers that apply right now (projected, visible, inside their window, same currency) and each listing's category chain, so the cart, the quote, the checkout and the voice read-back all
/// feed <see cref="OfferPricing.Price"/> the same inputs (decision Q-03). The window is checked in memory (SQLite and PostgreSQL order offset values differently, and a seller has few offers).
/// </summary>
public sealed class OfferLoader(MarketplaceDb db, CategoryMap categories)
{
    public async Task<IReadOnlyList<LoadedOffer>> ActiveAsync(IReadOnlyCollection<Guid> sellerIds, DateTimeOffset now, CancellationToken ct)
    {
        if (sellerIds.Count == 0) return [];
        var rows = await db.CatalogOffers.AsNoTracking().Where(o => o.Visible && sellerIds.Contains(o.SellerId)).ToListAsync(ct);
        var result = new List<LoadedOffer>();
        foreach (var o in rows)
        {
            if (o.Currency != "USD" || (o.ActiveFrom is { } f && f > now) || (o.ActiveTo is { } t && t <= now)) continue;
            var tiers = JsonSerializer.Deserialize<List<OfferTier>>(o.TiersJson) ?? [];
            var gifts = JsonSerializer.Deserialize<List<OfferGiftDoc>>(o.GiftsJson) ?? [];
            var def = new OfferDef(o.Id, o.Version, o.SellerId, o.BrandId, o.Scope,
                (JsonSerializer.Deserialize<List<Guid>>(o.ProductIdsJson) ?? []).ToHashSet(), (JsonSerializer.Deserialize<List<Guid>>(o.CategoryIdsJson) ?? []).ToHashSet(),
                tiers, gifts.Select(g => new OfferGiftDef(g.MinQuantity, g.ProductId, g.Quantity, g.Title)).ToList(), o.FirstSeenAt, o.CreatedAtTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);
            result.Add(new LoadedOffer(def, o.Name, gifts.GroupBy(g => g.ProductId).ToDictionary(g => g.Key, g => g.All(x => x.Available))));
        }
        return result;
    }

    /// <summary>The listing's category and every ancestor, for category-scoped offers.</summary>
    public async Task<IReadOnlyCollection<Guid>> ChainAsync(Guid categoryId, CancellationToken ct)
        => (await categories.PathAsync(categoryId, ct)).Select(c => c.Id).DefaultIfEmpty(categoryId).ToList();
}
