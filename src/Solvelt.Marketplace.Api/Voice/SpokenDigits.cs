using System.Text;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// What a caller SAYS for digits (decision V-41): the verification code and the choice on the test-line menu are spoken to SignalWire's own recogniser, never typed (the keypad is for the PIN and the card only). This
/// turns SignalWire's text (digits, "4 7 2", or words such as "four seven two", with the usual sound-alikes) into digits, and writes digits back as words for the read-back. Pure text work: no logging, no state; the
/// result of <see cref="Parse"/> for a verification code is held only long enough to be checked and is never logged, stored or given to a model.
/// </summary>
public static class SpokenDigits
{
    private static readonly Dictionary<string, char> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["oh"] = '0', ["o"] = '0', ["nought"] = '0', ["one"] = '1', ["won"] = '1', ["two"] = '2', ["to"] = '2', ["too"] = '2', ["three"] = '3', ["tree"] = '3',
        ["four"] = '4', ["for"] = '4', ["fore"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["ate"] = '8', ["nine"] = '9', ["niner"] = '9',
    };

    /// <summary>The words SignalWire is asked to favour while listening for digits (the <c>hints</c> of the Gather). Never the code itself.</summary>
    public const string Hints = "zero, oh, one, two, three, four, five, six, seven, eight, nine";

    /// <summary>The digits in what was said, in order; everything else is ignored. At most 32 digits.</summary>
    public static string Parse(string? speech)
    {
        var sb = new StringBuilder();
        foreach (var token in Tokens(speech))
        {
            if (token.All(char.IsAsciiDigit)) sb.Append(token);
            else if (Words.TryGetValue(token, out var d)) sb.Append(d);
            if (sb.Length >= 32) return sb.ToString(0, 32);
        }
        return sb.ToString();
    }

    /// <summary>The menu choice in what was said: the first digit or digit word (<c>1</c> to <c>9</c>), or <c>*</c> for "star", "again" or "repeat". Null when nothing was understood.</summary>
    public static string? Choice(string? speech)
    {
        foreach (var token in Tokens(speech))
        {
            if (token.Equals("star", StringComparison.OrdinalIgnoreCase) || token.Equals("again", StringComparison.OrdinalIgnoreCase) || token.Equals("repeat", StringComparison.OrdinalIgnoreCase) || token.Equals("asterisk", StringComparison.OrdinalIgnoreCase)) return "*";
            if (token.Length == 1 && token[0] is >= '1' and <= '9') return token;
            if (Words.TryGetValue(token, out var d) && d != '0') return d.ToString();
        }
        return null;
    }

    /// <summary>Digits as something to read out: "4, 7, 2" (each digit a word-sized beat for the voice).</summary>
    public static string ReadOut(string digits) => string.Join(", ", digits.Where(char.IsAsciiDigit).Take(12));

    private static IEnumerable<string> Tokens(string? s)
    {
        var sb = new StringBuilder();
        foreach (var ch in (s ?? "") + " ")
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
    }
}
