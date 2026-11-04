using System.Text;

namespace Solvelt.Marketplace.Api.Catalog;

public static class Slug
{
    // Accents are folded with a table, not Unicode normalisation: the app runs with invariant globalization, where string.Normalize does not decompose.
    private static readonly Dictionary<char, string> Fold = BuildFold();

    private static Dictionary<char, string> BuildFold()
    {
        var map = new Dictionary<char, string>();
        void Add(string chars, string to) { foreach (var c in chars) { map[c] = to; map[char.ToUpperInvariant(c)] = to; } }
        Add("àáâãäåāăą", "a"); Add("çćĉċč", "c"); Add("ďđ", "d"); Add("èéêëēĕėęě", "e"); Add("ĝğġģ", "g"); Add("ĥħ", "h");
        Add("ìíîïĩīĭįı", "i"); Add("ĵ", "j"); Add("ķ", "k"); Add("ĺļľŀł", "l"); Add("ñńņň", "n"); Add("òóôõöøōŏő", "o");
        Add("ŕŗř", "r"); Add("śŝşš", "s"); Add("ţťŧ", "t"); Add("ùúûüũūŭůűų", "u"); Add("ŵ", "w"); Add("ýÿŷ", "y"); Add("źżž", "z");
        map['ß'] = "ss"; map['æ'] = "ae"; map['Æ'] = "ae"; map['œ'] = "oe"; map['Œ'] = "oe";
        return map;
    }

    /// <summary>Lower-case ASCII words joined by single hyphens; common Latin accents are folded ("Zoë" becomes "zoe"); at most 100 characters; never empty.</summary>
    public static string From(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append(char.ToLowerInvariant(c));
            else if (Fold.TryGetValue(c, out var folded)) sb.Append(folded);
            else if (c is '\'' or '\u2019') continue;   // possessives: "Zoë's" is "zoes"
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 100) slug = slug[..100].TrimEnd('-');
        return slug.Length == 0 ? "listing" : slug;
    }
}
