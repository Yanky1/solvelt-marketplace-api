namespace Solvelt.Marketplace.Api.Voice;

// Settings of the vendor adapters of the pipeline options (decision V-22). Names only in VOICE.md; values live in the host's secret store. Nothing here is on by default: an option whose providers are
// not both named, or whose keys are missing, is "not available on this line" and the call runs on option 1.

/// <summary>Which providers an option uses. <c>Stt</c> and <c>Tts</c> are provider names (<see cref="VendorNames"/>); empty means the option is not configured.</summary>
public class PipelineProviders
{
    public string Stt { get; set; } = "";
    public string Tts { get; set; } = "";
    public bool Configured => Stt.Length > 0 && Tts.Length > 0;
}

/// <summary><c>Voice:Pipelines</c>: the provider choice per option and the shared text model.</summary>
/// <summary>Option p5: Gladia and Yiddish Labs Flash in parallel, a judge, the Yiddish Labs voice (<c>Stt</c> is <c>gladia+yiddishlabs</c>, <c>Tts</c> is <c>yiddishlabs</c>).</summary>
public sealed class P5Options : PipelineProviders
{
    /// <summary>The OpenAI text model that judges the candidate transcripts of one turn. Required when <see cref="YiddishLabsInJudge"/> is true (without a second transcript there is nothing to judge).</summary>
    public string JudgeModel { get; set; } = "";
    /// <summary>The provider of the judge: <c>anthropic</c> or <c>openai</c>; empty follows <c>Voice:Pipelines:Llm:Provider</c>.</summary>
    public string JudgeProvider { get; set; } = "";
    /// <summary>
    /// The Yiddish Labs Terms section 6 switch (their output may not be used to evaluate other models). FALSE (the default): Flash is never contacted and its text never reaches the judge or the model: p5 is Gladia for the
    /// words and Yiddish Labs for the voice only. TRUE: Flash's text is a second candidate and the judge picks. Only the owner turns it on, after reading the terms.
    /// </summary>
    public bool YiddishLabsInJudge { get; set; }
    /// <summary>
    /// TRUE adds OpenAI's transcription (decision V-47) as a candidate beside Gladia (and Flash when <see cref="YiddishLabsInJudge"/> is on) and the judge picks. FALSE (the default): OpenAI is never contacted by option 5.
    /// It needs the judge model like the Flash switch does (a second transcript is what there is to judge).
    /// </summary>
    public bool OpenAiInJudge { get; set; }
}

public sealed class VoicePipelinesOptions
{
    public PipelineProviders P2 { get; set; } = new();
    public PipelineProviders P3 { get; set; } = new();
    public PipelineProviders P4 { get; set; } = new();
    public P5Options P5 { get; set; } = new();
    /// <summary>Option 6 (SignalWire's speech recognition). Off by default; it needs only the text model's key, no new secret.</summary>
    public P6Options P6 { get; set; } = new();
    /// <summary>The OpenAI text model of every chained option (the same instructions and tools as the Realtime line). Required as soon as any option is configured; no default is assumed.</summary>
    public VoiceLlmOptions Llm { get; set; } = new();

    public PipelineProviders For(string option) => option switch { "p2" => P2, "p3" => P3, "p4" => P4, "p5" => P5, _ => new() };

    /// <summary>Whether the option sends the caller's speech or words to Yiddish Labs: those options need the consent step.</summary>
    public bool UsesYiddishLabs(string option) { var p = For(option); return p.Configured && (p.Stt.Contains("yiddishlabs", StringComparison.OrdinalIgnoreCase) || p.Tts.Contains("yiddishlabs", StringComparison.OrdinalIgnoreCase)); }
}

public sealed class P6Options { public bool Enabled { get; set; } }

/// <summary>
/// Option 6 settings (<c>Voice:SignalWireSpeech</c>, decision V-35): what the cXML <c>Gather input="speech"</c> asks SignalWire for. No secret: it uses the SignalWire project the line already has.
/// Defaults (decision V-36): language en-US, speechTimeout auto, <see cref="Enhanced"/> on and <see cref="SpeechModel"/> phone_call (the 8 kHz model), as the orchestrator's research recommended for telephone audio; enhanced costs extra.
/// </summary>
public sealed class SignalWireSpeechOptions
{
    public string Language { get; set; } = "en-US";
    public bool Enhanced { get; set; } = true;
    /// <summary>The <c>speechModel</c> attribute (for example phone_call, the 8 kHz model). Empty: SignalWire's default.</summary>
    public string SpeechModel { get; set; } = "phone_call";
    /// <summary>Seconds of silence that end the caller's turn, or <c>auto</c>.</summary>
    public string SpeechTimeout { get; set; } = "auto";
    /// <summary>The <c>voice</c> of the spoken reply (a SignalWire Say voice string). Empty: the default voice.</summary>
    public string Voice { get; set; } = "";
    /// <summary>The longest <c>hints</c> attribute we send (static words plus catalog words, V-36).</summary>
    public const int MaxHintsChars = 500;
    /// <summary>Words SignalWire should always favour, comma separated; the catalog's words (<see cref="IVoiceVocabulary"/>) are added after them, up to <see cref="MaxHintsChars"/>.</summary>
    public string Hints { get; set; } = "";
    /// <summary>Seconds to wait for the caller to start speaking before the line asks again.</summary>
    public int NoInputSeconds { get; set; } = 8;
    /// <summary>The most time one turn (the text model and its tools) may take before the platform gives up on it; SignalWire's own webhook wait is short.</summary>
    public int TurnSeconds { get; set; } = 12;
}

/// <summary>The text-model providers of the chained options (decision V-33).</summary>
public static class LlmProviders
{
    public const string Anthropic = "anthropic", OpenAi = "openai";
    public static bool IsKnown(string? p) => string.Equals(p, Anthropic, StringComparison.OrdinalIgnoreCase) || string.Equals(p, OpenAi, StringComparison.OrdinalIgnoreCase);
    public static bool IsAnthropic(string? p) => string.Equals(p, Anthropic, StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>Voice:Anthropic</c>: the Claude key for the chained options and the judge. Secret: environment only (<c>Voice__Anthropic__ApiKey</c>). Never logged; the only host it is sent to is api.anthropic.com.</summary>
public sealed class AnthropicVoiceOptions
{
    public string ApiKey { get; set; } = "";     // secret
}

public sealed class VoiceLlmOptions
{
    /// <summary><c>anthropic</c> (the default) or <c>openai</c>: which provider answers the chained options p2 to p5.</summary>
    public string Provider { get; set; } = LlmProviders.Anthropic;
    /// <summary>The conversation model. Empty with Claude means the built-in default (<see cref="AnthropicDefaults.ConversationModel"/>); with OpenAI it must be set.</summary>
    public string Model { get; set; } = "";
    /// <summary>The most tokens one spoken answer may have (a phone answer is short).</summary>
    public int MaxTokens { get; set; } = 1024;
    /// <summary>Longest wait for the first token or the next one, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary><c>Voice:Deepgram</c>: streaming speech to text (option p2).</summary>
public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = "";     // secret
    public string Model { get; set; } = "nova-3";
    public string Language { get; set; } = "en";
    /// <summary>Most catalog terms sent as <c>keyterm</c> (V-37). Deepgram suggests staying well under its 500-token limit: "focus on the most important 20-50 terms". 0 sends none.</summary>
    public int KeytermMax { get; set; } = 40;
    /// <summary>Send <c>redact_usage=true</c> with keyterms: otherwise Deepgram stores them in usage records (its docs).</summary>
    public bool RedactUsage { get; set; } = true;
    /// <summary><c>endpointing</c> in milliseconds (Deepgram's guidance: 300-500 for speakers who pause mid-thought). 0: not sent, Deepgram's default (10 ms).</summary>
    public int EndpointingMs { get; set; }
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

/// <summary><c>Voice:Gladia</c>: live speech to text with Yiddish (options p3 and p5).</summary>
public sealed class GladiaOptions
{
    public string ApiKey { get; set; } = "";     // secret
    public string Model { get; set; } = "solaria-1";     // the only model live transcription supports
    public string Language { get; set; } = "yi";
    /// <summary>Most catalog terms sent as custom vocabulary (V-37). Gladia compares every word with every entry, so long lists add false positives: keep it short. 0 sends none.</summary>
    public int VocabularyMax { get; set; } = 30;
    /// <summary><c>default_intensity</c> of the vocabulary (0 to 1; Gladia suggests starting at 0.4, range 0.4-0.6).</summary>
    public double VocabularyIntensity { get; set; } = 0.4;
    /// <summary>Gladia's call-centre preset (its recommended-parameters page): <c>endpointing</c> 0.2-0.4 s. 0: not sent (default 0.05 s).</summary>
    public double EndpointingSeconds { get; set; }
    /// <summary><c>maximum_duration_without_endpointing</c> in seconds (preset: 15; range 5-60). 0: not sent (default 5).</summary>
    public int MaxSecondsWithoutEndpointing { get; set; }
}

/// <summary><c>Voice:YiddishLabs</c>: Yiddish speech to text (Flash, one request per caller turn) and realtime text to speech (options p4 and p5). The vendor has no streaming recogniser and its voice is a preview.</summary>
public sealed class YiddishLabsOptions
{
    public string FlashApiKey { get; set; } = "";      // secret (a yl_flash_ key: the same key does the recogniser and the voice)
    public string SttModel { get; set; } = "yiddishlabs-3-turbo";
    /// <summary><c>yi</c>, <c>en</c>, <c>he</c> or <c>auto</c>.</summary>
    public string SttLanguage { get; set; } = "yi";
    /// <summary>Optional words that bias the start of the transcription (product names).</summary>
    public string SttPrompt { get; set; } = "";
    /// <summary>Append the catalog vocabulary to the Flash <c>prompt</c> (V-37), after <see cref="SttPrompt"/>, within 500 characters in all. Unmeasured for Yiddish: switch off if it hurts.</summary>
    public bool CatalogInPrompt { get; set; } = true;
    /// <summary>The rate of the WAV that is uploaded: 8000 (as the telephone sends it) or 16000 (up-sampled). Whether 8 kHz is accepted is OPEN until a key exists.</summary>
    public int UploadSampleRate { get; set; } = 8000;
    /// <summary>Silence that ends a caller's turn: the recogniser only takes whole recordings, so the adapter listens for the end of speech itself.</summary>
    public int VadSilenceMs { get; set; } = 450;
    /// <summary>Mean absolute amplitude (0 to 32767) of a 20 ms frame that counts as speech.</summary>
    public int VadEnergy { get; set; } = 400;
    public string TtsVoiceId { get; set; } = "";
    public bool TtsHd { get; set; }
    /// <summary>The vendor allows five concurrent sessions per account; this process keeps to the number set here.</summary>
    public int MaxConcurrentTtsSessions { get; set; } = 5;
    /// <summary>The consent line (the vendor's terms ask for consent before third-party personal data is sent): the OWNER's approved wording, never written in code. It is played with a keypad press before the pipeline starts; required for the options that use this vendor.</summary>
    public string ConsentLine { get; set; } = "";
}

/// <summary>Built-in Claude model ids (decision V-33). The ids are configuration; these are only the defaults.</summary>
public static class AnthropicDefaults
{
    public const string ConversationModel = "claude-sonnet-5-5";
    public const string JudgeModel = "claude-haiku-5-5";
}

public static class VendorNames
{
    public const string Deepgram = "deepgram", ElevenLabs = "elevenlabs", Gladia = "gladia", YiddishLabs = "yiddishlabs", OpenAi = "openai";
}

/// <summary>The fixed runtime hosts. Nothing is read from configuration as a host, so a setting can never point the line at another server.</summary>
public static class VendorHosts
{
    public const string Deepgram = "api.deepgram.com";
    public const string YiddishLabsFlash = "flash.yiddishlabs.com", YiddishLabsRealtime = "rt.yiddishlabs.com";
    public static string? ElevenLabs(string region) => region.ToLowerInvariant() switch
    {
        "" => "api.elevenlabs.io", "us" => "api.us.elevenlabs.io", "eu" => "api.eu.residency.elevenlabs.io", "in" => "api.in.residency.elevenlabs.io", "sg" => "api.sg.residency.elevenlabs.io", _ => null,
    };
}

/// <summary>
/// <c>Voice:OpenAiTranscribe</c>: OpenAI Realtime transcription as the recogniser of options 2 to 5 (decision V-47; <c>Voice:Pipelines:Pn:Stt</c> = <c>openai</c>). The key is the existing <c>Voice:OpenAiApiKey</c>; the host is
/// the one of <c>Voice:OpenAiRealtimeUrl</c> (checked to be OpenAI's in production). Nothing is on by default.
/// </summary>
public sealed class OpenAiTranscribeOptions
{
    /// <summary>OpenAI's docs: "Start with gpt-live-transcribe". whisper-1 and the gpt-4o-transcribe models shut down on 2027-02-26. Configuration, so a new model needs no release; a model other than the two that need no turn detection is not supported by this adapter.</summary>
    public string Model { get; set; } = "gpt-live-transcribe";
    /// <summary>The spoken languages, comma separated ISO codes (the model takes a list, <c>languages</c>); empty sends none and lets the model decide. Which languages gpt-live-transcribe covers (Yiddish in particular) is NOT stated in the facts this was built from: measure it with the test set.</summary>
    public string Languages { get; set; } = "en";
    /// <summary>Most catalog words sent as <c>keywords</c>.</summary>
    public int KeywordMax { get; set; } = 40;
    /// <summary>Silence that ends the caller's turn, in milliseconds: OUR detector, because this model takes no turn detection of its own.</summary>
    public int EndpointSilenceMs { get; set; } = 700;
    /// <summary>The quietest loudness (RMS of the 16-bit samples) counted as speech, over the line's own noise level. NOT tuned on real calls: the test set and the replay tool are how to tune it.</summary>
    public int MinEnergy { get; set; } = 400;
    /// <summary>A turn is ended and sent after this long even if the caller has not paused.</summary>
    public int MaxTurnSeconds { get; set; } = 30;
}
