using Solvelt.Marketplace.Api.Offers;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The shared pricing function (decision Q-01) against the portal's C18 figures and rules.</summary>
public sealed class OfferPricingTests
{
    private static readonly Guid Seller = Guid.NewGuid(), Brand = Guid.NewGuid(), Other = Guid.NewGuid();
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), Gift1 = Guid.NewGuid(), Gift2 = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static OfferDef Offer(string scope = "products", Guid? id = null, DateTimeOffset? seen = null, IEnumerable<OfferTier>? tiers = null, IEnumerable<OfferGiftDef>? gifts = null, Guid[]? cats = null, Guid? brand = null)
        => new(id ?? Guid.NewGuid(), 2, Seller, brand ?? Brand, scope, new HashSet<Guid> { P1, P2 }, new HashSet<Guid>(cats ?? []), (tiers ?? [new OfferTier(20, 90)]).ToList(), (gifts ?? []).ToList(), seen ?? T0);

    private static OfferLineInput Line(Guid listing, int qty, long unit = 100, Guid? seller = null, Guid? brand = null, Guid[]? chain = null)
        => new(Guid.NewGuid(), listing, seller ?? Seller, brand ?? Brand, chain ?? [], unit, qty);

    [Fact]
    public void Twenty_five_units_at_a_single_ninety_cent_tier_cost_2250_cents()
    {
        var l = Line(P1, 25);
        var r = OfferPricing.Price([l], [Offer()]);
        Assert.Equal(2250, 25 * 100 - r.Lines.Single().DiscountCents);
    }

    [Theory]
    [InlineData(9, 900)]
    [InlineData(10, 950)]
    [InlineData(15, 1425)]
    [InlineData(19, 1805)]
    [InlineData(20, 1800)]
    [InlineData(25, 2250)]
    public void Two_tiers_price_every_eligible_unit_at_the_highest_step_reached(int units, long expected)
    {
        var l = Line(P1, units);
        var r = OfferPricing.Price([l], [Offer(tiers: [new OfferTier(10, 95), new OfferTier(20, 90)])]);
        Assert.Equal(expected, units * 100L - (r.For(l.Key)?.DiscountCents ?? 0));
    }

    [Fact]
    public void Units_are_counted_across_the_products_of_the_offer_and_the_discount_is_per_line()
    {
        var a = Line(P1, 12); var b = Line(P2, 8);
        var r = OfferPricing.Price([a, b], [Offer()]);
        Assert.Equal((120L, 80L), (r.For(a.Key)!.DiscountCents, r.For(b.Key)!.DiscountCents));
        Assert.Equal(20, r.Offers.Single().EligibleUnits);
    }

    [Fact]
    public void A_tier_never_raises_a_price_and_a_lower_sale_price_beats_the_tier()
    {
        var sale = Line(P1, 20, unit: 80);   // already below the 90 cent tier
        var full = Line(P2, 5, unit: 100);
        var r = OfferPricing.Price([sale, full], [Offer()]);
        Assert.Equal(0, r.For(sale.Key)!.DiscountCents);       // stays at 80, never raised to 90
        Assert.Equal(50, r.For(full.Key)!.DiscountCents);       // 25 units reached the tier: 5 x 10
    }

    [Fact]
    public void Units_of_other_sellers_and_other_brands_never_count()
    {
        var mine = Line(P1, 15);
        var otherSeller = Line(P1, 10, seller: Other);
        var otherBrand = Line(P1, 10, brand: Other);
        var r = OfferPricing.Price([mine, otherSeller, otherBrand], [Offer()]);
        Assert.Equal(15, r.Offers.Single().EligibleUnits);
        Assert.Null(r.Offers.Single().Tier);
        Assert.Empty(r.Lines.Where(x => x.Key != mine.Key));
        Assert.Equal(0, r.For(mine.Key)!.DiscountCents);
    }

    [Fact]
    public void A_gift_is_earned_once_whatever_the_quantity_and_gifts_never_stack_across_equal_steps()
    {
        var l = Line(P1, 40);
        var r = OfferPricing.Price([l], [Offer(gifts: [new OfferGiftDef(10, Gift1, 1, "Wallet"), new OfferGiftDef(10, Gift2, 1, "Pack")])]);
        var earned = r.Offers.Single().Gifts;
        Assert.Equal([Gift1, Gift2].Order(), earned.Select(g => g.ProductId).Order());
        Assert.All(earned, g => Assert.Equal(1, g.Quantity));
    }

    [Fact]
    public void No_gift_below_its_minimum_and_the_next_tier_is_reported_for_the_cart_message()
    {
        var l = Line(P1, 15);
        var r = OfferPricing.Price([l], [Offer(gifts: [new OfferGiftDef(20, Gift1, 1, "Wallet")])]).Offers.Single();
        Assert.Empty(r.Gifts);
        Assert.Equal((20, 90, 5), (r.NextTier!.MinQuantity, (int)r.NextTier.UnitPriceCents, r.UnitsToNextTier));
    }

    [Fact]
    public void The_most_specific_scope_prices_a_line_and_a_tie_goes_to_the_older_offer()
    {
        var cat = Guid.NewGuid();
        var broad = Offer("all", tiers: [new OfferTier(1, 50)]);
        var narrow = Offer("products", tiers: [new OfferTier(1, 90)]);
        var l = Line(P1, 2);
        Assert.Equal(narrow.OfferId, OfferPricing.Price([l], [broad, narrow]).For(l.Key)!.OfferId);

        var older = Offer("category", cats: [cat], seen: T0, tiers: [new OfferTier(1, 90)]);
        var newer = Offer("category", cats: [cat], seen: T0.AddDays(1), tiers: [new OfferTier(1, 80)]);
        var c = Line(Guid.NewGuid(), 2, chain: [cat]);
        Assert.Equal(older.OfferId, OfferPricing.Price([c], [newer, older]).For(c.Key)!.OfferId);
    }

    [Fact]
    public void A_category_scope_includes_descendants_and_an_unlinked_listing_is_not_covered()
    {
        var parent = Guid.NewGuid(); var child = Guid.NewGuid();
        var o = Offer("category", cats: [parent]);
        var inChild = Line(Guid.NewGuid(), 20, chain: [child, parent]);
        var elsewhere = Line(Guid.NewGuid(), 20, chain: [Guid.NewGuid()]);
        var r = OfferPricing.Price([inChild, elsewhere], [o]);
        Assert.Equal(200, r.For(inChild.Key)!.DiscountCents);
        Assert.Null(r.For(elsewhere.Key));
    }

    [Fact]
    public void No_offers_means_no_change()
    {
        Assert.Empty(OfferPricing.Price([Line(P1, 30)], []).Lines);
    }
}
