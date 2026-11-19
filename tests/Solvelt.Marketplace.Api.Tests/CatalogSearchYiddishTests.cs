using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Microsoft.Extensions.DependencyInjection;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The Yiddish digraph letters in search (decision V-21): ײ (U+05F2) is יי, װ (U+05F0) is וו, ױ (U+05F1) is וי and the pointed ligature ײַ (U+FB1F) is יי, folded on the
/// index side and the query side alike, with niqqud still dropped. These run on whichever database the test run uses (SQLite here, PostgreSQL with
/// <c>TEST_DB_PROVIDER=postgres</c>, as in CI), so each fold is proved against both indexes.
/// </summary>
public class SearchTextYiddishTests
{
    [Theory]
    [InlineData("ײ", "יי")]                                   // U+05F2 tsvey yudn
    [InlineData("װ", "וו")]                                   // U+05F0 tsvey vovn
    [InlineData("ױ", "וי")]                                   // U+05F1 vov yud
    [InlineData("ײַ", "יי")]                               // the pointed ligature: two yods, NOT the digraph letter
    [InlineData("ײַ", "יי")]                           // U+05F2 + patah: the niqqud is dropped as before
    [InlineData("יַי", "יי")]                                // two yods with a patah between: the same word
    [InlineData("װאָס", "וואס")]                          // vov-vov, komets alef: "what"
    [InlineData("ײדיש", "יידיש")]                              // at the start of a word
    [InlineData("ייִדיש", "יידיש")]                            // yod with khirik yud: the existing niqqud rule
    [InlineData("זײַן", "זיין")]                         // in the middle: "to be"
    [InlineData("ױ ײ װ", "וי יי וו")]                         // each is a word of its own and stays apart
    [InlineData("ײ־װ", "יי וו")]                               // the maqaf still separates
    public void Each_digraph_is_the_letter_pair_it_stands_for(string text, string expected) => Assert.Equal(expected, SearchText.Normalize(text));

    [Fact]
    public void Letters_that_are_not_digraphs_are_untouched_and_nothing_else_changes_in_other_scripts()
    {
        Assert.Equal("יי", SearchText.Normalize("יי"));
        Assert.Equal("וו", SearchText.Normalize("וו"));
        Assert.Equal("שלום", SearchText.Normalize("שָׁלוֹם"));                     // Hebrew as before
        Assert.Equal("привет", SearchText.Normalize("ПРИВЕТ"));                   // Cyrillic as before
        Assert.Equal("creme brulee", SearchText.Normalize("Crème Brûlée"));       // Latin as before
        Assert.Equal("שבת", SearchText.Normalize("שבת"));                         // a final tav is not touched: no other Yiddish fold is made
    }

    [Fact]
    public void The_tokenizer_version_moved_with_the_rules() => Assert.Equal("2", SearchText.Version);
}

public class CatalogSearchYiddishTests
{
    private static readonly Guid WithDigraph = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WithPairs = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static TestFactory Catalog(out FixtureCatalogSource fx)
    {
        var f = new TestFactory();
        fx = Fixture(f);
        var template = fx.All().First(l => l.Type == "product");
        // one listing typed WITH the digraph letters, another typed with the letter pairs: the same Yiddish words
        fx.Add(Copy(template, WithDigraph, "ײדישע װאָכנבלאַט") with { Summary = "ײדיש", Description = "װאָכנבלאַט", SearchTerms = [] });
        fx.Add(Copy(template, WithPairs, "יידישע וואכנבלאט") with { Summary = "יידיש", Description = "וואכנבלאט", SearchTerms = [] });
        return f;
    }

    private static async Task<List<Guid>> Found(HttpClient c, string q)
        => (await Json(c, "/api/v1/catalog/listings?q=" + Uri.EscapeDataString(q))).GetProperty("items").EnumerateArray()
            .Select(i => Guid.Parse(i.GetProperty("id").GetString()!)).ToList();

    [Fact]
    public async Task A_listing_with_the_digraph_is_found_by_the_letter_pair_and_a_listing_with_the_pair_is_found_by_the_digraph_for_both_letters()
    {
        using var f = Catalog(out _);
        await Sync(f);
        using var c = f.CreateClient();
        foreach (var q in new[] { "יידישע", "ײדישע", "ײדיש" })                      // yod-yod, tsvey yudn, and a prefix
        {
            var ids = await Found(c, q);
            Assert.Contains(WithDigraph, ids); Assert.Contains(WithPairs, ids);                // the same word whichever way it is typed or indexed
        }
        foreach (var q in new[] { "וואכנבלאט", "װאכנבלאט", "װאָכנבלאַט" })           // vov-vov, tsvey vovn (with and without niqqud)
        {
            var ids = await Found(c, q);
            Assert.Contains(WithDigraph, ids); Assert.Contains(WithPairs, ids);
        }
    }

    [Fact]
    public async Task The_pointed_ligature_and_the_vov_yud_digraph_are_folded_too()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var template = fx.All().First(l => l.Type == "product");
        var ligature = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"); var vovYud = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
        fx.Add(Copy(template, ligature, "זײַן ברויט") with { SearchTerms = [] });                  // U+FB1F inside "to be"
        fx.Add(Copy(template, vovYud, "גאױם") with { SearchTerms = [] });                         // U+05F1 inside the word
        await Sync(f);
        using var c = f.CreateClient();
        Assert.Contains(ligature, await Found(c, "זיין"));                                         // two yods
        Assert.Contains(ligature, await Found(c, "זײן"));                                          // the digraph letter
        Assert.Contains(vovYud, await Found(c, "גאוים"));                                          // vov + yud (the final mem as the title has it)
    }

    [Fact]
    public async Task Unrelated_words_still_do_not_match()
    {
        using var f = Catalog(out _);
        await Sync(f);
        using var c = f.CreateClient();
        var ids = await Found(c, "בלומען");                                                        // "flowers": nothing to do with the two listings
        Assert.DoesNotContain(WithDigraph, ids); Assert.DoesNotContain(WithPairs, ids);
        ids = await Found(c, "וועלט");                                                             // "world": a different word with the same letters inside it
        Assert.DoesNotContain(WithDigraph, ids); Assert.DoesNotContain(WithPairs, ids);
    }

    [Fact]
    public async Task The_existing_Latin_Cyrillic_and_Hebrew_search_still_finds_what_it_found()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var template = fx.All().First(l => l.Type == "product");
        var cyr = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"); var heb = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006");
        fx.Add(Copy(template, cyr, "Привет Мир") with { SearchTerms = [] });
        fx.Add(Copy(template, heb, "גביע קידוש") with { SearchTerms = [] });
        await Sync(f);
        using var c = f.CreateClient();
        Assert.Contains(cyr, await Found(c, "привет"));
        Assert.Contains(heb, await Found(c, "קידוש"));
        Assert.Contains((await Json(c, "/api/v1/catalog/listings?q=challah")).GetProperty("items").EnumerateArray(), i => i.GetProperty("slug").GetString() == "classic-braided-challah");
    }

    // ---------- the rebuild of rows written by the older tokenizer ----------

    [Fact]
    public async Task Rows_written_by_an_older_tokenizer_are_rebuilt_once_and_then_left_alone()
    {
        using var f = Catalog(out _);
        await Sync(f);                                                                           // current rules; the version is recorded
        Assert.Equal(SearchText.Version, WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single(s => s.Key == SearchIndexer.TokenizerStateKey).Cursor));

        // what the OLD rules stored: the digraph kept as it was typed, and no recorded version
        WithDb(f, db =>
        {
            var row = db.CatalogSearch.Single(r => r.ListingId == WithPairs);
            row.Title = "ײדישע װאכנבלאט"; row.Text = "ײדישע װאכנבלאט"; row.Description = "װאכנבלאט";
            db.SaveChanges();
            db.CatalogSyncStates.Where(s => s.Key == SearchIndexer.TokenizerStateKey).ExecuteDelete();
            return 0;
        });
        using (var c = f.CreateClient()) Assert.DoesNotContain(WithPairs, await Found(c, "יידישע"));   // before the rebuild the old text does not match the folded query

        Assert.True(await Reindex(f) >= 2);                                                      // every row is rewritten
        var rebuilt = WithDb(f, db => db.CatalogSearch.AsNoTracking().Single(r => r.ListingId == WithPairs));
        Assert.Equal("יידישע וואכנבלאט", rebuilt.Title);
        Assert.StartsWith("יידישע וואכנבלאט", rebuilt.Text);                                  // the typo-match text is title, brand and terms
        using (var c = f.CreateClient()) Assert.Contains(WithPairs, await Found(c, "יידישע"));
        Assert.Equal(SearchText.Version, WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single(s => s.Key == SearchIndexer.TokenizerStateKey).Cursor));
        Assert.Equal(0, await Reindex(f));                                                       // recorded: the next run does nothing
    }

    [Fact]
    public async Task The_catalog_sync_itself_rebuilds_old_rows_so_a_deploy_needs_no_manual_step()
    {
        using var f = Catalog(out _);
        await Sync(f);
        WithDb(f, db =>
        {
            var row = db.CatalogSearch.Single(r => r.ListingId == WithPairs);
            row.Title = "ײדישע װאכנבלאט"; row.Text = "ײדישע װאכנבלאט";
            db.SaveChanges();
            db.CatalogSyncStates.Where(s => s.Key == SearchIndexer.TokenizerStateKey).ExecuteDelete();
            return 0;
        });
        await Sync(f);                                                                           // an ordinary run, as the background job does it
        Assert.StartsWith("יידישע וואכנבלאט", WithDb(f, db => db.CatalogSearch.AsNoTracking().Single(r => r.ListingId == WithPairs)).Title);
        using var c = f.CreateClient();
        Assert.Contains(WithPairs, await Found(c, "יידישע"));
        Assert.Equal(SearchText.Version, WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single(s => s.Key == SearchIndexer.TokenizerStateKey).Cursor));
    }

    [Fact]
    public async Task A_database_without_search_rows_only_records_the_version()
    {
        using var f = new TestFactory();
        Assert.Equal(0, await Reindex(f));
        Assert.Equal(SearchText.Version, WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single(s => s.Key == SearchIndexer.TokenizerStateKey).Cursor));
    }

    private static async Task<int> Reindex(TestFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await SearchIndexer.ReindexIfStaleAsync(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), CancellationToken.None);
    }
}

/// <summary>The same fold on the phone: the model's words are searched by the same index, so a caller's spelling and the listing's spelling meet.</summary>
public class VoiceYiddishSpellingTests
{
    [Fact]
    public async Task The_phone_search_finds_a_listing_typed_with_the_digraph_letter_from_the_letter_pair_and_the_reverse()
    {
        using var s = new VoiceScenario();
        var fx = CatalogKit.Fixture(s.F);
        var template = (await fx.GetListingAsync(CatalogKit.ListingId("challah-classic"), CancellationToken.None))!;
        var digraph = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"); var pair = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        fx.Add(CatalogKit.Copy(template, digraph, "ײדישע בײגל") with { Summary = "ײדיש", Description = "ײדיש", SearchTerms = [] });       // typed with the digraph letter
        fx.Add(CatalogKit.Copy(template, pair, "וואכנבלאט פון װאכן") with { Summary = "וואכן", Description = "וואכן", SearchTerms = [] });   // title with vov-vov, "װ" in the middle
        await s.SyncCatalog();
        await s.Dial();

        await s.Tool("search_products", new { query = "יידישע ביי" });                     // yod-yod typed by the caller's side
        Assert.Contains(s.State().LastResults, r => r.ListingId == digraph);
        Assert.Equal(0, s.State().OffTopicCount);

        await s.Tool("search_products", new { query = "וואכנבלאט" });
        Assert.Contains(s.State().LastResults, r => r.ListingId == pair);
        await s.Tool("search_products", new { query = "װאכנבלאט" });                                      // the digraph letter typed against a listing that has the pair
        Assert.Contains(s.State().LastResults, r => r.ListingId == pair);
    }
}
