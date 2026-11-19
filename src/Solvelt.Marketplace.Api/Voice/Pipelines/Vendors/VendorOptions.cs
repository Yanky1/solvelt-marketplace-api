namespace Solvelt.Marketplace.Api.Voice;

// Settings of the vendor adapters of the pipeline options (decision V-22). Names only in VOICE.md; values live in the host's secret store. Nothing here is on by default: an option whose providers are
// not both named, or whose keys are missing, is "not available on this line" and the call runs on option 1.

/// <summary>Which providers an option uses. <c>Stt</c> and <c>Tts</c> are provider names (<see cref="VendorNames"/>); empty means the option is not configured.</summary>
public sealed class PipelineProviders
{
    public string Stt { get; set; } = "";
    public string Tts { get; set; } = "";
    public bool Configured => Stt.Length > 0 && Tts.Length > 0;
}

/// <summary><c>Voice:Pipelines</c>: the provider choice per option and the shared text model.</summary>
public sealed class VoicePipelinesOptions
{
    public PipelineProviders P2 { get; set; } = new();
    public PipelineProviders P3 { get; set; } = new();
    public PipelineProviders P4 { get; set; } = new();
    public PipelineProviders P5 { get; set; } = new();
    /// <summary>The OpenAI text model of every chained option (the same instructions and tools as the Realtime line). Required as soon as any option is configured; no default is assumed.</summary>
    public VoiceLlmOptions Llm { get; set; } = new();

    public PipelineProviders For(string option) => option switch { "p2" => P2, "p3" => P3, "p4" => P4, "p5" => P5, _ => new() };
}

public sealed class VoiceLlmOptions
{
    public string Model { get; set; } = "";
    /// <summary>Longest wait for the first token or the next one, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary><c>Voice:Deepgram</c>: streaming speech to text (option p2).</summary>
public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = "";     // secret
    public string Model { get; set; } = "nova-3";
    public string Language { get; set; } = "en";
}

/// <summary><c>Voice:ElevenLabs</c>: streaming text to speech (options p2 and p3).</summary>
public sealed class ElevenLabsOptions
{
    public string ApiKey { get; set; } = "";     // secret
    public string VoiceId { get; set; } = "";
    /// <summary>The model id (no default is assumed: read it from the vendor's current model list when configuring).</summary>
    public string TtsModel { get; set; } = "";
    /// <summary>Empty (the default host), <c>us</c>, <c>eu</c>, <c>in</c> or <c>sg</c> (the vendor's data-residency hosts). A fixed list: the host is never free text.</summary>
    public string Region { get; set; } = "";
    /// <summary>The vendor's own request logging. Off by default (we never want the text kept); a plan that does not allow turning it off needs this set to true, which is then a visible owner decision.</summary>
    public bool EnableLogging { get; set; }
}

public static class VendorNames
{
    public const string Deepgram = "deepgram", ElevenLabs = "elevenlabs", Gladia = "gladia", YiddishLabs = "yiddishlabs";
}

/// <summary>The fixed runtime hosts. Nothing is read from configuration as a host, so a setting can never point the line at another server.</summary>
public static class VendorHosts
{
    public const string Deepgram = "api.deepgram.com";
    public static string? ElevenLabs(string region) => region.ToLowerInvariant() switch
    {
        "" => "api.elevenlabs.io", "us" => "api.us.elevenlabs.io", "eu" => "api.eu.residency.elevenlabs.io", "in" => "api.in.residency.elevenlabs.io", "sg" => "api.sg.residency.elevenlabs.io", _ => null,
    };
}
