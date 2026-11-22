using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The shared recogniser vocabulary (decision V-36): built from the live catalog, bounded, plain text, shared by every recogniser. Option 6's Gather hints are the first user.</summary>
public sealed class VoiceVocabularyTests
{
    private static readonly (string, int)[] Brands = [("Challah Co", 9), ("Zabar's", 5), ("Bagel & Bean", 5), ("A", 50)];
    private static readonly (string, int)[] Cats = [("Bakery", 12), ("Dairy", 4)];

    [Fact]
    public void The_list_is_brands_first_then_categories_then_the_most_common_title_words_without_duplicates_noise_or_numbers()
    {
        var titles = new[] { "Honey Challah Loaf 12 oz", "Honey Challah Roll", "Sesame Bagel (6 pack)", "The Bakery Challah" };
        var terms = CatalogVocabulary.Build(Brands, Cats, titles, 100);
        Assert.Equal(["Challah Co", "Bagel & Bean", "Zabar's"], terms.Take(3));                 // brands by listing count, then by name; the one-letter brand is noise
        Assert.Contains("Bakery", terms); Assert.Contains("Dairy", terms);
        Assert.True(terms.ToList().IndexOf("Bakery") > terms.ToList().IndexOf("Zabar's"));
        Assert.Contains("Challah", terms); Assert.Contains("Honey", terms);
        Assert.DoesNotContain("12", terms); Assert.DoesNotContain("oz", terms); Assert.DoesNotContain("The", terms);
        Assert.Equal(terms.Count, terms.Select(t => t.ToLowerInvariant()).Distinct().Count());   // "Bakery" (a category) is not repeated as a title word
        Assert.True(terms.ToList().IndexOf("Challah") < terms.ToList().IndexOf("Sesame"));       // the more common word first
    }

    [Fact]
    public void The_list_is_capped_and_every_term_is_plain_text_of_bounded_length()
    {
        var many = Enumerable.Range(0, 400).Select(i => ($"Brand{i} x, \"quoted\"\n{new string('z', 80)}", 400 - i)).ToArray();
        var terms = CatalogVocabulary.Build(many, [], [], 30);
        Assert.True(terms.Count <= 30);
        Assert.All(terms, t => { Assert.True(t.Length <= 40); Assert.DoesNotContain(',', t); Assert.DoesNotContain('"', t); Assert.DoesNotContain('\n', t); });
        Assert.Empty(CatalogVocabulary.Build([], [], [], 0));
    }

    [Fact]
    public void Hints_put_the_static_words_first_add_catalog_words_whole_and_never_pass_the_limit()
    {
        var terms = new[] { "Challah Co", "Bakery", "Dairy", "Zabar's" };
        Assert.Equal("kosher, Challah Co, Bakery, Dairy, Zabar's", CatalogVocabulary.Join("kosher", terms, 500));
        Assert.Equal("kosher, Challah Co, Bakery", CatalogVocabulary.Join("kosher", terms, 26));   // whole terms only, in order
        var cut = CatalogVocabulary.Join("kosher", terms, 25);
        Assert.True(cut.Length <= 25); Assert.StartsWith("kosher", cut);
        Assert.Equal("kosher, Challah Co, Dairy", cut);                                           // "Bakery" no longer fits (one character over): it is skipped whole and the shorter "Dairy" still fits
        Assert.Equal("Challah Co, Bakery", CatalogVocabulary.Join("Challah Co, Bakery", terms, 500)[..18]);   // a static word is not repeated by the catalog
        Assert.Equal("", CatalogVocabulary.Join(null, [], 500));
        var longStatic = CatalogVocabulary.Join(new string('s', 600), terms, 500);
        Assert.True(longStatic.Length <= 500);
    }

    [Fact]
    public async Task The_provider_reads_the_visible_catalog_and_option_six_sends_its_words_as_gather_hints_within_the_limit()
    {
        using var s = new VoiceScenario("+15551234567", VoicePipelineMenuTests.MenuConfig(["+15551234567"], new() { ["Voice:SignalWireSpeech:Hints"] = "yes, no" }),
            services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p6")));
        await s.SyncCatalog();
        var vocab = s.F.Services.GetRequiredService<CatalogVocabulary>();
        Assert.Empty(vocab.Terms);                                                                // nothing before the first refresh
        await vocab.RefreshAsync(CancellationToken.None);
        Assert.NotEmpty(vocab.Terms);
        string brand;
        using (var scope = s.F.Services.CreateScope()) brand = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().CatalogBrands.Select(b => b.Name).OrderBy(n => n).FirstAsync();
        Assert.Contains(vocab.Terms, t => t == CatalogVocabulary.Clean(brand));

        (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From)).EnsureSuccessStatusCode();
        await VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, s.From, extra: new() { ["Digits"] = "6" });
        var g = XDocument.Parse(await (await VoiceFactory.Hook(s.C, VoiceEndpoints.SpeechPath + "?first=1", s.Sid, s.From)).Content.ReadAsStringAsync()).Root!.Element("Gather")!;
        var hints = g.Attribute("hints")!.Value;
        Assert.StartsWith("yes, no", hints);                                                      // the configured words first
        Assert.Contains(CatalogVocabulary.Clean(brand), hints);                                   // then the catalog's
        Assert.True(hints.Length <= SignalWireSpeechOptions.MaxHintsChars);
    }

    [Fact]
    public async Task Switched_off_the_provider_gives_no_terms_and_the_gather_keeps_only_the_static_hints()
    {
        using var s = new VoiceScenario("+15551234567", VoicePipelineMenuTests.MenuConfig(["+15551234567"], new() { ["Voice:Vocabulary:Enabled"] = "false", ["Voice:SignalWireSpeech:Hints"] = "yes, no" }),
            services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p6")));
        await s.SyncCatalog();
        var vocab = s.F.Services.GetRequiredService<CatalogVocabulary>();
        await vocab.RefreshAsync(CancellationToken.None);
        Assert.Empty(vocab.Terms);
        (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From)).EnsureSuccessStatusCode();
        await VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, s.From, extra: new() { ["Digits"] = "6" });
        var g = XDocument.Parse(await (await VoiceFactory.Hook(s.C, VoiceEndpoints.SpeechPath + "?first=1", s.Sid, s.From)).Content.ReadAsStringAsync()).Root!.Element("Gather")!;
        Assert.Equal("yes, no", g.Attribute("hints")!.Value);
    }
}
