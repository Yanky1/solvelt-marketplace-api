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
    public string TurnDetection { get; set; } = "server_vad";
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
    /// <summary>Days a call's metrics row is kept (no audio, no transcript, only a masked number).</summary>
    public int CallRetentionDays { get; set; } = 400;
}

public sealed class SignalWireOptions
{
    public string? ProjectId { get; set; }
    public string? ApiToken { get; set; }
    public string? Space { get; set; }
    /// <summary>Verifies <c>X-SignalWire-Signature</c> (the project's signing key). Secret.</summary>
    public string? SigningKey { get; set; }
    public string? FromNumber { get; set; }
}

public static class VoiceConfig
{
    public static void Bind(VoiceOptions o, IConfiguration c, bool devLike)
    {
        c.GetSection("Voice").Bind(o);
        if (string.IsNullOrWhiteSpace(o.StreamTokenKey) && devLike) o.StreamTokenKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Start-up problems; only checked when the module is enabled.</summary>
    public static IReadOnlyList<string> Problems(VoiceOptions v, SignalWireOptions s, bool devLike)
    {
        var p = new List<string>();
        if (!v.Enabled) return p;
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey)) p.Add("Voice:OpenAiApiKey (env Voice__OpenAiApiKey) is required when Voice:Enabled.");
        if (string.IsNullOrWhiteSpace(s.SigningKey)) p.Add("SignalWire:SigningKey (env SignalWire__SigningKey) is required when Voice:Enabled.");
        if (string.IsNullOrWhiteSpace(v.StreamTokenKey) || v.StreamTokenKey.Length < 32) p.Add("Voice:StreamTokenKey (env Voice__StreamTokenKey) must be at least 32 characters when Voice:Enabled.");
        else if (!string.IsNullOrEmpty(s.SigningKey) && v.StreamTokenKey == s.SigningKey) p.Add("Voice:StreamTokenKey must differ from SignalWire:SigningKey (one key per purpose).");
        if (!Uri.TryCreate(v.PublicBaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && !(devLike && u.Scheme == "http")) || u.AbsolutePath != "/")
            p.Add("Voice:PublicBaseUrl must be the public https origin of this API (no path) when Voice:Enabled.");
        if (!new[] { "full", "mini", "split" }.Contains(v.ModelMode)) p.Add("Voice:ModelMode must be full, mini or split.");
        if (v.MiniPercent is < 0 or > 100) p.Add("Voice:MiniPercent must be between 0 and 100.");
        if (!new[] { "server_vad", "semantic_vad" }.Contains(v.TurnDetection)) p.Add("Voice:TurnDetection must be server_vad or semantic_vad.");
        return p;
    }
}
