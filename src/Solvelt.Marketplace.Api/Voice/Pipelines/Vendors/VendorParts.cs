using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>The stages of one vendor-backed option.</summary>
public sealed class VendorChainedParts(string optionId, IStreamingStt stt, ITts tts, IVoiceLlm llm, Func<ITurnDetector> detector) : IChainedPipelineParts
{
    public string OptionId => optionId;
    public IStreamingStt Stt => stt;
    public ITts Tts => tts;
    public IVoiceLlm Llm => llm;
    public ITurnDetector NewTurnDetector() => detector();
}

public static class VoicePipelineVendors
{
    public const string LlmClient = "voice-llm", GladiaClient = "voice-gladia", YiddishLabsClient = "voice-yiddishlabs";

    /// <summary>The provider pairs that have an adapter. A pair outside this list is a start-up problem, so a typo cannot silently leave an option off.</summary>
    internal static bool Supported(string option, PipelineProviders p)
        => option == "p6" || (option == "p2" && (Is(p.Stt, VendorNames.Deepgram) || Is(p.Stt, VendorNames.OpenAi)) && Is(p.Tts, VendorNames.ElevenLabs))
        || (option == "p3" && (Is(p.Stt, VendorNames.Gladia) || Is(p.Stt, VendorNames.OpenAi)) && Is(p.Tts, VendorNames.ElevenLabs))
        || (option == "p7" && Is(p.Stt, VendorNames.OpenAi) && Is(p.Tts, VendorNames.ElevenLabs))
        || (option == "p4" && (Is(p.Stt, VendorNames.YiddishLabs) || Is(p.Stt, VendorNames.OpenAi)) && Is(p.Tts, VendorNames.YiddishLabs))
        || (option == "p5" && Is(p.Stt, VendorNames.Gladia + "+" + VendorNames.YiddishLabs) && Is(p.Tts, VendorNames.YiddishLabs));

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers the source of vendor-backed parts. Which options exist is decided from the bound settings when the registry is built, never from the code path: nothing is configured by default.</summary>
    public static IServiceCollection AddVoicePipelineVendors(this IServiceCollection services)
    {
        services.AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>();
        services.AddHttpClient(LlmClient, c => c.Timeout = Timeout.InfiniteTimeSpan);       // the adapter bounds the wait itself
        services.AddHttpClient(GladiaClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(YiddishLabsClient, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<IChainedPartsSource, VendorPartsSource>();
        return services;
    }

    /// <summary>
    /// Start-up problems: only real INVALID values (half a provider pair, a pair with no adapter, an out-of-range number, a voice id with odd characters, a region outside the list), and only when the pipeline menu is on.
    /// A setting that is merely MISSING never lands here: it makes the option unavailable (see <see cref="Missing"/>), because a test-line setting must not take the whole API down.
    /// </summary>
    internal static void Problems(VoiceOptions v, List<string> p)
    {
        if (!v.PipelineMenu.Enabled) return;
        var configured = new List<string>();
        foreach (var o in new[] { "p2", "p3", "p4", "p5", "p7" })
        {
            var prov = v.Pipelines.For(o);
            var key = "Voice:Pipelines:" + o.ToUpperInvariant();
            if ((prov.Stt.Length > 0) != (prov.Tts.Length > 0)) { p.Add($"{key}: name both Stt and Tts, or neither (an option with one provider is not usable)."); continue; }
            if (!prov.Configured) continue;
            if (!Supported(o, prov)) { p.Add($"{key}: the providers '{prov.Stt}' and '{prov.Tts}' have no adapter yet for this option."); continue; }
            configured.Add(o);
        }
        if (v.Pipelines.P6.Enabled)
        {
            var sp = v.SignalWireSpeech;
            if (sp.Language.Length is < 2 or > 20 || sp.Language.Any(c => !char.IsLetterOrDigit(c) && c != '-')) p.Add("Voice:SignalWireSpeech:Language must be a language code such as en-US.");
            if (!(string.Equals(sp.SpeechTimeout, "auto", StringComparison.OrdinalIgnoreCase) || (int.TryParse(sp.SpeechTimeout, out var st) && st is >= 1 and <= 30))) p.Add("Voice:SignalWireSpeech:SpeechTimeout must be auto or 1 to 30 seconds.");
            if (sp.SpeechModel.Length > 40 || sp.SpeechModel.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.')) p.Add("Voice:SignalWireSpeech:SpeechModel may only hold letters, digits, . - and _ (at most 40).");
            if (sp.Voice.Length > 80 || sp.Voice.Any(char.IsControl)) p.Add("Voice:SignalWireSpeech:Voice must be at most 80 characters of plain text.");
            if (sp.Hints.Length > 500 || sp.Hints.Any(char.IsControl)) p.Add("Voice:SignalWireSpeech:Hints must be at most 500 characters of plain text.");
            if (sp.NoInputSeconds is < 2 or > 30) p.Add("Voice:SignalWireSpeech:NoInputSeconds must be between 2 and 30.");
            if (sp.TurnSeconds is < 3 or > 14) p.Add("Voice:SignalWireSpeech:TurnSeconds must be between 3 and 14 (SignalWire waits only a short time for our answer).");
            configured.Add("p6");
        }
        if (configured.Count == 0) return;
        if (v.Pipelines.Llm.TimeoutSeconds is < 1 or > 120) p.Add("Voice:Pipelines:Llm:TimeoutSeconds must be between 1 and 120.");
        if (v.Pipelines.Llm.MaxTokens is < 50 or > 4096) p.Add("Voice:Pipelines:Llm:MaxTokens must be between 50 and 4096.");
        if (!LlmProviders.IsKnown(v.Pipelines.Llm.Provider)) p.Add("Voice:Pipelines:Llm:Provider must be anthropic or openai.");
        if (configured.Contains("p5") && v.Pipelines.P5.JudgeProvider.Length > 0 && !LlmProviders.IsKnown(v.Pipelines.P5.JudgeProvider)) p.Add("Voice:Pipelines:P5:JudgeProvider must be empty, anthropic or openai.");
        if (configured.Contains("p4") || configured.Contains("p5"))
        {
            var y = v.YiddishLabs;
            if (y.TtsVoiceId.Length > 0 && y.TtsVoiceId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) p.Add("Voice:YiddishLabs:TtsVoiceId may only hold letters, digits, - and _.");
            if (y.SttLanguage is not ("yi" or "en" or "he" or "auto")) p.Add("Voice:YiddishLabs:SttLanguage must be yi, en, he or auto.");
            if (y.UploadSampleRate is not (8000 or 16000)) p.Add("Voice:YiddishLabs:UploadSampleRate must be 8000 or 16000.");
            if (y.VadSilenceMs is < 200 or > 2000) p.Add("Voice:YiddishLabs:VadSilenceMs must be between 200 and 2000.");
            if (y.VadEnergy is < 50 or > 5000) p.Add("Voice:YiddishLabs:VadEnergy must be between 50 and 5000.");
            if (y.MaxConcurrentTtsSessions is < 1 or > 50) p.Add("Voice:YiddishLabs:MaxConcurrentTtsSessions must be between 1 and 50.");
            if (y.SttPrompt.Length > 500) p.Add("Voice:YiddishLabs:SttPrompt must be at most 500 characters.");
            if (y.ConsentLine.Length > 600) p.Add("Voice:YiddishLabs:ConsentLine must be at most 600 characters.");
        }
        if (configured.Any(o => UsesOpenAi(v, o)) || (configured.Contains("p5") && v.Pipelines.P5.OpenAiInJudge))
        {
            var t = v.OpenAiTranscribe;
            if (t.Model.Length > 60 || t.Model.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.'))) p.Add("Voice:OpenAiTranscribe:Model may only hold letters, digits, . - and _ (at most 60).");
            if (OpenAiTranscribeStt.Languages(t).Any(l => !System.Text.RegularExpressions.Regex.IsMatch(l, "^[a-z]{2,3}(-[A-Za-z]{2,4})?$")) || t.Languages.Length > 100) p.Add("Voice:OpenAiTranscribe:Languages must be comma separated language codes such as en or en,yi.");
            if (t.KeywordMax is < 0 or > 100) p.Add("Voice:OpenAiTranscribe:KeywordMax must be between 0 and 100.");
            if (t.EndpointSilenceMs is < 200 or > 3000) p.Add("Voice:OpenAiTranscribe:EndpointSilenceMs must be between 200 and 3000.");
            if (t.MinEnergy is < 50 or > 5000) p.Add("Voice:OpenAiTranscribe:MinEnergy must be between 50 and 5000.");
            if (t.MaxTurnSeconds is < 5 or > 60) p.Add("Voice:OpenAiTranscribe:MaxTurnSeconds must be between 5 and 60.");
        }
        if (configured.Contains("p5") && v.Pipelines.P5.JudgeModel.Length > 100) p.Add("Voice:Pipelines:P5:JudgeModel must be at most 100 characters.");
        if (configured.Contains("p2") || configured.Contains("p3") || configured.Contains("p7"))
        {
            if (v.ElevenLabs.VoiceId.Length > 0 && v.ElevenLabs.VoiceId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) p.Add("Voice:ElevenLabs:VoiceId may only hold letters, digits, - and _.");
            if (VendorHosts.ElevenLabs(v.ElevenLabs.Region) is null) p.Add("Voice:ElevenLabs:Region must be empty, us, eu, in or sg.");
        }
    }

    /// <summary>What an option still lacks before it can run (empty = usable). Names settings only, never values.</summary>
    internal static IReadOnlyList<string> Missing(VoiceOptions v, string option)
    {
        var m = new List<string>();
        MissingLlm(v, m);
        switch (option)
        {
            case "p2":
                if (UsesOpenAi(v, option)) MissingOpenAi(v, m);
                else
                {
                    if (string.IsNullOrWhiteSpace(v.Deepgram.ApiKey)) m.Add("Voice:Deepgram:ApiKey");
                    if (string.IsNullOrWhiteSpace(v.Deepgram.Model)) m.Add("Voice:Deepgram:Model");
                    if (string.IsNullOrWhiteSpace(v.Deepgram.Language)) m.Add("Voice:Deepgram:Language");
                }
                MissingEleven(v, m);
                break;
            case "p3":
                if (UsesOpenAi(v, option)) MissingOpenAi(v, m);
                else
                {
                    if (string.IsNullOrWhiteSpace(v.Gladia.ApiKey)) m.Add("Voice:Gladia:ApiKey");
                    if (string.IsNullOrWhiteSpace(v.Gladia.Model)) m.Add("Voice:Gladia:Model");
                    if (string.IsNullOrWhiteSpace(v.Gladia.Language)) m.Add("Voice:Gladia:Language");
                }
                MissingEleven(v, m);
                break;
            case "p5":
                if (string.IsNullOrWhiteSpace(v.Gladia.ApiKey)) m.Add("Voice:Gladia:ApiKey");
                if (string.IsNullOrWhiteSpace(v.Gladia.Model)) m.Add("Voice:Gladia:Model");
                if (string.IsNullOrWhiteSpace(v.Gladia.Language)) m.Add("Voice:Gladia:Language");
                MissingYiddishLabs(v, m);
                if (v.Pipelines.P5.YiddishLabsInJudge || v.Pipelines.P5.OpenAiInJudge) MissingJudge(v, m);
                if (v.Pipelines.P5.OpenAiInJudge) MissingOpenAi(v, m);
                break;
            case "p4":
                MissingYiddishLabs(v, m);
                if (UsesOpenAi(v, option)) MissingOpenAi(v, m);
                break;
            case "p6":
                if (!v.Pipelines.P6.Enabled) m.Add("Voice:Pipelines:P6:Enabled");
                break;
            case "p7":
                if (!v.Pipelines.P7.Enabled) m.Add("Voice:Pipelines:P7:Enabled");
                MissingOpenAi(v, m);
                MissingEleven(v, m);
                break;
        }
        return m;
    }

    /// <summary>The settings of the text model: with Claude (the default) its key; with OpenAI its key and the model id. An unknown provider is a missing setting too (and an invalid value when the menu is on).</summary>
    private static void MissingLlm(VoiceOptions v, List<string> m)
    {
        var l = v.Pipelines.Llm;
        if (!LlmProviders.IsKnown(l.Provider)) { m.Add("Voice:Pipelines:Llm:Provider"); return; }
        if (LlmProviders.IsAnthropic(l.Provider)) { if (string.IsNullOrWhiteSpace(v.Anthropic.ApiKey)) m.Add("Voice:Anthropic:ApiKey"); return; }
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey)) m.Add("Voice:OpenAiApiKey");
        if (string.IsNullOrWhiteSpace(l.Model)) m.Add("Voice:Pipelines:Llm:Model");
    }

    /// <summary>The provider that judges option p5: its own setting, else the conversation's.</summary>
    internal static string JudgeProvider(VoiceOptions v) => v.Pipelines.P5.JudgeProvider.Length > 0 ? v.Pipelines.P5.JudgeProvider : v.Pipelines.Llm.Provider;

    private static void MissingJudge(VoiceOptions v, List<string> m)
    {
        var jp = JudgeProvider(v);
        if (!LlmProviders.IsKnown(jp)) { m.Add("Voice:Pipelines:P5:JudgeProvider"); return; }
        if (LlmProviders.IsAnthropic(jp)) { if (string.IsNullOrWhiteSpace(v.Anthropic.ApiKey) && !m.Contains("Voice:Anthropic:ApiKey")) m.Add("Voice:Anthropic:ApiKey"); return; }
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey) && !m.Contains("Voice:OpenAiApiKey")) m.Add("Voice:OpenAiApiKey");
        if (string.IsNullOrWhiteSpace(v.Pipelines.P5.JudgeModel)) m.Add("Voice:Pipelines:P5:JudgeModel");
    }

    /// <summary>Whether the option's recogniser is OpenAI's transcription (decision V-47).</summary>
    internal static bool UsesOpenAi(VoiceOptions v, string option) => string.Equals(v.Pipelines.For(option).Stt, VendorNames.OpenAi, StringComparison.OrdinalIgnoreCase);

    /// <summary>The OpenAI recogniser needs the existing OpenAI key and a model (V-47); a missing one makes the option unavailable, with one warning, never a start-up failure (V-26).</summary>
    private static void MissingOpenAi(VoiceOptions v, List<string> m)
    {
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey) && !m.Contains("Voice:OpenAiApiKey")) m.Add("Voice:OpenAiApiKey");
        if (string.IsNullOrWhiteSpace(v.OpenAiTranscribe.Model)) m.Add("Voice:OpenAiTranscribe:Model");
    }

    private static void MissingYiddishLabs(VoiceOptions v, List<string> m)
    {
        var y = v.YiddishLabs;
        if (string.IsNullOrWhiteSpace(y.FlashApiKey)) m.Add("Voice:YiddishLabs:FlashApiKey");
        if (string.IsNullOrWhiteSpace(y.TtsVoiceId)) m.Add("Voice:YiddishLabs:TtsVoiceId");
        if (string.IsNullOrWhiteSpace(y.SttModel)) m.Add("Voice:YiddishLabs:SttModel");
        // the owner's approved wording is what makes the option usable: no wording, no option (the vendor's terms ask for consent before third-party personal data is sent)
        if (string.IsNullOrWhiteSpace(y.ConsentLine)) m.Add("Voice:YiddishLabs:ConsentLine");
    }

    private static void MissingEleven(VoiceOptions v, List<string> m)
    {
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.ApiKey)) m.Add("Voice:ElevenLabs:ApiKey");
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.VoiceId)) m.Add("Voice:ElevenLabs:VoiceId");
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.TtsModel)) m.Add("Voice:ElevenLabs:TtsModel");
    }
}

/// <summary>Creates the parts of the options a deployment has configured. Several sources may exist (vendors are added slice by slice).</summary>
public interface IChainedPartsSource
{
    IEnumerable<IChainedPipelineParts> Create(VoiceOptions options);
}

public sealed class VendorPartsSource(IServiceProvider sp) : IChainedPartsSource
{
    public IEnumerable<IChainedPipelineParts> Create(VoiceOptions v)
    {
        var logs = sp.GetRequiredService<ILoggerFactory>();
        // an option that names its providers but lacks settings is "not available on this line" (calls get p1), with ONE warning that names the missing settings: never a start-up failure
        bool Ready(string o)
        {
            if (o == "p6" ? !v.Pipelines.P6.Enabled : !VoicePipelineVendors.Supported(o, v.Pipelines.For(o))) return false;   // p6 has no vendor pair: it is on or off
            var missing = VoicePipelineVendors.Missing(v, o);
            if (missing.Count == 0) return true;
            logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Pipelines").LogWarning("Voice pipeline option {Option} is not available on this line: missing setting(s) {Settings}", o, string.Join(", ", missing));
            return false;
        }
        var p2 = Ready("p2");
        var p3 = Ready("p3");
        var p4 = Ready("p4");
        var p5 = Ready("p5");
        var p6 = Ready("p6");
        var p7 = Ready("p7");
        if (!p2 && !p3 && !p4 && !p5 && !p6 && !p7) yield break;
        var sockets = sp.GetRequiredService<IVendorSocketConnector>();
        var vocab = sp.GetService<IVoiceVocabulary>();   // the shared catalog words (V-37); none when the provider is not registered
        var http = sp.GetRequiredService<IHttpClientFactory>();
        // the chained options' agent is Claude (decisions V-33 and V-40): OpenAI is for audio, not for the conversation. The OpenAI text model stays only as an explicit opt-in (never a default, never chosen because an OpenAI key
        // exists) and says so in the log. Claude's client is the SDK's, which only ever talks to api.anthropic.com
        if (!LlmProviders.IsAnthropic(v.Pipelines.Llm.Provider) || (p5 && !LlmProviders.IsAnthropic(VoicePipelineVendors.JudgeProvider(v))))
            logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Pipelines").LogWarning("Voice: an OpenAI text model is configured for the conversation or the judge of options 2 to 6 (Voice:Pipelines:Llm:Provider / P5:JudgeProvider). Claude is the agent by decision; this is an explicit override (V-40).");
        var claude = LlmProviders.IsAnthropic(v.Pipelines.Llm.Provider) || LlmProviders.IsAnthropic(VoicePipelineVendors.JudgeProvider(v))
            ? sp.GetService<Anthropic.AnthropicClient>() ?? AnthropicVoiceLlm.NewClient(v.Anthropic.ApiKey) : null;
        IVoiceLlm llm = LlmProviders.IsAnthropic(v.Pipelines.Llm.Provider)
            ? new AnthropicVoiceLlm(claude!, string.IsNullOrWhiteSpace(v.Pipelines.Llm.Model) ? AnthropicDefaults.ConversationModel : v.Pipelines.Llm.Model.Trim(), v.Pipelines.Llm.MaxTokens, v.Pipelines.Llm.TimeoutSeconds)
            : new OpenAiVoiceLlm(http.CreateClient(VoicePipelineVendors.LlmClient), v.OpenAiApiKey ?? "", v.Pipelines.Llm);
        if (p6) yield return new SignalWireSpeechParts(llm);
        Func<ITurnDetector> silence = () => new SilenceTurnDetector(v.PipelineMenu.EndpointMs);
        // OpenAI transcription (V-47): our own end-of-speech detector already waited and sent the commit, so its final words end the caller's turn, as Flash's do
        OpenAiTranscribeStt OpenAiStt() => new(v.OpenAiTranscribe, v.OpenAiApiKey ?? "", v.OpenAiRealtimeUrl, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.OpenAiTranscribe"), vocab);
        if (!p2 && !p3 && !p4 && !p5 && !p7) yield break;
        if (p2 || p3 || p7)
        {
            var tts = new ElevenLabsTts(v.ElevenLabs, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.ElevenLabs"));
            if (p2) yield return new VendorChainedParts("p2", VoicePipelineVendors.UsesOpenAi(v, "p2") ? OpenAiStt() : new DeepgramStt(v.Deepgram, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Deepgram"), vocab), tts, llm, VoicePipelineVendors.UsesOpenAi(v, "p2") ? () => new FinalTurnDetector() : silence);
            if (p7) yield return new VendorChainedParts("p7", OpenAiStt(), tts, llm, () => new FinalTurnDetector());   // V-48: OpenAI listening, Claude (llm above), ElevenLabs speaking
            if (p3) yield return new VendorChainedParts("p3", VoicePipelineVendors.UsesOpenAi(v, "p3") ? OpenAiStt() : new GladiaStt(v.Gladia, http.CreateClient(VoicePipelineVendors.GladiaClient), sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Gladia"), vocab), tts, llm, VoicePipelineVendors.UsesOpenAi(v, "p3") ? () => new FinalTurnDetector() : silence);
        }
        if (p4)
            yield return new VendorChainedParts("p4", VoicePipelineVendors.UsesOpenAi(v, "p4") ? OpenAiStt() : new YiddishLabsFlashStt(v.YiddishLabs, http.CreateClient(VoicePipelineVendors.YiddishLabsClient), logs.CreateLogger("Solvelt.Marketplace.Api.Voice.YiddishLabs"), vocab),
                new YiddishLabsTts(v.YiddishLabs, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.YiddishLabs")), llm, () => new FinalTurnDetector());   // the recogniser already waited for the end of speech
        if (p5)
        {
            // Gladia is the primary engine (it alone reports speech start, partials and speech end). Flash is only contacted when the owner has switched YiddishLabsInJudge on (Terms section 6): until then p5 is
            // Gladia for the words and Yiddish Labs for the voice only, and no audio or text of the caller goes to the Flash recogniser.
            var gladia = new GladiaStt(v.Gladia, http.CreateClient(VoicePipelineVendors.GladiaClient), sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Gladia"), vocab);
            IStreamingStt stt = gladia;
            if (v.Pipelines.P5.YiddishLabsInJudge || v.Pipelines.P5.OpenAiInJudge)
            {
                var engines = new List<(string, IStreamingStt)> { ("gladia", gladia) };
                if (v.Pipelines.P5.YiddishLabsInJudge) engines.Add(("yiddishlabs", new YiddishLabsFlashStt(v.YiddishLabs, http.CreateClient(VoicePipelineVendors.YiddishLabsClient), logs.CreateLogger("Solvelt.Marketplace.Api.Voice.YiddishLabs"), vocab)));
                if (v.Pipelines.P5.OpenAiInJudge) engines.Add(("openai", OpenAiStt()));   // a third candidate (V-47): one hypothesis, no confidence
                ITranscriptJudge judge = LlmProviders.IsAnthropic(VoicePipelineVendors.JudgeProvider(v))
                    ? new AnthropicTranscriptJudge(claude!, string.IsNullOrWhiteSpace(v.Pipelines.P5.JudgeModel) ? AnthropicDefaults.JudgeModel : v.Pipelines.P5.JudgeModel.Trim())
                    : new OpenAiTranscriptJudge(http.CreateClient(VoicePipelineVendors.LlmClient), v.OpenAiApiKey ?? "", v.Pipelines.P5.JudgeModel);
                stt = new ParallelStt(engines, [judge], v.PipelineMenu.ParallelWaitMs, v.PipelineMenu.ParallelBudgetMs, TimeProvider.System, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Parallel"));
            }
            yield return new VendorChainedParts("p5", stt, new YiddishLabsTts(v.YiddishLabs, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.YiddishLabs")), llm, silence);
        }
    }
}
