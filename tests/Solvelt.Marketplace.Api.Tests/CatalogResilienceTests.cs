using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Azure issue #4: ONE listing document the portal sends that we cannot read (a demo service without a category, a quote-priced service without a price) used to end the whole catalog sync, every run, so no later change was
/// applied. These run the real <see cref="CatalogSyncService"/> with the real <see cref="HttpCatalogSource"/> over the fake portal, and set the unreadable documents aside without stopping the others.
/// </summary>
public sealed class CatalogResilienceTests : IDisposable
{
    private const string Secret = "unit-test-inbound-secret-0123456789abcdef";
    private static readonly Guid SellerId = Guid.Parse("a0000000-0000-0000-0000-000000000001"), BrandId = Guid.Parse("b0000000-0000-0000-0000-000000000001"), CategoryId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);

    private readonly TestFactory _f = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePortal _portal;
    private readonly HttpCatalogSource _source;
    private readonly Dictionary<Guid, string?> _single = new();     // the single-listing answers: a document, or null for 404
    private string _feed = JsonSerializer.Serialize(new { items = Array.Empty<object>(), nextCursor = (string?)null, hasMore = false });
    public void Dispose() => _f.Dispose();

    public CatalogResilienceTests()
    {
        _portal = new FakePortal(Secret, _clock);
        _portal.RespondAlways(Route);
        var client = new SignedPortalClient(new HttpClient(_portal) { BaseAddress = new Uri("https://portal.test"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new PortalOptions { BaseUrl = "https://portal.test", InboundSecret = Secret, RetryBaseDelayMs = 0, MaxAttempts = 1 }), _clock);
        _source = new HttpCatalogSource(client, _clock, NullLogger<HttpCatalogSource>.Instance);
    }

    private HttpResponseMessage Route(ReceivedCall c)
    {
        var p = c.PathAndQuery;
        if (p.Contains("/catalog/categories")) return FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { categoryId = CategoryId, parentId = (Guid?)null, name = "Kitchen", slug = "kitchen", sortOrder = 0 } } }));
        if (p.Contains("/catalog/brands")) return FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { brandId = BrandId, sellerId = SellerId, name = "Clay", slug = "clay" } }, hasMore = false }));
        if (p.Contains("/catalog/listings/"))
        {
            var id = Guid.Parse(p.Split("/catalog/listings/")[1].Split('?')[0]);
            return _single.GetValueOrDefault(id) is { } doc ? FakePortal.Json(HttpStatusCode.OK, doc) : FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        }
        if (p.Contains("/catalog/listings")) return FakePortal.Json(HttpStatusCode.OK, _feed);
        return FakePortal.Json(HttpStatusCode.NotFound, "{}");
    }

    // ---------------------------------------------------------------- documents as the portal's CatalogReader builds them
    private static Dictionary<string, object?> Doc(int n, long version = 10, Action<Dictionary<string, object?>>? edit = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["listingId"] = G(n), ["version"] = version, ["listed"] = true, ["type"] = "product", ["title"] = $"Mug {n}", ["handle"] = $"mug-{n}", ["condition"] = "new",
            ["description"] = "A lovely mug.", ["descriptionHtml"] = "<p>A lovely mug.</p>",
            ["price"] = new { amountMinor = 2000L, currency = "USD" }, ["availability"] = new { state = "in_stock" },
            ["category"] = new { id = CategoryId, name = "Kitchen", slug = "kitchen", path = Array.Empty<object>() },
            ["brand"] = new { brandId = BrandId, name = "Clay", slug = "clay" }, ["seller"] = new { sellerId = SellerId, displayName = "Clay Ltd", country = "US" },
            ["variants"] = Array.Empty<object>(), ["images"] = Array.Empty<object>(), ["attributes"] = Array.Empty<object>(), ["faqs"] = Array.Empty<object>(),
            ["delivery"] = new { ships = true, localDelivery = false, serviceLocations = Array.Empty<string>() },
            ["publishedAt"] = "2026-10-01T00:00:00Z", ["updatedAt"] = "2026-11-01T00:00:00Z",
        };
        edit?.Invoke(d);
        return d;
    }
    /// <summary>The portal's demo service without a category: <c>category</c> is null.</summary>
    private static Dictionary<string, object?> NoCategory(int n, long version = 10) => Doc(n, version, d => { d["type"] = "service"; d["category"] = null; });
    /// <summary>A service the portal prices by quote: the price object is there, its amount is null, and <c>service.pricingModel</c> says <c>quote</c> (seen in the portal's CatalogReader; docs/CATALOG.md does not describe it).</summary>
    private static Dictionary<string, object?> QuotePriced(int n, long version = 10) => Doc(n, version, d =>
    {
        d["type"] = "service"; d["price"] = new { amountMinor = (long?)null, currency = "USD", compareAtMinor = (long?)null, sale = (object?)null };
        d["service"] = new { pricingModel = "quote", priceMinor = (long?)null, minMinutes = 60, maxMinutes = 240, incrementMinutes = 30, defaultDurationMinutes = 60, online = false, atCustomer = true, atSeller = false, bookingMode = "request", travelFeeMinor = 0 };
    });
    private static Dictionary<string, object?> NoPrice(int n, long version = 10) => Doc(n, version, d => d.Remove("price"));

    private static string Page(IEnumerable<object?> items, string cursor = "10:z") => JsonSerializer.Serialize(new { items, nextCursor = cursor, hasMore = false });
    private async Task<CatalogSyncResult> Run(OpsCounters? ops = null, int maxPending = 100)
    {
        using var scope = _f.Services.CreateScope();
        return await new CatalogSyncService(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), _source, _clock,
            Options.Create(new CatalogOptions { PageSize = 100, MaxPendingFetchesPerRun = maxPending }), NullLogger<CatalogSyncService>.Instance, ops).RunAsync();
    }
    private List<CatalogQuarantine> Quarantined() => WithDb(_f, db => db.CatalogQuarantine.AsNoTracking().OrderBy(q => q.Reason).ToList());

    [Fact]
    public async Task A_page_with_unreadable_documents_applies_the_readable_ones_sets_the_others_aside_and_counts_them_on_health()
    {
        _feed = Page([Doc(1), NoCategory(2), QuotePriced(3), NoPrice(4), "not even an object", Doc(5)]);
        var ops = _f.Services.GetRequiredService<OpsCounters>();
        var r = await Run(ops);
        Assert.True(r.Ran);
        Assert.Null(r.Error);                                                                  // the run did not fail
        Assert.Equal(2, r.Applied);                                                            // the two good ones
        Assert.Equal(4, r.Skipped);                                                            // the four it could not read
        Assert.Equal(new[] { (G(2), "category"), (G(4), "price"), (G(3), "quote_price") }.OrderBy(x => x.Item2).ToList(), Quarantined().Select(q => (q.ListingId, q.Reason)).ToList());   // the junk item has no id: counted, nothing to record
        Assert.Equal(new[] { G(1), G(5) }, WithDb(_f, db => db.CatalogListings.AsNoTracking().Where(l => l.Visible).Select(l => l.Id).OrderBy(i => i).ToList()));
        Assert.Equal("10:z", WithDb(_f, db => db.CatalogSyncStates.AsNoTracking().Single().Cursor));   // the cursor moved on
        Assert.NotNull(WithDb(_f, db => db.CatalogSyncStates.AsNoTracking().Single().LastSuccessAt));
        var health = JsonDocument.Parse(await (await _f.CreateClient().GetAsync("/health")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, health.GetProperty("catalog").GetProperty("unreadable").GetInt32());
        // the same page again: nothing doubles
        await Run(ops);
        Assert.Equal(3, Quarantined().Count);
        Assert.All(Quarantined(), q => Assert.Equal(2, q.Seen));
    }

    [Fact]
    public async Task An_unreadable_document_on_a_single_fetch_is_set_aside_and_backed_off_and_the_listings_behind_it_are_still_fetched()
    {
        // two listings we hold; the portal announced a newer version of each. The first (lower version, so first in line) now has no category: this used to end the run, every run, before the second was reached.
        _feed = Page([Doc(1, 10), Doc(2, 10)]);
        Assert.Equal(2, (await Run()).Applied);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await db.CatalogListings.Where(l => l.Id == G(1)).ExecuteUpdateAsync(s => s.SetProperty(l => l.KnownVersion, 11L));    // the portal announced newer versions
            await db.CatalogListings.Where(l => l.Id == G(2)).ExecuteUpdateAsync(s => s.SetProperty(l => l.KnownVersion, 12L));
        }
        _feed = Page([]);
        _single[G(1)] = JsonSerializer.Serialize(NoCategory(1, 11));
        _single[G(2)] = JsonSerializer.Serialize(Doc(2, 12, d => d["title"] = "Mug 2, second edition"));
        var r = await Run();
        Assert.Null(r.Error);                                                                  // not an outage
        Assert.Equal(1, r.Fetched);                                                            // the second one was reached and applied
        var two = WithDb(_f, db => db.CatalogListings.AsNoTracking().Single(l => l.Id == G(2)));
        Assert.Equal(("Mug 2, second edition", 12L, true), (two.Title, two.Version, two.Visible));
        var one = WithDb(_f, db => db.CatalogListings.AsNoTracking().Single(l => l.Id == G(1)));
        Assert.Equal(("Quarantined", false, 10L, 11L), (one.Status, one.Visible, one.Version, one.KnownVersion));   // hidden, and still waiting for a readable version
        Assert.Equal((1, "category"), (Quarantined().Count, Quarantined().Single().Reason));
        Assert.True(one.NextFetchAt > _clock.GetUtcNow() && one.FetchAttempts == 1);           // it does not take the first place in the queue again at once
        // the next run, before the back-off ends, does not ask for it again
        var calls = _portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains($"/catalog/listings/{G(1):D}"));
        Assert.Null((await Run()).Error);
        Assert.Equal(calls, _portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains($"/catalog/listings/{G(1):D}")));
    }

    [Fact]
    public async Task A_listing_set_aside_comes_back_when_the_portal_sends_a_readable_version_and_is_forgotten_when_it_is_removed()
    {
        _feed = Page([Doc(1, 10), Doc(2, 10)]);
        await Run();
        _feed = Page([NoCategory(1, 11), QuotePriced(2, 11)], "11:z");
        var ops = _f.Services.GetRequiredService<OpsCounters>();
        await Run(ops);
        Assert.Equal(2, Quarantined().Count);
        Assert.Equal(0, WithDb(_f, db => db.CatalogListings.Count(l => l.Visible)));          // both are hidden while their new document is unreadable
        // the portal fixes the first (version 12 has its category) and unlists the second (a tombstone)
        _feed = JsonSerializer.Serialize(new { items = new object[] { Doc(1, 12, d => d["title"] = "Mug 1, fixed"), new { listingId = G(2), version = 13, listed = false, reason = "unlisted" } }, nextCursor = "13:z", hasMore = false });
        await Run(ops);
        Assert.Empty(Quarantined());                                                           // nothing left to set aside
        Assert.Equal(0, ops.CatalogQuarantined);
        var one = WithDb(_f, db => db.CatalogListings.AsNoTracking().Single(l => l.Id == G(1)));
        Assert.Equal(("Mug 1, fixed", 12L, true, "Active"), (one.Title, one.Version, one.Visible, one.Status));
        Assert.False(WithDb(_f, db => db.CatalogListings.AsNoTracking().Single(l => l.Id == G(2)).Visible));
    }

    /// <summary>A product the seller prices only per variation: the portal sends price.amountMinor null and the resolved prices on the variants (docs/CATALOG.md).</summary>
    private static Dictionary<string, object?> VariantPriced(int n, params long?[] amounts) => Doc(n, 10, d =>
    {
        d["price"] = new { amountMinor = (long?)null, currency = "USD", compareAtMinor = (long?)null, sale = (object?)null };
        d["variants"] = amounts.Select((a, i) => (object)new { variantId = G(n * 100 + i), values = new[] { $"Size {i + 1}" }, price = new { amountMinor = a, currency = "USD" }, availability = new { state = "in_stock" } }).ToArray();
    });

    [Fact]
    public async Task A_product_priced_only_by_its_variants_is_listed_with_the_lowest_price_and_one_priced_nowhere_is_still_set_aside()
    {
        _feed = Page([Doc(1), VariantPriced(2, 2400, 1850), VariantPriced(3, null, null)]);
        var r = await Run();
        Assert.Null(r.Error);
        Assert.Equal(2, r.Applied);
        Assert.Equal(new[] { (G(3), "price") }, Quarantined().Select(q => (q.ListingId, q.Reason)));                     // priced nowhere: unreadable, as before
        var two = WithDb(_f, db => db.CatalogListings.AsNoTracking().Include(l => l.Variants).Single(l => l.Id == G(2)));
        Assert.Equal((true, 1850L, 2400L, "USD"), (two.Visible, two.PriceFromCents, two.PriceToCents, two.Currency));       // the listing's price range comes from its variants
        Assert.Equal(new long[] { 1850, 2400 }, two.Variants.Select(v => v.PriceCents).OrderBy(c => c));
        // what the buyer sees follows: the card, the sort, and the price filter (a range that overlaps the variants' prices)
        var c = _f.CreateClient();
        var items = async (string q) => (await c.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/listings?pageSize=100&{q}")).GetProperty("items").EnumerateArray().ToList();
        var asc = await items("sort=price_asc");
        Assert.Equal(new[] { (G(2), 1850L, 2400L), (G(1), 2000L, 2000L) }, asc.Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("priceFromCents").GetInt64(), i.GetProperty("priceToCents").GetInt64())));
        Assert.Equal(new[] { G(2) }, (await items("priceMin=2300&priceMax=2500")).Select(i => i.GetProperty("id").GetGuid()));    // 2400 is a price of Mug 2, and Mug 1 (2000) is out
        Assert.Equal(new[] { G(2) }, (await items("priceMax=1900")).Select(i => i.GetProperty("id").GetGuid()));                // 1850 is, and Mug 1 (2000) is out
    }
}
