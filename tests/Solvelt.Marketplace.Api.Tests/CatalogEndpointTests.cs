using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogEndpointTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string Base = "/api/v1/catalog";

    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK) => Json(_c, url, expected);
    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;

    // ---------- categories ----------
    [Fact]
    public async Task Categories_are_a_tree_of_only_visible_listings_with_counts()
    {
        var roots = (await Get($"{Base}/categories")).EnumerateArray().ToList();
        Assert.Equal(["food-drink", "home-judaica", "services"], roots.Select(r => r.GetProperty("slug").GetString()));
        Assert.Equal([21, 7, 12], roots.Select(r => r.GetProperty("listingCount").GetInt32()));      // hidden: suspended seller (2) and archived (1)
        var food = roots[0].GetProperty("children").EnumerateArray().ToDictionary(c => c.GetProperty("slug").GetString()!, c => c.GetProperty("listingCount").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["bakery"] = 8, ["meat-poultry"] = 6, ["wine-spirits"] = 4, ["pantry"] = 3 }, food);
        Assert.Equal(40, roots.Sum(r => r.GetProperty("listingCount").GetInt32()));
        var first = roots[0];
        Assert.Equal(["id", "slug", "name", "listingCount", "children"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Food & Drink", first.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_category_with_no_visible_listings_disappears_from_the_tree()
    {
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.CategoryId == FixtureCatalogSource.Id("category:books"))) fx.Upsert(l.Id, x => x with { Status = "Archived" });
        await Sync(_f);
        var home = (await Get($"{Base}/categories")).EnumerateArray().Single(r => r.GetProperty("slug").GetString() == "home-judaica");
        Assert.DoesNotContain(home.GetProperty("children").EnumerateArray(), c => c.GetProperty("slug").GetString() == "books");
        Assert.Equal(6, home.GetProperty("listingCount").GetInt32());
    }

    // ---------- brands ----------
    [Fact]
    public async Task Brands_lists_only_brands_with_visible_listings_by_name()
    {
        var brands = (await Get($"{Base}/brands")).EnumerateArray().ToList();
        var names = brands.Select(b => b.GetProperty("name").GetString()!).ToList();
        Assert.Equal(9, names.Count);
        Assert.DoesNotContain("Closed Corner", names);                                              // its seller is suspended
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.Equal(40, brands.Sum(b => b.GetProperty("listingCount").GetInt32()));
        var challah = brands.Single(b => b.GetProperty("slug").GetString() == "challah-corner");
        Assert.Equal(("Mazel Bakery", 3), (challah.GetProperty("seller").GetProperty("displayName").GetString(), challah.GetProperty("listingCount").GetInt32()));
    }

    [Fact]
    public async Task Brands_can_be_limited_to_one_seller()
    {
        var mazel = FixtureCatalogSource.Id("seller:mazel");
        var brands = (await Get($"{Base}/brands?sellerId={mazel}")).EnumerateArray().Select(b => b.GetProperty("slug").GetString()).ToList();
        Assert.Equal(["challah-corner", "mazel-bakery"], brands);
        Assert.Empty((await Get($"{Base}/brands?sellerId={Guid.NewGuid()}")).EnumerateArray());
        Assert.Equal("invalid_query", Code(await Get($"{Base}/brands?sellerId=nope", HttpStatusCode.BadRequest)));
    }

    // ---------- listings: paging, shape, filters, sorts ----------
    [Fact]
    public async Task Listings_default_to_newest_first_in_pages_of_24()
    {
        var p1 = await Get($"{Base}/listings");
        Assert.Equal((1, 24, 40, 2), (p1.GetProperty("page").GetInt32(), p1.GetProperty("pageSize").GetInt32(), p1.GetProperty("total").GetInt32(), p1.GetProperty("totalPages").GetInt32()));
        Assert.Equal(24, p1.GetProperty("items").GetArrayLength());
        Assert.Equal("torah-reading-and-trope-lessons", Slugs(p1)[0]);                                // created last, among the visible
        var p2 = await Get($"{Base}/listings?page=2");
        Assert.Equal(16, p2.GetProperty("items").GetArrayLength());
        Assert.Empty(Slugs(p1).Intersect(Slugs(p2)));                                               // pages do not overlap
        var p3 = await Get($"{Base}/listings?page=3");
        Assert.Equal(0, p3.GetProperty("items").GetArrayLength());
        Assert.Equal(40, p3.GetProperty("total").GetInt32());
        var small = await Get($"{Base}/listings?pageSize=7&page=6");
        Assert.Equal((5, 6), (small.GetProperty("items").GetArrayLength(), small.GetProperty("totalPages").GetInt32()));
    }

    [Fact]
    public async Task A_listing_card_has_the_documented_shape()
    {
        var page = await Get($"{Base}/listings?brand=challah-corner&sort=title");
        Assert.Equal(["challah-rolls-dozen", "classic-braided-challah", "whole-wheat-challah"], Slugs(page));
        var card = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "classic-braided-challah");
        Assert.Equal(["id", "slug", "type", "title", "summary", "priceFromCents", "priceToCents", "compareAtFromCents", "currency", "available", "brand", "seller", "category", "image"],
            card.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("classic-braided-challah", "product", "Classic Braided Challah", 650, 950, "USD", true),
            (card.GetProperty("slug").GetString(), card.GetProperty("type").GetString(), card.GetProperty("title").GetString(), card.GetProperty("priceFromCents").GetInt64(),
             card.GetProperty("priceToCents").GetInt64(), card.GetProperty("currency").GetString(), card.GetProperty("available").GetBoolean()));
        Assert.Equal(750, card.GetProperty("compareAtFromCents").GetInt64());
        Assert.Equal("challah-corner", card.GetProperty("brand").GetProperty("slug").GetString());
        Assert.Equal("Mazel Bakery", card.GetProperty("seller").GetProperty("displayName").GetString());
        Assert.Equal("bakery", card.GetProperty("category").GetProperty("slug").GetString());
        var image = card.GetProperty("image");
        Assert.Equal((1200, 1200), (image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32()));
        Assert.NotEqual(Guid.Empty, image.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Suspended_sellers_archived_listings_and_unavailable_state_are_handled_by_the_visibility_rule()
    {
        var all = Slugs(await Get($"{Base}/listings?pageSize=100"));
        Assert.Equal(40, all.Count);
        Assert.DoesNotContain("corner-shop-coffee-mug", all);
        Assert.DoesNotContain("retired-spice-gift-set", all);
        var honey = (await Get($"{Base}/listings?pageSize=100")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "honey-cake-for-rosh-hashanah");
        Assert.False(honey.GetProperty("available").GetBoolean());                                  // sold out but still listed
        Assert.Equal(1550, honey.GetProperty("priceFromCents").GetInt64());                         // all variants sold out: the full range is shown
    }

    [Theory]
    [InlineData("category=food-drink", 21)]
    [InlineData("category=bakery", 8)]
    [InlineData("category=services", 12)]
    [InlineData("category=cleaning", 4)]
    [InlineData("category=nope", 0)]
    [InlineData("brand=glatt-gourmet", 4)]
    [InlineData("brand=shabbos-grill", 2)]
    [InlineData("brand=closed-corner", 0)]
    [InlineData("brand=nope", 0)]
    [InlineData("type=service", 12)]
    [InlineData("type=product", 28)]
    [InlineData("category=food-drink&type=service", 0)]
    [InlineData("category=services&type=service", 12)]
    [InlineData("category=wine-spirits&brand=hudson-valley-vines", 4)]
    [InlineData("category=bakery&brand=glatt-gourmet", 0)]
    public async Task Filters_combine_and_a_parent_category_includes_its_children(string query, int expected)
        => Assert.Equal(expected, (await Get($"{Base}/listings?{query}&pageSize=100")).GetProperty("total").GetInt32());

    [Fact]
    public async Task Filters_accept_ids_as_well_as_slugs()
    {
        var byId = await Get($"{Base}/listings?category={FixtureCatalogSource.Id("category:food-drink")}&brand={FixtureCatalogSource.Id("brand:challah-corner")}");
        Assert.Equal(3, byId.GetProperty("total").GetInt32());
        var bySeller = await Get($"{Base}/listings?seller={FixtureCatalogSource.Id("seller:glatt")}");
        Assert.Equal(6, bySeller.GetProperty("total").GetInt32());   // Glatt Gourmet sells six meat and poultry listings
        Assert.Equal(0, (await Get($"{Base}/listings?seller={FixtureCatalogSource.Id("seller:closed")}")).GetProperty("total").GetInt32());   // suspended
    }

    [Fact]
    public async Task Sorting_by_price_and_title_is_deterministic()
    {
        var asc = (await Get($"{Base}/listings?sort=price_asc&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("priceFromCents").GetInt64()).ToList();
        Assert.Equal(asc.Order(), asc);
        Assert.Equal(600, asc[0]);
        var desc = (await Get($"{Base}/listings?sort=price_desc&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("priceFromCents").GetInt64()).ToList();
        Assert.Equal(desc.OrderDescending(), desc);
        Assert.Equal(48000, desc[0]);
        var titles = (await Get($"{Base}/listings?sort=title&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => Slug.From(i.GetProperty("title").GetString()!)).ToList();
        Assert.Equal(titles.Order(StringComparer.Ordinal), titles);                                 // same on SQLite and PostgreSQL (binary / C collation)
        Assert.Equal("all-beef-hot-dogs-8-count", Slugs(await Get($"{Base}/listings?sort=title&pageSize=1"))[0]);
        // paging through a sort never repeats or drops an item (stable tie-break on id)
        var seen = new List<string>();
        for (var page = 1; page <= 6; page++) seen.AddRange(Slugs(await Get($"{Base}/listings?sort=price_asc&pageSize=7&page={page}")));
        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=abc")]
    [InlineData("page=1.5")]
    [InlineData("page=100001")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=ten")]
    [InlineData("sort=cheapest")]
    [InlineData("type=gadget")]
    [InlineData("seller=not-a-guid")]
    public async Task Bad_query_values_are_a_400_with_a_code(string query)
        => Assert.Equal("invalid_query", Code(await Get($"{Base}/listings?{query}", HttpStatusCode.BadRequest)));

    // ---------- detail ----------
    [Fact]
    public async Task A_listing_detail_by_slug_has_variants_images_attributes_faqs_and_a_breadcrumb()
    {
        var d = await Get($"{Base}/listings/classic-braided-challah");
        Assert.Equal(["id", "slug", "type", "title", "summary", "description", "priceFromCents", "priceToCents", "compareAtFromCents", "currency", "available", "brand", "seller",
            "categoryPath", "variants", "images", "attributes", "faqs", "createdAt", "updatedAt"], d.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["food-drink", "bakery"], d.GetProperty("categoryPath").EnumerateArray().Select(c => c.GetProperty("slug").GetString()));
        var variants = d.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal(["Small", "Large"], variants.Select(v => v.GetProperty("name").GetString()));
        Assert.Equal([650L, 950L], variants.Select(v => v.GetProperty("priceCents").GetInt64()));
        Assert.Equal(750, variants[0].GetProperty("compareAtCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, variants[1].GetProperty("compareAtCents").ValueKind);
        Assert.Equal(3, d.GetProperty("images").GetArrayLength());
        Assert.Equal(["kosher-certification", "kosher-status", "weight", "delivery", "delivery"], d.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("key").GetString()));
        Assert.Contains("OU", d.GetProperty("faqs")[0].GetProperty("answer").GetString());
        Assert.Contains("Hand-braided", d.GetProperty("description").GetString());
        Assert.True(DateTimeOffset.TryParse(d.GetProperty("createdAt").GetString(), out var created) && created.Offset == TimeSpan.Zero);
    }

    [Fact]
    public async Task A_listing_detail_by_id_is_the_same_page_and_services_have_their_own_attributes()
    {
        var bySlug = await Get($"{Base}/listings/deep-house-cleaning");
        var byId = await Get($"{Base}/listings/{bySlug.GetProperty("id").GetGuid()}");
        Assert.Equal(bySlug.GetRawText(), byId.GetRawText());
        Assert.Equal("service", bySlug.GetProperty("type").GetString());
        Assert.Equal(["food-drink"], new[] { "food-drink" });
        Assert.Equal(["services", "cleaning"], bySlug.GetProperty("categoryPath").EnumerateArray().Select(c => c.GetProperty("slug").GetString()));
        Assert.Equal(3, bySlug.GetProperty("variants").GetArrayLength());
        Assert.Contains(bySlug.GetProperty("attributes").EnumerateArray(), a => a.GetProperty("key").GetString() == "service-area");
    }

    [Theory]
    [InlineData("corner-shop-coffee-mug")]       // seller suspended
    [InlineData("no-such-listing")]
    [InlineData("%27%3B%20DROP%20TABLE%20catalog_listings%3B--")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Hidden_and_unknown_listings_are_a_404(string slugOrId)
        => Assert.Equal("not_found", Code(await Get($"{Base}/listings/{slugOrId}", HttpStatusCode.NotFound)));

    [Fact]
    public async Task A_listing_whose_status_left_Active_is_410_gone_by_slug_and_by_id()      // was a 404 in M1; the SEO brief asks for 410 (M2-07)
    {
        var id = ListingId("babka-chocolate");
        await Get($"{Base}/listings/chocolate-babka");
        Fixture(_f).Upsert(id, l => l with { Status = "Archived" });
        await Sync(_f);
        Assert.Equal("gone", Code(await Get($"{Base}/listings/chocolate-babka", HttpStatusCode.Gone)));
        Assert.Equal("gone", Code(await Get($"{Base}/listings/{id}", HttpStatusCode.Gone)));
    }

    [Fact]
    public async Task A_listing_that_was_archived_before_it_was_ever_public_is_404_not_410()      // M2-16: 410 would reveal a listing that never existed publicly
    {
        Assert.Equal("not_found", Code(await Get($"{Base}/listings/retired-spice-gift-set", HttpStatusCode.NotFound)));         // archived in the seed, never Active
        Assert.Equal("not_found", Code(await Get($"{Base}/listings/{ListingId("retired-spice-set")}", HttpStatusCode.NotFound)));
    }

    [Fact]
    public async Task A_hidden_listings_id_is_a_404_too_not_a_hint_that_it_exists()
    {
        var hiddenId = ListingId("closed-corner-mug");                  // its seller is suspended: still no hint
        Assert.Equal("not_found", Code(await Get($"{Base}/listings/{hiddenId}", HttpStatusCode.NotFound)));
    }

    // ---------- cross-cutting ----------
    [Fact]
    public async Task Catalog_reads_are_anonymous_and_money_is_whole_cents()
    {
        var all = (await Get($"{Base}/listings?pageSize=100")).GetProperty("items").EnumerateArray().ToList();
        Assert.All(all, i =>
        {
            Assert.Equal(JsonValueKind.Number, i.GetProperty("priceFromCents").ValueKind);
            Assert.True(i.GetProperty("priceFromCents").TryGetInt64(out var v) && v > 0);          // never a float
            Assert.True(i.GetProperty("priceToCents").GetInt64() >= i.GetProperty("priceFromCents").GetInt64());
        });
    }

    [Fact]
    public async Task Catalog_endpoints_share_the_public_rate_limit()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "3" });
        using var c = f.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Base}/categories")).StatusCode);
        var limited = await c.GetAsync($"{Base}/listings");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }
}

public class CatalogConfigurationTests
{
    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IReadOnlyList<string> Validate(string? source)
    {
        var d = new Dictionary<string, string?>
        {
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Auth:GuestLinkKey"] = new string('G', 40), ["Checkout:QuoteKey"] = new string('Q', 40), ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
            ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
            ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp", ["Catalog:Source"] = source,
        };
        return StartupValidator.Validate(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env("Production"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("none")]
    public void Production_accepts_no_catalog(string? source) => Assert.Empty(Validate(source));      // was "no catalog or the portal feed": Http is refused until it exists (M2-15)

    [Theory]
    [InlineData("Http")]
    [InlineData("http")]
    public void Production_refuses_the_portal_feed_until_it_is_built(string source)
        => Assert.Contains(Validate(source), p => p.Contains("Http") && p.Contains("not available"));

    [Theory]
    [InlineData("Fixture")]
    [InlineData("fixture")]
    public void Production_refuses_seed_data(string source) => Assert.Contains(Validate(source), p => p.Contains("Fixture") && p.Contains("Development/Testing"));

    [Theory]
    [InlineData(null, true, "Fixture")]
    [InlineData("", true, "Fixture")]
    [InlineData(null, false, "None")]
    [InlineData("  ", false, "None")]
    [InlineData("None", true, "None")]
    [InlineData("Http", false, "Http")]
    [InlineData(" Fixture ", false, "Fixture")]
    public void The_default_source_is_the_seed_only_in_development_and_testing(string? configured, bool devLike, string expected)
        => Assert.Equal(expected, CatalogSourceSelection.Resolve(configured, devLike));

    [Fact]
    public void Production_refuses_an_unknown_source() => Assert.Contains(Validate("Magic"), p => p.Contains("not valid"));

    [Fact]
    public void The_source_is_chosen_from_configuration_with_the_fixture_only_by_default_in_development()
    {
        using var none = new TestFactory(new() { ["Catalog:Source"] = "None" });
        Assert.IsType<EmptyCatalogSource>(none.Services.GetRequiredService<ICatalogSource>());
        using var fixture = new TestFactory();
        Assert.IsType<FixtureCatalogSource>(fixture.Services.GetRequiredService<ICatalogSource>());
        using var http = new TestFactory(new() { ["Catalog:Source"] = "Http" });
        Assert.Throws<NotSupportedException>(() => http.Services.GetRequiredService<ICatalogSource>());
        using var bad = new TestFactory(new() { ["Catalog:Source"] = "Magic" });
        Assert.Throws<InvalidOperationException>(() => bad.Services.GetRequiredService<ICatalogSource>());
    }

    [Fact]
    public async Task An_empty_catalog_answers_empty_pages_not_errors()
    {
        using var f = new TestFactory(new() { ["Catalog:Source"] = "None" });
        var c = f.CreateClient();
        Assert.Equal(0, (await Json(c, "/api/v1/catalog/listings")).GetProperty("total").GetInt32());
        Assert.Empty((await Json(c, "/api/v1/catalog/categories")).EnumerateArray());
        Assert.Empty((await Json(c, "/api/v1/catalog/brands")).EnumerateArray());
        var r = await CatalogKit.Sync(f);
        Assert.Equal((true, 0, null), (r.Ran, r.Applied, r.Error));
    }
}
