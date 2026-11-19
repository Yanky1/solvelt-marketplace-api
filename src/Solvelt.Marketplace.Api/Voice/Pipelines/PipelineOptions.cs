namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Prices of the pieces of a chained pipeline (USD). All default to 0: nothing is priced until the owner sets them, and a call's estimated cost then counts only what is priced (decision V-19).</summary>
public sealed class PipelinePrices
{
    /// <summary>Per minute of caller audio streamed to speech-to-text.</summary>
    public decimal SttPerMinute { get; set; }
    /// <summary>Per million characters sent to text-to-speech.</summary>
    public decimal TtsPerMillionChars { get; set; }
    public decimal LlmInPerMillionTokens { get; set; }
    public decimal LlmOutPerMillionTokens { get; set; }
}

/// <summary>
/// The <c>Voice:PipelineMenu</c> section (decision V-19): a test-line menu, press 1 to 5 before the AI starts, to compare voice pipelines on real calls. OFF by default, and even when on it is offered only to
/// the numbers in <see cref="TestNumbers"/> (a caller or the dialed test line); every other call is the current line, untouched.
/// </summary>
public sealed class VoicePipelineMenuOptions
{
    public bool Enabled { get; set; }
    /// <summary>E.164 numbers the menu is offered to: the CALLER's number (the owner's own phones) or the DIALED number (a separate test line). Empty means nobody.</summary>
    public List<string> TestNumbers { get; set; } = [];
    /// <summary>Seconds the menu waits for a digit before it falls back to option 1.</summary>
    public int MenuTimeoutSeconds { get; set; } = 6;
    /// <summary>Chained pipelines, the default end-of-turn engine: silence after the caller's last words that ends their turn.</summary>
    public int EndpointMs { get; set; } = 700;
    /// <summary>Option 5: after the first engine's final text, how long to wait for the others.</summary>
    public int ParallelWaitMs { get; set; } = 500;
    /// <summary>Option 5: the HARD budget from the first engine's final text to the text handed on, judge included. When it runs out the fallback is used.</summary>
    public int ParallelBudgetMs { get; set; } = 1200;
    public PipelinePrices Prices { get; set; } = new();
}

/// <summary>The five options of the menu (<c>p1</c> to <c>p5</c>), recorded in the call's tier column. p1 is the current Realtime line.</summary>
public static class VoicePipelineIds
{
    public const string Realtime = "p1";
    public static readonly IReadOnlyList<string> All = ["p1", "p2", "p3", "p4", "p5"];

    /// <summary>The option for the digits typed: "1" to "5"; anything else (empty, other digits, several) is option 1.</summary>
    public static string FromDigits(string? digits) => digits?.Trim() is { Length: 1 } d && d[0] is >= '1' and <= '5' ? "p" + d : Realtime;

    public static bool IsOption(string? tier) => tier is not null && All.Contains(tier);

    /// <summary>A call that was not offered the menu has tier <c>full</c> or <c>mini</c>: it is the Realtime line, option 1.</summary>
    public static string OptionOf(string tier) => IsOption(tier) ? tier : Realtime;

    /// <summary>The Realtime price tier of a call: <c>full</c> or <c>mini</c> as chosen at its start. A call on the menu has tier p1; its model tells which.</summary>
    public static string PriceTier(VoiceOptions o, string tier, string model) => tier is "full" or "mini" ? tier : model == o.OpenAiModelMini ? "mini" : "full";
}

/// <summary>Who is offered the menu.</summary>
public static class PipelineMenu
{
    public static bool Offered(VoiceOptions v, string? from, string? to)
    {
        var m = v.PipelineMenu;
        if (!m.Enabled || m.TestNumbers.Count == 0) return false;
        var listed = m.TestNumbers.Select(Infrastructure.PhoneNumbers.ToE164).Where(n => n is not null).ToHashSet();
        return (Infrastructure.PhoneNumbers.ToE164(from) is { } f && listed.Contains(f)) || (Infrastructure.PhoneNumbers.ToE164(to) is { } t && listed.Contains(t));
    }
}

/// <summary>What the PLATFORM says on the test line (never the model).</summary>
public static class PipelineScripts
{
    public const string Menu = "Test line. Press 1 for the standard assistant, 2 for pipeline two, 3 for pipeline three, 4 for pipeline four, or 5 for pipeline five.";
    public const string Unavailable = "That option is not available on this line. Using option one.";
    public static string Chosen(string option) => "Option " + option[1..] + ".";
    public const string Filler = "One moment, please.";
}
