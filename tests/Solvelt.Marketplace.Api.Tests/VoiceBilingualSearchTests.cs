using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Bilingual phone search: the model passes the same request in the other language as <c>alternate_query</c>; the SERVER runs both searches and merges them (one entry per listing, best rank, at most three).
/// </summary>
public class VoiceBilingualSearchTests
{
    private const string YiddishTitle = "פֿריש געבאַקענע ברויט פֿאַר שבת";     // "fresh baked bread for Sabbath": no English words, no English-listing overlap
    private const string YiddishWord = "געבאַקענע";
    private static readonly Guid YiddishListing = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>The fixture catalog plus one listing that exists ONLY in Yiddish, synced, and a call connected.</summary>
    private static async Task<VoiceScenario> Ready()
    {
        var s = new VoiceScenario();
        var fixture = CatalogKit.Fixture(s.F);
        var template = (await fixture.GetListingAsync(CatalogKit.ListingId("challah-classic"), CancellationToken.None))!;
        fixture.Add(CatalogKit.Copy(template, YiddishListing, YiddishTitle) with { Summary = YiddishTitle, Description = YiddishTitle, SearchTerms = [] });
        await s.SyncCatalog();
        await s.Dial();
        return s;
    }

    private static List<string> Names(JsonNode r) => r["results"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();

    // ---------- through whole calls ----------

    [Fact]
    public async Task An_English_only_listing_is_found_from_a_Yiddish_request_with_the_English_rendering()
    {
        using var s = await Ready();
        var alone = await s.Tool("search_products", new { query = "חלה" });                                       // the Yiddish word alone: the English listings do not contain it
        Assert.Equal(0, alone["found"]!.GetValue<int>());
        var both = await s.Tool("search_products", new { query = "חלה", alternate_query = "challah" });
        Assert.Contains(Names(both), n => n.Contains("Challah"));
        Assert.True(VoiceSpeech.IsSpeakable(both["say"]!.GetValue<string>()));
        Assert.Equal(0, s.State().OffTopicCount);
    }

    [Fact]
    public async Task A_Yiddish_only_listing_is_found_from_an_English_request_with_the_Yiddish_rendering()
    {
        using var s = await Ready();
        var alone = await s.Tool("search_products", new { query = "fresh bread for the sabbath table" });
        Assert.DoesNotContain(Names(alone), n => n.Contains("בראָט") || n.Contains("ברויט"));
        var both = await s.Tool("search_products", new { query = "fresh bread for the sabbath table", alternate_query = YiddishWord });
        Assert.Contains(Names(both), n => n.Contains("ברויט"));                                                    // the Yiddish title, as the listing has it
        Assert.Equal(YiddishListing, s.State().LastResults.Single(r => r.Title == YiddishTitle).ListingId);
    }

    [Fact]
    public async Task The_two_searches_merge_without_repeats_and_never_read_more_than_three()
    {
        using var s = await Ready();
        var one = await s.Tool("search_products", new { query = "challah" });
        var both = await s.Tool("search_products", new { query = "challah", alternate_query = YiddishWord });   // English finds the challahs, the Yiddish word finds the Yiddish listing
        var names = Names(both);
        Assert.Equal(names.Count, names.Distinct().Count());                                                       // one entry per listing
        Assert.InRange(names.Count, 1, 3);
        Assert.Equal(names.Count, s.State().LastResults.Count);
        Assert.Contains(YiddishTitle, names);                                                                      // best rank 0 in its own list: it makes the cut next to the top English hit
        Assert.True(both["found"]!.GetValue<int>() >= one["found"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_same_words_twice_give_exactly_the_one_search_result()
    {
        using var s = await Ready();
        var one = await s.Tool("search_products", new { query = "challah" });
        var twice = await s.Tool("search_products", new { query = "challah", alternate_query = "  CHALLAH " });
        Assert.Equal(Names(one), Names(twice));
        Assert.Equal(one["found"]!.GetValue<int>(), twice["found"]!.GetValue<int>());
        var empty = await s.Tool("search_products", new { query = "challah", alternate_query = "" });
        Assert.Equal(Names(one), Names(empty));
    }

    [Fact]
    public async Task Only_the_other_language_words_are_the_search_and_a_category_still_applies()
    {
        using var s = await Ready();
        var onlyAlt = await s.Tool("search_products", new { alternate_query = "challah" });                         // no query at all: the alternate words are used
        Assert.Contains(Names(onlyAlt), n => n.Contains("Challah"));
        var inCategory = await s.Tool("search_products", new { query = "חלה", alternate_query = "challah", category = "kitchen-tableware" });
        Assert.Equal(["Olive Wood Challah Board"], Names(inCategory));
    }

    // ---------- the pure parts ----------

    private static JsonElement Item(Guid id) => JsonDocument.Parse($"{{\"id\":\"{id}\"}}").RootElement.Clone();

    [Fact]
    public void Merge_keeps_one_entry_per_listing_with_its_best_rank_and_the_callers_own_words_win_ties()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();
        var first = new List<JsonElement> { Item(a), Item(b), Item(c) };
        var second = new List<JsonElement> { Item(d), Item(c), Item(e) };       // c is rank 2 in the first list and rank 1 in the second
        var merged = SearchMerge.Merge([first, second], 3, out var dups);
        Assert.Equal([a, d, b], merged.Select(m => m.GetProperty("id").GetGuid()));                                // rank 0: a (first list) then d; rank 1: b before c (first list)
        Assert.Equal(1, dups);
        var all = SearchMerge.Merge([first, second], 10, out _);
        Assert.Equal(5, all.Count);                                                                                // c once
        Assert.Equal(c, all[3].GetProperty("id").GetGuid());                                                       // best rank 1 (from the second list), after b
    }

    [Fact]
    public void Merge_caps_and_copes_with_one_empty_list()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(3, SearchMerge.Merge([ids.Select(Item).ToList(), []], 3, out _).Count);
        Assert.Empty(SearchMerge.Merge([[], []], 3, out _));
    }

    [Theory]
    [InlineData("challah", null, "challah", null)]
    [InlineData("challah", "", "challah", null)]
    [InlineData("challah", "   ", "challah", null)]
    [InlineData("challah", "Challah ", "challah", null)]
    [InlineData("challah  bread", "challah bread", "challah  bread", null)]
    [InlineData("חלה", "challah", "חלה", "challah")]
    [InlineData("", "challah", "challah", null)]
    [InlineData("", null, "", null)]
    public void Queries_decides_when_a_second_search_is_worth_making(string text, string? alt, string wantText, string? wantAlt)
        => Assert.Equal((wantText, wantAlt), SearchMerge.Queries(text, alt));

    [Fact]
    public void An_alternate_query_that_is_too_long_is_ignored_not_an_error()
        => Assert.Equal(("challah", null), SearchMerge.Queries("challah", new string('x', 101)));

    // ---------- instructions and both pipelines ----------

    [Fact]
    public void The_instructions_tell_the_model_to_always_fill_alternate_query_when_the_caller_is_not_speaking_English()
    {
        var i = VoiceScripts.Instructions;
        Assert.Contains("When the caller is not speaking English, ALWAYS fill alternate_query", i);
        Assert.Contains("the server searches both and merges them", i);
    }

    [Fact]
    public void The_tool_is_defined_once_for_all_five_options()
    {
        using var s = new VoiceScenario();
        var search = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Single(d => d!["name"]!.GetValue<string>() == "search_products")!;
        var alt = search["parameters"]!["properties"]!["alternate_query"]!;
        Assert.Equal("string", alt["type"]!.GetValue<string>());
        Assert.Contains("OTHER language", alt["description"]!.GetValue<string>());
        Assert.Empty(search["parameters"]!["required"]!.AsArray());
    }
}
