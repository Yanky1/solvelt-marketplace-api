using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class SearchTermTests
{
    [Fact]
    public void Terms_are_lower_case_ascii_words_without_duplicates()
    {
        Assert.Equal(["creme", "brulee", "babka"], SearchTerms.From("  Crème   Brûlée, BABKA! crème "));
        Assert.Empty(SearchTerms.From("!!! ... ---"));
        Assert.Empty(SearchTerms.From(null));
        Assert.Equal(["zoes"], SearchTerms.From("Zoë's"));                // possessive, as in slugs
    }

    [Fact]
    public void Terms_are_capped_at_eight_words_and_forty_letters_each()
    {
        Assert.Equal(8, SearchTerms.From("a1 a2 a3 a4 a5 a6 a7 a8").Count);
        Assert.Equal(8, SearchTerms.From("a1 a2 a3 a4 a5 a6 a7 a8 a9").Count);
        Assert.Equal(["a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8"], SearchTerms.From("a1 a2 a3 a4 a5 a6 a7 a8 a9 a10"));
        Assert.Equal(40, SearchTerms.From(new string('x', 40))[0].Length);
        Assert.Equal(40, SearchTerms.From(new string('x', 41))[0].Length);
        Assert.Equal(40, SearchTerms.From(new string('x', 400))[0].Length);
    }

    [Fact]
    public void Search_limits_are_the_documented_literals()
        => Assert.Equal((100, 8, 40, 50_000), (SearchTerms.MaxQueryLength, SearchTerms.MaxTerms, SearchTerms.MaxTermLength, SearchTerms.MaxMatches));
}

public class CatalogSearchTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string Base = "/api/v1/catalog";

    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK) => Json(_c, url, expected);
    private async Task<List<string>> Search(string q, string extra = "") => Slugs(await Get($"{Base}/listings?q={Uri.EscapeDataString(q)}{extra}"));
    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;

    // ---------- matching ----------
    [Fact]
    public async Task A_title_word_finds_the_listing_and_a_prefix_does_too()
    {
        var whole = await Search("challah");
        Assert.Contains("classic-braided-challah", whole);
        Assert.Contains("olive-wood-challah-board", whole);
        Assert.Equal(whole.OrderBy(x => x), (await Search("chal")).OrderBy(x => x));   // prefix of the last word
        Assert.Equal(whole.OrderBy(x => x), (await Search("CHALLAH!")).OrderBy(x => x));   // case and punctuation do not matter
    }

    [Fact]
    public async Task Every_term_must_match_not_just_one()
    {
        var both = await Search("challah rolls");
        Assert.Equal(["challah-rolls-dozen"], both);
        Assert.Empty(await Search("challah submarine"));
    }

    [Fact]
    public async Task Brand_search_terms_and_description_words_all_match()
    {
        Assert.Contains("deep-house-cleaning", await Search("hudson home pros"));                        // brand
        Assert.Equal(["deep-house-cleaning"], await Search("maid service"));                             // seller keywords
        Assert.Equal(["pewter-kiddush-cup"], await Search("becher"));
        Assert.Contains("chocolate-babka", await Search("yeast"));                                       // summary / description
    }

    [Fact]
    public async Task Accents_are_folded_on_both_sides()
    {
        var fx = Fixture(_f);
        fx.Upsert(FixtureCatalogSource.Id("listing:babka-chocolate"), l => l with { Title = "Crème Brûlée Babka" });
        await Sync(_f);
        Assert.Contains("chocolate-babka", await Search("creme brulee"));
        Assert.Contains("chocolate-babka", await Search("Crème"));
    }

    [Fact]
    public async Task A_typo_still_finds_the_listing_when_nothing_matches_exactly()
    {
        Assert.Contains("classic-braided-challah", await Search("chalah"));                              // one letter missing
        Assert.Contains("cabernet-sauvignon-mevushal", await Search("cabarnet"));                        // one letter swapped
        Assert.Contains("deep-house-cleaning", await Search("claning"));
        Assert.Empty(await Search("zzzzzzzz"));
    }

    [Fact]
    public async Task Short_terms_are_not_guessed_at()
    {
        Assert.Empty(await Search("chx"));          // three letters: no typo tolerance
        Assert.Empty(await Search("chzz"));         // four letters, but no word is close enough
    }

    [Fact]
    public async Task Typo_tolerance_needs_every_term_to_be_close()
    {
        Assert.Contains("challah-rolls-dozen", await Search("chalah rolls"));
        Assert.Empty(await Search("chalah submarine"));
    }

    [Fact]
    public async Task No_match_is_an_empty_page_not_an_error()
    {
        var page = await Get($"{Base}/listings?q=qwertyuiop");
        Assert.Equal((0, 0, 0), (page.GetProperty("items").GetArrayLength(), page.GetProperty("total").GetInt32(), page.GetProperty("totalPages").GetInt32()));
    }

    // ---------- ranking ----------
    [Fact]
    public async Task A_title_hit_ranks_above_a_description_only_hit()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var inTitle = fx.Add(Copy(template, Guid.NewGuid(), "Zephyr Scarf") with { Description = "plain", Summary = "plain", SearchTerms = null });
        var inDescription = fx.Add(Copy(template, Guid.NewGuid(), "Plain Mug") with { Description = "a zephyr of a mug", SearchTerms = null });
        var inTerms = fx.Add(Copy(template, Guid.NewGuid(), "Plain Cup") with { Description = "plain", SearchTerms = ["zephyr"] });
        await Sync(_f);
        var order = (await Get($"{Base}/listings?q=zephyr")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([inTitle.Id, inTerms.Id, inDescription.Id], order);                                // title > terms > description
    }

    [Fact]
    public async Task A_brand_hit_ranks_above_a_description_hit()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product" && l.Brand.Name == "Brooklyn Judaica");
        var inDescription = fx.Add(Copy(template, Guid.NewGuid(), "Plain Mug") with { Description = "judaica", SearchTerms = null, Brand = fx.All().First(l => l.Brand.Name == "Mazel Bakery").Brand, Seller = fx.All().First(l => l.Brand.Name == "Mazel Bakery").Seller });
        await Sync(_f);
        var ids = (await Get($"{Base}/listings?q=judaica&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(inDescription.Id, ids[^1]);
        Assert.True(ids.Count > 1);
    }

    [Fact]
    public async Task An_exact_word_ranks_above_a_prefix_of_a_longer_word()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var longer = fx.Add(Copy(template, Guid.NewGuid(), "Ladleful Spoon") with { Description = "x", SearchTerms = null });
        var exact = fx.Add(Copy(template, Guid.NewGuid(), "Ladle") with { Description = "x", SearchTerms = null });
        await Sync(_f);
        var order = (await Get($"{Base}/listings?q=ladle")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([exact.Id, longer.Id], order);
    }

    [Fact]
    public async Task Relevance_is_the_default_order_with_a_query_and_paging_neither_repeats_nor_drops()
    {
        var all = await Search("challah", "&pageSize=100");
        Assert.True(all.Count >= 4);
        var seen = new List<string>();
        for (var p = 1; p <= all.Count; p++) seen.AddRange(await Search("challah", $"&pageSize=1&page={p}"));
        Assert.Equal(all, seen);
        Assert.Equal(all, await Search("challah", "&sort=relevance&pageSize=100"));
        var page = await Get($"{Base}/listings?q=challah&pageSize=2&page=2");
        Assert.Equal((2, all.Count, (int)Math.Ceiling(all.Count / 2.0)), (page.GetProperty("page").GetInt32(), page.GetProperty("total").GetInt32(), page.GetProperty("totalPages").GetInt32()));
    }

    [Fact]
    public async Task Other_sorts_still_apply_to_search_results()
    {
        var byPrice = await Get($"{Base}/listings?q=challah&sort=price_asc&pageSize=100");
        var prices = byPrice.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("priceFromCents").GetInt64()).ToList();
        Assert.Equal(prices.OrderBy(x => x), prices);
        var byTitle = await Search("challah", "&sort=title&pageSize=100");
        Assert.Equal(byTitle.OrderBy(x => x, StringComparer.Ordinal).Count(), byTitle.Count);
    }

    [Fact]
    public async Task Relevance_without_a_query_is_the_newest_order()
        => Assert.Equal(Slugs(await Get($"{Base}/listings")), Slugs(await Get($"{Base}/listings?sort=relevance")));

    [Fact]
    public async Task A_blank_query_is_no_query()
    {
        Assert.Equal(40, (await Get($"{Base}/listings?q=")).GetProperty("total").GetInt32());
        Assert.Equal(40, (await Get($"{Base}/listings?q=%20%20")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Punctuation_only_is_a_query_with_no_terms_and_finds_nothing()
        => Assert.Equal(0, (await Get($"{Base}/listings?q=!!!")).GetProperty("total").GetInt32());

    // ---------- filters combine with search ----------
    [Fact]
    public async Task A_query_combines_with_the_other_filters()
    {
        Assert.Equal(["challah-rolls-dozen", "classic-braided-challah", "whole-wheat-challah"], (await Search("challah", "&category=bakery")).OrderBy(x => x));
        Assert.Equal(["olive-wood-challah-board"], await Search("challah", "&category=kitchen-tableware"));
        Assert.Empty(await Search("challah", "&type=service"));
        Assert.Contains("deep-house-cleaning", await Search("cleaning", "&brand=hudson-home-pros&type=service"));
        Assert.DoesNotContain("deep-house-cleaning", await Search("cleaning", "&brand=challah-corner"));
        Assert.Equal(3, (await Get($"{Base}/listings?q=challah&brand=challah-corner")).GetProperty("total").GetInt32());
    }

    // ---------- visibility ----------
    [Fact]
    public async Task Hidden_listings_are_never_found()
    {
        Assert.Empty(await Search("coffee mug"));            // seller suspended
        Assert.Empty(await Search("retired spice"));         // archived
        Assert.Empty(await Search("closed corner"));         // a brand with only hidden listings
    }

    [Fact]
    public async Task An_unlist_event_removes_the_listing_from_search_at_once()
    {
        Assert.Contains("chocolate-babka", await Search("babka"));
        var babka = Row(_f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        Assert.Equal("applied", StatusOf((await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version)).Json));
        Assert.DoesNotContain("chocolate-babka", await Search("babka"));
        Assert.DoesNotContain("chocolate-babka", await Search("babkq"));        // nor through the typo pass
    }

    [Fact]
    public async Task A_suspended_seller_disappears_from_search_and_returns_when_reactivated()
    {
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.Seller.Id == FixtureCatalogSource.Id("seller:mazel"))) fx.Upsert(l.Id, x => x with { Seller = x.Seller with { Active = false } });
        await Sync(_f);
        Assert.Empty(await Search("challah rolls"));
        foreach (var l in fx.All().Where(l => l.Seller.Id == FixtureCatalogSource.Id("seller:mazel"))) fx.Upsert(l.Id, x => x with { Seller = x.Seller with { Active = true } });
        await Sync(_f);
        Assert.Equal(["challah-rolls-dozen"], await Search("challah rolls"));
    }

    // ---------- the index itself never offers hidden listings (they would otherwise use up the match cap) ----------
    private async Task<List<Guid>> Index(params string[] terms)
    {
        using var scope = _f.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISearchIndex>().SearchAsync(scope.ServiceProvider.GetRequiredService<Data.MarketplaceDb>(), terms, 50, default)).Select(m => m.ListingId).ToList();
    }

    [Fact]
    public async Task The_index_leaves_out_hidden_listings_itself()
    {
        Assert.Empty(await Index("coffee", "mug"));          // seller suspended
        Assert.Empty(await Index("retired", "spice"));       // archived
        var babka = Row(_f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        Assert.Contains(babka.Id, await Index("babka"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        Assert.DoesNotContain(babka.Id, await Index("babka"));
        Assert.DoesNotContain(babka.Id, await Index("babkq"));   // typo pass too
    }

    [Fact]
    public async Task Hidden_matches_do_not_use_up_the_requested_number_of_hits()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        for (var i = 0; i < 3; i++) fx.Add(Copy(template, Guid.NewGuid(), "Quokka Archived") with { Status = "Archived", SearchTerms = null });
        var visible = new[] { fx.Add(Copy(template, Guid.NewGuid(), "Quokka Plush") with { SearchTerms = null }), fx.Add(Copy(template, Guid.NewGuid(), "Quokka Mug") with { SearchTerms = null }) };
        await Sync(_f);
        using var scope = _f.Services.CreateScope();
        var hits = await scope.ServiceProvider.GetRequiredService<ISearchIndex>().SearchAsync(scope.ServiceProvider.GetRequiredService<Data.MarketplaceDb>(), ["quokka"], 2, default);
        Assert.Equal(visible.Select(v => v.Id).OrderBy(x => x), hits.Select(h => h.ListingId).OrderBy(x => x));
    }

    [Fact]
    public async Task A_suspended_seller_is_left_out_by_the_index_itself()
    {
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.Seller.Id == FixtureCatalogSource.Id("seller:glatt"))) fx.Upsert(l.Id, x => x with { Seller = x.Seller with { Active = false } });
        await Sync(_f);
        Assert.Empty(await Index("brisket"));
    }

    // ---------- the index follows the read model ----------
    [Fact]
    public async Task A_new_title_replaces_the_old_one_in_the_index()
    {
        var id = FixtureCatalogSource.Id("listing:babka-chocolate");
        Fixture(_f).Upsert(id, l => l with { Title = "Zebra Striped Cake", Description = "Marble.", SearchTerms = null });
        await Sync(_f);
        Assert.Equal(["chocolate-babka"], await Search("zebra"));                   // the slug is stable (M1-32): the title words moved, not the URL
        Assert.DoesNotContain("chocolate-babka", await Search("babka"));            // the slug stays, the words go
        Assert.DoesNotContain("chocolate-babka", await Search("yeast"));            // old search terms and description are gone too
    }

    [Fact]
    public async Task Renaming_a_brand_moves_its_listings_in_search()
    {
        Fixture(_f).RenameBrand(FixtureCatalogSource.Id("brand:challah-corner"), "Golden Loaf");
        await Sync(_f);
        Assert.Equal(3, (await Search("golden loaf")).Count);
        Assert.DoesNotContain("classic-braided-challah", await Search("corner"));
    }

    [Fact]
    public async Task Search_rows_exist_for_every_listing_and_never_twice()
    {
        var listings = WithDb(_f, db => db.CatalogListings.Count(l => !l.IsStub));
        Assert.Equal(listings, WithDb(_f, db => db.CatalogSearch.Count()));
        WithDb(_f, db => db.CatalogSearch.ExecuteDelete());
        Assert.Empty(await Search("challah"));
        await Sync(_f);
        Assert.Equal(listings, WithDb(_f, db => db.CatalogSearch.Count()));
        Assert.Contains("classic-braided-challah", await Search("challah"));
    }

    [Fact]
    public async Task Parallel_syncs_leave_exactly_one_search_row_per_listing()
    {
        var listings = WithDb(_f, db => db.CatalogListings.Count(l => !l.IsStub));
        Fixture(_f).Upsert(FixtureCatalogSource.Id("listing:babka-chocolate"), l => l with { Title = "Parallel Babka" });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Sync(_f)));
        Assert.Equal(listings, WithDb(_f, db => db.CatalogSearch.Count()));
        Assert.Equal(1, WithDb(_f, db => db.CatalogSearch.Count(s => s.Title == "parallel babka")));
    }

    [Fact]
    public async Task On_PostgreSQL_the_weighted_document_and_both_indexes_exist()
    {
        if (_f.Provider != "postgres") return;       // the objects are PostgreSQL-only; CI runs this on PostgreSQL
        var found = WithDb(_f, db => db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE tablename = 'catalog_search' AND indexname IN ('ix_catalog_search_document', 'ix_catalog_search_text_trgm') AND indexdef LIKE '%gin%'").Single());
        Assert.Equal(2, found);
        var weights = WithDb(_f, db => db.Database.SqlQueryRaw<string>(
            "SELECT document::text AS \"Value\" FROM catalog_search WHERE title = 'classic braided challah'").Single());
        Assert.Contains("'classic':1A", weights);          // title weight A
        Assert.Contains("'corner':", weights);             // brand
        await Task.CompletedTask;
    }

    // ---------- input limits and hostile input ----------
    [Fact]
    public async Task The_query_may_be_100_characters_but_not_101()
    {
        await Get($"{Base}/listings?q={new string('a', 100)}");
        Assert.Equal("invalid_query", Code(await Get($"{Base}/listings?q={new string('a', 101)}", HttpStatusCode.BadRequest)));
        await Get($"{Base}/suggest?q={new string('a', 100)}");
        Assert.Equal("invalid_query", Code(await Get($"{Base}/suggest?q={new string('a', 101)}", HttpStatusCode.BadRequest)));
    }

    [Theory]
    [InlineData("x' OR 1=1 --")]
    [InlineData("a:* | !b")]
    [InlineData(")(&|:*!<->")]
    [InlineData("'; DROP TABLE catalog_search; --")]
    [InlineData("%_\\")]
    [InlineData("ｃｈａｌｌａｈ")]
    [InlineData("\u0000chal")]
    public async Task Hostile_text_is_just_text(string q)
    {
        var res = await _c.GetAsync($"{Base}/listings?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var res2 = await _c.GetAsync($"{Base}/suggest?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.True(WithDb(_f, db => db.CatalogSearch.Count()) > 0);                                 // the table is still there
        Assert.Equal(40, (await Get($"{Base}/listings")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task What_people_search_for_never_reaches_the_logs()
    {
        await Get($"{Base}/listings?q=plumberzzz-secret");
        await Get($"{Base}/suggest?q=plumberzzz-secret");
        await Get($"{Base}/listings?q={new string('a', 101)}", HttpStatusCode.BadRequest);
        Assert.DoesNotContain(_f.Logs.Lines, l => l.Contains("plumberzzz", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_wildcard_character_does_not_match_everything()
    {
        Assert.Equal(0, (await Get($"{Base}/listings?q=%25")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Get($"{Base}/listings?q=_")).GetProperty("total").GetInt32());
    }

    // ---------- suggest ----------
    [Fact]
    public async Task Suggest_returns_listings_brands_and_categories_with_the_documented_shape()
    {
        var r = await Get($"{Base}/suggest?q=chal");
        Assert.Equal("chal", r.GetProperty("query").GetString());
        var s = r.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Equal(["kind", "text", "slug"], s[0].EnumerateObject().Select(p => p.Name));
        Assert.Contains(s, x => (x.GetProperty("kind").GetString(), x.GetProperty("slug").GetString()) == ("listing", "classic-braided-challah"));
        Assert.Contains(s, x => (x.GetProperty("kind").GetString(), x.GetProperty("text").GetString(), x.GetProperty("slug").GetString()) == ("brand", "Challah Corner", "challah-corner"));
        var bake = await Get($"{Base}/suggest?q=bake");
        Assert.Contains(bake.GetProperty("suggestions").EnumerateArray(), x => (x.GetProperty("kind").GetString(), x.GetProperty("slug").GetString()) == ("category", "bakery"));
    }

    [Fact]
    public async Task Suggest_needs_two_characters()
    {
        Assert.Empty((await Get($"{Base}/suggest?q=c")).GetProperty("suggestions").EnumerateArray());
        Assert.NotEmpty((await Get($"{Base}/suggest?q=ch")).GetProperty("suggestions").EnumerateArray());
        Assert.Empty((await Get($"{Base}/suggest")).GetProperty("suggestions").EnumerateArray());
        Assert.Empty((await Get($"{Base}/suggest?q=!!")).GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task Suggest_limit_is_one_to_ten_and_defaults_to_eight()
    {
        Assert.Equal(8, (await Get($"{Base}/suggest?q=ch")).GetProperty("suggestions").GetArrayLength());
        Assert.Equal(1, (await Get($"{Base}/suggest?q=ch&limit=1")).GetProperty("suggestions").GetArrayLength());
        Assert.Equal(10, (await Get($"{Base}/suggest?q=ch&limit=10")).GetProperty("suggestions").GetArrayLength());
        foreach (var bad in new[] { "0", "11", "-1", "x", "1.5" })
            Assert.Equal("invalid_query", Code(await Get($"{Base}/suggest?q=ch&limit={bad}", HttpStatusCode.BadRequest)));
    }

    [Fact]
    public async Task Suggest_never_offers_hidden_listings_brands_or_categories()
    {
        Assert.Empty((await Get($"{Base}/suggest?q=closed")).GetProperty("suggestions").EnumerateArray());
        Assert.Empty((await Get($"{Base}/suggest?q=coffee")).GetProperty("suggestions").EnumerateArray());
        var babka = Row(_f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        var after = (await Get($"{Base}/suggest?q=babka")).GetProperty("suggestions").EnumerateArray().Select(x => x.GetProperty("slug").GetString()).ToList();
        Assert.DoesNotContain("chocolate-babka", after);
    }

    [Fact]
    public async Task A_category_with_no_visible_listing_is_not_suggested()
    {
        var fx = Fixture(_f);
        foreach (var l in fx.All().Where(l => l.CategoryId == FixtureCatalogSource.Id("category:books"))) fx.Upsert(l.Id, x => x with { Status = "Archived" });
        await Sync(_f);
        Assert.DoesNotContain((await Get($"{Base}/suggest?q=books")).GetProperty("suggestions").EnumerateArray(), x => x.GetProperty("kind").GetString() == "category");
    }

    [Fact]
    public async Task Search_and_suggest_share_the_public_rate_limit()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "3" });
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Base}/suggest?q=ch")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Base}/listings?q=ch")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Base}/suggest?q=ch")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync($"{Base}/suggest?q=ch")).StatusCode);
    }
}
