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

    private Task<HttpResponseMessage> Checkout(string? session, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(body) };
        if (session != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        return _k.Http.SendAsync(r);
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
        foreach (var r in results) views.Add(await Ok(r));
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

    private static StripeHttpProvider Http(Script h, string? key = "sk_test_abc") => new(new HttpClient(h), Options.Create(new StripeOptions { SecretKey = key, BaseUrl = "https://stripe.test/" }));
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
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('d', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Auth:GuestLinkKey"] = new string('G', 40),
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
