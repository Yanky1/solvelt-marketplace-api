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
    /// <summary>What the menu says about each option (<c>Voice:PipelineMenu:Descriptions:P1</c> to <c>P5</c>); an empty entry means the built-in wording. The only place the wording is set.</summary>
    public PipelineDescriptions Descriptions { get; set; } = new();
}

/// <summary>
/// The spoken description of each option on the test-line menu: a short name and one plain sentence, read after "Press N for". Edited in configuration (decision V-32); an empty entry means the default below, so
/// nothing has to be set. Plain text only: it is spoken as it is written, and what a description says is the owner's, never a model's.
/// </summary>
public sealed class PipelineDescriptions
{
    public string P1 { get; set; } = "";
    public string P2 { get; set; } = "";
    public string P3 { get; set; } = "";
    public string P4 { get; set; } = "";
    public string P5 { get; set; } = "";

    public const int MaxLength = 300;

    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["p1"] = "OpenAI live voice. One model hears and speaks directly. The fastest and most natural, English first.",
        ["p2"] = "Deepgram and ElevenLabs. Separate speech recognition and voice, with an AI deciding. The English comparison.",
        ["p3"] = "Gladia and ElevenLabs. Live speech recognition that understands Yiddish, with an English voice.",
        ["p4"] = "Yiddish Labs. Yiddish speech recognition and a Yiddish voice.",
        ["p5"] = "Two engines and a judge. Gladia and Yiddish Labs listen together, and an AI picks what you most likely said.",
    };

    private string? Configured(string option) => option switch { "p1" => P1, "p2" => P2, "p3" => P3, "p4" => P4, "p5" => P5, _ => null };

    /// <summary>The words for the option: the configured text, else the default.</summary>
    public string For(string option) => Configured(option) is { } c && !string.IsNullOrWhiteSpace(c) ? c.Trim() : Defaults.GetValueOrDefault(option, "");

    /// <summary>The entries that are too long to be one plain sentence on a phone (a start-up problem when the menu is on).</summary>
    public IEnumerable<string> TooLong() => VoicePipelineIds.All.Where(o => Configured(o)?.Trim().Length > MaxLength).Select(o => "Voice:PipelineMenu:Descriptions:" + o.ToUpperInvariant());
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
    /// <summary>What the PLATFORM says (never the model, and not through the failing vendor) when a chained option's vendor fails during the call (decision V-25).</summary>
    public const string VendorLost = "Sorry, we are having trouble with the line right now. Please call back in a moment.";

    /// <summary>The menu as it is spoken when only option 1 exists and the wording is the default (what a test line with no pipeline configured says).</summary>
    public static string Menu { get; } = Compose(new VoicePipelineMenuOptions(), ["p1"]);

    /// <summary>
    /// The spoken menu: "Test line." then, for each AVAILABLE option in order, "Press N for" and its description; options that are not available are not mentioned at all. With more than one option the caller is also told
    /// that star repeats the list. The digit can be pressed at any time during it (the Gather takes it while the text plays).
    /// </summary>
    public static string Compose(VoicePipelineMenuOptions m, IReadOnlyList<string> available)
    {
        var parts = new List<string> { "Test line." };
        foreach (var o in available) parts.Add($"Press {o[1..]} for {EndWithStop(m.Descriptions.For(o))}");
        if (available.Count > 1) parts.Add("Press star to hear this again.");
        return string.Join(" ", parts);
    }

    private static string EndWithStop(string text) => text.Length == 0 || text[^1] is '.' or '!' or '?' ? text : text + ".";
    public const string ConsentDeclined = "Okay. Using the standard assistant.";
    public const string Unavailable = "That option is not available on this line. Using option one.";
    public static string Chosen(string option) => "Option " + option[1..] + ".";
    public const string Filler = "One moment, please.";
    /// <summary>The platform's own words (never the model's, never raw) when the text model refuses to answer, or when its answer was cut by the token limit (decision V-33).</summary>
    public const string ModelRefused = "Sorry, I can't help with that.";
    public const string ModelRanOut = "Sorry, I lost my train of thought. Could you say that again?";
}
