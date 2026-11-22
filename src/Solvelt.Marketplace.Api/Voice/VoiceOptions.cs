using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Per-million-token prices in USD for one model (defaults: OpenAI's published prices, V-01; override in configuration when they change).</summary>
public sealed class VoicePrices
{
    public decimal AudioIn { get; set; }
    public decimal AudioOut { get; set; }
    public decimal TextIn { get; set; }
    public decimal TextOut { get; set; }
}

/// <summary>The <c>Voice</c> section (decision V-01). Disabled by default: nothing in the voice module answers unless <see cref="Enabled"/> is true.</summary>
public sealed class VoiceOptions
{
    public bool Enabled { get; set; }
    /// <summary>OpenAI key. Secret: environment only. Never logged, never sent anywhere but api.openai.com.</summary>
    public string? OpenAiApiKey { get; set; }
    /// <summary>Full real-time model id (V-01: gpt-realtime-2.1; <c>gpt-realtime</c> and <c>gpt-realtime-mini</c> are marked deprecated by OpenAI). Configuration, so a new id needs no release.</summary>
    public string OpenAiModel { get; set; } = "gpt-realtime-2.1";
    public string OpenAiModelMini { get; set; } = "gpt-realtime-2.1-mini";
    /// <summary><c>full</c>, <c>mini</c> or <c>split</c>.</summary>
    public string ModelMode { get; set; } = "full";
    /// <summary>Share of calls (0-100) that go to mini in <c>split</c> mode.</summary>
    public int MiniPercent { get; set; } = 50;
    public string OpenAiVoice { get; set; } = "cedar";
    /// <summary><c>server_vad</c> or <c>semantic_vad</c>.</summary>
    public string TurnDetection { get; set; } = "semantic_vad";
    /// <summary><c>eagerness</c> of <c>semantic_vad</c>: <c>low</c>, <c>medium</c>, <c>high</c> or <c>auto</c> (= medium). OpenAI: "low will wait longer for the user to continue speaking"; max timeouts 8 s, 4 s, 2 s for low, medium, high. <c>auto</c> by default and then NOT SENT: that is today's line exactly (the API default, equal to medium), because option 1 is the unchanged benchmark (V-37). Try <c>low</c> if callers are cut off mid-thought. Ignored with server_vad.</summary>
    public string Eagerness { get; set; } = "auto";
    /// <summary>Input noise reduction of the Realtime line: <c>near_field</c>, <c>far_field</c> or <c>none</c>. OpenAI says near_field is "for close-talking microphones such as headphones" and does not say which suits a phone call: near_field would be OUR INFERENCE (a handset sits close to the mouth). <c>none</c> by default and then NOT SENT: option 1 is the unchanged benchmark (V-37). Try near_field if noise triggers turns.</summary>
    public string NoiseReduction { get; set; } = "none";
    /// <summary>A tool slower than this many milliseconds makes the agent say a short natural filler first: never silence.</summary>
    public int ToolFillerMs { get; set; } = 700;
    public string OpenAiRealtimeUrl { get; set; } = "wss://api.openai.com/v1/realtime";
    /// <summary>USD per million tokens, from OpenAI's model pages on 2026-11-17 (V-01). Cached input is priced lower; the estimate charges it at the full rate (it errs high).</summary>
    public VoicePrices PricePerMillionFull { get; set; } = new() { AudioIn = 32m, AudioOut = 64m, TextIn = 4m, TextOut = 24m };
    public VoicePrices PricePerMillionMini { get; set; } = new() { AudioIn = 10m, AudioOut = 20m, TextIn = 0.6m, TextOut = 2.4m };
    /// <summary>Human support line (E.164). Empty: transfer is never offered, the caller is told to use the web site.</summary>
    public string? SupportNumber { get; set; }
    /// <summary>Support hours, <c>HH:mm-HH:mm</c> in <see cref="SupportTimeZone"/>, optionally prefixed by days (<c>Mon-Fri 09:00-17:00</c>). Empty: always.</summary>
    public string? SupportHours { get; set; }
    public string SupportTimeZone { get; set; } = "America/New_York";
    public int MaxOrderAmount { get; set; } = 500;
    public int MaxItems { get; set; } = 20;
    public int MaxOrdersPerDay { get; set; } = 3;
    /// <summary>A call is ended after this many seconds (per-call limit).</summary>
    public int MaxCallSeconds { get; set; } = 900;
    /// <summary>The public https origin SignalWire reaches us on (no path). The signed URL must equal what SignalWire called, so it is configuration, never taken from request headers.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Key of the short-lived stream tokens. Its own key (never SignalWire:SigningKey or any other); development generates a random one per process.</summary>
    public string? StreamTokenKey { get; set; }
    public int StreamTokenSeconds { get; set; } = 120;
    /// <summary>Key of the SMS code hashes (a purpose of its own: never the stream token key or the SignalWire signing key).</summary>
    public string? PhoneCodeKey { get; set; }
    /// <summary>Key of the caller-number hash used to count phone orders per number (a purpose of its own).</summary>
    public string? CallerHashKey { get; set; }
    /// <summary><c>none</c> (default: nobody can pay by phone) or <c>fake</c> (development and tests only). A real provider (Sola) is chosen here once it exists; real-money phone orders stay off until then.</summary>
    public string PhonePaymentProvider { get; set; } = "none";
    public int MaxPaymentAttempts { get; set; } = 3;
    /// <summary>Off-topic requests (the <c>off_topic</c> tool) a call may have before the SERVER ends the AI part of it (V2, decision V-11). 1 to 10; per call; nothing resets it.</summary>
    public int MaxOffTopicTurns { get; set; } = 3;
    public int MaxSmsPerNumberPerHour { get; set; } = 3;
    public int MaxSmsPerHour { get; set; } = 300;
    /// <summary>Days a call's metrics row is kept (no audio, no transcript, only a masked number).</summary>
    public int CallRetentionDays { get; set; } = 400;
    /// <summary>The test-line menu to compare voice pipelines (decision V-19). Off by default.</summary>
    public VoicePipelineMenuOptions PipelineMenu { get; set; } = new();
    /// <summary>Which vendors run options p2 to p5 and the text model they share (decision V-22). Nothing is on by default.</summary>
    public VoicePipelinesOptions Pipelines { get; set; } = new();
    /// <summary>The Claude key for the chained options and the judge (decision V-33).</summary>
    public AnthropicVoiceOptions Anthropic { get; set; } = new();
    public DeepgramOptions Deepgram { get; set; } = new();
    public ElevenLabsOptions ElevenLabs { get; set; } = new();
    public GladiaOptions Gladia { get; set; } = new();
    public YiddishLabsOptions YiddishLabs { get; set; } = new();
    /// <summary>Option 6: SignalWire's own speech recognition, one <c>Gather</c> per turn (decision V-35).</summary>
    public SignalWireSpeechOptions SignalWireSpeech { get; set; } = new();
    /// <summary>The catalog words every recogniser is told to listen for (decision V-36).</summary>
    public VocabularyOptions Vocabulary { get; set; } = new();
    /// <summary>Recognised words of the owner's own test calls (decision V-39). Off by default.</summary>
    public DiagnosticsOptions Diagnostics { get; set; } = new();
    /// <summary>The recogniser's alternatives for a turn, given to the text model (decision V-42).</summary>
    public NBestOptions NBest { get; set; } = new();
}

public sealed class SignalWireOptions
{
    public string? ProjectId { get; set; }
    public string? ApiToken { get; set; }
    public string? Space { get; set; }
    /// <summary>Verifies <c>X-SignalWire-Signature</c> (the project's signing key). Secret.</summary>
    public string? SigningKey { get; set; }
    public string? FromNumber { get; set; }
    /// <summary>Development and the journey only: where SMS goes instead of <c>https://{Space}.signalwire.com</c> (a sink that lets a scripted call read its code). Refused in production.</summary>
    public string? ApiBaseUrl { get; set; }
}

public static class VoiceConfig
{
    public static void Bind(VoiceOptions o, IConfiguration c, bool devLike)
    {
        c.GetSection("Voice").Bind(o);
        if (string.IsNullOrWhiteSpace(o.StreamTokenKey) && devLike) o.StreamTokenKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (string.IsNullOrWhiteSpace(o.PhoneCodeKey) && devLike) o.PhoneCodeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (string.IsNullOrWhiteSpace(o.CallerHashKey) && devLike) o.CallerHashKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Start-up problems; only checked when the module is enabled.</summary>
    public static IReadOnlyList<string> Problems(VoiceOptions v, SignalWireOptions s, bool devLike, SolaOptions? sola = null)
    {
        var p = new List<string>();
        if (!v.Enabled) return p;
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey)) p.Add("Voice:OpenAiApiKey (env Voice__OpenAiApiKey) is required when Voice:Enabled.");
        if (string.IsNullOrWhiteSpace(s.SigningKey)) p.Add("SignalWire:SigningKey (env SignalWire__SigningKey) is required when Voice:Enabled.");
        if (string.IsNullOrWhiteSpace(v.StreamTokenKey) || v.StreamTokenKey.Length < 32) p.Add("Voice:StreamTokenKey (env Voice__StreamTokenKey) must be at least 32 characters when Voice:Enabled.");
        else if (!string.IsNullOrEmpty(s.SigningKey) && v.StreamTokenKey == s.SigningKey) p.Add("Voice:StreamTokenKey must differ from SignalWire:SigningKey (one key per purpose).");
        if (string.IsNullOrWhiteSpace(v.PhoneCodeKey) || v.PhoneCodeKey.Length < 32) p.Add("Voice:PhoneCodeKey (env Voice__PhoneCodeKey) must be at least 32 characters when Voice:Enabled.");
        else if (v.PhoneCodeKey == v.StreamTokenKey || v.PhoneCodeKey == s.SigningKey || v.PhoneCodeKey == s.ApiToken) p.Add("Voice:PhoneCodeKey must differ from the stream token key and every SignalWire secret (one key per purpose).");
        if (string.IsNullOrWhiteSpace(v.CallerHashKey) || v.CallerHashKey.Length < 32) p.Add("Voice:CallerHashKey (env Voice__CallerHashKey) must be at least 32 characters when Voice:Enabled.");
        else if (v.CallerHashKey == v.PhoneCodeKey || v.CallerHashKey == v.StreamTokenKey || v.CallerHashKey == s.SigningKey || v.CallerHashKey == s.ApiToken) p.Add("Voice:CallerHashKey must differ from every other key (one key per purpose).");
        if (!new[] { "none", "fake", "sola" }.Contains(v.PhonePaymentProvider)) p.Add("Voice:PhonePaymentProvider must be none, fake or sola.");
        // nothing real moves money by phone yet: the fake and the Sola SANDBOX are for development and tests only (the order is marked paid with the in-process fake money), until the owner decides about real money
        if (!devLike && v.PhonePaymentProvider is "fake" or "sola") p.Add($"Voice:PhonePaymentProvider={v.PhonePaymentProvider} is for development and tests only: real-money phone orders are not enabled.");
        if (v.PhonePaymentProvider == "sola") p.AddRange(SolaOptions.Problems(sola ?? new SolaOptions()));
        if (!devLike && (string.IsNullOrWhiteSpace(s.ProjectId) || string.IsNullOrWhiteSpace(s.ApiToken) || string.IsNullOrWhiteSpace(s.Space) || string.IsNullOrWhiteSpace(s.FromNumber)))
            p.Add("SignalWire:ProjectId, ApiToken, Space and FromNumber (env SignalWire__...) are required in production when Voice:Enabled: login codes are sent by SMS.");
        if (!Uri.TryCreate(v.PublicBaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && !(devLike && u.Scheme == "http")) || u.AbsolutePath != "/")
            p.Add("Voice:PublicBaseUrl must be the public https origin of this API (no path) when Voice:Enabled.");
        // the key and the audio only ever go to OpenAI and the texts only to SignalWire in production; a scripted upstream or an SMS sink is for development and the journey
        if (!devLike && !v.OpenAiRealtimeUrl.StartsWith("wss://api.openai.com/", StringComparison.OrdinalIgnoreCase)) p.Add("Voice:OpenAiRealtimeUrl must be OpenAI's (wss://api.openai.com/...) in production: a scripted upstream is for development and the journey only.");
        if (!string.IsNullOrWhiteSpace(s.ApiBaseUrl))
        {
            if (!devLike) p.Add("SignalWire:ApiBaseUrl is for development and the journey only (an SMS sink): it must be empty in production.");
            else if (!Uri.TryCreate(s.ApiBaseUrl, UriKind.Absolute, out var sink) || sink.Scheme is not ("http" or "https")) p.Add("SignalWire:ApiBaseUrl must be an absolute http(s) URL.");
        }
        if (v.MaxOffTopicTurns is < 1 or > 10) p.Add("Voice:MaxOffTopicTurns must be between 1 and 10.");
        if (!new[] { "full", "mini", "split" }.Contains(v.ModelMode)) p.Add("Voice:ModelMode must be full, mini or split.");
        if (v.MiniPercent is < 0 or > 100) p.Add("Voice:MiniPercent must be between 0 and 100.");
        if (!new[] { "server_vad", "semantic_vad" }.Contains(v.TurnDetection)) p.Add("Voice:TurnDetection must be server_vad or semantic_vad.");
        if (!new[] { "low", "medium", "high", "auto" }.Contains(v.Eagerness)) p.Add("Voice:Eagerness must be low, medium, high or auto.");
        if (!new[] { "near_field", "far_field", "none" }.Contains(v.NoiseReduction)) p.Add("Voice:NoiseReduction must be near_field, far_field or none.");
        VoicePipelineVendors.Problems(v, p);
        if (v.PipelineMenu.Enabled && v.Diagnostics.Enabled && v.Diagnostics.RetentionHours is < 1 or > DiagnosticsOptions.MaxRetentionHours) p.Add("Voice:Diagnostics:RetentionHours must be between 1 and 168.");   // only when the menu is on (V-26: a test-line setting never takes the API down)
        var m = v.PipelineMenu;
        if (m.Enabled && m.TestNumbers.Any(n => Infrastructure.PhoneNumbers.ToE164(n) is null)) p.Add("Voice:PipelineMenu:TestNumbers must all be phone numbers (E.164, like +15551234567).");
        if (m.Enabled) foreach (var key in m.Descriptions.TooLong()) p.Add($"{key} must be at most {PipelineDescriptions.MaxLength} characters (one plain sentence a caller can listen to).");
        if (m.MenuTimeoutSeconds is < 1 or > 30) p.Add("Voice:PipelineMenu:MenuTimeoutSeconds must be between 1 and 30.");
        if (m.EndpointMs is < 200 or > 5000) p.Add("Voice:PipelineMenu:EndpointMs must be between 200 and 5000.");
        if (m.ParallelWaitMs is < 0 or > 5000 || m.ParallelBudgetMs is < 100 or > 10000 || m.ParallelWaitMs > m.ParallelBudgetMs) p.Add("Voice:PipelineMenu:ParallelWaitMs (0-5000) must not exceed ParallelBudgetMs (100-10000).");
        return p;
    }
}
