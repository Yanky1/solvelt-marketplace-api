namespace Solvelt.Marketplace.Api.Infrastructure;

public static class PhoneNumbers
{
    /// <summary>E.164 for a number as people write it. Ten digits, or eleven starting with 1, are US/Canada (our default market; the code takes no other assumption); anything with a leading + keeps its country. Null when it is not a phone number.</summary>
    public static string? ToE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var plus = raw.TrimStart().StartsWith('+');
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (plus) return d.Length is >= 8 and <= 15 ? "+" + d : null;
        if (d.Length == 10) return "+1" + d;
        if (d.Length == 11 && d[0] == '1') return "+" + d;
        return null;
    }
}
