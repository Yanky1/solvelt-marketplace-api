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
    public const string LlmClient = "voice-llm";

    /// <summary>The provider pairs that have an adapter. A pair outside this list is a start-up problem, so a typo cannot silently leave an option off.</summary>
    internal static bool Supported(string option, PipelineProviders p)
        => option == "p2" && Is(p.Stt, VendorNames.Deepgram) && Is(p.Tts, VendorNames.ElevenLabs);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers the source of vendor-backed parts. Which options exist is decided from the bound settings when the registry is built, never from the code path: nothing is configured by default.</summary>
    public static IServiceCollection AddVoicePipelineVendors(this IServiceCollection services)
    {
        services.AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>();
        services.AddHttpClient(LlmClient, c => c.Timeout = Timeout.InfiniteTimeSpan);       // the adapter bounds the wait itself
        services.AddSingleton<IChainedPartsSource, VendorPartsSource>();
        return services;
    }

    /// <summary>Start-up problems of the configured options (only checked when the module is enabled).</summary>
    internal static void Problems(VoiceOptions v, List<string> p)
    {
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
        if (string.IsNullOrWhiteSpace(v.Pipelines.Llm.Model)) p.Add("Voice:Pipelines:Llm:Model is required when a pipeline option is configured.");
        if (v.Pipelines.Llm.TimeoutSeconds is < 1 or > 120) p.Add("Voice:Pipelines:Llm:TimeoutSeconds must be between 1 and 120.");
        if (configured.Contains("p2"))
        {
            if (string.IsNullOrWhiteSpace(v.Deepgram.ApiKey)) p.Add("Voice:Deepgram:ApiKey is required for option p2.");
            if (string.IsNullOrWhiteSpace(v.Deepgram.Model) || string.IsNullOrWhiteSpace(v.Deepgram.Language)) p.Add("Voice:Deepgram:Model and Language must not be empty.");
        }
        if (configured.Contains("p2") || configured.Contains("p3"))
        {
            if (string.IsNullOrWhiteSpace(v.ElevenLabs.ApiKey)) p.Add("Voice:ElevenLabs:ApiKey is required for options p2 and p3.");
            if (string.IsNullOrWhiteSpace(v.ElevenLabs.VoiceId) || v.ElevenLabs.VoiceId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) p.Add("Voice:ElevenLabs:VoiceId is required (letters, digits, - and _ only).");
            if (string.IsNullOrWhiteSpace(v.ElevenLabs.TtsModel)) p.Add("Voice:ElevenLabs:TtsModel is required.");
            if (VendorHosts.ElevenLabs(v.ElevenLabs.Region) is null) p.Add("Voice:ElevenLabs:Region must be empty, us, eu, in or sg.");
        }
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
        if (VoicePipelineVendors.Supported("p2", v.Pipelines.P2))
        {
            var logs = sp.GetRequiredService<ILoggerFactory>();
            var sockets = sp.GetRequiredService<IVendorSocketConnector>();
            var llm = new OpenAiVoiceLlm(sp.GetRequiredService<IHttpClientFactory>().CreateClient(VoicePipelineVendors.LlmClient), v.OpenAiApiKey ?? "", v.Pipelines.Llm);
            yield return new VendorChainedParts("p2", new DeepgramStt(v.Deepgram, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.Deepgram")),
                new ElevenLabsTts(v.ElevenLabs, sockets, logs.CreateLogger("Solvelt.Marketplace.Api.Voice.ElevenLabs")), llm, v.PipelineMenu.EndpointMs);
        }
    }
}
