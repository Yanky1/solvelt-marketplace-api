using System.Net;
using System.Text.Json;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogSeoTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string B = "/api/v1/catalog";

    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK) => Json(_c, url, expected);
    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;
    private List<ListingSnapshot> Shown() => Fixture(_f).All().Where(l => l.Status == "Active" && l.Seller.Active).ToList();
    private static Guid Id(string slug) => FixtureCatalogSource.Id("listing:" + slug);

    // ---------- storefronts ----------
    [Fact]
    public async Task A_brand_storefront_has_its_seller_count_and_category_tree()
    {
        var b = await Get($"{B}/brands/challah-corner");
        Assert.Equal(["id", "slug", "name", "logoImageId", "seller", "listingCount", "categories"], b.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("challah-corner", "Challah Corner", 3), (b.GetProperty("slug").GetString(), b.GetProperty("name").GetString(), b.GetProperty("listingCount").GetInt32()));
        Assert.Equal("Mazel Bakery", b.GetProperty("seller").GetProperty("displayName").GetString());
        var food = b.GetProperty("categories")[0];
        Assert.Equal(("food-drink", 3), (food.GetProperty("slug").GetString(), food.GetProperty("listingCount").GetInt32()));
        Assert.Equal(("bakery", 3), (food.GetProperty("children")[0].GetProperty("slug").GetString(), food.GetProperty("children")[0].GetProperty("listingCount").GetInt32()));
        var byId = await Get($"{B}/brands/{b.GetProperty("id").GetGuid()}");
        Assert.Equal(b.ToString(), byId.ToString());
    }

    [Fact]
    public async Task A_brand_with_nothing_visible_is_a_404()
    {
        await Get($"{B}/brands/closed-corner", HttpStatusCode.NotFound);          // its seller is suspended
        await Get($"{B}/brands/no-such-brand", HttpStatusCode.NotFound);
        await Get($"{B}/brands/{Guid.NewGuid()}", HttpStatusCode.NotFound);
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.Brand.Name == "Challah Corner")) fx.Upsert(l.Id, x => x with { Status = "Archived" });
        await Sync(_f);
        await Get($"{B}/brands/challah-corner", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_seller_storefront_lists_its_brands_and_categories()
    {
        var mazel = FixtureCatalogSource.Id("seller:mazel");
        var s = await Get($"{B}/sellers/{mazel}");
        Assert.Equal(["id", "displayName", "listingCount", "brands", "categories"], s.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Mazel Bakery", Shown().Count(l => l.Seller.Id == mazel)), (s.GetProperty("displayName").GetString(), s.GetProperty("listingCount").GetInt32()));
        Assert.Equal(["challah-corner", "mazel-bakery"], s.GetProperty("brands").EnumerateArray().Select(b => b.GetProperty("slug").GetString()));
        Assert.Equal(s.GetProperty("listingCount").GetInt32(), s.GetProperty("brands").EnumerateArray().Sum(b => b.GetProperty("listingCount").GetInt32()));
        Assert.Equal(s.GetProperty("listingCount").GetInt32(), s.GetProperty("categories").EnumerateArray().Sum(c => c.GetProperty("listingCount").GetInt32()));
    }

    [Fact]
    public async Task A_suspended_or_unknown_seller_is_a_404()
    {
        await Get($"{B}/sellers/{FixtureCatalogSource.Id("seller:closed")}", HttpStatusCode.NotFound);
        await Get($"{B}/sellers/{Guid.NewGuid()}", HttpStatusCode.NotFound);
        await Get($"{B}/sellers/not-a-guid", HttpStatusCode.NotFound);
    }

    // ---------- 410 for listings that are gone ----------
    [Fact]
    public async Task An_unlisted_listing_is_410_by_slug_and_by_id_and_a_relist_brings_it_back()
    {
        var babka = Row(_f, Id("babka-chocolate"));
        await Get($"{B}/listings/chocolate-babka");
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        var gone = await Get($"{B}/listings/chocolate-babka", HttpStatusCode.Gone);
        Assert.Equal("gone", Code(gone));
        await Get($"{B}/listings/{babka.Id}", HttpStatusCode.Gone);
        Fixture(_f).Upsert(babka.Id, l => l);                    // the portal relists it with a newer version
        await Sync(_f);
        await Get($"{B}/listings/chocolate-babka");
    }

    [Fact]
    public async Task An_archived_listing_is_410_and_a_portal_that_no_longer_has_it_is_410()
    {
        Assert.Equal("gone", Code(await Get($"{B}/listings/retired-spice-gift-set", HttpStatusCode.Gone)));             // archived in the seed
        var fx = Fixture(_f);
        var honey = Row(_f, Id("honey-cake"));
        fx.Upsert(honey.Id, l => l);
        await Event(_f, _c, "listing.upserted", honey.Id, honey.Version + 100);
        fx.Remove(honey.Id);
        await Sync(_f);
        await Get($"{B}/listings/honey-cake-for-rosh-hashanah", HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Everything_else_that_is_hidden_stays_404_with_no_hint()
    {
        await Get($"{B}/listings/corner-shop-coffee-mug", HttpStatusCode.NotFound);                 // seller suspended: not announced as "gone"
        await Get($"{B}/listings/{Id("closed-corner-mug")}", HttpStatusCode.NotFound);
        await Get($"{B}/listings/no-such-listing", HttpStatusCode.NotFound);
        await Get($"{B}/listings/{Guid.NewGuid()}", HttpStatusCode.NotFound);
        var fx = Fixture(_f);
        fx.Upsert(Id("babka-chocolate"), l => l with { Status = "Draft" });
        await Sync(_f);
        await Get($"{B}/listings/chocolate-babka", HttpStatusCode.NotFound);                          // a draft was never public
        var stub = Guid.NewGuid();
        await Event(_f, _c, "listing.unlisted", stub, 5);                                              // an event about a listing we never saw
        await Get($"{B}/listings/{stub}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_listing_of_a_seller_that_was_suspended_after_unlisting_is_404_not_410()
    {
        var babka = Row(_f, Id("babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        await Get($"{B}/listings/chocolate-babka", HttpStatusCode.Gone);
        // suspend the seller through ANOTHER of its listings, so the unlisted one is not touched (a new snapshot of it would relist it)
        Fixture(_f).Upsert(Id("rugelach-assorted"), l => l with { Seller = l.Seller with { Active = false } });
        await Sync(_f);
        await Get($"{B}/listings/chocolate-babka", HttpStatusCode.NotFound);
        await Get($"{B}/listings/{babka.Id}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_stub_is_404_even_when_a_seller_row_happens_to_match()
    {
        var stub = Guid.NewGuid();
        await Event(_f, _c, "listing.unlisted", stub, 5);           // we only know the id: a stub with an unlist version and no seller
        WithDb(_f, db => { db.CatalogSellers.Add(new Solvelt.Marketplace.Api.Data.CatalogSeller { Id = Guid.Empty, DisplayName = "x", Active = true }); return db.SaveChanges(); });
        await Get($"{B}/listings/{stub}", HttpStatusCode.NotFound);   // the stub guard alone must hold
    }

    [Fact]
    public async Task The_canonical_slug_is_reachable_from_the_id_so_the_web_tier_can_redirect()
    {
        var byId = await Get($"{B}/listings/{Id("babka-chocolate")}");
        Assert.Equal("chocolate-babka", byId.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task A_title_change_keeps_the_slug_so_there_is_nothing_to_redirect()
    {
        Fixture(_f).Upsert(Id("babka-chocolate"), l => l with { Title = "Completely Different Name" });
        await Sync(_f);
        Assert.Equal("chocolate-babka", (await Get($"{B}/listings/{Id("babka-chocolate")}")).GetProperty("slug").GetString());
        await Get($"{B}/listings/chocolate-babka");
    }

    // ---------- sitemap data ----------
    [Fact]
    public async Task The_listing_sitemap_has_every_visible_slug_with_its_last_modified_time()
    {
        var page = await Get($"{B}/sitemap/listings");
        Assert.Equal(("listings", 1, 1000, 40, 1), (page.GetProperty("kind").GetString(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32(), page.GetProperty("totalPages").GetInt32()));
        var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("lastModified").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(Shown().Select(l => Row(_f, l.Id).Slug).OrderBy(x => x), items.Keys.OrderBy(x => x));
        var babka = Shown().Single(l => l.Id == Id("babka-chocolate"));
        Assert.Equal(babka.UpdatedAt.UtcDateTime, items["chocolate-babka"]);
        Assert.DoesNotContain("corner-shop-coffee-mug", items.Keys);
        Assert.DoesNotContain("retired-spice-gift-set", items.Keys);
    }

    [Fact]
    public async Task Sitemap_pages_do_not_overlap_and_their_sizes_are_bounded_exactly()
    {
        var seen = new List<string>();
        for (var p = 1; p <= 6; p++)
        {
            var page = await Get($"{B}/sitemap/listings?pageSize=7&page={p}");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!));
            Assert.Equal(6, page.GetProperty("totalPages").GetInt32());
        }
        Assert.Equal(40, seen.Distinct().Count());
        Assert.Empty((await Get($"{B}/sitemap/listings?pageSize=7&page=7")).GetProperty("items").EnumerateArray());
        await Get($"{B}/sitemap/listings?pageSize=5000");
        await Get($"{B}/sitemap/listings?pageSize=1");
        foreach (var bad in new[] { "pageSize=5001", "pageSize=0", "pageSize=x", "page=0", "page=100001" })
            Assert.Equal("invalid_query", Code(await Get($"{B}/sitemap/listings?{bad}", HttpStatusCode.BadRequest)));
        await Get($"{B}/sitemap/nonsense", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_sitemap_drops_an_unlisted_listing_at_once_and_follows_updates()
    {
        var babka = Row(_f, Id("babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        var keys = (await Get($"{B}/sitemap/listings")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Equal(39, keys.Count);
        Assert.DoesNotContain("chocolate-babka", keys);
        var later = DateTimeOffset.UtcNow.AddDays(2);
        Fixture(_f).Upsert(Id("honey-cake"), l => l with { UpdatedAt = later });
        await Sync(_f);
        var honey = (await Get($"{B}/sitemap/listings")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "honey-cake-for-rosh-hashanah");
        Assert.Equal(later.UtcDateTime, honey.GetProperty("lastModified").GetDateTimeOffset().UtcDateTime, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Category_brand_and_seller_sitemaps_list_only_those_with_visible_listings_and_the_newest_time_below_them()
    {
        var cats = (await Get($"{B}/sitemap/categories")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("lastModified").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(13, cats.Count);                                                           // all 13 categories have something visible below them
        var rootNewest = Shown().Where(l => Row(_f, l.Id).CategoryId is var c && new[] { "bakery", "meat-poultry", "wine-spirits", "pantry" }.Any(k => FixtureCatalogSource.Id("category:" + k) == c)).Max(l => l.UpdatedAt).UtcDateTime;
        Assert.Equal(rootNewest, cats["food-drink"]);                                           // a category carries the newest time of its subtree

        var brands = (await Get($"{B}/sitemap/brands")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Equal(9, brands.Count);
        Assert.DoesNotContain("closed-corner", brands);
        var sellers = (await Get($"{B}/sitemap/sellers")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Equal(5, sellers.Count);
        Assert.DoesNotContain(FixtureCatalogSource.Id("seller:closed").ToString("D"), sellers);
        Assert.Contains(FixtureCatalogSource.Id("seller:mazel").ToString("D"), sellers);
    }

    [Fact]
    public async Task A_category_whose_listings_are_all_hidden_leaves_the_sitemap()
    {
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.CategoryId == FixtureCatalogSource.Id("category:books"))) fx.Upsert(l.Id, x => x with { Status = "Archived" });
        await Sync(_f);
        var cats = (await Get($"{B}/sitemap/categories")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain("books", cats);
        Assert.Equal(12, cats.Count);
    }
}
