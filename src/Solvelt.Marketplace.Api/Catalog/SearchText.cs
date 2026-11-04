using System.Globalization;
using System.Text;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// The search index's own tokenizer (O-13). URL slugs stay ASCII (<see cref="Slug"/>), but a Judaica marketplace must find Hebrew, Yiddish, Cyrillic
/// and CJK titles, so search words are <b>Unicode letters and digits</b>, lower-cased, with these rules:
/// <list type="bullet">
/// <item>Combining marks are dropped without splitting the word: Hebrew niqqud and cantillation (U+0591 to U+05C7 marks), Arabic harakat, and the accents of
/// decomposed Latin text. So pointed and unpointed Hebrew ("שָׁלוֹם", "שלום") are the same word, and "é" written as e + accent is "e".</item>
/// <item>Hebrew maqaf (U+05BE), paseq and sof pasuq are separators (a hyphenated word is two words); geresh and gershayim (U+05F3, U+05F4) and apostrophes are dropped
/// (so "צה״ל" and "צהל" match).</item>
/// <item>Latin letters with accents fold to ASCII exactly as in slugs ("Crème" is "creme"); full-width ASCII, Latin ligatures and the Hebrew presentation
/// forms are folded by hand because <c>string.Normalize</c> does nothing under invariant globalization (the app runs with it).</item>
/// <item>Everything else (punctuation, symbols, emoji, spaces) separates words. A word is cut to <see cref="MaxTokenLength"/> characters: PostgreSQL refuses a
/// text-search lexeme above 2046 bytes, and one such word must not fail a whole listing.</item>
/// </list>
/// Chinese and Japanese have no spaces, so a run of characters is one word and matches by its beginning (no word segmentation).
/// The output has only letters and digits and single spaces, so it can never carry query syntax.
/// </summary>
public static class SearchText
{
    public const int MaxTokenLength = 64;

    // Hebrew alphabetic presentation forms (U+FB1D..U+FB4F) that string.Normalize(NFKC) would decompose; the marks of the decomposition are dropped anyway.
    private static readonly Dictionary<int, string> Presentation = new()
    {
        [0xFB1D] = "י", [0xFB1F] = "ײ", [0xFB20] = "ע", [0xFB21] = "א", [0xFB22] = "ד", [0xFB23] = "ה", [0xFB24] = "כ", [0xFB25] = "ל", [0xFB26] = "ם", [0xFB27] = "ר", [0xFB28] = "ת",
        [0xFB2A] = "ש", [0xFB2B] = "ש", [0xFB2C] = "ש", [0xFB2D] = "ש", [0xFB2E] = "א", [0xFB2F] = "א", [0xFB30] = "א", [0xFB31] = "ב", [0xFB32] = "ג", [0xFB33] = "ד", [0xFB34] = "ה",
        [0xFB35] = "ו", [0xFB36] = "ז", [0xFB38] = "ט", [0xFB39] = "י", [0xFB3A] = "ך", [0xFB3B] = "כ", [0xFB3C] = "ל", [0xFB3E] = "מ", [0xFB40] = "נ", [0xFB41] = "ס", [0xFB43] = "ף",
        [0xFB44] = "פ", [0xFB46] = "צ", [0xFB47] = "ק", [0xFB48] = "ר", [0xFB49] = "ש", [0xFB4A] = "ת", [0xFB4B] = "ו", [0xFB4C] = "ב", [0xFB4D] = "כ", [0xFB4E] = "פ", [0xFB4F] = "אל",
        [0xFB00] = "ff", [0xFB01] = "fi", [0xFB02] = "fl", [0xFB03] = "ffi", [0xFB04] = "ffl", [0xFB05] = "st", [0xFB06] = "st",
    };

    /// <summary>The text cut to at most <paramref name="max"/> UTF-16 units without splitting a surrogate pair (a lone surrogate is not valid UTF-8 for PostgreSQL).</summary>
    public static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        var end = max;
        if (end > 0 && char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end].TrimEnd();
    }

    /// <summary>Lower-case words (Unicode letters and digits) separated by single spaces; empty when there are none.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var wordLength = 0;
        var pendingSpace = false;

        void Append(string s)
        {
            if (wordLength + s.Length > MaxTokenLength) { wordLength = MaxTokenLength; return; }       // the rest of an over-long word is ignored (whole characters only: no lone surrogate)
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(s);
            wordLength += s.Length;
        }
        void Break() { if (wordLength > 0) { pendingSpace = true; wordLength = 0; } }

        foreach (var rune in text.EnumerateRunes())
        {
            var r = rune;
            if (r.Value is >= 0xFF01 and <= 0xFF5E) r = new Rune(r.Value - 0xFEE0);          // full-width ASCII
            if (Presentation.TryGetValue(r.Value, out var expansion)) { Append(expansion); continue; }
            if (r.IsBmp && Slug.TryFold((char)r.Value, out var folded)) { Append(folded); continue; }
            var category = Rune.GetUnicodeCategory(r);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (r.Value is '\'' or 0x2019 or 0x05F3 or 0x05F4) continue;                      // apostrophes, geresh, gershayim: inside a word, not between words
            if (Rune.IsLetterOrDigit(r))
            {
                var lower = Rune.ToLowerInvariant(r);
                Append(lower.Value == 0x03C2 ? "σ" : lower.ToString());                      // final sigma folds to sigma
            }
            else Break();
        }
        return sb.ToString();
    }
}
