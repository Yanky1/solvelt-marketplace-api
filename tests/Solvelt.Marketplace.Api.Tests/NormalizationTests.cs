using System.Text;
using Solvelt.Marketplace.Api.Text;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The managed Unicode normaliser (decision M1-81). The vectors in Data/normalization-vectors.txt come from an independent implementation (Python's unicodedata,
/// Unicode 15.1), and tools/NormalizationCheck compares the normaliser with .NET's ICU-backed one where ICU exists; here the app runs in invariant mode, where
/// string.Normalize() does nothing, so none of these tests may lean on it.
/// </summary>
public class NormalizationTests
{
    private sealed record Vector(string Input, string Nfc, string Nfd, bool Single);

    private static string Cps(string hex) => string.Concat(hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16))));

    private static readonly Lazy<List<Vector>> Loaded = new(() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "normalization-vectors.txt"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(';'))
            .Select(p => new Vector(Cps(p[0]), Cps(p[1]), Cps(p[2]), p[0].IndexOf(' ') < 0))
            .ToList());

    private static string Show(string s) => string.Join(" ", s.EnumerateRunes().Select(r => r.Value.ToString("X4")));

    [Fact]
    public void The_tests_run_where_string_Normalize_does_nothing()
    {
        Assert.Equal("é", "é".Normalize());          // invariant globalization: the reason this class exists
    }

    [Fact]
    public void Every_vector_of_the_independent_implementation_is_reproduced_for_NFC_and_NFD()
    {
        var vectors = Loaded.Value;
        Assert.True(vectors.Count > 4000);
        var wrong = vectors.Where(v => UnicodeNormalizer.Nfc(v.Input) != v.Nfc || UnicodeNormalizer.Nfd(v.Input) != v.Nfd).Take(5)
            .Select(v => $"{Show(v.Input)}: NFC {Show(UnicodeNormalizer.Nfc(v.Input))} want {Show(v.Nfc)}, NFD {Show(UnicodeNormalizer.Nfd(v.Input))} want {Show(v.Nfd)}").ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_code_point_normalises_as_the_independent_implementation_says()
    {
        var changed = Loaded.Value.Where(v => v.Single).GroupBy(v => v.Input).ToDictionary(g => g.Key, g => g.First());
        Assert.True(changed.Count > 2000);
        var wrong = new List<string>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF or >= 0xAC00 and <= 0xD7A3) continue;
            var s = char.ConvertFromUtf32(cp);
            var (nfc, nfd) = changed.TryGetValue(s, out var v) ? (v.Nfc, v.Nfd) : (s, s);
            if (UnicodeNormalizer.Nfc(s) != nfc || UnicodeNormalizer.Nfd(s) != nfd) { wrong.Add(cp.ToString("X4")); if (wrong.Count == 5) break; }
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_Hangul_syllable_is_decomposed_and_composed_by_arithmetic()
    {
        for (var s = 0xAC00; s <= 0xD7A3; s++)
        {
            var i = s - 0xAC00;
            var expected = new StringBuilder().Append((char)(0x1100 + i / 588)).Append((char)(0x1161 + i % 588 / 28));
            if (i % 28 != 0) expected.Append((char)(0x11A7 + i % 28));
            var syllable = ((char)s).ToString();
            Assert.Equal(expected.ToString(), UnicodeNormalizer.Nfd(syllable));
            Assert.Equal(syllable, UnicodeNormalizer.Nfc(expected.ToString()));
            Assert.Equal(syllable, UnicodeNormalizer.Nfc(syllable));
        }
        Assert.Equal("각ᄀ", UnicodeNormalizer.Nfc("각ᄀ"));       // LVT, then a lone L stays
        Assert.Equal("\uAC01", UnicodeNormalizer.Nfc("\uAC00\u11A8"));                      // LV + T composes
    }

    [Fact]
    public void The_normal_forms_have_their_defining_properties_on_every_vector()
    {
        foreach (var v in Loaded.Value)
        {
            var c = UnicodeNormalizer.Nfc(v.Input);
            var d = UnicodeNormalizer.Nfd(v.Input);
            Assert.Equal(c, UnicodeNormalizer.Nfc(c));                              // idempotent
            Assert.Equal(d, UnicodeNormalizer.Nfd(d));
            Assert.Equal(d, UnicodeNormalizer.Nfd(c));                              // NFD(NFC(x)) = NFD(x)
            Assert.Equal(c, UnicodeNormalizer.Nfc(d));                              // NFC(NFD(x)) = NFC(x)
            Assert.True(UnicodeNormalizer.IsNfc(c));
            Assert.Equal(v.Input == c, UnicodeNormalizer.IsNfc(v.Input));
        }
    }

    [Theory]
    [InlineData("Å", "Å", "Å")]                                       // ANGSTROM SIGN: a singleton, NFC gives the letter
    [InlineData("Ω", "Ω", "Ω")]                                         // OHM SIGN
    [InlineData("ệ", "ệ", "ệ")]                          // two marks, composed in the right order
    [InlineData("ệ", "ệ", "ệ")]                          // the same marks the other way round: canonical ordering
    [InlineData("ạ́", "ạ́", "ạ́")]                    // only the mark next to it composes; the other is not blocked
    [InlineData("क़", "क़", "क़")]                       // composition exclusion: U+0958 is never produced
    [InlineData("क़", "क़", "क़")]
    [InlineData("̈́", "̈́", "̈́")]                             // a non-starter decomposition that grows under NFC
    [InlineData("ä́", "ä́", "ä́")]                    // an accent after a composed letter stays separate when there is no composite
    [InlineData("ẛ̣", "ẛ̣", "ẛ̣")]                // reordering of marks of different class
    [InlineData("Ǟ", "Ǟ", "Ǟ")]
    [InlineData("Á̀", "Á̀", "Á̀")]                // same class twice: the second one is blocked
    [InlineData("ﬁ", "ﬁ", "ﬁ")]                                         // a compatibility ligature is NOT touched (canonical forms only)
    [InlineData("豈", "豈", "豈")]                                         // a CJK compatibility ideograph has a canonical singleton mapping
    public void Known_cases_of_the_standard(string input, string nfc, string nfd)
    {
        Assert.Equal(nfc, UnicodeNormalizer.Nfc(input));
        Assert.Equal(nfd, UnicodeNormalizer.Nfd(input));
    }

    [Fact]
    public void Text_that_cannot_change_is_returned_as_the_same_object()
    {
        var ascii = new string('a', 1000) + "@example.com";
        var latin1 = "José Müller";
        Assert.Same(ascii, UnicodeNormalizer.Nfc(ascii));
        Assert.Same(ascii, UnicodeNormalizer.Nfd(ascii));
        Assert.Same(latin1, UnicodeNormalizer.Nfc(latin1));                                 // nothing below U+0300 changes under NFC
        Assert.Equal("", UnicodeNormalizer.Nfc(null));
        Assert.Equal("", UnicodeNormalizer.Nfd(""));
        Assert.True(UnicodeNormalizer.IsNfc(null));
    }

    [Fact]
    public void A_lone_surrogate_half_passes_through_and_does_not_stop_the_rest()
    {
        Assert.Equal("a\uD800é", UnicodeNormalizer.Nfc("a\uD800é"));
        Assert.Equal("\uDC00éb", UnicodeNormalizer.Nfc("\uDC00éb"));
        Assert.Equal("é\uD83D", UnicodeNormalizer.Nfd("é\uD83D"));
        Assert.Equal("😀é", UnicodeNormalizer.Nfc("😀é"));           // a valid pair stays whole
    }

    [Fact]
    public void Long_runs_of_marks_are_ordered_and_a_long_text_is_fast_enough()
    {
        var marks = string.Concat(Enumerable.Range(0, 2000).Select(i => i % 2 == 0 ? "̣" : "́"));    // class 220 and 230, alternating
        var sorted = UnicodeNormalizer.Nfd("e" + marks);
        Assert.Equal('e', sorted[0]);
        Assert.True(sorted.Skip(1).Take(1000).All(c => c == '̣') && sorted.Skip(1001).All(c => c == '́'));
        var big = string.Concat(Enumerable.Repeat("José über Café ", 20_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var nfc = UnicodeNormalizer.Nfc(big);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
        Assert.StartsWith("José über Café ", nfc);
    }
}
