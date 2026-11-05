using System.Text;

namespace Solvelt.Marketplace.Api.Text;

/// <summary>
/// Unicode normalisation forms C and D (UAX #15) in managed code (decision M1-81). The application runs with <c>InvariantGlobalization</c>, where
/// <c>string.Normalize()</c> silently does nothing, so an email, a password or a name typed with a combining accent and the same text typed with the
/// precomposed letter would be two different values. The data comes from <see cref="UnicodeNormalizationData"/> (generated, Unicode 15.1, the version of
/// ICU 74 and the .NET runtime); <c>tools/NormalizationCheck</c> compares this class with .NET's ICU-backed normaliser over every code point and a large
/// random sample, and the test vectors were made by an independent implementation (Python's unicodedata).
/// Reusable by every module (search tokens use <see cref="Nfd"/>). Lone surrogates pass through unchanged; the input is never rejected.
/// </summary>
public static class UnicodeNormalizer
{
    // Hangul syllable arithmetic (Unicode standard, section 3.12)
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7, LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>Nothing below U+0300 changes under NFC (no composition has such a second character and no such character decomposes in a way NFC undoes).</summary>
    private const char NfcQuickLimit = (char)0x0300;
    /// <summary>The first character that decomposes is U+00C0.</summary>
    private const char NfdQuickLimit = (char)0x00C0;

    public static string Nfc(string? text)
    {
        if (string.IsNullOrEmpty(text) || AllBelow(text, NfcQuickLimit)) return text ?? "";
        return Compose(Decompose(text));
    }

    public static string Nfd(string? text)
    {
        if (string.IsNullOrEmpty(text) || AllBelow(text, NfdQuickLimit)) return text ?? "";
        return ToString(Decompose(text));
    }

    public static bool IsNfc(string? text) => string.IsNullOrEmpty(text) || AllBelow(text, NfcQuickLimit) || string.Equals(text, Nfc(text), StringComparison.Ordinal);

    private static bool AllBelow(string text, char limit)
    {
        foreach (var c in text) if (c >= limit) return false;
        return true;
    }

    // ---------- tables ----------

    public static int CombiningClass(int cp)
    {
        if (cp < 0x300) return 0;
        var r = UnicodeNormalizationData.CombiningClassRanges;
        int lo = 0, hi = r.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var start = r[mid * 3];
            if (cp < start) hi = mid - 1;
            else if (cp > r[mid * 3 + 1]) lo = mid + 1;
            else return r[mid * 3 + 2];
        }
        return 0;
    }

    private static int FindDecomposition(int cp) => cp < 0xC0 ? -1 : Array.BinarySearch(UnicodeNormalizationData.DecompositionKeys, cp) is var i and >= 0 ? i : -1;

    private static int TryCompose(int first, int second)
    {
        if (first is >= LBase and < LBase + LCount && second is >= VBase and < VBase + VCount)   // L + V
            return SBase + ((first - LBase) * VCount + (second - VBase)) * TCount;
        if (first is >= SBase and < SBase + SCount && (first - SBase) % TCount == 0 && second is > TBase and < TBase + TCount)   // LV + T
            return first + (second - TBase);
        var i = Array.BinarySearch(UnicodeNormalizationData.CompositionPairs, ((long)first << 21) | (uint)second);
        return i >= 0 ? UnicodeNormalizationData.CompositionResults[i] : -1;
    }

    // ---------- decomposition and canonical ordering ----------

    private static List<int> Decompose(string text)
    {
        var cps = new List<int>(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
            AppendDecomposed(cps, cp);
        }
        // canonical ordering: a run of non-starters is sorted by combining class, stably (insertion sort: runs are short)
        for (var i = 1; i < cps.Count; i++)
        {
            var c = CombiningClass(cps[i]);
            if (c == 0) continue;
            var j = i;
            while (j > 0 && CombiningClass(cps[j - 1]) is var p && p > c) { (cps[j], cps[j - 1]) = (cps[j - 1], cps[j]); j--; }
        }
        return cps;
    }

    private static void AppendDecomposed(List<int> output, int cp)
    {
        if (cp is >= SBase and < SBase + SCount)
        {
            var s = cp - SBase;
            output.Add(LBase + s / NCount);
            output.Add(VBase + s % NCount / TCount);
            if (s % TCount != 0) output.Add(TBase + s % TCount);
            return;
        }
        var d = FindDecomposition(cp);
        if (d < 0) { output.Add(cp); return; }
        var start = UnicodeNormalizationData.DecompositionStart;
        for (var k = start[d]; k < start[d + 1]; k++) output.Add(UnicodeNormalizationData.DecompositionData[k]);   // the table holds the FULL decomposition
    }

    // ---------- composition ----------

    private static string Compose(List<int> cps)
    {
        if (cps.Count == 0) return "";
        var result = new List<int>(cps.Count);
        var starter = -1;          // index in result of the last starter
        var lastClass = -1;        // combining class of the last character appended after that starter (0 when it is the starter itself)
        foreach (var cp in cps)
        {
            var cc = CombiningClass(cp);
            if (starter >= 0)
            {
                // not blocked: nothing between the starter and this character, or only characters of a lower combining class
                if (lastClass < cc || lastClass == 0 && result.Count - 1 == starter)
                {
                    var composite = TryCompose(result[starter], cp);
                    if (composite >= 0) { result[starter] = composite; continue; }
                }
            }
            if (cc == 0) { starter = result.Count; lastClass = 0; }
            else lastClass = cc;
            result.Add(cp);
        }
        return ToString(result);
    }

    private static string ToString(List<int> cps)
    {
        var sb = new StringBuilder(cps.Count + 4);
        foreach (var cp in cps)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) sb.Append((char)cp);          // a lone surrogate half, kept as it came
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }
}
