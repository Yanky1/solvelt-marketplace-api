using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Option p2 end to end through the REAL adapters (Deepgram speech to text, the OpenAI text model, ElevenLabs text to speech) with fake vendors behind them (decision V-22): the menu choice, the greeting, a caller turn, and the
/// start-up rules. Off by default: without the settings the option is "not available on this line".
/// </summary>
public sealed class VoiceP2PipelineTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();

    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }

    internal static Dictionary<string, string?> P2Settings() => new()
    {
        ["Voice:Pipelines:P2:Stt"] = "deepgram", ["Voice:Pipelines:P2:Tts"] = "elevenlabs", ["Voice:Pipelines:Llm:Model"] = "model-x", ["Voice:Pipelines:Llm:Provider"] = "openai",
        ["Voice:Deepgram:ApiKey"] = "dg-key-not-real", ["Voice:ElevenLabs:ApiKey"] = "el-key-not-real", ["Voice:ElevenLabs:VoiceId"] = "voice_ABC", ["Voice:ElevenLabs:TtsModel"] = "model-t",
    };

    private async Task<(FakeSocketConnector Sockets, FakeLlmHandler Llm)> Call()
    {
        var sockets = new FakeSocketConnector();
        var llm = new FakeLlmHandler { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P2Settings()), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
        });
        _ = Task.Run(() => ElevenLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "2" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return (sockets, llm);
    }

    private VoiceScenario S => _s!;

    /// <summary>The fake ElevenLabs: for every sentence it is asked to speak it answers two audio chunks of 800 bytes (all 7s) and the final frame.</summary>
    internal static async Task ElevenLabsVendor(FakeSocketConnector sockets, CancellationToken ct)
    {
        var seen = 0;
        while (!ct.IsCancellationRequested)
        {
            var all = sockets.Connections;
            while (seen < all.Count)
            {
                var c = all[seen++];
                if (!c.Uri.Host.Contains("elevenlabs")) continue;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 3; i++) await MemorySocketPair.ReceiveAsync(c.Server);
                        var audio = $"{{\"audio\":\"{Convert.ToBase64String(Enumerable.Repeat((byte)7, 800).ToArray())}\"}}";
                        await MemorySocketPair.SendTextAsync(c.Server, audio); await MemorySocketPair.SendTextAsync(c.Server, audio);
                        await MemorySocketPair.SendTextAsync(c.Server, """{"isFinal":true}""");
                    }
                    catch { /* the adapter closed first */ }
                });
            }
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private async Task Advance(int ms) { for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); } }
    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);
    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);

    [Fact]
    public async Task The_greeting_is_written_by_the_text_model_and_spoken_with_the_vendors_audio_and_the_call_is_recorded_as_p2()
    {
        var (sockets, llm) = await Call();
        await Until(() => Frames("media").Count() >= 4);                                       // two sentences, two chunks each
        Assert.All(Frames("media"), m => Assert.Equal(Enumerable.Repeat((byte)7, 800), Convert.FromBase64String(m["media"]!["payload"]!.GetValue<string>())));
        Assert.Single(sockets.Connections, c => c.Uri.Host == "api.deepgram.com");             // ONE recogniser for the call: the one opened before the phone was accepted is the one used
        Assert.Equal(2, sockets.Connections.Count(c => c.Uri.Host == "api.elevenlabs.io"));    // one speech socket per sentence
        var first = JsonNode.Parse(llm.Requests[0].Body)!;
        Assert.Equal(VoiceScripts.Instructions, first["instructions"]!.GetValue<string>());     // the SAME instructions as the Realtime line
        var runner = S.F.Services.GetRequiredService<VoiceToolRunner>();
        Assert.Equal(runner.Definitions().Select(d => d!["name"]!.GetValue<string>()).Order(), first["tools"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).Order());   // the SAME tools
        Assert.False(first["store"]!.GetValue<bool>());
        var row = await S.Row();
        Assert.Equal(("p2", "pipeline:p2"), (row.Tier, row.Model));
        Assert.Empty(S.Fake.Of("session.update"));                                             // not the Realtime line
    }

    [Fact]
    public async Task A_caller_turn_goes_audio_to_Deepgram_then_words_to_the_text_model_then_speech_back()
    {
        var (sockets, llm) = await Call();
        await Until(() => Frames("media").Count() >= 4);
        var dg = await sockets.NextAsync();
        Assert.Equal("api.deepgram.com", dg.Uri.Host);
        await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) } });
        var audio = await MemorySocketPair.ReceiveAsync(dg.Server);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, audio!.Value.Data);                              // the telephone's bytes, untouched, as mu-law

        var before = llm.Requests.Count;
        await MemorySocketPair.SendTextAsync(dg.Server, """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"find me a challah","confidence":0.95}]}}""");
        await Advance(900);                                                                      // the silence that ends the caller's turn
        await Until(() => llm.Requests.Count > before);
        var input = JsonNode.Parse(llm.Requests[^1].Body)!["input"]!.AsArray();
        Assert.Contains(input, i => i!["role"]?.GetValue<string>() == "user" && i["content"]!.GetValue<string>() == "find me a challah");
        await Until(() => Frames("media").Count() >= 8);                                         // and the answer is spoken
    }

    [Fact]
    public async Task Without_the_settings_the_option_is_not_available_and_the_caller_is_told_and_gets_the_normal_line()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig());
        Assert.False(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p2"));
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "2" });
        var xml = System.Xml.Linq.XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!;
        Assert.Contains(VoicePipelineIds.Realtime, (await S.Row()).Tier);
        Assert.Contains(PipelineScripts.Unavailable, xml.Descendants("Say").Select(e => e.Value));
    }

    [Fact]
    public async Task With_the_settings_the_registry_offers_p2_and_still_not_p3_to_p5()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P2Settings()));
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.True(registry.IsAvailable("p1")); Assert.True(registry.IsAvailable("p2"));
        Assert.False(registry.IsAvailable("p3")); Assert.False(registry.IsAvailable("p4")); Assert.False(registry.IsAvailable("p5"));
        await Task.CompletedTask;
    }

    // ---------- start-up rules ----------

    private static VoiceOptions V(Action<VoiceOptions>? f = null)
    {
        var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
        o.PipelineMenu.Enabled = true;                                           // invalid values are only validated when the menu is on
        f?.Invoke(o); return o;
    }
    private static IReadOnlyList<string> Problems(Action<VoiceOptions>? f = null) => VoiceConfig.Problems(V(f), new SignalWireOptions { SigningKey = "sw" }, true);
    private static void Full(VoiceOptions o)
    {
        o.Pipelines.P2 = new PipelineProviders { Stt = "deepgram", Tts = "elevenlabs" }; o.Pipelines.Llm.Model = "m"; o.Pipelines.Llm.Provider = "openai";
        o.Deepgram.ApiKey = "k"; o.ElevenLabs.ApiKey = "k"; o.ElevenLabs.VoiceId = "voice_1"; o.ElevenLabs.TtsModel = "t";
    }

    [Fact]
    public void Nothing_configured_is_fine_and_a_complete_p2_is_fine()
    {
        Assert.Empty(Problems());
        Assert.Empty(Problems(Full));
    }

    [Fact]
    public void Start_up_refuses_half_a_pair_an_unsupported_pair_and_invalid_values_but_only_with_the_menu_on()
    {
        Assert.Contains(Problems(o => o.Pipelines.P2.Stt = "deepgram"), p => p.Contains("both Stt and Tts"));
        Assert.Contains(Problems(o => { Full(o); o.Pipelines.P2.Tts = "somewhere"; }), p => p.Contains("no adapter"));
        Assert.Contains(Problems(o => { Full(o); o.Pipelines.P5 = new P5Options { Stt = "gladia", Tts = "yiddishlabs" }; }), p => p.Contains("P5") && p.Contains("no adapter"));   // later slices
        Assert.Contains(Problems(o => { Full(o); o.Pipelines.Llm.TimeoutSeconds = 0; }), p => p.Contains("TimeoutSeconds"));
        Assert.Contains(Problems(o => { Full(o); o.ElevenLabs.VoiceId = "../evil"; }), p => p.Contains("VoiceId"));
        Assert.Contains(Problems(o => { Full(o); o.ElevenLabs.Region = "evil.example.com"; }), p => p.Contains("Region"));
        // the menu off: nobody can reach the options, so nothing about them stops the start
        Assert.Empty(Problems(o => { Full(o); o.PipelineMenu.Enabled = false; o.ElevenLabs.Region = "evil.example.com"; o.Pipelines.P2.Stt = ""; }));
    }

    [Fact]
    public void A_missing_setting_never_refuses_start_up_and_the_option_names_what_it_lacks()
    {
        // before: each of these was a start-up refusal; now the same condition is detected (Missing names the setting) and the outcome is "unavailable" (see the registry test below)
        foreach (var (name, blank) in new (string, Action<VoiceOptions>)[]
        {
            ("Voice:Pipelines:Llm:Model", o => o.Pipelines.Llm.Model = ""), ("Voice:Deepgram:ApiKey", o => o.Deepgram.ApiKey = ""), ("Voice:Deepgram:Language", o => o.Deepgram.Language = ""),
            ("Voice:Deepgram:Model", o => o.Deepgram.Model = ""), ("Voice:ElevenLabs:ApiKey", o => o.ElevenLabs.ApiKey = ""), ("Voice:ElevenLabs:VoiceId", o => o.ElevenLabs.VoiceId = ""), ("Voice:ElevenLabs:TtsModel", o => o.ElevenLabs.TtsModel = ""),
        })
        {
            Assert.Empty(Problems(o => { Full(o); blank(o); }));                                       // start-up goes on
            Assert.Contains(name, VoicePipelineVendors.Missing(V(o => { Full(o); blank(o); }), "p2"));  // and the option says what it lacks
        }
        Assert.Empty(VoicePipelineVendors.Missing(V(Full), "p2"));
    }

    [Fact]
    public void A_partial_p2_configuration_gives_ONE_warning_naming_the_setting_and_no_option()
    {
        var lines = new ListLogger();
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(lines)).BuildServiceProvider();
        var o = V(x => { Full(x); x.Deepgram.ApiKey = "dg-secret-value"; x.ElevenLabs.VoiceId = ""; });
        Assert.Empty(new VendorPartsSource(sp).Create(o));
        var warnings = lines.Lines.Where(l => l.Contains("p2")).ToList();
        Assert.Single(warnings);
        Assert.Contains("Voice:ElevenLabs:VoiceId", warnings[0]);
        Assert.DoesNotContain("dg-secret-value", warnings[0]);
    }

    private sealed class OneLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    [Fact]
    public async Task An_app_with_a_partial_p2_configuration_starts_and_serves_p1()
    {
        var more = P2Settings(); more.Remove("Voice:ElevenLabs:VoiceId");
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: more));                       // the host builds and the call works
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.True(registry.IsAvailable("p1")); Assert.False(registry.IsAvailable("p2"));
        await S.SyncCatalog();
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        Assert.True(res.IsSuccessStatusCode);
        var x = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!;
        Assert.NotNull(x.Descendants("Gather").FirstOrDefault() ?? x);                                       // the menu is answered, nothing refused
        var pick = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "2" });
        var xml = XDocument.Parse(await pick.Content.ReadAsStringAsync()).Root!;
        Assert.Equal(PipelineScripts.Unavailable, xml.Element("Say")!.Value);                                 // choosing p2 is "not available on this line"
        Assert.Equal("p1", (await S.Row()).Tier);                                                            // and the call is on the Realtime line
    }

    [Fact]
    public void The_secrets_are_never_part_of_a_problem_message()
    {
        var o2 = V(o => { Full(o); o.Deepgram.ApiKey = ""; o.ElevenLabs.VoiceId = "../evil"; o.ElevenLabs.ApiKey = "el-secret-value"; });
        var all = string.Join("\n", VoiceConfig.Problems(o2, new SignalWireOptions { SigningKey = "sw" }, true).Concat(VoicePipelineVendors.Missing(o2, "p2")));
        Assert.DoesNotContain("el-secret-value", all);
    }
}

/// <summary>Option p3 end to end through the real Gladia and ElevenLabs adapters and the text model, with fake vendors behind them.</summary>
public sealed class VoiceP3PipelineTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();

    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    internal static Dictionary<string, string?> P3Settings() => new()
    {
        ["Voice:Pipelines:P3:Stt"] = "gladia", ["Voice:Pipelines:P3:Tts"] = "elevenlabs", ["Voice:Pipelines:Llm:Model"] = "model-x", ["Voice:Pipelines:Llm:Provider"] = "openai",
        ["Voice:Gladia:ApiKey"] = "gl-key-not-real", ["Voice:ElevenLabs:ApiKey"] = "el-key-not-real", ["Voice:ElevenLabs:VoiceId"] = "voice_ABC", ["Voice:ElevenLabs:TtsModel"] = "model-t",
    };

    private async Task<(FakeSocketConnector Sockets, FakeLlmHandler Llm, FakeLlmHandler Gladia)> Call()
    {
        var sockets = new FakeSocketConnector();
        var llm = new FakeLlmHandler { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
        var gladia = new FakeLlmHandler { Body = """{"id":"s1","url":"wss://api.gladia.io/v2/live?token=one-time"}""" };
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P3Settings()), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
            svc.AddHttpClient(VoicePipelineVendors.GladiaClient).ConfigurePrimaryHttpMessageHandler(() => gladia);
        });
        _ = Task.Run(() => VoiceP2PipelineTests.ElevenLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "3" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return (sockets, llm, gladia);
    }

    private async Task Advance(int ms) { for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); } }
    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);
    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);

    [Fact]
    public async Task A_Yiddish_speaking_turn_goes_audio_to_Gladia_then_words_to_the_text_model_then_English_speech_back()
    {
        var (sockets, llm, gladia) = await Call();
        await Until(() => Frames("media").Count() >= 4);
        Assert.Equal(("p3", "pipeline:p3"), ((await S.Row()).Tier, (await S.Row()).Model));
        Assert.Equal("https://api.gladia.io/v2/live", gladia.Requests.Single().Uri!.ToString());
        var g = sockets.Connections.First(c => c.Uri.Host == "api.gladia.io");
        await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(new byte[] { 5, 6, 7 }) } });
        Assert.Equal(new byte[] { 5, 6, 7 }, (await MemorySocketPair.ReceiveAsync(g.Server))!.Value.Data);          // the telephone's bytes, as mu-law

        var before = llm.Requests.Count;
        await MemorySocketPair.SendTextAsync(g.Server, """{"type":"transcript","data":{"id":"u1","is_final":true,"utterance":{"text":"איך וויל א חלה","language":"yi"}}}""");
        await Advance(900);
        await Until(() => llm.Requests.Count > before);
        var input = JsonNode.Parse(llm.Requests[^1].Body)!["input"]!.AsArray();
        Assert.Contains(input, i => i!["role"]?.GetValue<string>() == "user" && i["content"]!.GetValue<string>() == "איך וויל א חלה");
        Assert.Contains(sockets.Connections, c => c.Uri.Host == "api.elevenlabs.io");                                  // the answer is spoken by ElevenLabs
        Assert.DoesNotContain(sockets.Connections, c => c.Uri.Host.Contains("yiddishlabs"));                            // Yiddish Labs is not part of p3
        Assert.Empty(S.Fake.Of("session.update"));
    }

    [Fact]
    public async Task P3_is_offered_only_with_its_settings_and_p2_is_not_offered_by_them()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P3Settings()));
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.True(registry.IsAvailable("p3")); Assert.False(registry.IsAvailable("p2")); Assert.False(registry.IsAvailable("p4"));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_p3_without_the_Gladia_key_or_with_an_empty_model_or_language_is_unavailable_not_a_start_up_failure()
    {
        VoiceOptions V(Action<VoiceOptions> f) { var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" }; o.PipelineMenu.Enabled = true; f(o); return o; }
        void Ok(VoiceOptions o) { o.Pipelines.P3 = new PipelineProviders { Stt = "gladia", Tts = "elevenlabs" }; o.Pipelines.Llm.Model = "m"; o.Pipelines.Llm.Provider = "openai"; o.Gladia.ApiKey = "k"; o.ElevenLabs.ApiKey = "k"; o.ElevenLabs.VoiceId = "v1"; o.ElevenLabs.TtsModel = "t"; }
        var sw = new SignalWireOptions { SigningKey = "sw" };
        Assert.Empty(VoiceConfig.Problems(V(Ok), sw, true));
        Assert.Empty(VoiceConfig.Problems(V(o => { Ok(o); o.Gladia.ApiKey = ""; }), sw, true));
        Assert.Contains("Voice:Gladia:ApiKey", VoicePipelineVendors.Missing(V(o => { Ok(o); o.Gladia.ApiKey = ""; }), "p3"));
        Assert.Empty(VoiceConfig.Problems(V(o => { Ok(o); o.Gladia.Language = ""; }), sw, true));
        Assert.Contains("Voice:Gladia:Language", VoicePipelineVendors.Missing(V(o => { Ok(o); o.Gladia.Language = ""; }), "p3"));
        Assert.Empty(VoiceConfig.Problems(V(o => { Ok(o); o.ElevenLabs.ApiKey = ""; }), sw, true));
        Assert.Contains("Voice:ElevenLabs:ApiKey", VoicePipelineVendors.Missing(V(o => { Ok(o); o.ElevenLabs.ApiKey = ""; }), "p3"));
    }
}
