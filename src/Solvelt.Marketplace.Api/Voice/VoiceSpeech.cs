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

    private static readonly Dictionary<string, string> States = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "the District of Columbia",
        ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana",
        ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska",
        ["NV"] = "Nevada", ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York", ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma",
        ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota", ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont",
        ["VA"] = "Virginia", ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming", ["PR"] = "Puerto Rico",
    };

    public static string State(string? code) => code is not null && States.TryGetValue(code.Trim(), out var n) ? n : (code ?? "");

    /// <summary>Digits one at a time ("11201" is "one one two zero one"), the way a postal code or a phone number is read.</summary>
    public static string Digits(string? text) => string.Join(" ", (text ?? "").Where(char.IsLetterOrDigit).Select(c => char.IsDigit(c) ? Ones[c - '0'] : c.ToString().ToUpperInvariant()));

    /// <summary>A street line as a person says it: a leading house number in pairs ("1245 Main Street" is "twelve forty-five Main Street"), other numbers digit by digit.</summary>
    public static string Street(string? line)
    {
        var words = (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var spoken = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].Trim(',', '.');
            if (w.Length > 0 && w.All(char.IsDigit)) spoken.Add(i == 0 && w.Length <= 4 ? HouseNumber(w) : Digits(w));
            else if (w.Any(char.IsDigit)) spoken.Add(Digits(w));
            else spoken.Add(w);
        }
        return string.Join(" ", spoken);
    }

    private static string HouseNumber(string w) => w.Length switch
    {
        <= 2 => Number(long.Parse(w, CultureInfo.InvariantCulture)),
        3 => Ones[w[0] - '0'] + " " + Pair(w[1..]),
        _ => Pair(w[..2]) + " " + (w[2..] == "00" ? "hundred" : Pair(w[2..])),
    };

    private static string Pair(string two) => two[0] == '0' ? (two[1] == '0' ? "hundred" : "oh " + Ones[two[1] - '0']) : Number(long.Parse(two, CultureInfo.InvariantCulture));

    /// <summary>"Dana Levi, one Main Street, apartment four B, Brooklyn, New York, one one two zero one."</summary>
    public static string Address(Solvelt.Marketplace.Api.Checkout.CheckoutAddress a)
        => string.Join(", ", new[] { Plain(a.Name, 60), Street(Plain(a.Line1, 100)), string.IsNullOrWhiteSpace(a.Line2) ? null : Street(Plain(a.Line2, 60)), Plain(a.City, 60),
            a.Country?.ToUpperInvariant() == "US" ? State(a.Region) : null, Digits(a.PostalCode) }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>The delivery window as a person says it: "Tuesday the fourth to Friday the seventh".</summary>
    public static string DateRange(string earliest, string latest, DateOnly today)
        => DateOnly.TryParse(earliest, CultureInfo.InvariantCulture, out var a) && DateOnly.TryParse(latest, CultureInfo.InvariantCulture, out var b)
            ? (a == b ? Date(a, today) : Date(a, today) + " to " + Date(b, today)) : "";

    /// <summary>At most three options, then an offer of more: "A, B and C, and 4 more if you like".</summary>
    public static string List(IReadOnlyList<string> items, int max = 3)
    {
        if (items.Count == 0) return "";
        var shown = items.Take(max).ToList();
        var s = shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        var more = items.Count - shown.Count;
        return more > 0 ? s + $", and {Number(more)} more if you like" : s;
    }

    /// <summary>Product text made fit for speech: links, markup and symbols removed, whitespace collapsed, cut at a word near <paramref name="max"/> characters.</summary>
    public static string Plain(string? text, int max = 240)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, @"https?://\S+|www\.\S+", " ");
        t = Regex.Replace(t, @"<[^>]*>", " ");
        t = Regex.Replace(t, @"[*_`#<>{}\[\]|\\~^]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length <= max) return t;
        var cut = t.LastIndexOf(' ', max);
        return (cut > max / 2 ? t[..cut] : t[..max]).TrimEnd(',', ';', ':', '-') + ".";
    }

    /// <summary>True when the text is fit to be spoken: no markdown, links, raw ISO dates or symbols a voice would read out.</summary>
    public static bool IsSpeakable(string text) => !Unspeakable().IsMatch(text);

    [GeneratedRegex(@"https?://|www\.|[*_`#<>{}\[\]|\\]|\b\d{4}-\d{2}-\d{2}\b|\$\d|\d+\.\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Unspeakable();
}
