using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The quote token and the order fingerprint, unit by unit (review K1, K3, K4, K5): exactly ONE input varied per case.</summary>
public sealed class QuoteBindingTests
{
    private static readonly Guid Seller = Guid.Parse("11111111-1111-1111-1111-111111111111"), Brand = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Variant = Guid.Parse("33333333-3333-3333-3333-333333333333"), Window = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static QuoteSigner Signer(string key = "test-quote-key-0123456789abcdef0123") => new(Options.Create(new CheckoutOptions { QuoteKey = key }));

    private static QuoteView Quote(Func<QuoteLine, QuoteLine>? line = null, Func<QuoteGroup, QuoteGroup>? group = null, Func<QuoteView, QuoteView>? view = null)
    {
        var l = new QuoteLine(Guid.NewGuid(), Guid.NewGuid(), Variant, "T", "V", 2, 1500, 3000, 266);
        l = line?.Invoke(l) ?? l;
        var g = new QuoteGroup(Seller, "S", Brand, "B", [l], 3000, [], [], null, new SelectedDelivery("carrier", "standard", null, null, 500), 500, 44, 310, 3810);
        g = group?.Invoke(g) ?? g;
        var v = new QuoteView("USD", [g], 3000, 500, 310, 3810, true);
        return view?.Invoke(v) ?? v;
    }

    private static string Token(QuoteView v, string country = "US", string? region = "NY", string postal = "11201") => Signer().Make(v, country, region, postal, Now);

    [Fact]
    public void Every_priced_field_changes_the_token_when_it_alone_is_changed()
    {
        var baseToken = Token(Quote());
        Assert.Equal(baseToken, Token(Quote()));                                                                        // deterministic
        var variants = new Dictionary<string, string>
        {
            ["total"] = Token(Quote(view: v => v with { TotalCents = v.TotalCents + 1 })),
            ["unit price"] = Token(Quote(line: l => l with { UnitPriceCents = l.UnitPriceCents + 1 })),
            ["quantity"] = Token(Quote(line: l => l with { Quantity = l.Quantity + 1 })),
            ["variant"] = Token(Quote(line: l => l with { VariantId = Guid.NewGuid() })),
            ["line tax"] = Token(Quote(line: l => l with { TaxCents = l.TaxCents + 1 })),
            ["currency"] = Token(Quote(view: v => v with { Currency = "EUR" })),
            ["speed"] = Token(Quote(group: g => g with { Selected = g.Selected! with { Speed = "overnight" } })),
            ["kind"] = Token(Quote(group: g => g with { Selected = g.Selected! with { Kind = "local" } })),
            ["window"] = Token(Quote(group: g => g with { Selected = g.Selected! with { WindowId = Window } })),
            ["window date"] = Token(Quote(group: g => g with { Selected = g.Selected! with { Date = "2026-11-06" } })),
            ["shipping"] = Token(Quote(group: g => g with { ShippingCents = g.ShippingCents + 1 })),
            ["shipping tax"] = Token(Quote(group: g => g with { ShippingTaxCents = g.ShippingTaxCents + 1 })),
            ["group tax"] = Token(Quote(group: g => g with { TaxCents = g.TaxCents + 1 })),
            ["seller"] = Token(Quote(group: g => g with { SellerId = Guid.NewGuid() })),
            ["brand"] = Token(Quote(group: g => g with { BrandId = Guid.NewGuid() })),
            ["region"] = Token(Quote(), region: "NJ"),
            ["country"] = Token(Quote(), country: "CA"),
            ["postal code"] = Token(Quote(), postal: "11202"),
        };
        foreach (var (field, token) in variants) Assert.True(token != baseToken, $"changing only the {field} must change the token");
        Assert.Equal(variants.Count, variants.Values.Distinct().Count());                                               // and no two of those changes collide
        Assert.Equal(baseToken, Token(Quote(), postal: " 11201 "));                                                      // the postal code is compared trimmed and upper case
    }

    [Fact]
    public void The_token_is_keyed_so_a_client_cannot_forge_it_or_move_the_expiry()
    {
        var quote = Quote();
        var token = Token(quote);
        var parts = token.Split('.');
        // the expiry five years out with the digest kept: refused (the digest covers the expiry)
        var moved = (long.Parse(parts[0]) + 5L * 365 * 24 * 3600) + "." + parts[1];
        Assert.False(Signer().Valid(moved, quote, "US", "NY", "11201", Now.AddHours(6)));
        Assert.False(Signer().Valid(token, quote, "US", "NY", "11201", Now.AddHours(6)));                              // and the real one has expired by then
        Assert.True(Signer().Valid(token, quote, "US", "NY", "11201", Now.AddMinutes(14)));
        // a plain SHA-256 of the same inputs (everything a client can see) is not accepted: the key is needed
        var exp = long.Parse(parts[0]) + 5L * 365 * 24 * 3600;
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(exp + "|USD|US|NY|11201|3810")));
        Assert.False(Signer().Valid(exp + "." + plain, quote, "US", "NY", "11201", Now.AddHours(6)));
        // another server key does not accept it either
        Assert.False(Signer("a-different-quote-key-0123456789abcdef").Valid(token, quote, "US", "NY", "11201", Now));
        // junk, empty, too long, no dot
        foreach (var junk in new[] { "", ".", "x", "1.", ".abc", new string('9', 200), "abc.def", null })
            Assert.False(Signer().Valid(junk, quote, "US", "NY", "11201", Now));
    }

    [Fact]
    public void Checkout_totals_are_refused_above_the_cap_exactly_at_it_they_are_allowed()
    {
        Assert.Equal(99_999_999, CartLimits.MaxTotalCents);
        Assert.False(100_000_000 <= CartLimits.MaxTotalCents);
        Assert.True(99_999_999 <= CartLimits.MaxTotalCents);                                                              // the boundary the comparison in QuoteService uses is `>`
        Assert.Equal(CartLimits.MaxTotalCents, CartLimits.MaxUnitPriceCents);
    }

    private static MarketplaceOrder Order(Action<MarketplaceOrder>? edit = null, Action<OrderGroup>? group = null)
    {
        var o = new MarketplaceOrder { AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555"), CartId = Guid.Parse("66666666-6666-6666-6666-666666666666"), Currency = "USD", TotalCents = 3810, ShipName = "Jane", ShipLine1 = "1 Main St", ShipCity = "Brooklyn", ShipPostalCode = "11201", ShipCountry = "US" };
        var g = new OrderGroup { SellerId = Seller, BrandId = Brand, DeliveryKind = "carrier", ShippingSpeed = "standard", ShippingCents = 500, TaxCents = 310 };
        group?.Invoke(g);
        o.Groups.Add(g);
        o.Lines.Add(new OrderLine { VariantId = Variant, Quantity = 2, UnitPriceCents = 1500, TaxCents = 266 });
        edit?.Invoke(o);
        return o;
    }

    [Fact]
    public void Free_text_fields_with_separators_cannot_make_two_different_checkouts_the_same_order()
    {
        // review K3: "Jane|1 Main St" + "Apt 2" and "Jane" + "1 Main St|Apt 2" used to hash alike
        var a = CheckoutService.Fingerprint(Order(o => { o.ShipName = "Jane|1 Main St"; o.ShipLine1 = "Apt 2"; }));
        var b = CheckoutService.Fingerprint(Order(o => { o.ShipName = "Jane"; o.ShipLine1 = "1 Main St|Apt 2"; }));
        Assert.NotEqual(a, b);
        Assert.NotEqual(CheckoutService.Fingerprint(Order(o => { o.ShipLine1 = "1;"; o.ShipLine2 = "x"; })), CheckoutService.Fingerprint(Order(o => { o.ShipLine1 = "1"; o.ShipLine2 = ";x"; })));
        Assert.NotEqual(CheckoutService.Fingerprint(Order(o => o.ShipLine2 = null)), CheckoutService.Fingerprint(Order(o => o.ShipLine2 = "")));   // absent is not empty
        // review S4: the group fields keep their own place (shipping 12 / tax 3 versus shipping 1 / tax 23)
        Assert.NotEqual(CheckoutService.Fingerprint(Order(group: g => { g.ShippingCents = 12; g.TaxCents = 3; })), CheckoutService.Fingerprint(Order(group: g => { g.ShippingCents = 1; g.TaxCents = 23; })));
        Assert.Equal(CheckoutService.Fingerprint(Order()), CheckoutService.Fingerprint(Order()));                          // the same checkout twice is the same value
    }

    private static ShippingSettings Settings(ShippingSpeedSetting speed) => new(Guid.NewGuid(), Guid.NewGuid(), 1, null,
        [new ShippingProfile(Guid.NewGuid(), "d", true, "USD", null, [new ShippingZone("US", "domestic", ["US"], [speed])])]);

    [Fact]
    public void An_absurd_fee_or_an_overflowing_weight_price_makes_the_speed_not_offered_never_a_wrapped_number()
    {
        var today = new DateOnly(2026, 11, 4);
        Assert.Single(ShippingRates.Options(Settings(new ShippingSpeedSetting("standard", true, "flat", CartLimits.MaxTotalCents, null, null)), "US", 100, null, today, new ShippingOptions()).Options);   // exactly at the cap: offered
        Assert.Empty(ShippingRates.Options(Settings(new ShippingSpeedSetting("standard", true, "flat", CartLimits.MaxTotalCents + 1, null, null)), "US", 100, null, today, new ShippingOptions()).Options);
        Assert.Empty(ShippingRates.Options(Settings(new ShippingSpeedSetting("standard", true, "flat", long.MaxValue, null, null)), "US", 100, null, today, new ShippingOptions()).Options);
        Assert.Empty(ShippingRates.Options(Settings(new ShippingSpeedSetting("standard", true, "weight", null, long.MaxValue - 5, 1000)), "US", 100, 5000, today, new ShippingOptions()).Options);        // base + 5 x 1000 overflows
        Assert.Empty(ShippingRates.Options(Settings(new ShippingSpeedSetting("standard", true, "weight", null, 1, long.MaxValue / 2)), "US", 100, 5000, today, new ShippingOptions()).Options);               // 5 x per kg overflows
    }
}
