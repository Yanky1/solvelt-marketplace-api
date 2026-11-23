using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Option 7 (decision V-48), Ronald's choice of 2026-11-23: OpenAI live transcription listens, Claude is the agent and ElevenLabs speaks. Off by default; the V-26 tolerance rule (a missing setting makes it unavailable with
/// one warning, never a start-up failure); the same fallback to the Realtime line as the other chained options; chosen by SAYING "seven" on the speech-only menu.
/// </summary>
public sealed class VoiceP7PipelineTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();
    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    /// <summary>Everything option 7 needs: the OpenAI key (the Realtime line's own), the voice, and the text model (the OpenAI one is an explicit override only so the fake text API can answer; the default is Claude).</summary>
    internal static Dictionary<string, string?> P7Settings(bool enabled = true) => new()
    {
        ["Voice:Pipelines:P7:Enabled"] = enabled ? "true" : "false", ["Voice:Pipelines:Llm:Model"] = "model-x", ["Voice:Pipelines:Llm:Provider"] = "openai",
        ["Voice:ElevenLabs:ApiKey"] = "el-key-not-real", ["Voice:ElevenLabs:VoiceId"] = "voice_ABC", ["Voice:ElevenLabs:TtsModel"] = "model-t",
    };

    private static XElement Root(HttpResponseMessage r) => XDocument.Parse(r.Content.ReadAsStringAsync().Result).Root!;

    private async Task<(FakeSocketConnector Sockets, FakeLlmHandler Llm)> Call(Dictionary<string, string?>? settings = null, FakeSocketConnector? sockets = null, string? said = null, string digits = "7")
    {
        sockets ??= new FakeSocketConnector();
        var llm = new FakeLlmHandler { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
        var more = settings ?? P7Settings(); more["Voice:ModelMode"] = "mini";
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: more), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
        });
        _ = Task.Run(() => VoiceP2PipelineTests.ElevenLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: said is null ? new() { ["Digits"] = digits } : new() { ["SpeechResult"] = said });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return (sockets, llm);
    }

    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);
    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);

    // ---------- off by default, and what the menu says ----------

    [Fact]
    public async Task It_is_off_by_default_even_with_every_key_and_the_menu_neither_offers_nor_grants_it()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P7Settings(enabled: false)));
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.False(registry.IsAvailable("p7"));
        await S.SyncCatalog();
        var menu = Root(await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number));
        Assert.DoesNotContain("seven", string.Join(" ", menu.Descendants("Say").Select(e => e.Value)));
        var pick = Root(await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "7" }));
        Assert.Equal(PipelineScripts.Unavailable, pick.Element("Say")!.Value);                                         // "not available on this line", then the standard line
        Assert.Equal("p1", (await S.Row()).Tier);
    }

    [Fact]
    public async Task When_enabled_the_menu_reads_the_seventh_option_with_its_default_or_configured_description()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P7Settings()));
        Assert.True(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p7"));
        await S.SyncCatalog();
        var menu = string.Join(" ", Root(await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number)).Descendants("Say").Select(e => e.Value));
        Assert.Contains("Say seven for OpenAI listening, Claude, ElevenLabs voice.", menu);
        _s.Dispose();
        var custom = P7Settings(); custom["Voice:PipelineMenu:Descriptions:P7"] = "The newest line.";
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: custom));
        await S.SyncCatalog();
        var menu2 = string.Join(" ", Root(await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number)).Descendants("Say").Select(e => e.Value));
        Assert.Contains("Say seven for The newest line.", menu2);
        Assert.DoesNotContain("OpenAI listening", menu2);
    }

    [Fact]
    public void The_description_and_the_option_number_are_part_of_the_menu_contract()
    {
        Assert.Equal("OpenAI listening, Claude, ElevenLabs voice.", new PipelineDescriptions().For("p7"));
        Assert.Equal(["p1", "p2", "p3", "p4", "p5", "p6", "p7"], VoicePipelineIds.All.ToArray());
        Assert.Equal(("p7", "p1", "p1"), (VoicePipelineIds.FromDigits("7"), VoicePipelineIds.FromDigits("8"), VoicePipelineIds.FromDigits("77")));
        Assert.Equal("7", SpokenDigits.Choice("seven")); Assert.Equal("7", SpokenDigits.Choice("option seven please"));
        Assert.Contains("Voice:PipelineMenu:Descriptions:P7", new PipelineDescriptions { P7 = new string('x', 301) }.TooLong());
    }

    // ---------- the V-26 tolerance rule ----------

    private static VoiceOptions V(Action<VoiceOptions>? f = null)
    {
        var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
        o.PipelineMenu.Enabled = true; o.Anthropic.ApiKey = "a"; o.Pipelines.P7.Enabled = true; o.Pipelines.Llm.Model = "m";
        o.ElevenLabs.ApiKey = "e"; o.ElevenLabs.VoiceId = "voice_1"; o.ElevenLabs.TtsModel = "t";
        f?.Invoke(o); return o;
    }

    [Fact]
    public void A_missing_setting_never_refuses_start_up_and_the_option_names_what_it_lacks()
    {
        Assert.Empty(VoicePipelineVendors.Missing(V(), "p7"));
        foreach (var (name, blank) in new (string, Action<VoiceOptions>)[]
        {
            ("Voice:OpenAiApiKey", o => o.OpenAiApiKey = ""), ("Voice:Anthropic:ApiKey", o => o.Anthropic.ApiKey = ""), ("Voice:ElevenLabs:ApiKey", o => o.ElevenLabs.ApiKey = ""),
            ("Voice:ElevenLabs:VoiceId", o => o.ElevenLabs.VoiceId = ""), ("Voice:ElevenLabs:TtsModel", o => o.ElevenLabs.TtsModel = ""), ("Voice:OpenAiTranscribe:Model", o => o.OpenAiTranscribe.Model = ""),
        })
        {
            Assert.Contains(name, VoicePipelineVendors.Missing(V(blank), "p7"));
            Assert.DoesNotContain(VoiceConfig.Problems(V(blank), new SignalWireOptions { SigningKey = "sw" }, true), p => p.Contains("P7"));   // start-up goes on
        }
        Assert.Contains("Voice:Pipelines:P7:Enabled", VoicePipelineVendors.Missing(V(o => o.Pipelines.P7.Enabled = false), "p7"));
    }

    [Fact]
    public void A_partial_configuration_gives_ONE_warning_naming_the_setting_and_no_option()
    {
        var lines = new ListLogger();
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(lines)).BuildServiceProvider();
        Assert.Empty(new VendorPartsSource(sp).Create(V(o => { o.OpenAiApiKey = ""; o.ElevenLabs.ApiKey = "el-secret-value"; })));
        var warnings = lines.Lines.Where(l => l.Contains("p7")).ToList();
        Assert.Single(warnings);
        Assert.Contains("Voice:OpenAiApiKey", warnings[0]);
        Assert.DoesNotContain("el-secret-value", warnings[0]);
        var quiet = new ListLogger();
        var sp2 = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(quiet)).BuildServiceProvider();
        Assert.Empty(new VendorPartsSource(sp2).Create(V(o => o.Pipelines.P7.Enabled = false)));                      // off: no parts and no warning at all
        Assert.Empty(quiet.Lines);
    }

    private sealed class OneLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    [Fact]
    public void Claude_is_the_agent_by_default_and_the_parts_are_OpenAi_listening_and_ElevenLabs_speaking()
    {
        var services = new ServiceCollection().AddLogging().AddHttpClient();
        VoicePipelineVendors.AddVoicePipelineVendors(services);
        using var provider = services.BuildServiceProvider();
        var o = V(x => x.Pipelines.Llm.Provider = "anthropic");
        var p7 = provider.GetRequiredService<IChainedPartsSource>().Create(o).Single(p => p.OptionId == "p7");
        Assert.IsType<AnthropicVoiceLlm>(p7.Llm);                                                                      // Claude
        Assert.IsType<OpenAiTranscribeStt>(p7.Stt);                                                                    // OpenAI listening
        Assert.IsType<ElevenLabsTts>(p7.Tts);                                                                          // ElevenLabs voice
        Assert.IsType<FinalTurnDetector>(p7.NewTurnDetector());                                                        // our detector already waited
    }

    // ---------- a call ----------

    [Theory]
    [InlineData(null, "7")]
    [InlineData("seven", null)]
    [InlineData("option seven please", null)]
    public async Task Choosing_it_by_digit_or_by_saying_seven_starts_the_call_on_OpenAi_ElevenLabs_and_the_text_model_with_the_same_instructions_and_tools(string? said, string? digits)
    {
        var (sockets, llm) = await Call(said: said, digits: digits ?? "7");
        await Until(() => Frames("media").Count() >= 4);                                                                // two sentences, two chunks each
        Assert.All(Frames("media"), m => Assert.Equal(Enumerable.Repeat((byte)7, 800), Convert.FromBase64String(m["media"]!["payload"]!.GetValue<string>())));
        var openai = Assert.Single(sockets.Connections, c => c.Uri.Host == "api.openai.com");                           // ONE recogniser for the call
        Assert.Equal("?intent=transcription", openai.Uri.Query);
        Assert.DoesNotContain(sockets.Connections, c => c.Uri.Host is "api.deepgram.com" or "api.gladia.io");
        Assert.Equal(2, sockets.Connections.Count(c => c.Uri.Host == "api.elevenlabs.io"));
        var first = JsonNode.Parse(llm.Requests[0].Body)!;
        Assert.Equal(VoiceScripts.Instructions, first["instructions"]!.GetValue<string>());                             // the SAME instructions as the Realtime line
        var runner = S.F.Services.GetRequiredService<VoiceToolRunner>();
        Assert.Equal(runner.Definitions().Select(d => d!["name"]!.GetValue<string>()).Order(), first["tools"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).Order());   // the SAME tools
        var row = await S.Row();
        Assert.Equal(("p7", "pipeline:p7"), (row.Tier, row.Model));
        Assert.Empty(S.Fake.Of("session.update"));                                                                      // not the Realtime line
    }

    [Fact]
    public async Task A_caller_turn_goes_audio_to_OpenAi_then_its_final_words_to_the_text_model_then_speech_back()
    {
        var (sockets, llm) = await Call();
        await Until(() => Frames("media").Count() >= 4);
        var openai = sockets.Connections.Single(c => c.Uri.Host == "api.openai.com");
        await MemorySocketPair.ReceiveAsync(openai.Server);                                                             // session.update
        static byte[] Frame(double a, int ph = 0) { var b = new byte[160]; for (var i = 0; i < 160; i++) b[i] = PhoneAudio.PcmToMulaw((short)(a * Math.Sin((i + ph) * 0.4))); return b; }
        for (var i = 0; i < 12; i++) await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(Frame(9000, i)) } });
        for (var i = 0; i < 40; i++) await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(Frame(15)) } });
        var types = new List<string>();
        while (!types.Contains("input_audio_buffer.commit")) types.Add(System.Text.Json.JsonDocument.Parse((await MemorySocketPair.ReceiveAsync(openai.Server))!.Value.Data).RootElement.GetProperty("type").GetString()!);
        var before = llm.Requests.Count;
        await MemorySocketPair.SendTextAsync(openai.Server, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"A","transcript":"find me a challah"}""");
        await Until(() => llm.Requests.Count > before);
        Assert.Contains(JsonNode.Parse(llm.Requests[^1].Body)!["input"]!.AsArray(), i => i!["role"]?.GetValue<string>() == "user" && i["content"]!.GetValue<string>() == "find me a challah");
        await Until(() => Frames("media").Count() >= 8);                                                                // and the answer is spoken
    }

    [Fact]
    public async Task When_OpenAi_refuses_at_the_start_the_call_goes_on_on_the_Realtime_line_and_is_counted_for_option_7()
    {
        await Call(sockets: new FakeSocketConnector { FailWhen = u => u.Host == "api.openai.com" && u.Query.Contains("intent=transcription") });
        await Until(() => S.Fake.Of("session.update").Any());                                                           // the Realtime session opened for this call
        var row = await S.Row();
        Assert.Equal(("mini", "model-mini", "p7"), (row.Tier, row.Model, row.FallbackFrom));
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("could not open its speech recogniser") && l.Contains("Realtime line"));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("vendor refused"));
    }
}
