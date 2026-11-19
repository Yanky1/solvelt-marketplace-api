namespace Solvelt.Marketplace.Api.Offers;

public sealed record OfferTier(int MinQuantity, long UnitPriceCents);
public sealed record OfferGiftDef(int MinQuantity, Guid ProductId, int Quantity, string Title);

/// <summary>
/// An offer as the pricing function reads it (portal C18). <see cref="Scope"/> is <c>products</c>, <c>category</c> or <c>all</c>; <see cref="FirstSeenAt"/> breaks a tie between equally specific offers
/// (the older one wins, C18.1-2). Money is <c>long</c> cents.
/// </summary>
public sealed record OfferDef(Guid OfferId, long Version, Guid SellerId, Guid BrandId, string Scope, IReadOnlySet<Guid> ProductIds, IReadOnlySet<Guid> CategoryIds,
    IReadOnlyList<OfferTier> Tiers, IReadOnlyList<OfferGiftDef> Gifts, DateTimeOffset FirstSeenAt);

/// <summary>One physical product line. <see cref="CategoryChain"/> is the listing's category and all its ancestors. <see cref="UnitPriceCents"/> is the price charged before any offer, sale price included.</summary>
public sealed record OfferLineInput(Guid Key, Guid ListingId, Guid SellerId, Guid BrandId, IReadOnlyCollection<Guid> CategoryChain, long UnitPriceCents, int Quantity);

public sealed record OfferLineResult(Guid Key, Guid? OfferId, long DiscountCents);
public sealed record OfferGiftEarned(Guid ProductId, int Quantity, int MinQuantity, string Title);

/// <summary>
/// What one offer did to one cart: <see cref="EligibleUnits"/> (units of the lines it prices), the tier reached, the next tier (for "add N more"), the gifts earned once.
/// </summary>
public sealed record OfferOutcome(Guid OfferId, long Version, Guid SellerId, Guid BrandId, int EligibleUnits, OfferTier? Tier, OfferTier? NextTier, int UnitsToNextTier,
    IReadOnlyList<OfferGiftEarned> Gifts, long DiscountCents);

public sealed record OfferPricingResult(IReadOnlyList<OfferLineResult> Lines, IReadOnlyList<OfferOutcome> Offers)
{
    public static readonly OfferPricingResult None = new([], []);
    public OfferLineResult? For(Guid key) => Lines.FirstOrDefault(l => l.Key == key);
}

/// <summary>
/// THE one pricing function for quantity offers (decision Q-01): the cart, the quote, the checkout, the order sent to the portal and the voice read-back all call it, so they can never disagree. Pure: no
/// database, no clock, no I/O. Rules are the portal's C18.1: one offer per line (most specific scope, then the older offer), eligible units counted per offer, the highest tier reached prices every
/// eligible unit, a tier never raises a price (<c>max(0, unit - tier) x quantity</c>, so a lower sale price stays), each earned gift once per order.
/// </summary>
public static class OfferPricing
{
    private static int Specificity(string scope) => scope switch { "products" => 0, "category" => 1, _ => 2 };

    public static bool Covers(OfferDef o, OfferLineInput l)
    {
        if (l.SellerId != o.SellerId || l.BrandId != o.BrandId) return false;   // other sellers' and other brands' units never count
        return o.Scope switch
        {
            "products" => o.ProductIds.Contains(l.ListingId),
            "category" => l.CategoryChain.Any(o.CategoryIds.Contains),
            "all" => true,
            _ => false,
        };
    }

    public static OfferPricingResult Price(IReadOnlyList<OfferLineInput> lines, IReadOnlyList<OfferDef> offers)
    {
        if (lines.Count == 0 || offers.Count == 0) return OfferPricingResult.None;
        var ordered = offers.OrderBy(o => Specificity(o.Scope)).ThenBy(o => o.FirstSeenAt).ThenBy(o => o.OfferId).ToList();
        var pricedBy = new Dictionary<Guid, OfferDef>();
        foreach (var l in lines)
            if (ordered.FirstOrDefault(o => Covers(o, l)) is { } winner) pricedBy[l.Key] = winner;

        var results = new List<OfferLineResult>();
        var outcomes = new List<OfferOutcome>();
        foreach (var o in ordered)
        {
            var mine = lines.Where(l => pricedBy.TryGetValue(l.Key, out var w) && w.OfferId == o.OfferId).ToList();
            if (mine.Count == 0) continue;
            var units = mine.Sum(l => l.Quantity);
            var tiers = o.Tiers.OrderBy(t => t.MinQuantity).ToList();
            var tier = tiers.LastOrDefault(t => t.MinQuantity <= units);
            var next = tiers.FirstOrDefault(t => t.MinQuantity > units);
            long total = 0;
            foreach (var l in mine)
            {
                var d = tier is null ? 0 : Math.Max(0, l.UnitPriceCents - tier.UnitPriceCents) * l.Quantity;
                results.Add(new OfferLineResult(l.Key, o.OfferId, d));
                total += d;
            }
            var gifts = o.Gifts.Where(g => g.MinQuantity <= units && g.Quantity > 0).OrderBy(g => g.MinQuantity).ThenBy(g => g.ProductId)
                .Select(g => new OfferGiftEarned(g.ProductId, g.Quantity, g.MinQuantity, g.Title)).ToList();
            outcomes.Add(new OfferOutcome(o.OfferId, o.Version, o.SellerId, o.BrandId, units, tier, next, next is null ? 0 : next.MinQuantity - units, gifts, total));
        }
        // lines in the order they came in, never in offer order
        var byKey = results.ToDictionary(r => r.Key);
        return new OfferPricingResult(lines.Where(l => byKey.ContainsKey(l.Key)).Select(l => byKey[l.Key]).ToList(), outcomes);
    }
}
