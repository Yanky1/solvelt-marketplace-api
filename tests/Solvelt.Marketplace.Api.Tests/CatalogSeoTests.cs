using System.Net;
using Microsoft.EntityFrameworkCore;
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
        var fx0 = Fixture(_f);
        fx0.Upsert(Id("hamantaschen-box"), l => l with { Status = "Archived" });                                         // public, then archived
        await Sync(_f);
        Assert.Equal("gone", Code(await Get($"{B}/listings/hamantaschen-gift-box", HttpStatusCode.Gone)));
        var fx = Fixture(_f);
        var honey = Row(_f, Id("honey-cake"));
        fx.Upsert(honey.Id, l => l);
        await Event(_f, _c, "listing.upserted", honey.Id, honey.Version + 100);
        fx.Remove(honey.Id);
        await Sync(_f);
        await Get($"{B}/listings/honey-cake-for-rosh-hashanah", HttpStatusCode.Gone);
    }

    [Fact]
    public async Task A_draft_that_was_never_public_is_404_even_after_an_unlist_event()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var draft = fx.Add(Copy(template, Guid.NewGuid(), "Secret Draft Item") with { Status = "Draft" });
        await Sync(_f);
        await Event(_f, _c, "listing.unlisted", draft.Id, draft.Version);                                   // the portal says it was unlisted: it was never listed
        await Get($"{B}/listings/{draft.Id}", HttpStatusCode.NotFound);
        await Get($"{B}/listings/secret-draft-item", HttpStatusCode.NotFound);
        Assert.False(WithDb(_f, db => db.CatalogListings.Single(l => l.Id == draft.Id).WasPublic));
    }

    [Fact]
    public async Task A_listing_first_seen_archived_is_404_but_one_archived_after_being_public_is_410()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var neverPublic = fx.Add(Copy(template, Guid.NewGuid(), "Born Archived") with { Status = "Archived" });
        var wasPublic = fx.Add(Copy(template, Guid.NewGuid(), "Public Then Archived"));
        await Sync(_f);
        fx.Upsert(wasPublic.Id, l => l with { Status = "Archived" });
        await Sync(_f);
        await Get($"{B}/listings/born-archived", HttpStatusCode.NotFound);
        await Get($"{B}/listings/public-then-archived", HttpStatusCode.Gone);
        Assert.True(WithDb(_f, db => db.CatalogListings.Single(l => l.Id == wasPublic.Id).WasPublic));
        Assert.False(WithDb(_f, db => db.CatalogListings.Single(l => l.Id == neverPublic.Id).WasPublic));
    }

    [Fact]
    public async Task Once_public_always_public_for_the_gone_rule_even_after_a_relist_and_a_second_unlist()
    {
        var babka = Row(_f, Id("babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        Fixture(_f).Upsert(babka.Id, l => l);
        await Sync(_f);
        await Get($"{B}/listings/chocolate-babka");
        var again = Row(_f, babka.Id);
        await Event(_f, _c, "listing.unlisted", again.Id, again.Version);
        await Get($"{B}/listings/chocolate-babka", HttpStatusCode.Gone);
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
        // a category carries the newest of: its subtree's listings and the change times of the category rows in it (the sync stamped them at the first run, after the seed's listing times)
        var rowTimes = WithDb(_f, db => db.CatalogCategories.Where(c => new[] { "food-drink", "bakery", "meat-poultry", "wine-spirits", "pantry" }.Contains(c.Slug)).Select(c => c.UpdatedAt).ToList()).Max().UtcDateTime;
        Assert.Equal(rowTimes > rootNewest ? rowTimes : rootNewest, cats["food-drink"], TimeSpan.FromMilliseconds(1));

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

public class CatalogSitemapValidatorTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string B = "/api/v1/catalog";
    public async Task InitializeAsync() { _f = new TestFactory(); await CatalogKit.Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private static Guid Id(string slug) => FixtureCatalogSource.Id("listing:" + slug);
    private static DateTimeOffset RoundDown(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Theory]
    [InlineData("listings")]
    [InlineData("categories")]
    [InlineData("brands")]
    [InlineData("sellers")]
    public async Task Every_sitemap_page_has_an_ETag_and_a_Last_Modified_of_its_newest_entry(string kind)
    {
        var res = await _c.GetAsync($"{B}/sitemap/{kind}");
        res.EnsureSuccessStatusCode();
        var page = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var newest = page.GetProperty("items").EnumerateArray().Max(i => i.GetProperty("lastModified").GetDateTimeOffset());
        Assert.NotNull(res.Headers.ETag);
        Assert.Matches("^\"[0-9a-f]{32}\"$", res.Headers.ETag!.Tag);
        Assert.Equal(RoundDown(newest), res.Content.Headers.LastModified);
        Assert.Equal("public, max-age=5", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_conditional_request_gets_304_with_no_body_until_the_page_changes()
    {
        var first = await _c.GetAsync($"{B}/sitemap/listings");
        var etag = first.Headers.ETag!.Tag;
        var req = new HttpRequestMessage(HttpMethod.Get, $"{B}/sitemap/listings"); req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await _c.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, second.Headers.ETag!.Tag);
        Assert.Equal("public, max-age=5", second.Headers.CacheControl?.ToString());                       // a 304 is cacheable like the 200 it stands for
        foreach (var other in new[] { "\"nope\"", "W/\"nope\"" })
        {
            var r = new HttpRequestMessage(HttpMethod.Get, $"{B}/sitemap/listings"); r.Headers.TryAddWithoutValidation("If-None-Match", other);
            Assert.Equal(HttpStatusCode.OK, (await _c.SendAsync(r)).StatusCode);
        }
        var star = new HttpRequestMessage(HttpMethod.Get, $"{B}/sitemap/listings"); star.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.NotModified, (await _c.SendAsync(star)).StatusCode);
        // the page changes: a new ETag
        Fixture(_f).Upsert(Id("honey-cake"), l => l with { Title = "Honey Cake Renamed" });
        await CatalogKit.Sync(_f);
        var again = new HttpRequestMessage(HttpMethod.Get, $"{B}/sitemap/listings"); again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var changed = await _c.SendAsync(again);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task If_Modified_Since_is_honoured_to_the_second_and_If_None_Match_wins_when_both_are_sent()
    {
        var first = await _c.GetAsync($"{B}/sitemap/listings");
        var lastModified = first.Content.Headers.LastModified!.Value;
        HttpRequestMessage Req(string name, string value) { var r = new HttpRequestMessage(HttpMethod.Get, $"{B}/sitemap/listings"); r.Headers.TryAddWithoutValidation(name, value); return r; }
        Assert.Equal(HttpStatusCode.NotModified, (await _c.SendAsync(Req("If-Modified-Since", lastModified.ToString("R")))).StatusCode);              // exactly the time: not modified
        Assert.Equal(HttpStatusCode.OK, (await _c.SendAsync(Req("If-Modified-Since", lastModified.AddSeconds(-1).ToString("R")))).StatusCode);        // one second older: modified
        Assert.Equal(HttpStatusCode.NotModified, (await _c.SendAsync(Req("If-Modified-Since", lastModified.AddSeconds(1).ToString("R")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.SendAsync(Req("If-Modified-Since", "not a date"))).StatusCode);
        var both = Req("If-Modified-Since", lastModified.AddDays(1).ToString("R")); both.Headers.TryAddWithoutValidation("If-None-Match", "\"stale\"");
        Assert.Equal(HttpStatusCode.OK, (await _c.SendAsync(both)).StatusCode);                          // the ETag does not match, so the date is not consulted
    }

    [Fact]
    public async Task A_category_or_brand_last_modified_cannot_move_backwards_when_its_newest_listing_leaves()
    {
        var fx = Fixture(_f);
        var later = DateTimeOffset.UtcNow.AddDays(3);
        fx.Upsert(Id("babka-chocolate"), l => l with { UpdatedAt = later });                          // the newest listing of bakery and of mazel-bakery
        await CatalogKit.Sync(_f);
        async Task<(DateTimeOffset Cat, DateTimeOffset Brand)> Times()
        {
            var cats = await CatalogKit.Json(_c, $"{B}/sitemap/categories");
            var brands = await CatalogKit.Json(_c, $"{B}/sitemap/brands");
            DateTimeOffset T(JsonElement p, string key) => p.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key).GetProperty("lastModified").GetDateTimeOffset();
            return (T(cats, "bakery"), T(brands, "mazel-bakery"));
        }
        var before = await Times();
        Assert.Equal(later.UtcDateTime, before.Cat.UtcDateTime, TimeSpan.FromMilliseconds(1));
        var babka = CatalogKit.Row(_f, Id("babka-chocolate"));
        await CatalogKit.Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);                 // the newest listing leaves
        var after = await Times();
        // the newest listing left, but the row's own change time (when the sync first saw it) is the floor: it can fall back to that and no further
        var floorCat = Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogCategories.Where(c => c.Slug == "bakery").Select(c => c.UpdatedAt).Single());
        var floorBrand = Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogBrands.Where(b => b.Slug == "mazel-bakery").Select(b => b.UpdatedAt).Single());
        Assert.True(after.Cat >= floorCat.AddMilliseconds(-1) && after.Brand >= floorBrand.AddMilliseconds(-1));
        Assert.True(after.Cat >= RoundDown(floorCat));
    }

    [Fact]
    public async Task The_category_and_brand_floors_are_their_own_change_times()
    {
        var future = DateTimeOffset.UtcNow.AddDays(30);
        Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogCategories.Where(c => c.Slug == "bakery").ExecuteUpdate(s => s.SetProperty(c => c.UpdatedAt, future)));
        Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogBrands.Where(b => b.Slug == "challah-corner").ExecuteUpdate(s => s.SetProperty(b => b.UpdatedAt, future)));
        var cats = await CatalogKit.Json(_c, $"{B}/sitemap/categories");
        var brands = await CatalogKit.Json(_c, $"{B}/sitemap/brands");
        DateTimeOffset T(JsonElement p, string key) => p.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key).GetProperty("lastModified").GetDateTimeOffset();
        Assert.Equal(future.UtcDateTime, T(cats, "bakery").UtcDateTime, TimeSpan.FromMilliseconds(2));        // newer than any listing: the row's own time wins
        Assert.Equal(future.UtcDateTime, T(brands, "challah-corner").UtcDateTime, TimeSpan.FromMilliseconds(2));
        Assert.True(T(cats, "food-drink") >= T(cats, "bakery"));                                              // and the parent carries the newest below it
    }

    [Fact]
    public async Task A_category_rename_by_the_feed_moves_its_last_modified_forward()
    {
        var before = (await CatalogKit.Json(_c, $"{B}/sitemap/categories")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "pantry").GetProperty("lastModified").GetDateTimeOffset();
        _f.Clock.Advance(TimeSpan.FromDays(400));                                                             // after every listing time in the seed
        var cat = Fixture(_f).Categories().Single(c => c.Slug == "pantry");
        Fixture(_f).SetCategory(cat with { Name = "Pantry Renamed" });
        await CatalogKit.Sync(_f);
        var after = (await CatalogKit.Json(_c, $"{B}/sitemap/categories")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "pantry").GetProperty("lastModified").GetDateTimeOffset();
        Assert.True(after > before);
        Assert.Equal(_f.Clock.GetUtcNow().UtcDateTime, after.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_brand_storefront_shows_a_seller_that_really_has_its_listings()
    {
        // the brand feed names another seller than the one whose listings carry the brand
        var other = FixtureCatalogSource.Id("seller:glatt");
        Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogBrands.Where(b => b.Slug == "challah-corner").ExecuteUpdate(s => s.SetProperty(b => b.SellerId, other)));
        var b = await CatalogKit.Json(_c, $"{B}/brands/challah-corner");
        Assert.Equal("Mazel Bakery", b.GetProperty("seller").GetProperty("displayName").GetString());        // the seller of the 3 listings, not the brand row's claim
        // a brand row naming a seller that does not exist is no 500 either
        Infrastructure.WebhookTestKit.WithDb(_f, db => db.CatalogBrands.Where(b2 => b2.Slug == "challah-corner").ExecuteUpdate(s => s.SetProperty(b2 => b2.SellerId, Guid.NewGuid())));
        Assert.Equal("Mazel Bakery", (await CatalogKit.Json(_c, $"{B}/brands/challah-corner")).GetProperty("seller").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task The_sellers_facet_is_capped_like_the_brands()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        for (var i = 0; i < 520; i++)
            fx.Add(Copy(template, Guid.NewGuid()) with { Seller = new SellerRef(Guid.NewGuid(), $"Seller {i:D4}", true) });
        await CatalogKit.Sync(_f);
        var facets = (await CatalogKit.Json(_c, $"{B}/listings?facets=true&pageSize=1")).GetProperty("facets");
        Assert.Equal(500, facets.GetProperty("sellers").GetArrayLength());
    }

    private static ListingSnapshot Copy(ListingSnapshot t, Guid id) => CatalogKit.Copy(t, id);
}
