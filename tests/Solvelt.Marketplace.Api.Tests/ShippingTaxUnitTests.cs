using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Boundary cases of the pure shipping and tax functions (checkout review survivors).</summary>
public sealed class ShippingTaxUnitTests
{
    private static ShippingSettings Settings(long? freeAt) => new(Guid.NewGuid(), Guid.NewGuid(), 1, null,
        [new ShippingProfile(Guid.NewGuid(), "d", true, "USD", freeAt, [new ShippingZone("US", "domestic", ["US"], [new ShippingSpeedSetting("standard", true, "flat", 500, null, null)])])]);

    [Theory]
    [InlineData(1999, 500, false)]
    [InlineData(2000, 0, true)]      // the threshold itself is free
    [InlineData(2001, 0, true)]
    public void Free_shipping_starts_exactly_at_the_threshold(long items, long price, bool free)
    {
        var o = ShippingRates.Options(Settings(2000), "US", items, null, new DateOnly(2026, 11, 4), new ShippingOptions()).Options.Single();
        Assert.Equal((price, free), (o.PriceCents, o.Free));
    }

    private static ShippingSettings WeightSettings(long? freeAt) => new(Guid.NewGuid(), Guid.NewGuid(), 1, null,
        [new ShippingProfile(Guid.NewGuid(), "d", true, "USD", freeAt, [new ShippingZone("US", "domestic", ["US"], [new ShippingSpeedSetting("standard", true, "weight", null, 400, 300)])])]);

    [Fact]
    public void A_free_group_ships_free_even_when_a_weight_is_unknown_and_a_paid_one_still_needs_it()
    {
        var today = new DateOnly(2026, 11, 4);
        var free = ShippingRates.Options(WeightSettings(2000), "US", 2500, null, today, new ShippingOptions());
        var o = Assert.Single(free.Options);
        Assert.Equal((0L, true), (o.PriceCents, o.Free));
        Assert.Empty(free.Unavailable);
        var paid = ShippingRates.Options(WeightSettings(2000), "US", 1999, null, today, new ShippingOptions());   // below the threshold: unknown weight cannot be priced
        Assert.Empty(paid.Options);
        Assert.Equal("weight_unknown", Assert.Single(paid.Unavailable).Reason);
        Assert.Equal(400 + 2 * 300, Assert.Single(ShippingRates.Options(WeightSettings(2000), "US", 1999, 1500, today, new ShippingOptions()).Options).PriceCents);   // known weight: base + ceil(1.5 kg) x per kg
    }

    [Fact]
    public void No_threshold_never_means_free()
        => Assert.Equal(500, ShippingRates.Options(Settings(null), "US", 1_000_000, null, new DateOnly(2026, 11, 4), new ShippingOptions()).Options.Single().PriceCents);

    private static SimpleTaxCalculator Calc(Action<TaxOptions>? tweak = null)
    {
        var o = new TaxOptions(); tweak?.Invoke(o);
        return new SimpleTaxCalculator(Options.Create(o));
    }
    private static IReadOnlyList<TaxGroupInput> One(long shipping = 1000) => [new TaxGroupInput("g", shipping, [new TaxLineInput("l", 10_000, true)])];

    [Fact]
    public void Shipping_is_taxed_only_in_the_listed_states_items_everywhere_a_rate_exists()
    {
        var nj = Calc().Calculate("US", "NJ", One());                  // NJ has a rate but is not a shipping-taxable state
        Assert.Equal(663, nj.LineTaxCents["l"]);                        // 6.625% of 10000 = 662.5, half up
        Assert.Equal(0, nj.ShippingTaxCents["g"]);
        var ny = Calc().Calculate("US", "NY", One());                  // NY taxes shipping too
        Assert.Equal(888, ny.LineTaxCents["l"]);                        // 8.875% of 10000 = 887.5
        Assert.Equal(89, ny.ShippingTaxCents["g"]);                     // 8.875% of 1000 = 88.75
    }

    [Fact]
    public void A_state_configured_with_rate_zero_is_valid_and_taxes_nothing_and_an_unlisted_state_is_zero_too()
    {
        var zero = Calc(o => o.StateRates["OR"] = 0m).Calculate("US", "OR", One());
        Assert.Equal((0L, 0L), (zero.LineTaxCents["l"], zero.ShippingTaxCents["g"]));
        var unlisted = Calc().Calculate("US", "WY", One());
        Assert.Equal((0L, 0L), (unlisted.LineTaxCents["l"], unlisted.ShippingTaxCents["g"]));
    }
}
