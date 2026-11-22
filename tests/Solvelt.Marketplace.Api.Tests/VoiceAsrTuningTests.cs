using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Recognition quality, vendor wiring (decision V-37), built against docs/agent-briefs/asr-vendor-facts.md: each vendor gets the shared catalog words in ITS format, bounded, and the Realtime line gets noise reduction and a patient turn detector.</summary>
public sealed class VoiceAsrTuningTests
{
    private sealed class FakeVocabulary(params string[] terms) : IVoiceVocabulary
    {
        public IReadOnlyList<string> Terms { get; } = terms;
        public string Hints(string? staticHints, int maxChars) => string.Join(", ", Terms);
    }

    private static readonly string[] Words = ["Challah Co", "Bagel & Bean", "Zabar's", "Honey Cake"];

    // ---------- Deepgram ----------

    [Fact]
    public void Deepgram_gets_one_keyterm_parameter_per_term_with_phrases_escaped_never_joined_and_the_usage_redacted()
    {
        var uri = DeepgramStt.Address(new DeepgramOptions { Model = "nova-3", Language = "en" }, Words).AbsoluteUri;
        Assert.Contains("&keyterm=Challah%20Co", uri);
        Assert.Contains("&keyterm=Bagel%20%26%20Bean", uri);
        Assert.Contains("&keyterm=Honey%20Cake", uri);
        Assert.Equal(4, uri.Split("&keyterm=").Length - 1);
        Assert.DoesNotContain("%2C", uri); Assert.DoesNotContain("%3B", uri);                              // no comma or semicolon joins: those "silently boost nothing"
        Assert.EndsWith("&redact_usage=true", uri);                                                       // keyterms would otherwise be stored in Deepgram's usage records
        Assert.Contains("encoding=mulaw&sample_rate=8000", uri);                                          // the audio format is untouched
    }

    [Fact]
    public void Deepgram_keyterms_are_bounded_only_for_nova_3_and_flux_and_every_setting_can_turn_them_off()
    {
        var many = Enumerable.Range(0, 200).Select(i => "Term" + i).ToArray();
        Assert.Equal(40, DeepgramStt.Address(new DeepgramOptions(), many).AbsoluteUri.Split("&keyterm=").Length - 1);             // KeytermMax 40
        Assert.Equal(10, DeepgramStt.Address(new DeepgramOptions { KeytermMax = 10 }, many).AbsoluteUri.Split("&keyterm=").Length - 1);
        var longTerms = Enumerable.Range(0, 100).Select(i => new string((char)('a' + i % 26), 40)).ToArray();                     // 40 terms of 40 characters would be 1600
        var chars = DeepgramStt.Address(new DeepgramOptions { KeytermMax = 100 }, longTerms).Query.Split('&').Where(p => p.StartsWith("keyterm=")).Sum(p => p.Length - 8);
        Assert.True(chars <= DeepgramStt.KeytermCharBudget);
        Assert.DoesNotContain("keyterm", DeepgramStt.Address(new DeepgramOptions { KeytermMax = 0 }, many).AbsoluteUri);
        Assert.DoesNotContain("keyterm", DeepgramStt.Address(new DeepgramOptions { Model = "nova-2" }, many).AbsoluteUri);       // keyterm is Nova-3 and Flux only
        Assert.Contains("keyterm", DeepgramStt.Address(new DeepgramOptions { Model = "flux-general-en" }, many).AbsoluteUri);
        Assert.DoesNotContain("redact_usage", DeepgramStt.Address(new DeepgramOptions { RedactUsage = false }, many).AbsoluteUri);
        Assert.DoesNotContain("keyterm", DeepgramStt.Address(new DeepgramOptions(), null).AbsoluteUri);                           // no vocabulary: the address is exactly as before
        Assert.DoesNotContain("redact_usage", DeepgramStt.Address(new DeepgramOptions(), []).AbsoluteUri);
    }

    [Fact]
    public void Deepgram_endpointing_is_sent_only_when_set_and_clamped()
    {
        Assert.DoesNotContain("endpointing", DeepgramStt.Address(new DeepgramOptions()).AbsoluteUri);
        Assert.Contains("&endpointing=400", DeepgramStt.Address(new DeepgramOptions { EndpointingMs = 400 }).AbsoluteUri);
        Assert.Contains("&endpointing=2000", DeepgramStt.Address(new DeepgramOptions { EndpointingMs = 99999 }).AbsoluteUri);
    }

    [Fact]
    public async Task The_deepgram_adapter_reads_the_current_catalog_words_when_a_call_opens()
    {
        var sockets = new FakeSocketConnector();
        var session = await new DeepgramStt(new DeepgramOptions { ApiKey = "k" }, sockets, new ListLogger(), new FakeVocabulary(Words)).OpenAsync(CancellationToken.None);
        await using var _s = session;
        var conn = await sockets.NextAsync();
        Assert.Contains("keyterm=Challah%20Co", conn.Uri.AbsoluteUri);
    }

    // ---------- Gladia ----------

    [Fact]
    public void Gladia_gets_the_custom_vocabulary_flag_and_config_together_short_and_with_the_default_intensity()
    {
        var body = GladiaStt.InitBody(new GladiaOptions { VocabularyMax = 3, VocabularyIntensity = 0.5 }, Words);
        var rp = body["realtime_processing"]!;
        Assert.True(rp["custom_vocabulary"]!.GetValue<bool>());
        var cfg = rp["custom_vocabulary_config"]!;
        Assert.Equal(["Challah Co", "Bagel & Bean", "Zabar's"], cfg["vocabulary"]!.AsArray().Select(x => x!.GetValue<string>()));   // capped at VocabularyMax
        Assert.Equal(0.5, cfg["default_intensity"]!.GetValue<double>());
        Assert.Equal("wav/ulaw", body["encoding"]!.GetValue<string>());                                  // the audio format is untouched
        Assert.Equal("yi", body["language_config"]!["languages"]![0]!.GetValue<string>());               // the language stays explicit
    }

    [Fact]
    public void Gladia_sends_nothing_extra_without_words_or_with_the_cap_at_zero_and_the_endpointing_preset_only_when_set()
    {
        Assert.Null(GladiaStt.InitBody(new GladiaOptions(), null)["realtime_processing"]);
        Assert.Null(GladiaStt.InitBody(new GladiaOptions(), [])["realtime_processing"]);
        Assert.Null(GladiaStt.InitBody(new GladiaOptions { VocabularyMax = 0 }, Words)["realtime_processing"]);
        var plain = GladiaStt.InitBody(new GladiaOptions(), null);
        Assert.Null(plain["endpointing"]); Assert.Null(plain["maximum_duration_without_endpointing"]);
        var preset = GladiaStt.InitBody(new GladiaOptions { EndpointingSeconds = 0.3, MaxSecondsWithoutEndpointing = 15 }, null);
        Assert.Equal(0.3, preset["endpointing"]!.GetValue<double>()); Assert.Equal(15, preset["maximum_duration_without_endpointing"]!.GetValue<int>());
        Assert.Equal(60, GladiaStt.InitBody(new GladiaOptions { MaxSecondsWithoutEndpointing = 999 }, null)["maximum_duration_without_endpointing"]!.GetValue<int>());   // range 5-60
        Assert.Equal(1.0, GladiaStt.InitBody(new GladiaOptions { VocabularyIntensity = 7 }, Words)["realtime_processing"]!["custom_vocabulary_config"]!["default_intensity"]!.GetValue<double>());   // 0 to 1
    }

    // ---------- Yiddish Labs ----------

    [Fact]
    public void The_yiddish_labs_prompt_is_the_configured_text_then_the_catalog_words_within_500_characters()
    {
        Assert.Equal("Shop talk. Words: Challah Co, Bagel & Bean, Zabar's, Honey Cake", CatalogVocabulary.Prompt("Shop talk.", Words, 500));
        Assert.Equal("Challah Co, Bagel & Bean", CatalogVocabulary.Prompt("", Words.Take(2).ToList(), 500));               // no configured prompt: just the words
        Assert.Equal("Shop talk.", CatalogVocabulary.Prompt("Shop talk.", null, 500));
        Assert.Equal("", CatalogVocabulary.Prompt(null, [], 500));
        var cut = CatalogVocabulary.Prompt("Shop talk.", Words, 40);
        Assert.True(cut.Length <= 40); Assert.StartsWith("Shop talk.", cut);
        Assert.Equal(500, CatalogVocabulary.Prompt(new string('p', 900), Words, 500).Length);              // a too long prompt is cut, the words are not forced in
    }

    [Fact]
    public async Task The_yiddish_labs_upload_carries_the_prompt_with_the_catalog_words_and_the_switch_turns_them_off()
    {
        foreach (var on in new[] { true, false })
        {
            var http = new FakeFlashHandler(FlashAnswer.Text("x"));
            var o = new YiddishLabsOptions { FlashApiKey = "yl_flash_not_real", TtsVoiceId = "v", ConsentLine = "c", SttPrompt = "Shop talk.", CatalogInPrompt = on };
            var session = await new YiddishLabsFlashStt(o, new HttpClient(http), new ListLogger(), new FakeVocabulary(Words)).OpenAsync(CancellationToken.None);
            var quiet = new byte[160 * 10]; Array.Fill(quiet, (byte)0xFF);
            var loud = new byte[160 * 25]; Array.Fill(loud, PhoneAudio.PcmToMulaw(8000));
            foreach (var chunk in new[] { quiet, loud, new byte[160 * 25].Select(_ => (byte)0xFF).ToArray() }) await session.SendAudioAsync(chunk, CancellationToken.None);
            await Task.Delay(400);
            await session.DisposeAsync();
            var prompt = http.Requests.Single().Fields["prompt"];
            Assert.Equal(on ? "Shop talk. Words: Challah Co, Bagel & Bean, Zabar's, Honey Cake" : "Shop talk.", prompt);
        }
    }

    // ---------- OpenAI Realtime (option 1) ----------

    private static async Task<JsonNode> SessionOf(Dictionary<string, string?>? config)
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, config);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        return (await fake.Wait("session.update"))["session"]!["audio"]!["input"]!;
    }

    [Fact]
    public async Task The_realtime_line_defaults_to_near_field_noise_reduction_and_a_patient_semantic_turn_detector()
    {
        var input = await SessionOf(null);
        Assert.Equal("near_field", input["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Equal(("semantic_vad", "low"), (input["turn_detection"]!["type"]!.GetValue<string>(), input["turn_detection"]!["eagerness"]!.GetValue<string>()));
        Assert.Equal("audio/pcmu", input["format"]!["type"]!.GetValue<string>());                          // the audio format is untouched
        Assert.True(input["turn_detection"]!["create_response"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_realtime_noise_reduction_and_eagerness_are_settings_and_none_or_server_vad_leave_them_out()
    {
        var far = await SessionOf(new() { ["Voice:NoiseReduction"] = "far_field", ["Voice:Eagerness"] = "high" });
        Assert.Equal("far_field", far["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Equal("high", far["turn_detection"]!["eagerness"]!.GetValue<string>());
        var none = await SessionOf(new() { ["Voice:NoiseReduction"] = "none", ["Voice:TurnDetection"] = "server_vad" });
        Assert.Null(none["noise_reduction"]);                                                             // "none" sends no filter at all
        Assert.Null(none["turn_detection"]!["eagerness"]);                                                // eagerness belongs to semantic_vad only
    }

    [Fact]
    public void Wrong_realtime_values_are_a_start_up_problem()
    {
        List<string> Problems(Action<VoiceOptions> f) { var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('t', 40), PhoneCodeKey = new string('p', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://voice.test" }; f(o); return VoiceConfig.Problems(o, new SignalWireOptions { SigningKey = "sw" }, true).ToList(); }
        Assert.DoesNotContain(Problems(_ => { }), p => p.Contains("Eagerness") || p.Contains("NoiseReduction"));
        Assert.Contains(Problems(o => o.Eagerness = "eager"), p => p.Contains("Voice:Eagerness"));
        Assert.Contains(Problems(o => o.NoiseReduction = "studio"), p => p.Contains("Voice:NoiseReduction"));
    }
}
