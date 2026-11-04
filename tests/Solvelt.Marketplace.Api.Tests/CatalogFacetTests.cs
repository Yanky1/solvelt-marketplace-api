using System.Net;
using System.Text.Json;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogFacetTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string L = "/api/v1/catalog/listings";

    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK) => Json(_c, url, expected);
    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;
    private static int Total(JsonElement e) => e.GetProperty("total").GetInt32();
    private Task<JsonElement> Bad(string query) => Get($"{L}?{query}", HttpStatusCode.BadRequest);

    /// <summary>What the catalog should show: Active listings of active sellers.</summary>
    private List<ListingSnapshot> Shown() => Fixture(_f).All().Where(l => l.Status == "Active" && l.Seller.Active).ToList();
    private static bool Has(ListingSnapshot l, string key, params string[] values) => l.Attributes.Any(a => a.Key == key && values.Contains(a.Value));

    // ---------- price ----------
    [Fact]
    public async Task Price_filters_include_the_boundary_and_exclude_one_cent_beyond_it()
    {
        // brisket: one variant at 1999 cents
        Assert.Contains("glatt-brisket-first-cut-per-lb", Slugs(await Get($"{L}?priceMin=1999&priceMax=1999&pageSize=100")));
        Assert.DoesNotContain("glatt-brisket-first-cut-per-lb", Slugs(await Get($"{L}?priceMin=2000&pageSize=100")));
        Assert.DoesNotContain("glatt-brisket-first-cut-per-lb", Slugs(await Get($"{L}?priceMax=1998&pageSize=100")));
        Assert.Contains("glatt-brisket-first-cut-per-lb", Slugs(await Get($"{L}?priceMin=1998&pageSize=100")));
        Assert.Contains("glatt-brisket-first-cut-per-lb", Slugs(await Get($"{L}?priceMax=2000&pageSize=100")));
    }

    [Fact]
    public async Task A_price_range_matches_when_any_variant_price_is_inside_it()
    {
        // classic challah: Small 650, Large 950 (range 650..950)
        const string slug = "classic-braided-challah";
        Assert.Contains(slug, Slugs(await Get($"{L}?priceMin=950&pageSize=100")));
        Assert.DoesNotContain(slug, Slugs(await Get($"{L}?priceMin=951&pageSize=100")));
        Assert.Contains(slug, Slugs(await Get($"{L}?priceMax=650&pageSize=100")));
        Assert.DoesNotContain(slug, Slugs(await Get($"{L}?priceMax=649&pageSize=100")));
        Assert.Contains(slug, Slugs(await Get($"{L}?priceMin=700&priceMax=800&pageSize=100")));       // between the two variants: the range overlaps
    }

    [Fact]
    public async Task Price_values_are_validated()
    {
        Assert.Equal("invalid_query", Code(await Bad("priceMin=10&priceMax=9")));
        Assert.Equal(0, Total(await Get($"{L}?priceMin=10&priceMax=10")));                    // equal bounds are fine (nothing costs 10 cents)
        foreach (var bad in new[] { "priceMin=-1", "priceMin=1.5", "priceMax=abc", "priceMin=1000000000001", "priceMax=1000000000001" })
            Assert.Equal("invalid_query", Code(await Bad(bad)));
        Assert.Equal(40, Total(await Get($"{L}?priceMin=0&priceMax=1000000000000")));
    }

    // ---------- availability, type ----------
    [Fact]
    public async Task Available_only_leaves_out_sold_out_listings()
    {
        var expected = Shown().Count(l => l.Variants.Any(v => v.Available));
        var page = await Get($"{L}?available=true&pageSize=100");
        Assert.Equal(expected, Total(page));
        Assert.True(expected < 40);
        Assert.DoesNotContain("honey-cake-for-rosh-hashanah", Slugs(page));          // its only variant is sold out
        foreach (var item in page.GetProperty("items").EnumerateArray()) Assert.True(item.GetProperty("available").GetBoolean());
        Assert.Equal("invalid_query", Code(await Bad("available=false")));
        Assert.Equal(40, Total(await Get($"{L}?available=")));
    }

    // ---------- attributes ----------
    [Fact]
    public async Task Kosher_certification_filter_takes_alternatives()
    {
        Assert.Equal(Shown().Count(l => Has(l, "kosher-certification", "OU")), Total(await Get($"{L}?kosher=OU")));
        Assert.Equal(Shown().Count(l => Has(l, "kosher-certification", "OU", "Star-K")), Total(await Get($"{L}?kosher=OU,Star-K")));
        Assert.Equal(Shown().Count(l => Has(l, "kosher-certification", "OU", "Star-K")), Total(await Get($"{L}?attr=kosher-certification:OU&attr=kosher-certification:Star-K")));
        Assert.Equal(0, Total(await Get($"{L}?kosher=Nobody")));
    }

    [Fact]
    public async Task Different_attributes_must_all_hold()
    {
        var expected = Shown().Count(l => Has(l, "kosher-certification", "OU") && Has(l, "kosher-status", "dairy"));
        Assert.True(expected > 0);
        Assert.Equal(expected, Total(await Get($"{L}?kosher=OU&attr=kosher-status:dairy")));
        Assert.Equal(Shown().Count(l => Has(l, "kosher-for-passover", "yes")), Total(await Get($"{L}?attr=kosher-for-passover:yes")));
        Assert.Equal(0, Total(await Get($"{L}?kosher=Badatz&attr=kosher-status:dairy")));
    }

    [Fact]
    public async Task Delivery_options_filter_products_and_services()
    {
        Assert.Equal(Shown().Count(l => Has(l, "delivery", "ships")), Total(await Get($"{L}?delivery=ships")));
        Assert.Equal(Shown().Count(l => Has(l, "delivery", "local")), Total(await Get($"{L}?delivery=local")));
        var online = await Get($"{L}?delivery=online&pageSize=100");
        Assert.Equal(Shown().Count(l => Has(l, "delivery", "online")), Total(online));
        Assert.All(online.GetProperty("items").EnumerateArray(), i => Assert.Equal("service", i.GetProperty("type").GetString()));
        Assert.Equal(Shown().Count(l => Has(l, "delivery", "at-home")), Total(await Get($"{L}?delivery=at-home")));
        Assert.Equal(Shown().Count(l => Has(l, "delivery", "online", "at-home")), Total(await Get($"{L}?delivery=online,at-home")));
        Assert.Equal(0, Total(await Get($"{L}?delivery=ships&type=service")));
        Assert.Equal("invalid_query", Code(await Bad("delivery=teleport")));
        Assert.Equal("invalid_query", Code(await Bad("delivery=ships,teleport")));
    }

    [Fact]
    public async Task Attribute_input_limits_are_exact()
    {
        string Pairs(int n) => string.Join("&", Enumerable.Range(0, n).Select(i => $"attr=k{i}:v"));
        await Get($"{L}?{Pairs(12)}");
        Assert.Equal("invalid_query", Code(await Bad(Pairs(13))));
        string Values(int n) => string.Join("&", Enumerable.Range(0, n).Select(i => $"attr=k:v{i}"));
        await Get($"{L}?{Values(10)}");
        Assert.Equal("invalid_query", Code(await Bad(Values(11))));
        await Get($"{L}?attr=k:{new string('v', 60)}");
        Assert.Equal("invalid_query", Code(await Bad($"attr=k:{new string('v', 61)}")));
        await Get($"{L}?attr={new string('k', 100)}:v");
        Assert.Equal("invalid_query", Code(await Bad($"attr={new string('k', 101)}:v")));
        foreach (var bad in new[] { "attr=nocolon", "attr=:v", "attr=k:", "attr=" }) Assert.Equal("invalid_query", Code(await Bad(bad)));
        Assert.Equal("invalid_query", Code(await Bad($"kosher={new string('x', 61)}")));
    }

    [Fact]
    public async Task A_parameter_given_twice_is_refused_except_attr()
    {
        Assert.Equal("invalid_query", Code(await Bad("type=product&type=service")));
        Assert.Equal("invalid_query", Code(await Bad("sort=title&sort=newest")));
        await Get($"{L}?attr=kosher-status:meat&attr=kosher-status:dairy");
    }

    [Fact]
    public async Task Attribute_values_are_data_not_query_language()
    {
        foreach (var v in new[] { "' OR 1=1 --", "%", "_", "\\", "a:b:c" })
            Assert.Equal(0, Total(await Get($"{L}?attr=k:{Uri.EscapeDataString(v)}")));
        Assert.Equal(40, Total(await Get(L)));
    }

    // ---------- facets ----------
    [Fact]
    public async Task Facets_are_only_sent_when_asked_for()
    {
        Assert.False((await Get(L)).TryGetProperty("facets", out _));
        Assert.False((await Get($"{L}?facets=false")).TryGetProperty("facets", out _));
        var f = (await Get($"{L}?facets=true")).GetProperty("facets");
        Assert.Equal(["categories", "brands", "sellers", "types", "available", "price", "attributes"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal("invalid_query", Code(await Bad("facets=1")));
    }

    [Fact]
    public async Task Unfiltered_facets_describe_the_whole_visible_catalog()
    {
        var f = (await Get($"{L}?facets=true")).GetProperty("facets");
        Assert.Equal(40, f.GetProperty("categories").EnumerateArray().Sum(c => c.GetProperty("listingCount").GetInt32()));
        Assert.Equal(40, f.GetProperty("brands").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Equal(40, f.GetProperty("sellers").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Equal(Shown().GroupBy(l => l.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count())).ToList(),
            f.GetProperty("types").EnumerateArray().Select(t => (t.GetProperty("value").GetString()!, t.GetProperty("count").GetInt32())).ToList());
        Assert.Equal(Shown().Count(l => l.Variants.Any(v => v.Available)), f.GetProperty("available").GetInt32());
        Assert.Equal((Shown().Min(l => l.Variants.Min(v => v.PriceCents)), Shown().Max(l => l.Variants.Max(v => v.PriceCents))),
            (f.GetProperty("price").GetProperty("minCents").GetInt64(), f.GetProperty("price").GetProperty("maxCents").GetInt64()));
        Assert.DoesNotContain(f.GetProperty("brands").EnumerateArray(), b => b.GetProperty("slug").GetString() == "closed-corner");     // hidden listings are never counted
        Assert.Equal(["kosher-certification", "kosher-status", "kosher-for-passover", "delivery"], f.GetProperty("attributes").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_facet_ignores_its_own_filter_but_obeys_all_the_others()
    {
        var f = (await Get($"{L}?brand=challah-corner&facets=true")).GetProperty("facets");
        // brands: counted as if no brand was chosen, so the buyer can switch brand
        Assert.Equal(40, f.GetProperty("brands").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        // everything else: only the chosen brand's three listings
        Assert.Equal(3, f.GetProperty("categories").EnumerateArray().Sum(c => c.GetProperty("listingCount").GetInt32()));
        Assert.Equal(3, f.GetProperty("sellers").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Equal(3, f.GetProperty("types").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        var cats = (await Get($"{L}?category=bakery&facets=true")).GetProperty("facets").GetProperty("categories");
        Assert.Equal(40, cats.EnumerateArray().Sum(c => c.GetProperty("listingCount").GetInt32()));       // the category facet ignores the category filter
        var brands = (await Get($"{L}?category=bakery&facets=true")).GetProperty("facets").GetProperty("brands");
        Assert.Equal(8, brands.EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Every_facet_count_is_what_choosing_that_value_would_return()
    {
        var baseFilter = "type=product&kosher=OU,Star-K";
        var f = (await Get($"{L}?{baseFilter}&facets=true")).GetProperty("facets");
        foreach (var b in f.GetProperty("brands").EnumerateArray())
            Assert.Equal(b.GetProperty("count").GetInt32(), Total(await Get($"{L}?{baseFilter}&brand={b.GetProperty("slug").GetString()}")));
        foreach (var s in f.GetProperty("sellers").EnumerateArray())
            Assert.Equal(s.GetProperty("count").GetInt32(), Total(await Get($"{L}?{baseFilter}&seller={s.GetProperty("id").GetGuid()}")));
        foreach (var key in new[] { "kosher-status", "kosher-for-passover", "delivery" })
            foreach (var v in f.GetProperty("attributes").GetProperty(key).EnumerateArray())
                Assert.Equal(v.GetProperty("count").GetInt32(), Total(await Get($"{L}?{baseFilter}&attr={key}:{Uri.EscapeDataString(v.GetProperty("value").GetString()!)}")));
        foreach (var c in f.GetProperty("categories").EnumerateArray())
            Assert.Equal(c.GetProperty("listingCount").GetInt32(), Total(await Get($"{L}?{baseFilter}&category={c.GetProperty("slug").GetString()}")));
        Assert.Equal(f.GetProperty("available").GetInt32(), Total(await Get($"{L}?{baseFilter}&available=true")));
    }

    [Fact]
    public async Task The_certification_facet_ignores_the_certification_filter_but_not_the_other_attributes()
    {
        var f = (await Get($"{L}?kosher=OU&attr=kosher-status:dairy&facets=true")).GetProperty("facets").GetProperty("attributes");
        var certs = f.GetProperty("kosher-certification").EnumerateArray().ToDictionary(x => x.GetProperty("value").GetString()!, x => x.GetProperty("count").GetInt32());
        var expected = Shown().Where(l => Has(l, "kosher-status", "dairy")).SelectMany(l => l.Attributes.Where(a => a.Key == "kosher-certification").Select(a => a.Value)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(expected, certs);
        var status = f.GetProperty("kosher-status").EnumerateArray().ToDictionary(x => x.GetProperty("value").GetString()!, x => x.GetProperty("count").GetInt32());
        Assert.Equal(Shown().Where(l => Has(l, "kosher-certification", "OU")).SelectMany(l => l.Attributes.Where(a => a.Key == "kosher-status").Select(a => a.Value)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()), status);
    }

    [Fact]
    public async Task A_listing_with_two_values_of_an_attribute_counts_once_for_each()
    {
        var delivery = (await Get($"{L}?type=product&facets=true")).GetProperty("facets").GetProperty("attributes").GetProperty("delivery").EnumerateArray()
            .ToDictionary(x => x.GetProperty("value").GetString()!, x => x.GetProperty("count").GetInt32());
        var products = Shown().Where(l => l.Type == "product").ToList();
        Assert.Equal(products.Count, delivery["ships"]);                                                       // every product ships ...
        Assert.Equal(products.Count(l => Has(l, "delivery", "local")), delivery["local"]);                     // ... and bakery and meat also deliver locally
        Assert.True(delivery["local"] > 0 && delivery["local"] < products.Count);
    }

    [Fact]
    public async Task The_price_facet_follows_the_other_filters_and_is_empty_when_nothing_matches()
    {
        var f = (await Get($"{L}?brand=challah-corner&facets=true")).GetProperty("facets").GetProperty("price");
        Assert.Equal((650L, 1450L), (f.GetProperty("minCents").GetInt64(), f.GetProperty("maxCents").GetInt64()));
        var priced = (await Get($"{L}?brand=challah-corner&priceMin=900&facets=true")).GetProperty("facets").GetProperty("price");
        Assert.Equal((650L, 1450L), (priced.GetProperty("minCents").GetInt64(), priced.GetProperty("maxCents").GetInt64()));   // the price facet ignores the price filter
        var none = (await Get($"{L}?kosher=Nobody&facets=true")).GetProperty("facets");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("price").GetProperty("minCents").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("price").GetProperty("maxCents").ValueKind);
        Assert.Equal(0, none.GetProperty("available").GetInt32());
        Assert.Empty(none.GetProperty("brands").EnumerateArray());
    }

    [Fact]
    public async Task Facets_follow_a_search()
    {
        var page = await Get($"{L}?q=challah&facets=true");
        var f = page.GetProperty("facets");
        Assert.Equal(Total(page), f.GetProperty("brands").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(Total(page), f.GetProperty("sellers").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Facets_never_count_listings_that_were_unlisted()
    {
        var babka = Row(_f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        var f = (await Get($"{L}?facets=true")).GetProperty("facets");
        Assert.Equal(39, f.GetProperty("brands").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Equal(39, f.GetProperty("categories").EnumerateArray().Sum(c => c.GetProperty("listingCount").GetInt32()));
    }

    // ---------- cursors ----------
    private async Task<(List<string> Slugs, int Pages)> Walk(string query, int size)
    {
        var all = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Get($"{L}?{query}&pageSize={size}" + (cursor is null ? "" : $"&cursor={cursor}"));
            all.AddRange(Slugs(page));
            pages++;
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            Assert.True(pages <= 100);
        } while (cursor is not null);
        return (all, pages);
    }

    [Theory]
    [InlineData("sort=newest")]
    [InlineData("sort=best_selling")]
    [InlineData("sort=price_asc")]
    [InlineData("sort=price_desc")]
    [InlineData("sort=title")]
    [InlineData("q=challah&sort=relevance")]
    [InlineData("q=a")]
    [InlineData("sort=price_asc&type=product&kosher=OU")]
    public async Task Walking_by_cursor_gives_the_same_order_as_one_big_page(string query)
    {
        var everything = Slugs(await Get($"{L}?{query}&pageSize=100"));
        var (walked, pages) = await Walk(query, 7);
        Assert.Equal(everything, walked);
        Assert.Equal((int)Math.Ceiling(everything.Count / 7.0), pages);
        Assert.Equal(everything.Count, walked.Distinct().Count());
    }

    [Fact]
    public async Task The_cursor_of_the_last_page_is_null_and_one_item_more_gives_a_cursor()
    {
        Assert.Equal(JsonValueKind.Null, (await Get($"{L}?pageSize=40")).GetProperty("nextCursor").ValueKind);        // 40 items, page of 40: no more
        Assert.Equal(JsonValueKind.String, (await Get($"{L}?pageSize=39")).GetProperty("nextCursor").ValueKind);      // one left over
        Assert.Equal(JsonValueKind.Null, (await Get($"{L}?pageSize=100")).GetProperty("nextCursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Get($"{L}?q=qqqqqq")).GetProperty("nextCursor").ValueKind);
        var second = await Get($"{L}?pageSize=24&page=2");
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_cursor_does_not_shift_when_a_newer_listing_appears_but_a_page_number_does()
    {
        var first = await Get($"{L}?pageSize=10");
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var pageTwoBefore = Slugs(await Get($"{L}?pageSize=10&cursor={cursor}"));
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        fx.Add(Copy(template, Guid.NewGuid(), "Brand New Arrival") with { CreatedAt = DateTimeOffset.UtcNow.AddDays(30) });
        await Sync(_f);
        Assert.Equal(pageTwoBefore, Slugs(await Get($"{L}?pageSize=10&cursor={cursor}")));                              // the cursor is a position, not a count
        Assert.NotEqual(pageTwoBefore, Slugs(await Get($"{L}?pageSize=10&page=2")));                                     // page 2 now starts one item earlier
        Assert.Contains("brand-new-arrival", Slugs(await Get($"{L}?pageSize=10")));
    }

    [Theory]
    [InlineData("sort=newest")]
    [InlineData("sort=price_asc")]
    [InlineData("sort=price_desc")]
    [InlineData("sort=title")]
    public async Task Listings_that_tie_on_the_sort_value_are_neither_repeated_nor_dropped_by_cursors(string query)
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var at = DateTimeOffset.UtcNow.AddDays(60);
        for (var i = 0; i < 5; i++)     // five listings with the same title, price and creation time: only the id can order them
            fx.Add(Copy(template, Guid.NewGuid(), "Twin Teapot") with { CreatedAt = at, Variants = [new VariantDto(Guid.NewGuid(), "One", 4242, null, true)] });
        await Sync(_f);
        var everything = Slugs(await Get($"{L}?{query}&pageSize=100"));
        Assert.Equal(45, everything.Distinct().Count());
        Assert.Equal(everything, (await Walk(query, 1)).Slugs);
        Assert.Equal(everything, (await Walk(query, 4)).Slugs);
    }

    [Fact]
    public async Task A_cursor_page_reports_page_zero_and_the_same_totals()
    {
        var cursor = (await Get($"{L}?pageSize=10")).GetProperty("nextCursor").GetString();
        var p = await Get($"{L}?pageSize=10&cursor={cursor}");
        Assert.Equal((0, 10, 40, 4), (p.GetProperty("page").GetInt32(), p.GetProperty("pageSize").GetInt32(), Total(p), p.GetProperty("totalPages").GetInt32()));
    }

    [Fact]
    public async Task Bad_cursors_are_refused()
    {
        var cursor = (await Get($"{L}?pageSize=10&sort=price_asc")).GetProperty("nextCursor").GetString()!;
        Assert.Equal("invalid_query", Code(await Bad($"sort=price_asc&cursor={cursor}&page=2")));      // page and cursor together
        Assert.Equal("invalid_query", Code(await Bad($"sort=title&cursor={cursor}")));                  // a cursor belongs to its sort
        Assert.Equal("invalid_query", Code(await Bad($"sort=price_desc&cursor={cursor}")));
        foreach (var bad in new[] { "garbage", "!!!!", "e30", "W10", "WyJhIiwiYiJd", new string('A', 401) })
            Assert.Equal("invalid_query", Code(await Bad($"cursor={bad}")));
        await Get($"{L}?sort=price_asc&cursor={cursor}");
    }

    [Fact]
    public async Task A_cursor_with_a_non_numeric_key_is_a_clean_400_not_a_500()
    {
        var forged = new ListingCursor("price_asc", "not-a-number", Guid.NewGuid()).Encode();
        var res = await _c.GetAsync($"{L}?sort=price_asc&cursor={forged}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var forgedDate = new ListingCursor("newest", "99999999999999999999", Guid.NewGuid()).Encode();
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"{L}?cursor={forgedDate}")).StatusCode);
        var forgedScore = new ListingCursor("relevance", "x", Guid.NewGuid()).Encode();
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync($"{L}?q=challah&cursor={forgedScore}")).StatusCode);
    }

    [Fact]
    public async Task Best_selling_is_the_newest_order_until_sales_exist()
        => Assert.Equal(Slugs(await Get($"{L}?sort=newest&pageSize=100")), Slugs(await Get($"{L}?sort=best_selling&pageSize=100")));
}
