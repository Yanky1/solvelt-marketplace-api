using Solvelt.Marketplace.Api.Carts;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class CheckoutTests : IDisposable
{
    private static readonly Guid Judaica = FixtureCatalogSource.Id("seller:judaica"), ShabbatTable = FixtureCatalogSource.Id("brand:shabbat-table"),
        Mazel = FixtureCatalogSource.Id("seller:mazel"), ChallahCorner = FixtureCatalogSource.Id("brand:challah-corner");

    private readonly AuthKit _k = new();
    private FakePaymentProvider Stripe => _k.F.Services.GetRequiredService<FakePaymentProvider>();

    public CheckoutTests()
    {
        _k.F.Portal.RespondAlways(c =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(c.PathAndQuery[(c.PathAndQuery.IndexOf('?') + 1)..]);
            return c.PathAndQuery.StartsWith("/api/internal/v1/shipping/settings")
                ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sellerId = Guid.Parse(q["sellerId"]!), brandId = Guid.Parse(q["brandId"]!), handlingDays = 1,
                    profiles = new[] { new { id = Guid.NewGuid(), name = "Default", isDefault = true, currency = "USD", freeShippingThresholdCents = 2000, zones = new[] { new { name = "US", kind = "domestic", countries = new[] { "US" },
                        speeds = new object[] { new { speed = "standard", enabled = true, method = "flat", flatCents = 500 }, new { speed = "overnight", enabled = true, method = "flat", flatCents = 1800 } } } } } },
                }))
                : FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        });
    }

    public void Dispose() => _k.Dispose();

    private static Guid Variant(string slug, int i = 0) => FixtureCatalogSource.Id($"variant:{slug}:{i}");

    private async Task<string> Buyer(string email = "pay1@example.com")
    {
        await CatalogKit.Sync(_k.F);
        return await _k.VerifiedSession(email);
    }

    private async Task AddToCart(string session, string slug, int qty = 1)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/lines") { Content = JsonContent.Create(new { variantId = Variant(slug), quantity = qty }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        Assert.Equal(HttpStatusCode.OK, (await _k.Http.SendAsync(r)).StatusCode);
    }

    private static object Address(string region = "NY") => new { name = "Jane Brown", line1 = "1 Main St", city = "Brooklyn", region, postalCode = "11201", country = "US", phone = "+1 718 555 0100" };

    private async Task<HttpResponseMessage> Checkout(string? session, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = await QuoteHashes.WithHashAsync(_k.Http, session, body) };
        if (session != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return await _k.Http.SendAsync(r);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var s = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {s}");
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    [Fact]
    public async Task Checkout_creates_one_pending_order_and_one_intent_for_exactly_the_server_total_and_ignores_client_money()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        await AddToCart(s, "challah-classic", 2);
        var r = await Ok(await Checkout(s, new { address = Address(), giftMessage = "Shabbat shalom", totalCents = 1, itemsCents = 1, priceCents = 1, amountCents = 1, taxCents = 0, shippingCents = 0 }));
        var number = r.GetProperty("orderNumber").GetString()!;
        Assert.Matches(@"^MP-\d{8}-[A-Z2-9]{8}$", number);
        Assert.Equal("pending_payment", r.GetProperty("status").GetString());
        var total = r.GetProperty("quote").GetProperty("totalCents").GetInt64();

        var intent = Assert.Single(Stripe.Intents);
        Assert.Equal(total, intent.AmountCents);
        Assert.Equal("usd", intent.Currency);
        Assert.Equal(number, intent.Metadata["marketplace_order_id"]);
        Assert.Equal(intent.ClientSecret, r.GetProperty("payment").GetProperty("clientSecret").GetString());

        // money sums per group equal the intent amount, in the stored rows as well
        var order = await _k.Db(db => db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().SingleAsync());
        Assert.Equal(intent.AmountCents, order.TotalCents);
        Assert.Equal(order.TotalCents, order.Groups.Sum(g => g.TotalCents));
        Assert.Equal(order.Groups.Sum(g => g.ItemsCents + g.ShippingCents + g.TaxCents), order.TotalCents);
        Assert.Equal(order.Lines.Sum(l => l.LineTotalCents + l.TaxCents) + order.Groups.Sum(g => g.ShippingCents + g.ShippingTaxCents), order.TotalCents);
        Assert.Equal(2, order.Groups.Count);
        Assert.All(order.Groups, g => Assert.Equal(g.ItemsCents, order.Lines.Where(l => l.GroupId == g.Id).Sum(l => l.LineTotalCents)));
        Assert.Equal(4200 + 2 * 650, order.ItemsCents);               // catalog prices, whatever the body said
        Assert.Equal(intent.Id, order.PaymentIntentId);
        Assert.Equal("Jane B.", order.BuyerName);                    // the portal sees a public name and the relay address, never the real email
        Assert.StartsWith("relay-", order.RelayEmail);
        Assert.DoesNotContain("pay1@example.com", order.RelayEmail);
        Assert.All(order.Lines, l => Assert.StartsWith(number + "-", l.MarketplaceLineId));
    }

    [Fact]
    public async Task A_parallel_double_submit_creates_one_order_and_one_payment_intent()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var body = new { address = Address(), giftMessage = "hello" };
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Checkout(s, body)));
        var views = new List<JsonElement>();
        foreach (var r in results) { if (!r.IsSuccessStatusCode) throw new Exception(string.Join("\n", _k.F.Logs.Lines.Where(l => l.Contains("Unhandled")).Take(3))); views.Add(await Ok(r)); }
        Assert.Single(views.Select(v => v.GetProperty("orderNumber").GetString()).Distinct());
        Assert.Single(views.Select(v => v.GetProperty("payment").GetProperty("clientSecret").GetString()).Distinct());
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Equal(1, await _k.Db(db => db.OrderGroups.CountAsync()));
        Assert.Single(Stripe.Intents);                                  // one charge, however many requests reached Stripe
        Assert.Equal(Stripe.Intents.Single().Id, (await _k.Db(db => db.Orders.SingleAsync())).PaymentIntentId);
        // and a plain repeat later is still that same order
        Assert.Equal(views[0].GetProperty("orderNumber").GetString(), (await Ok(await Checkout(s, body))).GetProperty("orderNumber").GetString());
        Assert.Single(Stripe.Intents);
    }

    [Fact]
    public async Task Changing_the_delivery_choice_makes_a_new_order_and_supersedes_and_cancels_the_old_one()
    {
        var s = await Buyer();
        await AddToCart(s, "challah-classic");
        var first = await Ok(await Checkout(s, new { address = Address() }));
        var second = await Ok(await Checkout(s, new { address = Address(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, speed = "overnight" } } }));
        Assert.NotEqual(first.GetProperty("orderNumber").GetString(), second.GetProperty("orderNumber").GetString());
        Assert.Equal(650 + 1800, second.GetProperty("quote").GetProperty("itemsCents").GetInt64() + second.GetProperty("quote").GetProperty("shippingCents").GetInt64());
        var orders = await _k.Db(db => db.Orders.AsNoTracking().OrderBy(o => o.CreatedAt).ToListAsync());
        Assert.Equal(2, orders.Count);
        Assert.Single(orders, o => o.Status == "pending_payment");
        var old = orders.Single(o => o.Status == "superseded");
        Assert.Null(old.ActiveKey);
        Assert.Equal(first.GetProperty("orderNumber").GetString(), old.Number);
        Assert.Equal(2, Stripe.Intents.Count);
        while (await _k.Dispatch() > 0) { }                                                 // the cancel goes through the outbox
        Assert.Single(Stripe.Intents, i => i.Cancelled);
        Assert.True(Stripe.Intents.Single(i => i.Cancelled).Id == old.PaymentIntentId);
        // the original choice again is a third, new order (the old pending key was released)
        var third = await Ok(await Checkout(s, new { address = Address() }));
        Assert.NotEqual(first.GetProperty("orderNumber").GetString(), third.GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task A_new_address_or_a_price_change_is_a_different_checkout()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var a = await Ok(await Checkout(s, new { address = Address("NY") }));
        var b = await Ok(await Checkout(s, new { address = Address("NJ") }));      // other tax
        Assert.NotEqual(a.GetProperty("orderNumber").GetString(), b.GetProperty("orderNumber").GetString());
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Variants = l.Variants.Select(v => v with { PriceCents = 4300 }).ToList() });
        await CatalogKit.Sync(_k.F);
        var c = await Ok(await Checkout(s, new { address = Address("NJ") }));
        Assert.NotEqual(b.GetProperty("orderNumber").GetString(), c.GetProperty("orderNumber").GetString());
        Assert.Equal(4300, c.GetProperty("quote").GetProperty("itemsCents").GetInt64());
        Assert.Equal(c.GetProperty("quote").GetProperty("totalCents").GetInt64(), Stripe.Intents.Single(i => i.Metadata["marketplace_order_id"] == c.GetProperty("orderNumber").GetString()).AmountCents);
    }

    [Fact]
    public async Task When_the_processor_is_down_nothing_is_charged_the_order_stays_pending_and_the_retry_finds_it()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        Stripe.FailNext = 1;
        var down = await Ok(await Checkout(s, new { address = Address() }), HttpStatusCode.ServiceUnavailable);
        Assert.Equal("payments_unavailable", down.GetProperty("code").GetString());
        Assert.Empty(Stripe.Intents);
        var order = await _k.Db(db => db.Orders.AsNoTracking().SingleAsync());
        Assert.Equal("pending_payment", order.Status);
        Assert.Null(order.PaymentIntentId);
        var ok = await Ok(await Checkout(s, new { address = Address() }));
        Assert.Equal(order.Number, ok.GetProperty("orderNumber").GetString());
        Assert.Single(Stripe.Intents);
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));
    }

    // ---- "Something went wrong" at checkout on a misconfigured site (M8-12): each cause is a clear status and a log line that names the fix, never an unhandled 500 --------------------------------
    [Fact]
    public async Task A_refused_lookup_of_the_buyers_earlier_pending_order_is_a_502_with_the_cause_in_the_log_not_a_500()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        await Ok(await Checkout(s, new { address = Address("NY") }));                 // a first checkout leaves a pending order with an intent
        Stripe.RejectLookupWith = "invalid_api_key";                                    // the key was rotated or is wrong: Stripe refuses to read that intent
        var refused = await Ok(await Checkout(s, new { address = Address("NJ") }), HttpStatusCode.BadGateway);   // another destination = another order, so the earlier one is looked up
        Assert.Equal("payment_rejected", refused.GetProperty("code").GetString());
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("invalid_api_key") && l.Contains("Stripe:SecretKey"));
    }

    [Fact]
    public async Task A_payment_provider_that_cannot_be_reached_says_why_in_the_log()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        Stripe.FailNext = 1;
        Assert.Equal("payments_unavailable", (await Ok(await Checkout(s, new { address = Address() }), HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString());
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("Payments are unavailable for order") && l.Contains("unreachable"));
    }

    [Theory]
    [InlineData("<html>Welcome to nginx</html>")]
    [InlineData("")]
    [InlineData("not json")]
    public async Task A_2xx_from_stripe_that_is_not_json_is_unavailable_never_an_unhandled_exception(string body)
    {
        var h = new Script((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
        var ex = await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(h).CreateIntentAsync(4573, "USD", "pi:MP-1", new Dictionary<string, string>(), default));
        Assert.Equal("bad_response", ex.Message);
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(h).GetIntentStatusAsync("pi_1", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Portal:InboundSecret")]
    [InlineData(HttpStatusCode.NotFound, "Portal:BaseUrl")]
    [InlineData(HttpStatusCode.InternalServerError, "The portal is failing")]
    public async Task A_quote_the_portal_cannot_answer_is_a_503_and_the_log_names_what_to_check(HttpStatusCode portalAnswer, string hint)
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        _k.F.Portal.RespondAlways(_ => FakePortal.Json(portalAnswer, "{\"code\":\"x\"}"));
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(new { address = new { country = "US", region = "NY", postalCode = "11201" } }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        var res = await _k.Http.SendAsync(r);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("shipping_unavailable", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains(((int)portalAnswer).ToString()) && l.Contains(hint));
    }

    [Fact]
    public void A_stripe_secret_key_without_a_usable_publishable_key_is_warned_about_at_start_up()
    {
        string[] Warn(params (string, string?)[] kv) => StartupValidator.Warnings(new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build(), new FakeEnv("Testing")).ToArray();
        Assert.Contains(Warn(("Stripe:SecretKey", "sk_test_abc")), w => w.Contains("Stripe:PublishableKey") && w.Contains("not available"));
        Assert.Contains(Warn(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:PublishableKey", "pk_test_has_underscores_x")), w => w.Contains("does not look like"));
        Assert.Empty(Warn(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:PublishableKey", "pk_test_abcdefgh1234")));
        Assert.Empty(Warn());                                                         // payments simply off: nothing to say
    }

    [Fact]
    public async Task Checkout_needs_a_signed_in_buyer_a_valid_address_and_a_payable_cart()
    {
        var s = await Buyer();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Checkout(null, new { address = Address() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Checkout("bogus", new { address = Address() })).StatusCode);
        var empty = await Ok(await Checkout(s, new { address = Address() }), HttpStatusCode.Conflict);
        Assert.Equal("cart_not_ready", empty.GetProperty("code").GetString());
        await AddToCart(s, "kiddush-cup-pewter");
        foreach (var bad in new object[] { new { }, new { address = new { name = "", line1 = "x", city = "y", postalCode = "1", country = "US" } }, new { address = new { name = "A", line1 = "x", city = "y", postalCode = "1", country = "USA" } },
                     new { address = new { name = "A", line1 = "x", city = "y", postalCode = "1", country = "US", phone = "call me" } }, new { address = Address(), giftMessage = new string('x', 501) } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Checkout(s, bad)).StatusCode);
        var unserviceable = await Ok(await Checkout(s, new { address = new { name = "A", line1 = "x", city = "Berlin", postalCode = "10115", country = "DE" } }), HttpStatusCode.Conflict);
        Assert.Equal("delivery_unavailable", unserviceable.GetProperty("code").GetString());
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Empty(Stripe.Intents);
    }

    [Fact]
    public async Task A_quote_the_buyer_did_not_see_is_refused_with_the_new_quote_and_nothing_is_created_or_charged()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var seen = (await Ok(await Checkout(s, new { address = Address() }))).GetProperty("quote");
        var seenHash = seen.GetProperty("quoteHash").GetString();
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));
        // the price moves after the buyer looked: they confirm the old quote, so nothing is created and the new quote comes back to be shown
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Variants = l.Variants.Select(v => v with { PriceCents = 4300 }).ToList() });
        await CatalogKit.Sync(_k.F);
        var refused = await Ok(await Checkout(s, new { address = Address(), quoteHash = seenHash }), HttpStatusCode.Conflict);
        Assert.Equal("price_changed", refused.GetProperty("code").GetString());
        var fresh = refused.GetProperty("quote");
        Assert.NotEqual(seen.GetProperty("totalCents").GetInt64(), fresh.GetProperty("totalCents").GetInt64());
        Assert.Equal(4300, fresh.GetProperty("itemsCents").GetInt64());
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));                       // still only the first order
        Assert.Single(Stripe.Intents);
        // confirming the NEW quote goes through, and it is the server's total that is charged
        var ok = await Ok(await Checkout(s, new { address = Address(), quoteHash = fresh.GetProperty("quoteHash").GetString() }));
        Assert.Equal(fresh.GetProperty("totalCents").GetInt64(), Stripe.Intents.Single(i => i.Metadata["marketplace_order_id"] == ok.GetProperty("orderNumber").GetString()).AmountCents);
    }

    private async Task<string> QuoteHashFor(string session, object address)
    {
        var q = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(new { address }) };
        q.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return (await Ok(await _k.Http.SendAsync(q))).GetProperty("quoteHash").GetString()!;
    }

    [Fact]
    public async Task A_quote_that_went_stale_because_the_cart_changed_is_refused_end_to_end_without_the_test_helper_and_a_forged_expiry_is_refused()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var hash = await QuoteHashFor(s, Address());
        await AddToCart(s, "challah-classic");                                                    // the cart changed after the buyer looked
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(new { address = Address(), quoteHash = hash }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        Assert.Equal("price_changed", (await Ok(await _k.Http.SendAsync(r), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        // review K1: the real hash of the NEW cart, with its expiry moved five years out and the digest kept, is refused; so is one moved past the clock
        var fresh = await QuoteHashFor(s, Address());
        var parts = fresh.Split('.');
        var forged = (long.Parse(parts[0]) + 5L * 365 * 24 * 3600) + "." + parts[1];
        _k.F.Clock.Advance(TimeSpan.FromHours(6));                                                // the real one has expired by now
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address(), quoteHash = forged })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address(), quoteHash = fresh })).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Empty(Stripe.Intents);
    }

    [Fact]
    public async Task A_total_of_exactly_99999999_cents_can_be_paid_and_one_cent_more_cannot()
    {
        var one = await Buyer("cap1@example.com");
        await AddToCart(one, "kiddush-cup-pewter");
        // the price is the cap itself and the group ships free (over the threshold) with no tax in Oregon: the total is exactly 99,999,999
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Variants = l.Variants.Select(v => v with { PriceCents = 99_999_999 }).ToList() });
        await CatalogKit.Sync(_k.F);
        var ok = await Ok(await Checkout(one, new { address = Address("OR") }));
        Assert.Equal(99_999_999, ok.GetProperty("quote").GetProperty("totalCents").GetInt64());
        Assert.Equal(99_999_999, Stripe.Intents.Single().AmountCents);
        // another buyer adds anything more (a challah): the total is above the cap by its price and is refused
        var two = await Buyer("cap2@example.com");
        await AddToCart(two, "kiddush-cup-pewter");
        await AddToCart(two, "challah-classic");
        Assert.Equal("amount_too_large", (await Ok(await Checkout(two, new { address = Address("OR") }), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString());
        Assert.Single(Stripe.Intents);
    }

    [Fact]
    public async Task A_checkout_without_any_quote_hash_is_refused_with_the_fresh_quote_and_creates_nothing()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        // sent exactly as a client would that never asked for a quote: no quoteHash field at all (review C5: the hash is required on EVERY checkout)
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(new { address = Address(), quoteHash = (string?)null }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        var res = await Ok(await _k.Http.SendAsync(r), HttpStatusCode.Conflict);
        Assert.Equal("price_changed", res.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(res.GetProperty("quote").GetProperty("quoteHash").GetString()));   // the fresh quote carries the hash to confirm
        var r2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(new { address = Address() }) };
        r2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        Assert.Equal(HttpStatusCode.Conflict, (await _k.Http.SendAsync(r2)).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Empty(Stripe.Intents);
    }

    [Fact]
    public async Task The_quote_hash_is_required_and_bound_to_what_was_priced_and_to_the_destination_and_it_expires()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        string Quote(JsonElement e) => e.GetProperty("quoteHash").GetString()!;
        // none, garbage, and a hash of another destination are all refused before anything is created
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address(), quoteHash = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address(), quoteHash = "x.y" })).StatusCode);
        var ny = Quote((await Ok(await Checkout(s, new { address = Address("CA") }))).GetProperty("quote"));      // a hash for CA (this also creates a CA order)
        var orders = await _k.Db(db => db.Orders.CountAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address("NY"), quoteHash = ny })).StatusCode);   // same hash, other state: refused
        Assert.Equal(orders, await _k.Db(db => db.Orders.CountAsync()));
        // changing the stored expiry (the part before the dot) invalidates the hash
        var parts = ny.Split('.');
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address("CA"), quoteHash = (long.Parse(parts[0]) + 1) + "." + parts[1] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Checkout(s, new { address = Address("CA"), quoteHash = ny })).StatusCode);
        // past its expiry a correct hash is refused with a fresh quote
        _k.F.Clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await Ok(await Checkout(s, new { address = Address("CA"), quoteHash = ny }), HttpStatusCode.Conflict);
        Assert.Equal("price_changed", expired.GetProperty("code").GetString());
        Assert.NotEqual(ny, Quote(expired.GetProperty("quote")));
    }

    [Fact]
    public async Task Order_answers_are_never_cached_also_for_an_unknown_order_and_without_a_session()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var number = (await Ok(await Checkout(s, new { address = Address() }))).GetProperty("orderNumber").GetString();
        foreach (var (path, session) in new[] { ($"/api/v1/checkout/orders/{number}", s), ("/api/v1/checkout/orders/MP-NOPE", s), ($"/api/v1/checkout/orders/{number}", (string?)null) })
        {
            var r = new HttpRequestMessage(HttpMethod.Get, path);
            if (session != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            var res = await _k.Http.SendAsync(r);
            Assert.Contains("no-store", string.Join(",", res.Headers.CacheControl?.ToString() ?? ""));
        }
    }

    [Fact]
    public async Task A_buyer_reads_only_their_own_order()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        var number = (await Ok(await Checkout(s, new { address = Address() }))).GetProperty("orderNumber").GetString();
        var own = await _k.Get($"/api/v1/checkout/orders/{number}", s);
        var body = await Ok(own);
        Assert.Equal("pending_payment", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("groups").GetArrayLength());
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Get($"/api/v1/checkout/orders/{number}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get($"/api/v1/checkout/orders/{number}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Get("/api/v1/checkout/orders/MP-20260101-NOPE", s)).StatusCode);
    }

    // ---- Stripe provider (the real HTTP client against a scripted handler) ----

    private sealed class Script(Func<HttpRequestMessage, string, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r, body));
            return f(r, body);
        }
    }

    private static StripeHttpProvider Http(Script h, string? key = "sk_test_abc") => new(new HttpClient(h), Options.Create(new StripeOptions { SecretKey = key, BaseUrl = "https://stripe.test/", InUseRetryDelayMs = 1 }));
    private static HttpResponseMessage J(HttpStatusCode s, string json) => new(s) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_stripe_client_sends_an_idempotency_key_the_secret_key_and_a_form_body_and_maps_answers()
    {
        var h = new Script((_, _) => J(HttpStatusCode.OK, "{\"id\":\"pi_1\",\"client_secret\":\"pi_1_secret_x\",\"status\":\"requires_payment_method\"}"));
        var r = await Http(h).CreateIntentAsync(4573, "USD", "pi:MP-1", new Dictionary<string, string> { ["marketplace_order_id"] = "MP-1" }, default);
        Assert.Equal(("pi_1", "pi_1_secret_x"), (r.Id, r.ClientSecret));
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("https://stripe.test/v1/payment_intents", req.RequestUri!.ToString());
        Assert.Equal("Bearer sk_test_abc", req.Headers.Authorization!.ToString());
        Assert.Equal("pi:MP-1", Assert.Single(req.Headers.GetValues("Idempotency-Key")));
        Assert.Contains("amount=4573", body);
        Assert.Contains("currency=usd", body);
        Assert.Contains("metadata%5Bmarketplace_order_id%5D=MP-1", body);

        var h2 = new Script((_, _) => J(HttpStatusCode.OK, "{\"id\":\"re_1\",\"status\":\"succeeded\",\"amount\":500}"));
        var refund = await Http(h2).RefundAsync("pi_1", 500, "refund:abc", new Dictionary<string, string>(), default);
        Assert.Equal(("re_1", 500), (refund.Id, refund.AmountCents));
        Assert.Equal("https://stripe.test/v1/refunds", h2.Seen[0].Req.RequestUri!.ToString());
        Assert.Equal("refund:abc", h2.Seen[0].Req.Headers.GetValues("Idempotency-Key").Single());
        Assert.Contains("payment_intent=pi_1", h2.Seen[0].Body);

        var h3 = new Script((_, _) => J(HttpStatusCode.OK, "{}"));
        await Http(h3).CancelIntentAsync("pi_1", "cancel:MP-1", default);
        Assert.Equal("https://stripe.test/v1/payment_intents/pi_1/cancel", h3.Seen[0].Req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Stripe_errors_are_split_into_retryable_and_final_and_never_echo_the_message()
    {
        foreach (var status in new[] { 429, 500, 503, 409 })
            await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(new Script((_, _) => J((HttpStatusCode)status, "{}"))).CreateIntentAsync(1000, "usd", "k", new Dictionary<string, string>(), default));
        var ex = await Assert.ThrowsAsync<PaymentRejectedException>(() => Http(new Script((_, _) => J(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"charge_exceeds_amount\",\"message\":\"card 4242 secret detail\"}}")))
            .RefundAsync("pi_1", 1, "k", new Dictionary<string, string>(), default));
        Assert.Equal("charge_exceeds_amount", ex.Code);
        Assert.DoesNotContain("4242", ex.Message);
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(new Script((_, _) => throw new HttpRequestException("down"))).CreateIntentAsync(1000, "usd", "k", new Dictionary<string, string>(), default));
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(new Script((_, _) => J(HttpStatusCode.OK, "{}")), key: null).CreateIntentAsync(1000, "usd", "k", new Dictionary<string, string>(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Http(new Script((_, _) => J(HttpStatusCode.OK, "{}"))).CreateIntentAsync(1000, "usd", "", new Dictionary<string, string>(), default));
    }

    [Fact]
    public async Task A_409_idempotency_key_in_use_is_asked_again_with_the_same_key_and_gives_up_as_try_again_after_the_retries()
    {
        // the first request with the key is still running at Stripe: two 409s, then its result
        var n = 0;
        var h = new Script((_, _) => ++n < 3 ? J(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"idempotency_key_in_use\"}}") : J(HttpStatusCode.OK, "{\"id\":\"pi_1\",\"client_secret\":\"s\",\"status\":\"requires_payment_method\"}"));
        var r = await Http(h).CreateIntentAsync(1000, "usd", "pi:MP-9", new Dictionary<string, string>(), default);
        Assert.Equal("pi_1", r.Id);
        Assert.Equal(3, h.Seen.Count);
        Assert.All(h.Seen, x => Assert.Equal("pi:MP-9", x.Req.Headers.GetValues("Idempotency-Key").Single()));
        // still busy after every retry: retryable, not final (4 requests = 1 + 3 retries)
        var busy = new Script((_, _) => J(HttpStatusCode.Conflict, "{}"));
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => Http(busy).CreateIntentAsync(1000, "usd", "k", new Dictionary<string, string>(), default));
        Assert.Equal(4, busy.Seen.Count);
    }

    [Fact]
    public async Task The_fake_processor_behaves_like_stripe_on_idempotency_and_refund_limits()
    {
        var f = new FakePaymentProvider();
        var m = new Dictionary<string, string> { ["a"] = "b" };
        var one = await f.CreateIntentAsync(1000, "usd", "k1", m, default);
        Assert.Equal(one, await f.CreateIntentAsync(1000, "usd", "k1", m, default));
        Assert.Single(f.Intents);
        await Assert.ThrowsAsync<PaymentRejectedException>(() => f.CreateIntentAsync(2000, "usd", "k1", m, default));   // same key, other parameters
        await Assert.ThrowsAsync<PaymentRejectedException>(() => f.RefundAsync(one.Id, 100, "r0", m, default));        // nothing captured yet
        f.Capture(one.Id);
        var r1 = await f.RefundAsync(one.Id, 600, "r1", m, default);
        Assert.Equal(r1, await f.RefundAsync(one.Id, 600, "r1", m, default));                                          // retry: same refund
        Assert.Single(f.Refunds);
        await Assert.ThrowsAsync<PaymentRejectedException>(() => f.RefundAsync(one.Id, 500, "r2", m, default));        // 600 + 500 > 1000
        await f.RefundAsync(one.Id, 400, "r3", m, default);
        Assert.Equal(1000, f.Intents.Single().RefundedCents);
    }

    // ---- webhook signature (used by the Stripe webhook endpoint) ----

    [Fact]
    public void Stripe_signatures_are_checked_over_the_raw_body_with_a_five_minute_window()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        const string secret = "whsec_test_secret_value_0123456789";
        var body = "{\"id\":\"evt_1\"}";
        var good = StripeSignature.Sign(body, secret, now);
        Assert.True(StripeSignature.Verify(good, Encoding.UTF8.GetBytes(body), secret, now));
        Assert.False(StripeSignature.Verify(good, Encoding.UTF8.GetBytes(body + " "), secret, now));                         // tampered body
        Assert.False(StripeSignature.Verify(good, Encoding.UTF8.GetBytes(body), "whsec_other_secret_value_0123456789", now));   // another key
        Assert.True(StripeSignature.Verify(StripeSignature.Sign(body, secret, now.AddSeconds(-300)), Encoding.UTF8.GetBytes(body), secret, now));
        Assert.False(StripeSignature.Verify(StripeSignature.Sign(body, secret, now.AddSeconds(-301)), Encoding.UTF8.GetBytes(body), secret, now));
        Assert.False(StripeSignature.Verify(StripeSignature.Sign(body, secret, now.AddSeconds(301)), Encoding.UTF8.GetBytes(body), secret, now));
        var mac = good[(good.IndexOf("v1=") + 3)..];
        Assert.True(StripeSignature.Verify($"t={now.ToUnixTimeSeconds()},v1=deadbeef,v1={mac}", Encoding.UTF8.GetBytes(body), secret, now));   // a rotating secret sends two
        foreach (var bad in new[] { "", "garbage", $"v1={mac}", $"t={now.ToUnixTimeSeconds()}", $"t=abc,v1={mac}", $"t={now.ToUnixTimeSeconds()},v1=zz" })
            Assert.False(StripeSignature.Verify(bad, Encoding.UTF8.GetBytes(body), secret, now));
        Assert.False(StripeSignature.Verify(good, Encoding.UTF8.GetBytes(body), "", now));
        Assert.False(StripeSignature.Verify(null, Encoding.UTF8.GetBytes(body), secret, now));
    }

    // ---- start-up rules for the Stripe keys ----

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> values, string env)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return StartupValidator.Validate(config, new FakeEnv(env));
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> Production(params (string, string?)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('d', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Notifications:PublicApiBaseUrl"] = "https://api.example.test", ["Auth:GuestLinkKey"] = new string('G', 40), ["Checkout:QuoteKey"] = new string('Q', 40),
            ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example.com", ["Email:From"] = "no-reply@shop.example.com",
            ["Portal:BaseUrl"] = "https://portal.example.com", ["Marketplace:RelayDomain"] = "relay.example.com", ["Marketplace:PublicBaseUrl"] = "https://shop.example.com",
            ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=x",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Production")]
    public void Live_stripe_keys_stop_the_application_in_every_environment(string env)
    {
        foreach (var live in new[] { "sk_live_abc123", "rk_live_abc123", "SK_LIVE_x" })
            Assert.Contains(Validate(env == "Production" ? Production(("Stripe:SecretKey", live), ("Stripe:WebhookSecret", new string('w', 40))) : new() { ["Stripe:SecretKey"] = live }, env), p => p.Contains("LIVE key") || p.Contains("test key"));
        Assert.Contains(Validate(new() { ["Stripe:PublishableKey"] = "pk_live_abc" }, env), p => p.Contains("LIVE key"));
        Assert.Contains(Validate(new() { ["Stripe:SecretKey"] = "whatever" }, env), p => p.Contains("test key"));
    }

    [Fact]
    public void The_quote_key_is_required_long_distinct_and_never_the_dev_default_in_production()
    {
        Assert.Empty(Validate(Production(), "Production"));
        var missing = Production(); missing.Remove("Checkout:QuoteKey");
        Assert.Contains(Validate(missing, "Production"), p => p.Contains("Checkout:QuoteKey") && p.Contains("required"));
        var shortKey = Production(); shortKey["Checkout:QuoteKey"] = "short";
        Assert.Contains(Validate(shortKey, "Production"), p => p.Contains("Checkout:QuoteKey") && p.Contains("32"));
        var dev = Production(); dev["Checkout:QuoteKey"] = DevDefaults.QuoteKey;
        Assert.Contains(Validate(dev, "Production"), p => p.Contains("Checkout:QuoteKey") && p.Contains("Development default"));
        var reused = Production(); reused["Checkout:QuoteKey"] = reused["Auth:GuestLinkKey"]!;
        Assert.Contains(Validate(reused, "Production"), p => p.Contains("must differ"));
        // Development and Testing fall back to the dev default, so a missing key never stops them
        Assert.Equal(DevDefaults.QuoteKey, Ok(devLike: true));
        static string? Ok(bool devLike) { var o = new Solvelt.Marketplace.Api.Infrastructure.CheckoutOptions(); OptionsSetup.Checkout(o, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), devLike); return o.QuoteKey; }
    }

    [Fact]
    public void A_stripe_key_must_not_double_as_one_of_our_own_keys()
    {
        var shared = "sk_test_" + new string('k', 40);
        var cfg = Production(("Stripe:SecretKey", shared), ("Stripe:WebhookSecret", "whsec_" + new string('w', 40)));
        cfg["Portal:InboundSecret"] = shared;
        Assert.Contains(Validate(cfg, "Production"), p => p.Contains("Stripe:SecretKey") && p.Contains("must differ"));
        var pub = "pk_test_" + new string('p', 40);
        var cfg2 = Production(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:PublishableKey", pub), ("Stripe:WebhookSecret", "whsec_" + new string('w', 40)));
        cfg2["Web:ForwardingKey"] = pub;
        Assert.Contains(Validate(cfg2, "Production"), p => p.Contains("Stripe:PublishableKey") && p.Contains("must differ"));
    }

    [Fact]
    public async Task A_us_destination_needs_a_real_state_a_price_above_the_cap_is_unavailable_and_a_total_above_the_cap_is_refused()
    {
        var s = await Buyer();
        await AddToCart(s, "kiddush-cup-pewter");
        foreach (var region in new object?[] { null, "", "ZZ", "N1" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Checkout(s, new { address = new { name = "A", line1 = "x", city = "c", region, postalCode = "20001", country = "US" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Checkout(s, new { address = Address("dc") })).StatusCode);          // lower case is accepted and normalised
        Assert.Equal("DC", await _k.Db(db => db.Orders.Select(o => o.ShipRegion).SingleAsync()));
        // a price above the cap makes the line unavailable (it blocks checkout until removed)
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Variants = l.Variants.Select(v => v with { PriceCents = CartLimits.MaxUnitPriceCents + 1 }).ToList() });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(s, new { address = Address() })).StatusCode);
        // at the cap one unit is fine, but 20 of them exceed the total cap: refused, not charged, not overflowed
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Variants = l.Variants.Select(v => v with { PriceCents = CartLimits.MaxUnitPriceCents }).ToList() });
        await CatalogKit.Sync(_k.F);
        var variant = await _k.Db(db => db.CatalogVariants.Where(v => v.PriceCents == CartLimits.MaxUnitPriceCents).Select(v => v.Id).FirstAsync());
        var set = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cart"); set.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        var cart = JsonDocument.Parse(await (await _k.Http.SendAsync(set)).Content.ReadAsStringAsync()).RootElement;
        var line = cart.GetProperty("groups")[0].GetProperty("lines")[0].GetProperty("id").GetGuid();
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/cart/lines/" + line) { Content = JsonContent.Create(new { quantity = 20 }) }; put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s);
        await _k.Http.SendAsync(put);
        var intents = Stripe.Intents.Count;
        var r = await Ok(await Checkout(s, new { address = Address() }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("amount_too_large", r.GetProperty("code").GetString());
        Assert.Equal(intents, Stripe.Intents.Count);
    }

    [Fact]
    public void Test_keys_pass_and_production_needs_a_distinct_webhook_secret_once_a_key_is_set()
    {
        Assert.Empty(Validate(new() { ["Stripe:SecretKey"] = "sk_test_abc", ["Stripe:PublishableKey"] = "pk_test_abc" }, "Testing"));
        Assert.Empty(Validate(Production(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:WebhookSecret", "whsec_" + new string('w', 40))), "Production"));
        Assert.Empty(Validate(Production(), "Production"));                                                                     // no Stripe configured: payments are simply off
        Assert.Contains(Validate(Production(("Stripe:SecretKey", "sk_test_abc")), "Production"), p => p.Contains("Stripe:WebhookSecret"));
        Assert.Contains(Validate(Production(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:WebhookSecret", new string('a', 40))), "Production"), p => p.Contains("must differ"));   // equals the portal inbound secret
        Assert.Contains(Validate(Production(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:WebhookSecret", DevDefaults.StripeWebhookSecret)), "Production"), p => p.Contains("Development default"));
        Assert.Contains(Validate(Production(("Stripe:SecretKey", "sk_test_abc"), ("Stripe:WebhookSecret", "short")), "Production"), p => p.Contains("32"));
    }

    [Fact]
    public async Task Checkout_answers_503_when_no_processor_is_configured_outside_development()
    {
        // the unconfigured provider is what a production app without a Stripe key gets
        var p = new UnconfiguredPaymentProvider();
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => p.CreateIntentAsync(1000, "usd", "k", new Dictionary<string, string>(), default));
        await Assert.ThrowsAsync<PaymentsUnavailableException>(() => p.RefundAsync("pi", 1, "k", new Dictionary<string, string>(), default));
    }
}
