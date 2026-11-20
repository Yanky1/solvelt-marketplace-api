using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>The stages of one vendor-backed option.</summary>
public sealed class VendorChainedParts(string optionId, IStreamingStt stt, ITts tts, IVoiceLlm llm, int endpointMs) : IChainedPipelineParts
{
    public string OptionId => optionId;
    public IStreamingStt Stt => stt;
    public ITts Tts => tts;
    public IVoiceLlm Llm => llm;
    public ITurnDetector NewTurnDetector() => new SilenceTurnDetector(endpointMs);
}

public static class VoicePipelineVendors
{
    public const string LlmClient = "voice-llm", GladiaClient = "voice-gladia";

    /// <summary>The provider pairs that have an adapter. A pair outside this list is a start-up problem, so a typo cannot silently leave an option off.</summary>
    internal static bool Supported(string option, PipelineProviders p)
        => (option == "p2" && Is(p.Stt, VendorNames.Deepgram) && Is(p.Tts, VendorNames.ElevenLabs))
        || (option == "p3" && Is(p.Stt, VendorNames.Gladia) && Is(p.Tts, VendorNames.ElevenLabs));

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers the source of vendor-backed parts. Which options exist is decided from the bound settings when the registry is built, never from the code path: nothing is configured by default.</summary>
    public static IServiceCollection AddVoicePipelineVendors(this IServiceCollection services)
    {
        services.AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>();
        services.AddHttpClient(LlmClient, c => c.Timeout = Timeout.InfiniteTimeSpan);       // the adapter bounds the wait itself
        services.AddHttpClient(GladiaClient, c => c.Timeout = TimeSpan.FromSeconds(15));
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
        foreach (var o in new[] { "p2", "p3", "p4", "p5" })
        {
            var prov = v.Pipelines.For(o);
            var key = "Voice:Pipelines:" + o.ToUpperInvariant();
            if ((prov.Stt.Length > 0) != (prov.Tts.Length > 0)) { p.Add($"{key}: name both Stt and Tts, or neither (an option with one provider is not usable)."); continue; }
            if (!prov.Configured) continue;
            if (!Supported(o, prov)) { p.Add($"{key}: the providers '{prov.Stt}' and '{prov.Tts}' have no adapter yet for this option."); continue; }
            configured.Add(o);
        }
        if (configured.Count == 0) return;
        if (v.Pipelines.Llm.TimeoutSeconds is < 1 or > 120) p.Add("Voice:Pipelines:Llm:TimeoutSeconds must be between 1 and 120.");
        if (configured.Contains("p2") || configured.Contains("p3"))
        {
            if (v.ElevenLabs.VoiceId.Length > 0 && v.ElevenLabs.VoiceId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) p.Add("Voice:ElevenLabs:VoiceId may only hold letters, digits, - and _.");
            if (VendorHosts.ElevenLabs(v.ElevenLabs.Region) is null) p.Add("Voice:ElevenLabs:Region must be empty, us, eu, in or sg.");
        }
    }

    /// <summary>What an option still lacks before it can run (empty = usable). Names settings only, never values.</summary>
    internal static IReadOnlyList<string> Missing(VoiceOptions v, string option)
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(v.OpenAiApiKey)) m.Add("Voice:OpenAiApiKey");
        if (string.IsNullOrWhiteSpace(v.Pipelines.Llm.Model)) m.Add("Voice:Pipelines:Llm:Model");
        switch (option)
        {
            case "p2":
                if (string.IsNullOrWhiteSpace(v.Deepgram.ApiKey)) m.Add("Voice:Deepgram:ApiKey");
                if (string.IsNullOrWhiteSpace(v.Deepgram.Model)) m.Add("Voice:Deepgram:Model");
                if (string.IsNullOrWhiteSpace(v.Deepgram.Language)) m.Add("Voice:Deepgram:Language");
                MissingEleven(v, m);
                break;
            case "p3":
                if (string.IsNullOrWhiteSpace(v.Gladia.ApiKey)) m.Add("Voice:Gladia:ApiKey");
                if (string.IsNullOrWhiteSpace(v.Gladia.Model)) m.Add("Voice:Gladia:Model");
                if (string.IsNullOrWhiteSpace(v.Gladia.Language)) m.Add("Voice:Gladia:Language");
                MissingEleven(v, m);
                break;
        }
        return m;
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
            if (!VoicePipelineVendors.Supported(o, v.Pipelines.For(o))) return false;
            var missing = VoicePipelineVendors.Missing(v, o);
            if (missing.Count == 0) return true;
            logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Pipelines").LogWarning("Voice pipeline option {Option} is not available on this line: missing setting(s) {Settings}", o, string.Join(", ", missing));
            return false;
        }
        var p2 = Ready("p2");
        var p3 = Ready("p3");
        if (!p2 && !p3) yield break;
        var sockets = sp.GetRequiredService<IVendorSocketConnector>();
        var http = sp.GetRequiredService<IHttpClientFactory>();
        var llm = new OpenAiVoiceLlm(http.CreateClient(VoicePipelineVendors.LlmClient), v.OpenAiApiKey ?? "", v.Pipelines.Llm);
        var tts = new ElevenLabsTts(v.ElevenLabs, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.ElevenLabs"));
        if (p2) yield return new VendorChainedParts("p2", new DeepgramStt(v.Deepgram, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Deepgram")), tts, llm, v.PipelineMenu.EndpointMs);
        if (p3) yield return new VendorChainedParts("p3", new GladiaStt(v.Gladia, http.CreateClient(VoicePipelineVendors.GladiaClient), sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Gladia")), tts, llm, v.PipelineMenu.EndpointMs);
    }
}
