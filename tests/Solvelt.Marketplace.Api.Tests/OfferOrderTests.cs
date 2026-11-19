using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Offers;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Quantity offers through the cart, the quote, the checkout, the order sent to the portal and the voice read-back (decisions Q-03..Q-06, portal C18). The fixture seller is Judaica / Shabbat Table (couscous 600,
/// halva 900, tahini 1100); the offer is "10+ at 95c, 20+ at 90c" with a gift from 10. Every figure is the portal's own: 25 units at 90c = 2250, 15 units at 95c = 1425.
/// </summary>
public sealed class OfferOrderTests : OrderFlowBase
{
    private static readonly Guid GiftProduct = Guid.Parse("0a5d0000-0000-0000-0000-0000000000a1");

    private async Task<Guid> Offer(string scope = "all", int[]? tiers = null, bool gift = true, bool giftAvailable = true, Guid? seller = null, Guid? brand = null, string[]? products = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, long version = 1, Guid? id = null)
    {
        var oid = id ?? Guid.NewGuid();
        tiers ??= [10, 95, 20, 90];
        var t = new List<OfferTier>();
        for (var i = 0; i < tiers.Length; i += 2) t.Add(new OfferTier(tiers[i], tiers[i + 1]));
        await _k.Db(async db =>
        {
            db.CatalogOffers.Add(new CatalogOffer
            {
                Id = oid, SellerId = seller ?? Judaica, BrandId = brand ?? ShabbatTable, Name = "Bundle", Currency = "USD", Scope = scope,
                ProductIdsJson = JsonSerializer.Serialize((products ?? []).Select(p => FixtureCatalogSource.Id("listing:" + p))), CategoryIdsJson = "[]",
                TiersJson = JsonSerializer.Serialize(t), GiftsJson = JsonSerializer.Serialize(gift ? new[] { new OfferGiftDoc(10, GiftProduct, 1, "Card wallet", giftAvailable) } : []),
                ActiveFrom = from, ActiveTo = to, Version = version, KnownVersion = version, Visible = true, FirstSeenAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return oid;
    }

    private async Task<JsonElement> Cart()
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cart");
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(r);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> Quote(HttpStatusCode expect = HttpStatusCode.OK)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(new { address = Address() }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(r);
        var s = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expect, s);
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    private static JsonElement Group(JsonElement cart, Guid seller) => cart.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("sellerId").GetGuid() == seller && g.GetProperty("brandId").GetGuid() == ShabbatTable);
    private static long Net(JsonElement cart) => cart.GetProperty("subtotalCents").GetInt64();

    [Fact]
    public async Task Twenty_five_units_over_several_lines_cost_2250_and_fifteen_cost_1425()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 20);
        await AddToCart("halva-pistachio", 5);                                  // the cart limits a line to 20, so 25 units are two lines
        var cart = await Cart();
        Assert.Equal(2250, Net(cart));
        Assert.Equal(10200 + 4050, Group(cart, Judaica).GetProperty("discountCents").GetInt64());
        var offer = Group(cart, Judaica).GetProperty("offers").EnumerateArray().Single();
        Assert.Equal(25, offer.GetProperty("eligibleUnits").GetInt32());
        Assert.Equal(20, offer.GetProperty("tier").GetProperty("minQuantity").GetInt32());
        Assert.Equal(90, offer.GetProperty("tier").GetProperty("unitPriceCents").GetInt64());
        Assert.Equal(1800, Group(cart, Judaica).GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("quantity").GetInt32() == 20).GetProperty("lineTotalCents").GetInt64());   // net of the discount
        Assert.Equal(25, cart.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task The_tier_boundary_prices_15_units_at_95_and_shows_how_many_more_reach_the_next_step()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 15);
        var cart = await Cart();
        Assert.Equal(1425, Net(cart));
        var offer = Group(cart, Judaica).GetProperty("offers").EnumerateArray().Single();
        Assert.Equal(5, offer.GetProperty("unitsToNextTier").GetInt32());
        Assert.Equal(90, offer.GetProperty("nextTier").GetProperty("unitPriceCents").GetInt64());
        await AddToCart("israeli-couscous", 5);                                 // 20 units: the next step prices every unit again
        Assert.Equal(1800, Net(await Cart()));
    }

    [Fact]
    public async Task A_tier_never_raises_a_price_and_a_sale_price_below_it_wins()
    {
        await Login();
        await Offer(tiers: [2, 700]);                                           // tier above the 600 unit price
        await AddToCart("israeli-couscous", 3);
        var cart = await Cart();
        Assert.Equal(1800, Net(cart));
        Assert.Equal(0, Group(cart, Judaica).GetProperty("discountCents").GetInt64());
    }

    [Fact]
    public async Task Units_of_another_seller_or_brand_never_count_toward_the_tier()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 10);                                // Judaica / Shabbat Table: 10 units, tier 95
        await AddToCart("challah-classic", 12);                                 // Mazel: not the offer's seller
        await AddToCart("shabbat-candles-72", 5);                               // Judaica but another brand (Brooklyn Judaica)
        var cart = await Cart();
        var mine = Group(cart, Judaica).GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("offerId").ValueKind != JsonValueKind.Null).ToList();
        Assert.Single(mine);
        Assert.Equal(950, mine[0].GetProperty("lineTotalCents").GetInt64());
        Assert.Equal(10, Group(cart, Judaica).GetProperty("offers").EnumerateArray().Single().GetProperty("eligibleUnits").GetInt32());
        var other = cart.GetProperty("groups").EnumerateArray().Where(g => g.GetProperty("sellerId").GetGuid() != Judaica || g.GetProperty("brandId").GetGuid() != ShabbatTable);
        Assert.All(other, g => Assert.Equal(0, g.GetProperty("discountCents").GetInt64()));
    }

    [Fact]
    public async Task An_offer_that_ended_or_is_outside_its_window_stops_applying()
    {
        await Login();
        var id = await Offer();
        await AddToCart("israeli-couscous", 10);
        Assert.Equal(950, Net(await Cart()));
        await _k.Db(async db => { var o = await db.CatalogOffers.SingleAsync(x => x.Id == id); o.UnlistedVersion = 1; o.RecomputeVisibility(); await db.SaveChangesAsync(); return 0; });   // the tombstone
        Assert.Equal(6000, Net(await Cart()));
        await Offer(to: _k.F.Clock.GetUtcNow().AddMinutes(-1));                 // already over
        await Offer(from: _k.F.Clock.GetUtcNow().AddDays(1));                   // not yet
        Assert.Equal(6000, Net(await Cart()));
    }

    [Fact]
    public async Task A_gift_is_earned_once_per_order_however_many_units_and_an_unavailable_one_is_left_out_with_a_notice()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 20);
        await AddToCart("halva-pistachio", 20);                                 // 40 units
        var q = await Quote();
        var g = Group(q, Judaica);
        var gift = g.GetProperty("gifts").EnumerateArray().Single();                             // once, never multiplied
        Assert.Equal((GiftProduct, 1), (gift.GetProperty("productId").GetGuid(), gift.GetProperty("quantity").GetInt32()));
        Assert.False(g.GetProperty("offers").EnumerateArray().Single().GetProperty("giftUnavailable").GetBoolean());
        Assert.Equal(40 * 90, q.GetProperty("itemsCents").GetInt64());          // the gift is $0
    }

    [Fact]
    public async Task An_unavailable_gift_never_rejects_checkout_it_is_flagged_and_left_out()
    {
        await Login();
        await Offer(giftAvailable: false);
        await AddToCart("israeli-couscous", 10);
        var q = await Quote();
        var g = Group(q, Judaica);
        Assert.Empty(g.GetProperty("gifts").EnumerateArray());
        Assert.True(g.GetProperty("offers").EnumerateArray().Single().GetProperty("giftUnavailable").GetBoolean());
        var (number, _, total) = await Checkout();
        Assert.Equal(950 + (await Order(number)).Groups.Sum(x => x.ShippingCents + x.TaxCents), total);
    }

    [Fact]
    public async Task The_order_sent_to_the_portal_carries_the_offer_discounts_and_the_gift_line_and_the_totals_agree()
    {
        await Login();
        var oid = await Offer(version: 3);
        await AddToCart("israeli-couscous", 20);
        await AddToCart("halva-pistachio", 5);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var order = await Order(number);
        Assert.Equal("placed", order.Status);
        var body = JsonDocument.Parse(Portal("orders").Single().BodyText).RootElement;
        var offer = body.GetProperty("offers").EnumerateArray().Single();
        Assert.Equal((oid, 3L), (offer.GetProperty("offerId").GetGuid(), offer.GetProperty("offerVersion").GetInt64()));
        Assert.False(offer.TryGetProperty("giftUnavailable", out _));
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        var paying = lines.Where(l => !l.TryGetProperty("isGift", out _)).ToList();
        Assert.Equal(new long[] { 10200, 4050 }, paying.Select(l => l.GetProperty("discountCents").GetInt64()).OrderByDescending(x => x).ToArray());
        Assert.All(paying, l => Assert.Equal(oid, l.GetProperty("offerId").GetGuid()));
        Assert.Equal(600, paying.Select(l => l.GetProperty("unitPriceCents").GetInt64()).Min());   // the price BEFORE the discount: the portal recomputes
        var giftLine = Assert.Single(lines, l => l.TryGetProperty("isGift", out _));
        Assert.Equal((GiftProduct, oid, 0L, 0L, 0L), (giftLine.GetProperty("productId").GetGuid(), giftLine.GetProperty("giftOfOfferId").GetGuid(), giftLine.GetProperty("unitPriceCents").GetInt64(),
            giftLine.GetProperty("discountCents").GetInt64(), giftLine.GetProperty("taxCents").GetInt64()));
        Assert.False(giftLine.TryGetProperty("variantId", out _));
        Assert.Equal(2250, order.ItemsCents);
        Assert.Equal(order.TotalCents, order.Groups.Sum(g => g.TotalCents));
        Assert.Equal(14250, order.Lines.Where(l => !l.IsGift).Sum(l => l.DiscountCents));
        // the order view shows the discount per line and the free gift line
        var rq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders/" + number);
        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var view = JsonDocument.Parse(await (await _k.Http.SendAsync(rq)).Content.ReadAsStringAsync()).RootElement;
        var vlines = view.GetProperty("groups")[0].GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(14250, vlines.Where(l => !l.GetProperty("isGift").GetBoolean()).Sum(l => l.GetProperty("discountCents").GetInt64()));
        Assert.Single(vlines, l => l.GetProperty("isGift").GetBoolean());
    }

    [Fact]
    public async Task An_offer_changed_after_the_quote_is_refused_with_a_fresh_quote_and_nothing_is_charged()
    {
        await Login();
        var id = await Offer();
        await AddToCart("israeli-couscous", 20);
        var seen = (await Quote()).GetProperty("quoteHash").GetString();
        await _k.Db(async db => { var o = await db.CatalogOffers.SingleAsync(x => x.Id == id); o.TiersJson = JsonSerializer.Serialize(new[] { new OfferTier(10, 95), new OfferTier(20, 85) }); o.Version = 2; o.KnownVersion = 2; await db.SaveChangesAsync(); return 0; });
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(new { address = Address(), quoteHash = seen }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(r);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("price_changed", j.GetProperty("code").GetString());
        Assert.Equal("offer_changed", j.GetProperty("messageCode").GetString());
        Assert.Equal(20 * 85, j.GetProperty("quote").GetProperty("itemsCents").GetInt64());
        Assert.Empty(Stripe.Intents);                                           // never charge a stale total
    }

    [Fact]
    public async Task The_portal_refusing_the_offer_pricing_fails_the_order_and_refunds_everything_and_the_next_quote_is_re_priced()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 10);
        var (number, intent, total) = await Checkout();
        _onOrder = _ => FakePortal.Json(HttpStatusCode.UnprocessableEntity,
            "{\"code\":\"offer_mismatch\",\"message\":\"x\",\"errors\":[{\"index\":0,\"kind\":\"discount\",\"offerId\":\"" + Guid.NewGuid() + "\",\"message\":\"m\",\"expected\":{\"discountCents\":0},\"actual\":{\"discountCents\":5050}}],\"expected\":{\"offers\":[]}}");
        await Pay(number, intent, total);
        await Drain();
        var order = await Order(number);
        Assert.Equal("failed", order.Status);
        Assert.Equal("offer_mismatch", order.FailureCode);
        Assert.Equal(total, order.RefundedCents);
        Assert.Equal(total, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(Portal("payment-events"));                                 // nothing captured at the portal
        Assert.Contains("quantity offer", Assert.Single(_k.Sink.Sent, m => m.Subject.Contains("couldn't place")).TextBody);
    }

    [Fact]
    public async Task Parallel_cart_edits_end_at_the_price_of_the_final_cart()
    {
        await Login();
        await Offer();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AddToCart("israeli-couscous", 2)));   // 10 units, five racing adds
        Assert.Equal(950, Net(await Cart()));
        await Task.WhenAll(AddToCart("halva-pistachio", 5), AddToCart("tahini-roasted", 5));
        Assert.Equal(20 * 90, Net(await Cart()));
    }

    [Fact]
    public void The_voice_read_back_says_the_discount_and_the_gift_from_the_same_quote()
    {
        var offerId = Guid.NewGuid();
        var line = new Solvelt.Marketplace.Api.Checkout.QuoteLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Cards", "Default", 25, 100, 2250, 0, 250, offerId);
        var g = new Solvelt.Marketplace.Api.Checkout.QuoteGroup(Judaica, "Seller", ShabbatTable, "Brand", [line], 2250, [], [], null, new Solvelt.Marketplace.Api.Checkout.SelectedDelivery("carrier", "standard", null, null, 0), 0, 0, 0, 2250,
            250, [new Solvelt.Marketplace.Api.Checkout.QuoteOffer(offerId, 1, "Bundle", 250, false)], [new Solvelt.Marketplace.Api.Checkout.QuoteGift(offerId, GiftProduct, "Card wallet", 1)]);
        var text = Solvelt.Marketplace.Api.Voice.OrderReadBack.Build(new Solvelt.Marketplace.Api.Checkout.QuoteView("USD", [g], 2250, 0, 0, 2250, true),
            new Solvelt.Marketplace.Api.Checkout.CheckoutAddress("Jane", "1 Main St", null, "Brooklyn", "NY", "11201", "US", null), new DateOnly(2026, 11, 19));
        Assert.Contains("quantity discount saves you", text);
        Assert.Contains("a free Card wallet", text);
        Assert.Contains(Solvelt.Marketplace.Api.Voice.VoiceSpeech.Money(250), text);
    }

    private async Task<JsonElement> Stale(string seen)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(new { address = Address(), quoteHash = seen }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(r);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.Equal("offer_changed", j.GetProperty("messageCode").GetString());
        Assert.Empty(Stripe.Intents);                                           // nothing was charged
        return j;
    }

    [Fact]
    public async Task A_gift_that_became_unavailable_or_an_offer_that_ended_after_the_quote_is_refused_with_a_fresh_quote_and_nothing_is_charged()
    {
        await Login();
        var id = await Offer();
        await AddToCart("israeli-couscous", 10);
        var seen = (await Quote()).GetProperty("quoteHash").GetString();
        await _k.Db(async db => { var o = await db.CatalogOffers.SingleAsync(x => x.Id == id); o.GiftsJson = JsonSerializer.Serialize(new[] { new OfferGiftDoc(10, GiftProduct, 1, "Card wallet", false) }); o.Version = 2; await db.SaveChangesAsync(); return 0; });
        var fresh = (await Stale(seen!)).GetProperty("quote");
        Assert.Empty(Group(fresh, Judaica).GetProperty("gifts").EnumerateArray());
        var seen2 = fresh.GetProperty("quoteHash").GetString();
        await _k.Db(async db => { var o = await db.CatalogOffers.SingleAsync(x => x.Id == id); o.UnlistedVersion = 2; o.RecomputeVisibility(); await db.SaveChangesAsync(); return 0; });   // tombstone
        var ended = (await Stale(seen2!)).GetProperty("quote");
        Assert.Equal(6000, ended.GetProperty("itemsCents").GetInt64());
    }

    [Fact]
    public async Task A_gift_the_portal_left_out_is_shown_on_the_order_and_changes_nothing_else()
    {
        await Login();
        await Offer();
        await AddToCart("israeli-couscous", 10);
        var (number, intent, total) = await Checkout();
        _onOrder = c =>
        {
            var created = Created(c, 201, true);
            var json = created.Content.ReadAsStringAsync().Result.Replace("\"oversold\":false", "\"oversold\":false,\"giftUnavailable\":true");
            return FakePortal.Json(HttpStatusCode.Created, json);
        };
        await Pay(number, intent, total);
        await Drain();
        Assert.Equal("placed", (await Order(number)).Status);
        var rq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders/" + number);
        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var view = JsonDocument.Parse(await (await _k.Http.SendAsync(rq)).Content.ReadAsStringAsync()).RootElement;
        Assert.True(view.GetProperty("groups")[0].GetProperty("giftUnavailable").GetBoolean());
    }

    [Fact]
    public async Task A_listing_shows_the_one_offer_that_applies_now_and_nothing_once_it_ended()
    {
        await Login();
        var id = await Offer(scope: "products", products: ["israeli-couscous"]);
        async Task<JsonElement> Detail(string slug) => JsonDocument.Parse(await (await _k.Http.GetAsync("/api/v1/catalog/listings/" + FixtureCatalogSource.Id("listing:" + slug))).Content.ReadAsStringAsync()).RootElement.Clone();
        var d = await Detail("israeli-couscous");
        var offer = d.GetProperty("offers").EnumerateArray().Single();
        Assert.Equal(id, offer.GetProperty("offerId").GetGuid());
        Assert.Equal([10, 20], offer.GetProperty("tiers").EnumerateArray().Select(t => t.GetProperty("minQuantity").GetInt32()).ToArray());
        Assert.Equal("Card wallet", offer.GetProperty("gifts")[0].GetProperty("title").GetString());
        Assert.Empty((await Detail("halva-pistachio")).GetProperty("offers").EnumerateArray());      // not in the offer's scope
        await _k.Db(async db => { var o = await db.CatalogOffers.SingleAsync(x => x.Id == id); o.UnlistedVersion = 1; o.RecomputeVisibility(); await db.SaveChangesAsync(); return 0; });
        Assert.Empty((await Detail("israeli-couscous")).GetProperty("offers").EnumerateArray());
    }
}
