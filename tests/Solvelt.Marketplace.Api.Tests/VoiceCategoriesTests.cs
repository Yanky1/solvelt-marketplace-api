using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Browsing by category on the phone (a bug from a real test call: "what categories of items are available?" got no answer). <c>list_categories</c> reads the web's own category handler in process,
/// counting only products that can be bought now; <c>search_products</c> takes a category; the scope names browsing as on topic; the chained pipelines get the same tools.
/// </summary>
public class VoiceCategoriesTests
{
    private static async Task<VoiceScenario> Ready()
    {
        var s = new VoiceScenario();
        await s.SyncCatalog();
        await s.Dial();
        return s;
    }

    private static List<string> Names(JsonNode r) => r["categories"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
    private static List<string> Slugs(JsonNode r) => r["categories"]!.AsArray().Select(c => c!["slug"]!.GetValue<string>()).ToList();
    private static List<string> Products(JsonNode r) => r["results"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();

    private static async Task Db(VoiceScenario s, Func<MarketplaceDb, Task> change)
    {
        using var scope = s.F.Services.CreateScope();
        await change(scope.ServiceProvider.GetRequiredService<MarketplaceDb>());
    }

    // ---------- the tool ----------

    [Fact]
    public async Task The_top_level_lists_only_categories_with_products_to_buy_and_leaves_out_services()
    {
        using var s = await Ready();
        var r = await s.Tool("list_categories");
        Assert.Equal(["Food & Drink", "Home & Judaica"], Names(r));            // "Services" has only services, which the phone does not sell
        Assert.Equal(2, r["total"]!.GetValue<int>());
        Assert.Equal(0, r["more"]!.GetValue<int>());
        Assert.True(VoiceSpeech.IsSpeakable(r["say"]!.GetValue<string>()), r["say"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_category_whose_products_are_all_sold_out_is_left_out_and_comes_back_when_stock_does()
    {
        using var s = await Ready();
        var books = FixtureCatalogSource.Id("category:books");
        await Db(s, db => db.CatalogListings.Where(l => l.CategoryId == books).ExecuteUpdateAsync(u => u.SetProperty(l => l.Available, false)));
        Assert.DoesNotContain("Books", Names(await s.Tool("list_categories", new { parent = "home-judaica" })));
        await Db(s, db => db.CatalogListings.Where(l => l.CategoryId == books).ExecuteUpdateAsync(u => u.SetProperty(l => l.Available, true)));
        Assert.Contains("Books", Names(await s.Tool("list_categories", new { parent = "home-judaica" })));
    }

    [Fact]
    public async Task Drilling_down_by_slug_or_by_the_name_the_caller_used_reads_at_most_three_and_offers_more()
    {
        using var s = await Ready();
        var first = await s.Tool("list_categories", new { parent = "food-drink" });
        Assert.False(string.IsNullOrEmpty(first["parent"]!.GetValue<string>()));
        Assert.Equal(["bakery", "meat-poultry", "wine-spirits"], Slugs(first));
        Assert.Equal(4, first["total"]!.GetValue<int>());
        Assert.Equal(1, first["more"]!.GetValue<int>());                        // at most three, then offer more
        Assert.Contains("skip 3", first["instruction"]!.GetValue<string>());

        var rest = await s.Tool("list_categories", new { parent = "Food and drink", skip = 3 });   // the caller's own words: any case, and "&" said as "and"
        Assert.Equal(["pantry"], Slugs(rest));
        Assert.Equal(0, rest["more"]!.GetValue<int>());

        var named = await s.Tool("list_categories", new { parent = "HOME-JUDAICA" });
        Assert.Equal(["candlesticks", "kitchen-tableware", "books"], Slugs(named));
    }

    [Fact]
    public async Task An_unknown_category_or_one_with_nothing_to_buy_is_told_plainly_and_a_leaf_says_it_has_no_subcategories()
    {
        using var s = await Ready();
        Assert.Equal("unknown_category", (await s.Tool("list_categories", new { parent = "weapons" }))["error"]!.GetValue<string>());
        Assert.Equal("unknown_category", (await s.Tool("list_categories", new { parent = "cleaning" }))["error"]!.GetValue<string>());   // a services category: nothing to buy by phone
        var leaf = await s.Tool("list_categories", new { parent = "bakery" });
        Assert.Empty(leaf["categories"]!.AsArray());
        Assert.Contains("no subcategories", leaf["instruction"]!.GetValue<string>());
    }

    [Fact]
    public async Task Browsing_is_never_counted_as_off_topic()
    {
        using var s = await Ready();
        await s.Tool("list_categories");
        await s.Tool("list_categories", new { parent = "food-drink" });
        Assert.Equal(0, s.State().OffTopicCount);
    }

    // ---------- the filter in the search ----------

    [Fact]
    public async Task A_search_can_stay_inside_one_category_named_by_slug_or_by_name_and_needs_no_words()
    {
        using var s = await Ready();
        var bySlug = await s.Tool("search_products", new { category = "bakery" });                  // "what do you have in bakery?"
        Assert.Equal(3, bySlug["results"]!.AsArray().Count);
        Assert.True(bySlug["found"]!.GetValue<int>() > 3);
        var byName = await s.Tool("search_products", new { category = "Bakery" });
        Assert.Equal(Products(bySlug), Products(byName));

        var inside = await s.Tool("search_products", new { query = "challah", category = "kitchen-tableware" });
        Assert.Equal(["Olive Wood Challah Board"], Products(inside));                                // the bakery's challahs are not in this category
        var outside = await s.Tool("search_products", new { query = "challah" });
        Assert.True(outside["found"]!.GetValue<int>() > 1);
    }

    [Fact]
    public async Task A_parent_category_includes_what_is_below_it_and_a_sold_out_category_finds_nothing()
    {
        using var s = await Ready();
        var parent = await s.Tool("search_products", new { category = "food-drink" });
        var bakery = await s.Tool("search_products", new { category = "bakery" });
        Assert.True(parent["found"]!.GetValue<int>() > bakery["found"]!.GetValue<int>());

        var books = FixtureCatalogSource.Id("category:books");
        await Db(s, db => db.CatalogListings.Where(l => l.CategoryId == books).ExecuteUpdateAsync(u => u.SetProperty(l => l.Available, false)));
        Assert.Equal("unknown_category", (await s.Tool("search_products", new { category = "books" }))["error"]!.GetValue<string>());   // not offered, so not searchable either
    }

    [Fact]
    public async Task An_unknown_category_or_a_search_with_neither_words_nor_category_is_refused_with_what_to_do()
    {
        using var s = await Ready();
        var unknown = await s.Tool("search_products", new { query = "challah", category = "spaceships" });
        Assert.Equal("unknown_category", unknown["error"]!.GetValue<string>());
        Assert.Contains("list_categories", unknown["instruction"]!.GetValue<string>());
        Assert.Equal("bad_query", (await s.Tool("search_products", new { category = "   " }))["error"]!.GetValue<string>());
    }

    // ---------- one scripted call ----------

    [Fact]
    public async Task One_call_asks_what_is_sold_drills_into_a_category_searches_it_and_hears_about_a_product()
    {
        using var s = await Ready();
        var top = await s.Tool("list_categories");                                                  // "what categories do you have?"
        var food = Slugs(top)[0];
        var kinds = await s.Tool("list_categories", new { parent = food });                         // "tell me about food"
        var bakery = Slugs(kinds)[0];
        var found = await s.Tool("search_products", new { category = bakery });                     // "what do you have in bakery?"
        Assert.Equal(3, found["results"]!.AsArray().Count);
        Assert.Equal(3, s.State().LastResults.Count);                                               // the positions the model may use now
        var one = await s.Tool("describe_product", new { position = 1 });
        Assert.False(string.IsNullOrEmpty(one["name"]!.GetValue<string>()));
        Assert.Equal(0, s.State().OffTopicCount);
    }

    // ---------- scope and the shared tool set ----------

    [Fact]
    public void The_scope_names_categories_and_browsing_as_on_topic_before_the_off_topic_list()
    {
        var i = VoiceScripts.Instructions;
        Assert.Contains("what categories and kinds of products Solvelt sells, and browsing them", i);
        foreach (var asked in new[] { "what do you sell?", "what categories do you have?", "what do you have in kitchen?" }) Assert.Contains(asked, i);
        Assert.Contains("call list_categories", i);
        Assert.True(i.IndexOf("browsing them", StringComparison.Ordinal) < i.IndexOf("Everything else is OFF TOPIC", StringComparison.Ordinal));
        Assert.Contains("at most three options, then offer more", i);                              // the existing rule stays
    }

    [Fact]
    public void Both_pipelines_get_the_tool_from_the_one_shared_definition_list()
    {
        using var s = new VoiceScenario();
        var names = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(d => d!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_categories", names);
        Assert.Contains("search_products", names);
        var search = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Single(d => d!["name"]!.GetValue<string>() == "search_products")!;
        Assert.NotNull(search["parameters"]!["properties"]!["category"]);
        Assert.Empty(search["parameters"]!["required"]!.AsArray());                                 // words are optional now
    }
}
