using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class QuoteTests : IDisposable
{
    private static readonly Guid Mazel = FixtureCatalogSource.Id("seller:mazel"), ChallahCorner = FixtureCatalogSource.Id("brand:challah-corner"),
        MazelBakery = FixtureCatalogSource.Id("brand:mazel-bakery"), Judaica = FixtureCatalogSource.Id("seller:judaica"), ShabbatTable = FixtureCatalogSource.Id("brand:shabbat-table");

    private readonly AuthKit _k = new();
    private string _token = "";

    public void Dispose() => _k.Dispose();

    private static Guid Variant(string slug, int i = 0) => FixtureCatalogSource.Id($"variant:{slug}:{i}");

    private bool _weightOnly;

    private string Settings(Guid seller, Guid brand, long? threshold = 2000, int handling = 2) => JsonSerializer.Serialize(new
    {
        sellerId = seller, brandId = brand, handlingDays = handling, shipsFrom = new { country = "US", region = "NY" },
        profiles = new[] { new { id = Guid.NewGuid(), name = "Default", isDefault = true, currency = "USD", freeShippingThresholdCents = threshold, zones = new[] { new { name = "Domestic", kind = "domestic", countries = new[] { "US" },
            speeds = _weightOnly
                ? new object[] { new { speed = "standard", enabled = true, method = "weight", baseCents = 400, perKgCents = 100 }, new { speed = "overnight", enabled = true, method = "weight", baseCents = 1500, perKgCents = 300 } }
                : new object[] { new { speed = "standard", enabled = true, method = "flat", flatCents = 500 }, new { speed = "overnight", enabled = true, method = "weight", baseCents = 1500, perKgCents = 300 }, new { speed = "expedited", enabled = false, method = "flat", flatCents = 900 } } } } } },
    });

    private void PortalAnswers(Func<ReceivedCall, HttpResponseMessage>? local = null)
        => _k.F.Portal.RespondAlways(c =>
        {
            if (c.PathAndQuery.StartsWith("/api/internal/v1/shipping/settings"))
            {
                var q = System.Web.HttpUtility.ParseQueryString(c.PathAndQuery[(c.PathAndQuery.IndexOf('?') + 1)..]);
                return FakePortal.Json(HttpStatusCode.OK, Settings(Guid.Parse(q["sellerId"]!), Guid.Parse(q["brandId"]!)));
            }
            if (c.PathAndQuery.StartsWith("/api/internal/v1/local-delivery/eligibility") && local is not null) return local(c);
            return FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        });

    private async Task<string> Cart(params (string Slug, int Qty, int Variant)[] items)
    {
        await CatalogKit.Sync(_k.F);
        string? token = null;
        foreach (var (slug, qty, v) in items)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/lines") { Content = JsonContent.Create(new { variantId = Variant(slug, v), quantity = qty }) };
            if (token != null) r.Headers.Add(CartEndpoints.GuestHeader, token);
            var res = await _k.Http.SendAsync(r);
            var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            Assert.True(res.IsSuccessStatusCode, body.ToString());
            token ??= body.GetProperty("cartToken").GetString();
        }
        return _token = token!;
    }

    private async Task<HttpResponseMessage> Quote(object body, string? cart = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(body) };
        r.Headers.Add(CartEndpoints.GuestHeader, cart ?? _token);
        return await _k.Http.SendAsync(r);
    }

    private static object Addr(string? region = "NY", string country = "US") => new { country, region, postalCode = "11201" };

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var s = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {s}");
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    private static JsonElement Group(JsonElement q, Guid brand) => q.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("brandId").GetGuid() == brand);
    private static long Price(JsonElement g, string speed) => g.GetProperty("shippingOptions").EnumerateArray().Single(o => o.GetProperty("speed").GetString() == speed).GetProperty("priceCents").GetInt64();

    [Fact]
    public async Task Shipping_is_flat_or_weight_based_and_disabled_speeds_are_not_offered()
    {
        PortalAnswers();
        await Cart(("challah-classic", 2, 0));       // 2 x 550 g = 1100 g -> 2 kg
        var q = await Ok(await Quote(new { address = Addr() }));
        var g = Group(q, ChallahCorner);
        Assert.Equal(500, Price(g, "standard"));
        Assert.Equal(1500 + 2 * 300, Price(g, "overnight"));     // ceil(1100 / 1000) = 2 kg
        Assert.DoesNotContain(g.GetProperty("shippingOptions").EnumerateArray(), o => o.GetProperty("speed").GetString() == "expedited");
        Assert.Equal("standard", g.GetProperty("selected").GetProperty("speed").GetString());   // cheapest by default
        Assert.Equal(500, g.GetProperty("shippingCents").GetInt64());
    }

    [Fact]
    public async Task Free_shipping_looks_at_the_items_subtotal_of_that_seller_brand_group_only()
    {
        PortalAnswers();
        // group A: 1300 (below the 2000 threshold) although the cart as a whole is far above it; group B: 4200 -> free
        await Cart(("challah-classic", 2, 0), ("kiddush-cup-pewter", 1, 0));
        var q = await Ok(await Quote(new { address = Addr() }));
        var a = Group(q, ChallahCorner);
        var b = Group(q, ShabbatTable);
        Assert.Equal(1300, a.GetProperty("itemsCents").GetInt64());
        Assert.Equal(500, a.GetProperty("shippingCents").GetInt64());
        Assert.False(a.GetProperty("shippingOptions")[0].GetProperty("free").GetBoolean());
        Assert.Equal(4200, b.GetProperty("itemsCents").GetInt64());
        Assert.Equal(0, b.GetProperty("shippingCents").GetInt64());
        Assert.All(b.GetProperty("shippingOptions").EnumerateArray(), o => { Assert.True(o.GetProperty("free").GetBoolean()); Assert.Equal(0, o.GetProperty("priceCents").GetInt64()); });
    }

    [Fact]
    public async Task A_threshold_met_exactly_is_free()
    {
        PortalAnswers();
        await Cart(("challah-whole-wheat", 1, 0), ("challah-rolls-dozen", 1, 0), ("challah-classic", 1, 0));   // 750 + 1450 = 2200 >= 2000
        var g = Group(await Ok(await Quote(new { address = Addr() })), ChallahCorner);
        Assert.Equal(0, g.GetProperty("shippingCents").GetInt64());
    }

    [Fact]
    public async Task Tax_is_per_line_rounded_half_up_exempts_food_taxes_shipping_where_configured_and_totals_are_exact_sums()
    {
        PortalAnswers();
        await Cart(("challah-classic", 1, 0), ("kiddush-cup-pewter", 1, 0), ("shabbat-candles-72", 1, 0));
        var q = await Ok(await Quote(new { address = Addr("NY") }));
        var food = Group(q, ChallahCorner);
        Assert.Equal(0, food.GetProperty("taxCents").GetInt64());                           // unprepared food: exempt, and no taxable item means no shipping tax
        var goods = Group(q, ShabbatTable).GetProperty("lines");
        Assert.Equal(373, goods[0].GetProperty("taxCents").GetInt64());                      // 4200 x 8.875 % = 372.75 -> 373
        var judaica = q.GetProperty("groups").EnumerateArray().Where(g => g.GetProperty("sellerId").GetGuid() == Judaica).ToList();
        foreach (var grp in q.GetProperty("groups").EnumerateArray())
        {
            var lineTax = grp.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("taxCents").GetInt64());
            Assert.Equal(lineTax + grp.GetProperty("shippingTaxCents").GetInt64(), grp.GetProperty("taxCents").GetInt64());
            Assert.Equal(grp.GetProperty("itemsCents").GetInt64() + grp.GetProperty("shippingCents").GetInt64() + grp.GetProperty("taxCents").GetInt64(), grp.GetProperty("totalCents").GetInt64());
        }
        Assert.Equal(q.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("totalCents").GetInt64()), q.GetProperty("totalCents").GetInt64());
        Assert.Equal(q.GetProperty("itemsCents").GetInt64() + q.GetProperty("shippingCents").GetInt64() + q.GetProperty("taxCents").GetInt64(), q.GetProperty("totalCents").GetInt64());
        Assert.NotEmpty(judaica);

        // outside the US and in a state without a rate: no tax at all
        Assert.Equal(0, (await Ok(await Quote(new { address = Addr("ON", "CA") }))).GetProperty("taxCents").GetInt64());
        Assert.Equal(0, (await Ok(await Quote(new { address = Addr("OR") }))).GetProperty("taxCents").GetInt64());
    }

    [Fact]
    public void Tax_rounds_half_up_in_exact_decimal_arithmetic()
    {
        Assert.Equal(89, SimpleTaxCalculator.Round(1000, 8.875m));    // 88.75
        Assert.Equal(1, SimpleTaxCalculator.Round(2, 25m));          // 0.5 -> 1 (half up, never banker's)
        Assert.Equal(2, SimpleTaxCalculator.Round(10, 15m));         // 1.5 -> 2
        Assert.Equal(0, SimpleTaxCalculator.Round(1, 25m));          // 0.25
        Assert.Equal(1, SimpleTaxCalculator.Round(3, 17m));          // 0.51
        Assert.Equal(0, SimpleTaxCalculator.Round(0, 8m));
        Assert.Equal(0, SimpleTaxCalculator.Round(-5, 8m));
        Assert.Equal(8_875_000_000_000L, SimpleTaxCalculator.Round(100_000_000_000_000L, 8.875m));
    }

    [Fact]
    public async Task Client_supplied_prices_totals_tax_and_time_are_ignored()
    {
        PortalAnswers();
        await Cart(("kiddush-cup-pewter", 1, 0));
        var honest = await Ok(await Quote(new { address = Addr() }));
        var forged = await Ok(await Quote(new
        {
            address = Addr(), totalCents = 1, itemsCents = 1, shippingCents = 0, taxCents = 0, priceCents = 1, unitPriceCents = 1, now = "2020-01-01T00:00:00Z",
            selections = new[] { new { sellerId = Judaica, brandId = ShabbatTable, speed = "standard", shippingCents = 0, priceCents = 0 } },
        }));
        Assert.Equal(honest.GetProperty("totalCents").GetInt64(), forged.GetProperty("totalCents").GetInt64());
        Assert.Equal(4200 + 373, honest.GetProperty("totalCents").GetInt64());   // free shipping (4200 >= 2000)
    }

    [Fact]
    public async Task Bad_addresses_selections_and_carts_are_refused_without_guessing()
    {
        PortalAnswers();
        await Cart(("challah-classic", 1, 0));
        foreach (var bad in new object[] { new { }, new { address = new { country = "USA", postalCode = "11201" } }, new { address = new { country = "US", postalCode = "" } }, new { address = new { country = "US", region = "N1", postalCode = "1" } } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Quote(bad)).StatusCode);
        var bogusSpeed = await Ok(await Quote(new { address = Addr(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, speed = "teleport" } } }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("invalid_selection", bogusSpeed.GetProperty("code").GetString());
        var disabled = await Quote(new { address = Addr(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, speed = "expedited" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Quote(new { address = Addr(), selections = new[] { new { sellerId = Mazel, brandId = Guid.NewGuid(), speed = "standard" } } })).StatusCode);
        // outside every zone: nothing to ship with, so the quote cannot be paid
        var abroad = await Ok(await Quote(new { address = Addr(null, "DE") }));
        Assert.False(abroad.GetProperty("canPay").GetBoolean());
        // empty cart
        var empty = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(new { address = Addr() }) };
        Assert.Equal(HttpStatusCode.Conflict, (await _k.Http.SendAsync(empty)).StatusCode);
        // a cart holding an item that stopped being available
        var f = CatalogKit.Fixture(_k.F);
        f.Upsert(CatalogKit.ListingId("challah-classic"), l => l with { Status = "Archived" });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(HttpStatusCode.Conflict, (await Quote(new { address = Addr() })).StatusCode);
    }

    [Fact]
    public async Task Local_delivery_windows_come_from_the_portal_and_a_chosen_window_must_be_one_of_them()
    {
        var window = Guid.NewGuid();
        PortalAnswers(c => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            eligible = true, method = "local", feeCents = 400, zone = new { id = Guid.NewGuid(), name = "Brooklyn" }, timeZone = "America/New_York",
            windows = new[] { new { windowId = window, date = "2026-11-06", start = "2026-11-06T17:00:00Z", end = "2026-11-06T19:00:00Z", remaining = 2 }, new { windowId = Guid.NewGuid(), date = "2026-11-06", start = "2026-11-06T19:00:00Z", end = "2026-11-06T21:00:00Z", remaining = 0 } },
        })));
        await Cart(("challah-classic", 1, 0));       // bakery: delivery attribute includes "local"
        var q = await Ok(await Quote(new { address = Addr() }));
        var g = Group(q, ChallahCorner);
        Assert.True(g.GetProperty("local").GetProperty("eligible").GetBoolean());
        var call = _k.F.Portal.VerifiedCalls.Last(c => c.PathAndQuery.Contains("local-delivery/eligibility"));
        using (var sent = JsonDocument.Parse(call.BodyText))
            Assert.Equal(650, sent.RootElement.GetProperty("subtotalCents").GetInt64());   // OUR group subtotal

        var pick = await Ok(await Quote(new { address = Addr(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, localWindowId = window, localDate = "2026-11-06" } } }));
        var sel = Group(pick, ChallahCorner).GetProperty("selected");
        Assert.Equal("local", sel.GetProperty("kind").GetString());
        Assert.Equal(400, sel.GetProperty("shippingCents").GetInt64());     // the portal's fee, not a client value
        // a full window, a wrong date, an unknown window, or a speed mixed with a window
        foreach (var bad in new object[]
        {
            new { sellerId = Mazel, brandId = ChallahCorner, speed = (string?)null, localWindowId = (Guid?)Guid.NewGuid(), localDate = (string?)"2026-11-06" },
            new { sellerId = Mazel, brandId = ChallahCorner, speed = (string?)null, localWindowId = (Guid?)window, localDate = (string?)"2026-11-07" },
            new { sellerId = Mazel, brandId = ChallahCorner, speed = (string?)"standard", localWindowId = (Guid?)window, localDate = (string?)"2026-11-06" },
        })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Quote(new { address = Addr(), selections = new[] { bad } })).StatusCode);
        // food shipped from a non-local product does not even ask the portal
        var calls = _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains("local-delivery/eligibility"));
        await Cart(("kiddush-cup-pewter", 1, 0));
        await Quote(new { address = Addr() });
        Assert.Equal(calls, _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains("local-delivery/eligibility")));
    }

    [Fact]
    public async Task A_failing_local_delivery_check_only_hides_local_delivery()
    {
        PortalAnswers(_ => throw new HttpRequestException("down"));
        await Cart(("challah-classic", 1, 0));
        var g = Group(await Ok(await Quote(new { address = Addr() })), ChallahCorner);
        Assert.Equal(JsonValueKind.Null, g.GetProperty("local").ValueKind);
        Assert.Equal(500, g.GetProperty("shippingCents").GetInt64());
    }

    [Fact]
    public async Task Shipping_settings_are_cached_for_five_minutes_coalesced_and_an_error_is_not_cached()
    {
        PortalAnswers();
        await Cart(("challah-classic", 1, 0));
        int Fetches() => _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains("shipping/settings"));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Quote(new { address = Addr() })));
        Assert.Equal(1, Fetches());                                  // eight parallel quotes, one portal call
        _k.F.Clock.Advance(TimeSpan.FromMinutes(4));
        await Quote(new { address = Addr() });
        Assert.Equal(1, Fetches());
        _k.F.Clock.Advance(TimeSpan.FromMinutes(2));                 // 6 minutes old
        await Quote(new { address = Addr() });
        Assert.Equal(2, Fetches());

        _k.F.Clock.Advance(TimeSpan.FromMinutes(6));
        _k.F.Portal.Respond(HttpStatusCode.UnprocessableEntity, "{\"code\":\"nope\"}");
        var down = await Quote(new { address = Addr() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        PortalAnswers();
        Assert.Equal(HttpStatusCode.OK, (await Quote(new { address = Addr() })).StatusCode);   // the failure was not cached
    }

    [Fact]
    public async Task Delivery_estimates_count_handling_days_plus_transit_in_business_days_on_our_clock()
    {
        PortalAnswers();
        await Cart(("challah-classic", 1, 0));
        // 2026-11-04 is a Wednesday. handling 2 + standard 5 business days = Friday 2026-11-13; latest +2 = Tuesday 2026-11-17
        var o = Group(await Ok(await Quote(new { address = Addr() })), ChallahCorner).GetProperty("shippingOptions").EnumerateArray().Single(x => x.GetProperty("speed").GetString() == "standard");
        Assert.Equal("2026-11-13", o.GetProperty("estimate").GetProperty("earliest").GetString());
        Assert.Equal("2026-11-17", o.GetProperty("estimate").GetProperty("latest").GetString());
        Assert.Equal(new DateOnly(2026, 11, 9), ShippingRates.AddBusinessDays(new DateOnly(2026, 11, 6), 1));   // Friday + 1 = Monday
    }

    [Theory]
    [InlineData("550 g", 550L)]
    [InlineData("1.5 kg", 1500L)]
    [InlineData("12 oz", 341L)]
    [InlineData("2 lb", 908L)]
    [InlineData("heavy", null)]
    [InlineData("-3 kg", null)]
    [InlineData("0 g", null)]
    [InlineData("5 stone", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Weights_are_parsed_from_the_weight_attribute_and_unknown_is_null_never_zero(string? text, long? grams)
        => Assert.Equal(grams, ShippingRates.Grams(text));

    [Fact]
    public async Task Quoting_needs_a_valid_owner()
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(new { address = Addr() }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bogus");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.SendAsync(r)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_weight_makes_weight_based_speeds_unavailable_but_flat_speeds_still_work_decision_O_15()
    {
        PortalAnswers();
        // the pewter cup has no weight attribute: standard (flat) is offered, overnight (weight based) is not, and the quote says why
        await Cart(("kiddush-cup-pewter", 1, 0));
        var g = Group(await Ok(await Quote(new { address = Addr() })), ShabbatTable);
        Assert.Equal("standard", Assert.Single(g.GetProperty("shippingOptions").EnumerateArray()).GetProperty("speed").GetString());
        var gone = Assert.Single(g.GetProperty("unavailableSpeeds").EnumerateArray());
        Assert.Equal(("overnight", "weight_unknown"), (gone.GetProperty("speed").GetString(), gone.GetProperty("reason").GetString()));
        var refused = await Ok(await Quote(new { address = Addr(), selections = new[] { new { sellerId = Judaica, brandId = ShabbatTable, speed = "overnight" } } }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("invalid_selection", refused.GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_line_of_unknown_weight_is_enough_and_a_fully_known_group_is_priced()
    {
        PortalAnswers();
        await Cart(("challah-classic", 1, 0), ("challah-whole-wheat", 1, 0));      // 550 g known, the whole wheat challah has no weight
        var g = Group(await Ok(await Quote(new { address = Addr() })), ChallahCorner);
        Assert.DoesNotContain(g.GetProperty("shippingOptions").EnumerateArray(), o => o.GetProperty("speed").GetString() == "overnight");
        Assert.Equal("weight_unknown", g.GetProperty("unavailableSpeeds")[0].GetProperty("reason").GetString());
        // remove it again: with every weight known the weight-based speed is back, priced on the whole group
        var cart = await Ok(await _k.Http.SendAsync(Get("/api/v1/cart")));
        var wheat = cart.GetProperty("groups")[0].GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("title").GetString()!.Contains("Whole Wheat")).GetProperty("id").GetGuid();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/cart/lines/{wheat}");
        del.Headers.Add(CartEndpoints.GuestHeader, _token);
        await _k.Http.SendAsync(del);
        var again = Group(await Ok(await Quote(new { address = Addr() })), ChallahCorner);
        Assert.Equal(1500 + 300, Price(again, "overnight"));                          // 550 g -> 1 kg
        Assert.Equal(0, again.GetProperty("unavailableSpeeds").GetArrayLength());
    }

    private HttpRequestMessage Get(string path)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        r.Headers.Add(CartEndpoints.GuestHeader, _token);
        return r;
    }

    [Fact]
    public async Task A_group_with_only_weight_priced_speeds_and_an_unknown_weight_cannot_be_ordered_but_local_delivery_still_can()
    {
        _weightOnly = true;
        var window = Guid.NewGuid();
        PortalAnswers(c => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            eligible = true, method = "local", feeCents = 400, timeZone = "America/New_York",
            windows = new[] { new { windowId = window, date = "2026-11-06", start = "2026-11-06T17:00:00Z", end = "2026-11-06T19:00:00Z", remaining = 2 } },
        })));
        await Cart(("challah-whole-wheat", 1, 0));       // no weight, a bakery item that can be delivered locally
        var q = await Ok(await Quote(new { address = Addr() }));
        var g = Group(q, ChallahCorner);
        Assert.Equal(0, g.GetProperty("shippingOptions").GetArrayLength());
        Assert.Equal(2, g.GetProperty("unavailableSpeeds").GetArrayLength());
        Assert.False(q.GetProperty("canPay").GetBoolean());                           // "shipping unavailable for this seller": nothing to ship it with
        Assert.Equal(JsonValueKind.Null, g.GetProperty("selected").ValueKind);
        var local = await Ok(await Quote(new { address = Addr(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, localWindowId = window, localDate = "2026-11-06" } } }));
        Assert.True(local.GetProperty("canPay").GetBoolean());
        Assert.Equal("local", Group(local, ChallahCorner).GetProperty("selected").GetProperty("kind").GetString());
    }
}
