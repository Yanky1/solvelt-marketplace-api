using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("שָׁלוֹם", "שלום")]                              // pointed Hebrew (niqqud) is the unpointed word
    [InlineData("שלום עולם", "שלום עולם")]
    [InlineData("בְּרֵאשִׁ֖ית", "בראשית")]                       // niqqud and cantillation (U+0596)
    [InlineData("בית־ספר", "בית ספר")]                             // maqaf separates words
    [InlineData("צה״ל", "צהל")]                                   // gershayim is dropped, not a separator
    [InlineData("ג׳ינס", "גינס")]                                  // geresh too
    [InlineData("שׁלום", "שלום")]                              // presentation form: shin with shin dot
    [InlineData("ПРИВЕТ Мир", "привет мир")]                       // Cyrillic is case folded
    [InlineData("ΟΔΟΣ οδος", "οδοσ οδοσ")]                         // Greek, final sigma
    [InlineData("你好 世界", "你好 世界")]                           // CJK runs stay whole words
    [InlineData("Ａｂｃ１", "abc1")]                                // full-width ASCII
    [InlineData("ﬁsh ﬂour", "fish flour")]                         // Latin ligatures
    [InlineData("Crème Brûlée", "creme brulee")]
    [InlineData("école", "ecole")]                           // decomposed accent
    [InlineData("a😀b", "a b")]                                     // emoji separate words
    [InlineData("Zoë's", "zoes")]
    [InlineData("Challah חלה Braided", "challah חלה braided")]     // mixed scripts
    [InlineData("  ---  ", "")]
    [InlineData("ָּ", "")]                                // marks alone
    public void Words_are_unicode_letters_and_digits_lower_cased_without_marks(string text, string expected)
        => Assert.Equal(expected, SearchText.Normalize(text));

    [Fact]
    public void Nothing_and_null_are_empty()
    {
        Assert.Equal("", SearchText.Normalize(null));
        Assert.Equal("", SearchText.Normalize(""));
    }

    [Fact]
    public void A_word_is_cut_at_64_characters_and_never_inside_a_surrogate_pair()
    {
        Assert.Equal(new string('a', 64), SearchText.Normalize(new string('a', 64)));
        Assert.Equal(new string('a', 64), SearchText.Normalize(new string('a', 65)));
        Assert.Equal(new string('a', 64) + " b", SearchText.Normalize(new string('a', 200) + " b"));
        var astral = char.ConvertFromUtf32(0x20000);                                             // a CJK extension B character: two UTF-16 units
        var cut = SearchText.Normalize(new string('a', 63) + astral);                             // 63 + 2 = 65 units: the character does not fit and is dropped whole
        Assert.Equal(new string('a', 63), cut);
        Assert.Equal(new string('a', 62) + astral, SearchText.Normalize(new string('a', 62) + astral));    // 62 + 2 = 64: fits
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }

    [Theory]
    [InlineData(5, "abcde")]
    [InlineData(4, "abcd")]
    public void Clip_cuts_to_the_limit(int max, string expected) => Assert.Equal(expected, SearchText.Clip("abcde", max));

    [Fact]
    public void Clip_never_leaves_half_a_surrogate_pair()
    {
        var s = "ab" + char.ConvertFromUtf32(0x20000) + "cd";                                      // a b hi lo c d
        Assert.Equal("ab", SearchText.Clip(s, 3));                                                // would end on the high surrogate
        Assert.Equal("ab" + char.ConvertFromUtf32(0x20000), SearchText.Clip(s, 4));
    }

    [Fact]
    public void Query_terms_use_the_same_tokenizer()
    {
        Assert.Equal(["שלום", "עולם"], SearchTerms.From("שָׁלוֹם עוֹלָם"));
        Assert.Equal(["привет"], SearchTerms.From("ПРИВЕТ!"));
        Assert.Equal(40, SearchTerms.From(new string('ש', 41))[0].Length);
    }
}

public class CatalogSearchUnicodeTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string L = "/api/v1/catalog/listings";

    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private ListingSnapshot Add(string title, string description = "plain", IReadOnlyList<string>? terms = null)
    {
        var fx = Fixture(_f);
        return fx.Add(Copy(fx.All().First(l => l.Type == "product"), Guid.NewGuid(), title) with { Description = description, Summary = "s", SearchTerms = terms });
    }
    private async Task<List<Guid>> Ids(string q)
        => (await Json(_c, $"{L}?q={Uri.EscapeDataString(q)}&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Pointed_and_unpointed_Hebrew_find_each_other()
    {
        var pointed = Add("גְּבִיעַ קִידּוּשׁ מְהֻדָּר");
        var plain = Add("גביע קידוש פשוט");
        await Sync(_f);
        foreach (var q in new[] { "גביע", "גְּבִיעַ", "קידוש", "קִידּוּשׁ", "גבי" })                       // pointed, unpointed, prefix
        {
            var ids = await Ids(q);
            Assert.Contains(pointed.Id, ids);
            Assert.Contains(plain.Id, ids);
        }
        Assert.Contains(pointed.Id, await Ids("קידוש מהדר"));                                          // two words, all must match
        Assert.DoesNotContain(plain.Id, await Ids("קידוש מהדר"));
    }

    [Fact]
    public async Task Hebrew_works_in_search_terms_descriptions_and_with_a_maqaf()
    {
        var byTerm = Add("Silver Goblet", terms: ["גביע כסף"]);
        var byDescription = Add("Plain Cup", description: "גביע למצוות בר־מצווה");
        await Sync(_f);
        Assert.Contains(byTerm.Id, await Ids("כסף"));
        Assert.Contains(byDescription.Id, await Ids("מצווה"));                                        // the second half of a maqaf word
        Assert.Contains(byDescription.Id, await Ids("בר"));
    }

    [Fact]
    public async Task Mixed_Hebrew_and_English_titles_match_in_either_language()
    {
        var mixed = Add("Challah חלה Braided");
        await Sync(_f);
        Assert.Contains(mixed.Id, await Ids("חלה"));
        Assert.Contains(mixed.Id, await Ids("braided חלה"));
        Assert.Contains(mixed.Id, await Ids("challah"));
    }

    [Fact]
    public async Task Cyrillic_and_Greek_and_CJK_titles_are_searchable()
    {
        var ru = Add("Привет Мир");
        var gr = Add("Ελληνικό Μέλι");
        var zh = Add("你好世界 Mug");
        await Sync(_f);
        Assert.Contains(ru.Id, await Ids("привет"));
        Assert.Contains(ru.Id, await Ids("ПРИВЕТ МИР"));
        Assert.Contains(gr.Id, await Ids("ελληνικο"));                                               // accents are marks: dropped on both sides
        Assert.Contains(zh.Id, await Ids("你好"));                                                    // by the beginning of the run
        Assert.Contains(zh.Id, await Ids("你好世界"));
        Assert.DoesNotContain(ru.Id, await Ids("你好"));
    }

    [Fact]
    public async Task Suggest_completes_Hebrew_prefixes()
    {
        var item = Add("גביע קידוש כסף");
        await Sync(_f);
        var r = await Json(_c, "/api/v1/catalog/suggest?q=" + Uri.EscapeDataString("גבי"));
        Assert.Contains(r.GetProperty("suggestions").EnumerateArray(), s => s.GetProperty("kind").GetString() == "listing" && s.GetProperty("text").GetString() == "גביע קידוש כסף");
    }

    [Fact]
    public async Task A_non_Latin_title_still_gets_an_ASCII_slug_and_a_working_detail_page()
    {
        var item = Add("גביע קידוש");
        await Sync(_f);
        var detail = await Json(_c, $"/api/v1/catalog/listings/{item.Id}");
        var slug = detail.GetProperty("slug").GetString()!;
        Assert.Matches("^[a-z0-9-]+$", slug);                                                          // slugs stay ASCII (URLs)
        Assert.Equal(item.Id, (await Json(_c, $"/api/v1/catalog/listings/{slug}")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_typo_pass_with_a_row_that_has_no_words_does_not_fail()                         // was a 500 on SQLite
    {
        var fx = Fixture(_f);
        fx.Add(Copy(fx.All().First(l => l.Type == "product"), Guid.NewGuid(), "!!!") with { Brand = new BrandRef(Guid.NewGuid(), "???"), SearchTerms = null, Description = "???" });
        await Sync(_f);
        Assert.Contains((await Json(_c, $"{L}?q=chalah")).GetProperty("items").EnumerateArray(), i => i.GetProperty("slug").GetString() == "classic-braided-challah");
        Assert.Equal(0, (await Json(_c, $"{L}?q=zzzzzzzz")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Words_longer_than_the_lexeme_limit_do_not_fail_the_listing()
    {
        var item = Add("Long Word", description: new string('x', 3000) + " tail");
        await Sync(_f);
        Assert.Contains(item.Id, await Ids("tail"));
        Assert.Contains(item.Id, await Ids(new string('x', 40)));                                       // a prefix of the cut word
    }

    // ---------- filters apply to every match, not to the best 1000 (S1) ----------
    [Fact]
    public async Task A_search_with_more_than_a_thousand_matches_still_filters_and_sorts_all_of_them()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var service = fx.All().First(l => l.Type == "service");
        for (var i = 0; i < 1100; i++)           // 1100 strong matches: "widget" in the title
            fx.Add(Copy(template, Guid.NewGuid(), $"Widget number {i}") with { Description = "x", SearchTerms = null, Variants = [new VariantDto(Guid.NewGuid(), "One", 1000 + i, null, true)] });
        var weak = Enumerable.Range(0, 5).Select(i =>      // 5 weak matches: only in the description
            fx.Add(Copy(service, Guid.NewGuid(), $"Service number {i}") with { Description = "we also fix widget problems", SearchTerms = null })).ToList();
        var cheapest = fx.Add(Copy(template, Guid.NewGuid(), "Widget zzz last by title") with { Description = "x", SearchTerms = null, Variants = [new VariantDto(Guid.NewGuid(), "One", 1, null, true)] });
        await Sync(_f);
        var onlyServices = await Json(_c, $"{L}?q=widget&type=service&pageSize=100");
        Assert.Equal(5, onlyServices.GetProperty("total").GetInt32());                                  // the old cap made this 0
        Assert.Equal(weak.Select(w => w.Id).Order(), onlyServices.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
        var all = await Json(_c, $"{L}?q=widget&pageSize=1&sort=price_asc");
        Assert.Equal(1106, all.GetProperty("total").GetInt32());                                         // not 1000
        Assert.Equal(cheapest.Id, all.GetProperty("items")[0].GetProperty("id").GetGuid());             // the cheapest is first, however it ranks
        var facets = (await Json(_c, $"{L}?q=widget&facets=true&pageSize=1")).GetProperty("facets");
        Assert.Equal(1106, facets.GetProperty("brands").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(5, facets.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("value").GetString() == "service").GetProperty("count").GetInt32());
        // walking by cursor through more than a thousand results reaches every one exactly once
        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await Json(_c, $"{L}?q=widget&pageSize=100&sort=price_desc" + (cursor is null ? "" : $"&cursor={cursor}"));
            foreach (var i in page.GetProperty("items").EnumerateArray()) Assert.True(seen.Add(i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(1106, seen.Count);
    }

    // ---------- the id breaks every tie, whatever order rows were inserted in (S6) ----------
    private static Guid Id(int n) => new(0, 0, 0, [0, 0, 0, 0, 0, 0, 0, (byte)n]);

    [Theory]
    [InlineData("sort=relevance&q=tiebreaker")]
    [InlineData("sort=newest")]
    [InlineData("sort=price_asc")]
    [InlineData("sort=price_desc")]
    [InlineData("sort=title")]
    [InlineData("sort=best_selling")]
    public async Task Equal_listings_come_back_in_id_order_not_insertion_order(string query)
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        var at = DateTimeOffset.UtcNow.AddDays(90);
        const int n = 20;     // enough that an unstable sort cannot happen to return them in id order by luck; inserted in DEscending id order, so a stable one returns the wrong order too
        for (var i = n; i >= 1; i--)
            fx.Add(Copy(template, Id(i), "Tiebreaker Twin") with { CreatedAt = at, Description = "x", SearchTerms = null, Variants = [new VariantDto(Guid.NewGuid(), "One", 9_999_999, null, true)] });
        await Sync(_f);
        var expected = Enumerable.Range(1, n).Select(Id).ToList();
        var page = await Json(_c, $"{L}?{query}&pageSize=100");
        var twins = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Where(expected.Contains).ToList();
        Assert.Equal(expected, twins);
        // the same order by cursor, three at a time
        var walked = new List<Guid>();
        string? cursor = null;
        do
        {
            var p = await Json(_c, $"{L}?{query}&pageSize=3" + (cursor is null ? "" : $"&cursor={cursor}"));
            walked.AddRange(p.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Where(expected.Contains));
            cursor = p.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : p.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(expected, walked);
    }

    [Fact]
    public async Task Suggestions_for_equal_matches_are_in_id_order_too()
    {
        var fx = Fixture(_f);
        var template = fx.All().First(l => l.Type == "product");
        for (var i = 20; i >= 1; i--)
            fx.Add(Copy(template, Id(i), "Tiebreaker Twin") with { Description = "x", SearchTerms = null });
        await Sync(_f);
        // rewrite the five smallest ids: PostgreSQL moves a rewritten row to the end of the table, so physical order is no longer id order (EF inserts in key order, which hides a missing tie-break)
        for (var i = 1; i <= 5; i++) fx.Upsert(Id(i), l => l with { Description = "y" });
        await Sync(_f);
        var slugs = (await Json(_c, "/api/v1/catalog/suggest?q=tiebreaker&limit=10")).GetProperty("suggestions").EnumerateArray()
            .Where(s => s.GetProperty("kind").GetString() == "listing").Select(s => s.GetProperty("slug").GetString()!).ToList();
        var idsBySlug = WithDb(_f, db => db.CatalogListings.Where(l => slugs.Contains(l.Slug)).ToDictionary(l => l.Slug, l => l.Id));
        Assert.Equal(Enumerable.Range(1, 10).Select(Id).ToList(), slugs.Select(sl => idsBySlug[sl]).ToList());      // the ten smallest ids, in order: the index breaks score ties by id
    }
}

public class CatalogSearchPostgresTests
{
    [PostgresFact]
    public async Task The_typo_pass_uses_the_trigram_index_and_the_threshold_does_not_leak()
    {
        using var f = new TestFactory();
        await Sync(f);
        using var c = f.CreateClient();
        Assert.Contains((await Json(c, "/api/v1/catalog/listings?q=chalah")).GetProperty("items").EnumerateArray(), i => i.GetProperty("slug").GetString() == "classic-braided-challah");

        var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")!;
        var builder = new NpgsqlConnectionStringBuilder(cs) { Database = WithDb(f, db => db.Database.GetDbConnection().Database) };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        async Task<List<string>> Run(string sql) { await using var cmd = new NpgsqlCommand(sql, conn); await using var r = await cmd.ExecuteReaderAsync(); var rows = new List<string>(); while (await r.ReadAsync()) rows.Add(r.GetString(0)); return rows; }

        // Tiny tables get sequential scans and the real query also joins, so the planner's choice on 43 rows proves nothing. What can be proved: the operator form the
        // query uses is one the trigram index serves (forbid sequential scans for this session and ask for the plan of the bare operator on the indexed column) ...
        await Run("SET enable_seqscan = off");
        await Run("SET pg_trgm.word_similarity_threshold = 0.5");
        var plan = string.Join("\n", await Run("EXPLAIN SELECT listing_id FROM catalog_search s WHERE 'chalah' <% s.text"));
        Assert.Contains("ix_catalog_search_text_trgm", plan);
        // ... and the query uses that operator as its filter; the similarity FUNCTION only scores rows that already passed (it was the filter before, which scanned every row)
        var sql = PostgresSearchIndex.FuzzySql(2);
        Assert.Contains("{0} <% s.text AND {1} <% s.text", sql);
        Assert.DoesNotContain("word_similarity(", sql.Split(" WHERE ")[1].Split(" ORDER BY ")[0]);
        Assert.Contains("word_similarity({0}, s.text)", sql);

        // the app sets the threshold for its own transaction only: a fresh pooled connection still has the default
        await Json(c, "/api/v1/catalog/listings?q=chalah");                       // runs the typo pass (and its transaction) again
        var threshold = WithDb(f, db => db.Database.SqlQueryRaw<string>("SELECT coalesce(current_setting('pg_trgm.word_similarity_threshold', true), 'default') AS \"Value\"").Single());
        Assert.NotEqual("0.5", threshold);                                         // "default" (setting not loaded in this backend) or the server default 0.6: never the app's local 0.5
    }

    [PostgresFact]
    public async Task The_tsvector_does_not_depend_on_the_text_search_parser_so_non_Latin_words_are_indexed()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        fx.Add(Copy(fx.All().First(l => l.Type == "product"), Guid.NewGuid(), "גביע קידוש"));
        await Sync(f);
        var vector = WithDb(f, db => db.Database.SqlQueryRaw<string>("SELECT document::text AS \"Value\" FROM catalog_search WHERE title = 'גביע קידוש'").Single());
        Assert.Contains("'גביע':1A", vector);
        Assert.Contains("'קידוש':2A", vector);
    }
}
