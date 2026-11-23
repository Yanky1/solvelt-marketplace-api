using Solvelt.Marketplace.Api.Catalog;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>How far a recogniser's words are from the reference words (decision V-46): the edits needed, and the length they are counted against.</summary>
public readonly record struct EditCount(int Substitutions, int Deletions, int Insertions, int ReferenceLength)
{
    public int Errors => Substitutions + Deletions + Insertions;
    /// <summary>Errors over reference length; 0 when both are empty. A hypothesis longer than the reference can exceed 1.</summary>
    public double Rate => ReferenceLength == 0 ? (Errors == 0 ? 0 : 1) : (double)Errors / ReferenceLength;
    public static EditCount operator +(EditCount a, EditCount b) => new(a.Substitutions + b.Substitutions, a.Deletions + b.Deletions, a.Insertions + b.Insertions, a.ReferenceLength + b.ReferenceLength);
}

/// <summary>
/// Word and character error rates for the recognition test set. Both sides are normalised the same way first, so only the words matter, not the style: case, punctuation, Hebrew points,
/// the Yiddish digraph letters (as in search, V-21), final letter forms and the way a digit is written ("4" or "four"). A corpus rate is the sum of all errors over the sum of all
/// reference lengths (a long phrase counts for more than a short one), never the mean of per-phrase rates.
/// </summary>
public static class ErrorRates
{
    private static readonly Dictionary<string, string> DigitWords = new(StringComparer.Ordinal)
    {
        ["zero"] = "0", ["oh"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
    };

    /// <summary>The words as they are compared: lower case, letters and digits only, one space between words, final letters as ordinary ones, digit words as digits.</summary>
    public static string Normalize(string? text)
    {
        var folded = SearchText.Normalize(text);
        if (folded.Length == 0) return "";
        var chars = folded.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch { 'ך' => 'כ', 'ם' => 'מ', 'ן' => 'נ', 'ף' => 'פ', 'ץ' => 'צ', var c => c };
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => DigitWords.TryGetValue(w, out var d) ? d : w));
    }

    public static string[] Words(string? text) => Normalize(text) is { Length: > 0 } n ? n.Split(' ') : [];

    public static EditCount Wer(string? reference, string? hypothesis) => Count(Words(reference), Words(hypothesis));

    /// <summary>Characters of the normalised text, spaces included (a missing or extra space is an error, as in the usual definition).</summary>
    public static EditCount Cer(string? reference, string? hypothesis)
        => Count(Normalize(reference).Select(c => c.ToString()).ToArray(), Normalize(hypothesis).Select(c => c.ToString()).ToArray());

    private static EditCount Count(string[] reference, string[] hypothesis)
    {
        var n = reference.Length; var m = hypothesis.Length;
        // distance and, along the best path, the kind of each step (ties prefer a substitution, then a deletion)
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1), Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
        int s = 0, del = 0, ins = 0, a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && d[a, b] == d[a - 1, b - 1] + (reference[a - 1] == hypothesis[b - 1] ? 0 : 1)) { if (reference[a - 1] != hypothesis[b - 1]) s++; a--; b--; }
            else if (a > 0 && d[a, b] == d[a - 1, b] + 1) { del++; a--; }
            else { ins++; b--; }
        }
        return new EditCount(s, del, ins, n);
    }
}
