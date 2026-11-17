using System.Globalization;
using System.Text.RegularExpressions;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Speech-ready text for tool results (Vapi-level quality, brief): money, dates and lists the way a person says them, never raw digits, ISO dates, markdown or URLs.</summary>
public static partial class VoiceSpeech
{
    private static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    public static string Number(long n)
    {
        if (n < 0) return "minus " + Number(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
        if (n < 1000) return Ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + Number(n % 100));
        if (n < 1_000_000) return Number(n / 1000) + " thousand" + (n % 1000 == 0 ? "" : " " + Number(n % 1000));
        return n.ToString(CultureInfo.InvariantCulture);   // never reached by prices a phone order can have (the limit is far lower)
    }

    /// <summary>1299 cents is "twelve ninety-nine"; 1200 is "twelve dollars"; 5 is "five cents"; 100 is "one dollar".</summary>
    public static string Money(long cents)
    {
        if (cents < 0) return "minus " + Money(-cents);
        var d = cents / 100; var c = cents % 100;
        if (d == 0) return c == 1 ? "one cent" : Number(c) + " cents";
        if (c == 0) return d == 1 ? "one dollar" : Number(d) + " dollars";
        return Number(d) + " " + (c < 10 ? "oh " + Number(c) : Number(c));
    }

    public static string Ordinal(int day) => day switch
    {
        1 => "first", 2 => "second", 3 => "third", 5 => "fifth", 8 => "eighth", 9 => "ninth", 12 => "twelfth", 20 => "twentieth", 30 => "thirtieth",
        < 20 => Number(day) + "th",
        _ => day % 10 == 0 ? Tens[day / 10].TrimEnd('y') + "ieth" : Tens[day / 10] + "-" + Ordinal(day % 10),
    };

    /// <summary>"Tuesday the fourth", and the month when it is not this month: "Tuesday the fourth of December".</summary>
    public static string Date(DateOnly date, DateOnly today)
    {
        var s = $"{date.DayOfWeek} the {Ordinal(date.Day)}";
        if (date.Month != today.Month || date.Year != today.Year) s += " of " + CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month);
        return s;
    }

    /// <summary>An order number such as MP-10492 read digit by digit in groups, so it can be written down: "M P, one zero four, nine two".</summary>
    public static string Spelled(string code)
    {
        var parts = new List<string>();
        foreach (var chunk in code.Split('-', ' ', StringSplitOptions.RemoveEmptyEntries))
            parts.Add(chunk.All(char.IsDigit) ? string.Join(", ", Chunks(chunk).Select(c => string.Join(" ", c.Select(ch => Ones[ch - '0'])))) : string.Join(" ", chunk.ToUpperInvariant().Select(ch => ch.ToString())));
        return string.Join(", ", parts);
    }

    private static IEnumerable<string> Chunks(string digits) { for (var i = 0; i < digits.Length; i += 3) yield return digits.Substring(i, Math.Min(3, digits.Length - i)); }

    /// <summary>At most three options, then an offer of more: "A, B and C, and 4 more if you like".</summary>
    public static string List(IReadOnlyList<string> items, int max = 3)
    {
        if (items.Count == 0) return "";
        var shown = items.Take(max).ToList();
        var s = shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        var more = items.Count - shown.Count;
        return more > 0 ? s + $", and {Number(more)} more if you like" : s;
    }

    /// <summary>True when the text is fit to be spoken: no markdown, links, raw ISO dates or symbols a voice would read out.</summary>
    public static bool IsSpeakable(string text) => !Unspeakable().IsMatch(text);

    [GeneratedRegex(@"https?://|www\.|[*_`#<>{}\[\]|\\]|\b\d{4}-\d{2}-\d{2}\b|\$\d|\d+\.\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Unspeakable();
}
