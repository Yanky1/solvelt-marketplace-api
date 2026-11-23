using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>One recogniser setting to score: a name, the option whose recogniser it is (p2 to p5), configuration overrides on top of the base settings, and optionally the catalog words to listen for.</summary>
public sealed record VariantSpec(string Name, string Option, IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string>? Vocabulary = null);

/// <summary>Builds the recognisers of the scoring tool from the SAME configuration and adapters the telephone line uses (decision V-46). Nothing connects until a recording is played.</summary>
public sealed class ScoringSetup : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    private sealed class FixedVocabulary(IReadOnlyList<string> terms) : IVoiceVocabulary
    {
        public IReadOnlyList<string> Terms => terms;
        public string Hints(string? staticHints, int maxChars) => CatalogVocabulary.Join(staticHints, terms, maxChars);
    }

    /// <summary>The options whose recogniser can be replayed from audio: p2 to p5, when their settings are complete. Option 1 is OpenAI's speech-to-speech line and option 6 is SignalWire's own recogniser inside the call; neither takes recorded audio.</summary>
    public static IReadOnlyList<string> ReplayableOptions(IConfiguration baseConfig)
    {
        var v = Bind(baseConfig, new Dictionary<string, string>());
        return new[] { "p2", "p3", "p4", "p5", "p7" }.Where(o => VoicePipelineVendors.Supported(o, v.Pipelines.For(o)) && VoicePipelineVendors.Missing(v, o).Count == 0).ToList();
    }

    public static IReadOnlyList<VariantSpec> DefaultVariants(IConfiguration baseConfig)
        => ReplayableOptions(baseConfig).Select(o => new VariantSpec(o, o, new Dictionary<string, string>())).ToList();

    private static VoiceOptions Bind(IConfiguration baseConfig, IReadOnlyDictionary<string, string> overrides)
    {
        var cfg = new ConfigurationBuilder().AddConfiguration(baseConfig).AddInMemoryCollection(overrides.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))).Build();
        var v = new VoiceOptions();
        VoiceConfig.Bind(v, cfg, devLike: true);
        // the text model is never contacted when replaying audio, but building an option wants its key to exist: a placeholder, never sent anywhere
        if (string.IsNullOrWhiteSpace(v.Anthropic.ApiKey)) v.Anthropic.ApiKey = "replay-only-not-used";
        // likewise the speech side: replaying audio plays nothing, so the voice settings of an option need not be real. (A judge that IS used by option 5 needs its own real key.)
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.ApiKey)) v.ElevenLabs.ApiKey = "replay-only-not-used";
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.VoiceId)) v.ElevenLabs.VoiceId = "replay-only";
        if (string.IsNullOrWhiteSpace(v.ElevenLabs.TtsModel)) v.ElevenLabs.TtsModel = "replay-only";
        if (string.IsNullOrWhiteSpace(v.YiddishLabs.TtsVoiceId)) v.YiddishLabs.TtsVoiceId = "replay-only";
        if (string.IsNullOrWhiteSpace(v.YiddishLabs.ConsentLine)) v.YiddishLabs.ConsentLine = "replay-only";
        return v;
    }

    /// <summary>The recogniser of one variant. Throws <see cref="InvalidOperationException"/> naming the settings that are missing (never their values).</summary>
    public ScoringVariant Build(IConfiguration baseConfig, VariantSpec spec)
    {
        if (spec.Option is not ("p2" or "p3" or "p4" or "p5" or "p7")) throw new InvalidOperationException($"Variant {spec.Name}: option {spec.Option} cannot be replayed from audio (use p2, p3, p4, p5 or p7).");
        var v = Bind(baseConfig, spec.Settings);
        if (!VoicePipelineVendors.Supported(spec.Option, v.Pipelines.For(spec.Option))) throw new InvalidOperationException($"Variant {spec.Name}: option {spec.Option} has no recogniser configured (Voice:Pipelines).");
        var missing = VoicePipelineVendors.Missing(v, spec.Option);
        if (missing.Count > 0) throw new InvalidOperationException($"Variant {spec.Name}: missing setting(s) {string.Join(", ", missing)}.");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true));
        services.AddHttpClient();
        VoicePipelineVendors.AddVoicePipelineVendors(services);
        if (spec.Vocabulary is { Count: > 0 }) services.AddSingleton<IVoiceVocabulary>(new FixedVocabulary(spec.Vocabulary));
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        var parts = sp.GetRequiredService<IChainedPartsSource>().Create(v).FirstOrDefault(p => p.OptionId == spec.Option)
            ?? throw new InvalidOperationException($"Variant {spec.Name}: option {spec.Option} is not available with these settings.");
        return new ScoringVariant(spec.Name, parts.Stt);
    }

    public void Dispose() { foreach (var p in _providers) p.Dispose(); _providers.Clear(); }

    /// <summary>A matrix file: <c>{"variants":[{"name":"...","option":"p2","settings":{"Voice:Deepgram:EndpointingMs":"400"},"vocabulary":["challah","kiddush"]}]}</c>. Names are unique and plain.</summary>
    public static IReadOnlyList<VariantSpec> ParseMatrix(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { throw new FormatException("The matrix file is not valid JSON."); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("variants", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) throw new FormatException("The matrix file needs a non-empty \"variants\" array.");
            var list = new List<VariantSpec>();
            foreach (var e in arr.EnumerateArray())
            {
                string Get(string n) => e.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()! : "";
                var name = Get("name"); var option = Get("option");
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9 ._+-]{0,27}$")) throw new FormatException($"Variant name \"{name}\" must be 1 to 28 plain characters.");
                if (list.Any(x => x.Name == name)) throw new FormatException($"Variant name {name} appears twice.");
                if (option is not ("p2" or "p3" or "p4" or "p5" or "p7")) throw new FormatException($"Variant {name}: option must be p2, p3, p4, p5 or p7.");
                var settings = new Dictionary<string, string>();
                if (e.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object)
                    foreach (var kv in st.EnumerateObject())
                    {
                        if (!kv.Name.StartsWith("Voice:", StringComparison.Ordinal)) throw new FormatException($"Variant {name}: only Voice:... settings can be overridden ({kv.Name}).");
                        settings[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString()! : kv.Value.ToString();
                    }
                List<string>? vocab = null;
                if (e.TryGetProperty("vocabulary", out var vo) && vo.ValueKind == JsonValueKind.Array) vocab = vo.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                list.Add(new VariantSpec(name, option, settings, vocab));
            }
            return list;
        }
    }
}
