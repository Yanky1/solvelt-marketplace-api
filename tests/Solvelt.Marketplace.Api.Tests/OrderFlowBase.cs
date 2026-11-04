using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Everything an order flow test needs: the app, a fake portal that creates seller orders, a fake Stripe, a signed-in buyer, a cart, checkout, signed Stripe webhooks and the outbox.</summary>
public abstract class OrderFlowBase : IDisposable
{
    protected static readonly Guid Mazel = FixtureCatalogSource.Id("seller:mazel"), ChallahCorner = FixtureCatalogSource.Id("brand:challah-corner"),
        Judaica = FixtureCatalogSource.Id("seller:judaica"), ShabbatTable = FixtureCatalogSource.Id("brand:shabbat-table");

    protected readonly AuthKit _k = new();
    protected FakePaymentProvider Stripe => _k.F.Services.GetRequiredService<FakePaymentProvider>();
    protected string _session = "";
    protected int _eventCounter;

    /// <summary>How the fake portal answers <c>POST /orders</c>; null means "create it and answer 201".</summary>
    protected Func<ReceivedCall, HttpResponseMessage>? _onOrder;
    protected int _orderCalls;
    protected readonly Dictionary<string, (Guid Id, string Number)> _sellerOrders = new();
    protected bool _oversold;

    protected OrderFlowBase()
    {
        _k.F.Portal.RespondAlways(c =>
        {
            var path = c.PathAndQuery;
            if (path.StartsWith("/api/internal/v1/shipping/settings"))
            {
                var q = System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]);
                return FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sellerId = Guid.Parse(q["sellerId"]!), brandId = Guid.Parse(q["brandId"]!), handlingDays = 1,
                    profiles = new[] { new { id = Guid.NewGuid(), name = "Default", isDefault = true, currency = "USD", freeShippingThresholdCents = (long?)null, zones = new[] { new { name = "US", kind = "domestic", countries = new[] { "US" },
                        speeds = new object[] { new { speed = "standard", enabled = true, method = "flat", flatCents = 500 }, new { speed = "overnight", enabled = true, method = "flat", flatCents = 1800 } } } } } },
                }));
            }
            if (path == "/api/internal/v1/orders")
            {
                Interlocked.Increment(ref _orderCalls);
                return _onOrder is not null ? _onOrder(c) : Created(c, 201, true);
            }
            if (path == "/api/internal/v1/payment-events") return FakePortal.Json(HttpStatusCode.OK, "{\"status\":\"ok\"}");
            return FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        });
    }

    public void Dispose() => _k.Dispose();

    protected static Guid Variant(string slug, int i = 0) => FixtureCatalogSource.Id($"variant:{slug}:{i}");

    /// <summary>The portal's answer to an ingest: one seller order per (seller, brand), stable numbers per marketplace id and group.</summary>
    protected HttpResponseMessage Created(ReceivedCall c, int status, bool created)
    {
        using var doc = JsonDocument.Parse(c.BodyText);
        var mid = doc.RootElement.GetProperty("marketplaceOrderId").GetString()!;
        var groups = doc.RootElement.GetProperty("lines").EnumerateArray().GroupBy(l => (l.GetProperty("sellerId").GetGuid(), l.GetProperty("brandId").GetGuid()));
        var orders = new List<object>();
        foreach (var g in groups)
        {
            var key = $"{mid}|{g.Key.Item1}|{g.Key.Item2}";
            if (!_sellerOrders.TryGetValue(key, out var so)) _sellerOrders[key] = so = (Guid.NewGuid(), $"SO-2026-{_sellerOrders.Count + 1:D6}");
            var items = g.Sum(l => l.GetProperty("unitPriceCents").GetInt64() * l.GetProperty("quantity").GetInt64());
            var tax = g.Sum(l => l.GetProperty("taxCents").GetInt64());
            var ship = doc.RootElement.GetProperty("shipping").EnumerateArray().Where(s => s.GetProperty("sellerId").GetGuid() == g.Key.Item1 && s.GetProperty("brandId").GetGuid() == g.Key.Item2).Sum(s => s.GetProperty("amountCents").GetInt64());
            orders.Add(new { sellerOrderId = so.Id, number = so.Number, sellerId = g.Key.Item1, brandId = g.Key.Item2, status = "New", oversold = _oversold, totalCents = items + tax + ship, feeCents = items / 10, netCents = items + ship - items / 10 });
        }
        return FakePortal.Json((HttpStatusCode)status, JsonSerializer.Serialize(new { created, orders }));
    }

    protected async Task Login(string email = "ing1@example.com")
    {
        await CatalogKit.Sync(_k.F);
        _session = await _k.VerifiedSession(email);
    }

    protected async Task AddToCart(string slug, int qty = 1)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/lines") { Content = JsonContent.Create(new { variantId = Variant(slug), quantity = qty }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        Assert.Equal(HttpStatusCode.OK, (await _k.Http.SendAsync(r)).StatusCode);
    }

    protected static object Address(string region = "NY") => new { name = "Jane Brown", line1 = "1 Main St", city = "Brooklyn", region, postalCode = "11201", country = "US", phone = "+1 718 555 0100" };

    /// <summary>Checks out the cart and returns (order number, intent id, total).</summary>
    protected async Task<(string Number, string Intent, long Total)> Checkout(object? extra = null, string region = "NY")
    {
        var body = extra ?? new { address = Address(region) };
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(r);
        var s = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, s);
        var j = JsonDocument.Parse(s).RootElement;
        var number = j.GetProperty("orderNumber").GetString()!;
        var intent = Stripe.Intents.Single(i => i.Metadata["marketplace_order_id"] == number);
        return (number, intent.Id, j.GetProperty("quote").GetProperty("totalCents").GetInt64());
    }

    protected string Event(string type, string intentId, string? number, long amount, string currency = "usd", string? id = null, object? extra = null)
    {
        var obj = new Dictionary<string, object?> { ["id"] = intentId, ["amount"] = amount, ["amount_received"] = amount, ["currency"] = currency, ["metadata"] = number is null ? new { } : new { marketplace_order_id = number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) obj[p.Name] = p.GetValue(extra);
        return JsonSerializer.Serialize(new { id = id ?? $"evt_{++_eventCounter:D4}", type, created = _k.F.Clock.GetUtcNow().ToUnixTimeSeconds(), data = new { @object = obj } });
    }

    protected async Task<HttpResponseMessage> PostStripe(string body, string? secret = null, string? header = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhooks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("Stripe-Signature", header ?? StripeSignature.Sign(body, secret ?? DevDefaults.StripeWebhookSecret, _k.F.Clock.GetUtcNow()));
        return await _k.Http.SendAsync(r);
    }

    protected async Task Pay(string number, string intent, long total, string? id = null)
    {
        Stripe.Capture(intent);
        var res = await PostStripe(Event("payment_intent.succeeded", intent, number, total, id: id));
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    protected async Task Drain() { while (await _k.Dispatch() > 0) { } }
    protected Task<MarketplaceOrder> Order(string number) => _k.Db(db => db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().SingleAsync(o => o.Number == number));
    protected IReadOnlyList<ReceivedCall> Portal(string path) => _k.F.Portal.VerifiedCalls.Where(c => c.PathAndQuery == "/api/internal/v1/" + path).ToList();
    protected Task<int> Outbox(string type) => _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == type));
}
