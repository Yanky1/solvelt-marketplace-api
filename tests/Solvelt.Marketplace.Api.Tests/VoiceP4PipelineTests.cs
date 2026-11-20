using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Option p4 (Yiddish Labs end to end, decision V-22 and V-26): the consent step the owner's wording is played in, the start-up rules, and a Yiddish turn through the real adapters with fake vendors. Off by default.
/// </summary>
public sealed class VoiceP4PipelineTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Consent = "For this test your voice goes to Yiddish Labs. Press 1 to agree, or any other key for the standard assistant.";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();

    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    internal static Dictionary<string, string?> P4Settings(Action<Dictionary<string, string?>>? f = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["Voice:Pipelines:P4:Stt"] = "yiddishlabs", ["Voice:Pipelines:P4:Tts"] = "yiddishlabs", ["Voice:Pipelines:Llm:Model"] = "model-x", ["Voice:Pipelines:Llm:Provider"] = "openai",
            ["Voice:YiddishLabs:FlashApiKey"] = "yl_flash_not_real", ["Voice:YiddishLabs:TtsVoiceId"] = "voice1", ["Voice:YiddishLabs:ConsentLine"] = Consent,
        };
        f?.Invoke(d); return d;
    }

    private static async Task<XElement> Xml(HttpResponseMessage r) => XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;
    private Task<HttpResponseMessage> Menu(string digit) => VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = digit });
    private Task<HttpResponseMessage> Answer(string option, string? digit, string? signature = null, string? from = null)
        => VoiceFactory.Hook(S.C, VoiceEndpoints.PipelineConsentPath + "?o=" + option, S.Sid, from ?? Number, signature: signature, extra: digit is null ? null : new() { ["Digits"] = digit });

    private async Task Start(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: settings ?? P4Settings()), services: services);
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
    }

    // ---------- the consent step ----------

    [Fact]
    public async Task Choosing_the_Yiddish_Labs_option_plays_the_owners_consent_line_and_chooses_nothing_yet()
    {
        await Start();
        var x = await Xml(await Menu("4"));
        var gather = x.Element("Gather")!;
        Assert.Equal(Consent, gather.Element("Say")!.Value);                                       // the owner's wording, word for word
        Assert.Equal("1", gather.Attribute("numDigits")!.Value);
        Assert.Contains(VoiceEndpoints.PipelineConsentPath + "?o=p4", gather.Attribute("action")!.Value);
        Assert.Null(x.Descendants("Stream").FirstOrDefault());                                      // no stream, no pipeline before the answer
        var row = await S.Row();
        Assert.Equal("", row.BaseTier);                                                              // nothing recorded: no kept original, the menu has not been used yet
        Assert.NotEqual("p4", row.Tier);
    }

    [Fact]
    public async Task Pressing_1_continues_on_p4_and_any_other_answer_gives_the_standard_assistant()
    {
        await Start();
        var yes = await Xml(await Answer("p4", "1"));
        Assert.NotNull(yes.Descendants("Stream").SingleOrDefault());
        Assert.Contains(PipelineScripts.Chosen("p4"), yes.Descendants("Say").Select(e => e.Value));
        var row = await S.Row();
        Assert.Equal(("p4", "pipeline:p4"), (row.Tier, row.Model));
        foreach (var answer in new string?[] { "2", "0", "", null, "11", "a" })
        {
            await Start();                                                                         // a fresh call each time
            var no = await Xml(await Answer("p4", answer));
            Assert.NotNull(no.Descendants("Stream").SingleOrDefault());                            // the call goes on, on the standard line
            Assert.Contains(PipelineScripts.ConsentDeclined, no.Descendants("Say").Select(e => e.Value));
            Assert.Equal("p1", (await S.Row()).Tier);
            _s!.Dispose();
        }
    }

    [Fact]
    public async Task The_answer_is_signed_cannot_be_replayed_into_a_running_call_and_cannot_name_another_option()
    {
        await Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await Answer("p4", "1", signature: "forged")).StatusCode);
        Assert.NotEqual("p4", (await S.Row()).Tier);

        // an option that does not ask for consent (or does not exist) can never be chosen through this endpoint: the answer falls back to option 1
        foreach (var other in new[] { "p2", "p3", "p5", "p1", "p6", "evil", "" })
        {
            var x = await Xml(await Answer(other, "1"));
            Assert.Equal("p1", (await S.Row()).Tier);
            Assert.NotNull(x.Descendants("Stream").SingleOrDefault());
            _s!.Dispose(); await Start();
        }

        // after the stream has connected, a replay changes nothing
        var tok = (await VoiceScenario.StreamFrom(await Answer("p4", "1"))).Token;
        _phone = await FakePhone.Connect(S.F, tok); await _phone.Start();
        var replay = await Xml(await Answer("p4", "1"));
        Assert.Contains(VoiceEndpoints.AfterStreamPath, replay.Element("Redirect")!.Value);
    }

    [Fact]
    public async Task Even_when_another_option_is_available_the_consent_answer_can_only_choose_an_option_that_asked_for_consent()
    {
        var more = VoiceP2PipelineTests.P2Settings(); foreach (var kv in P4Settings()) more[kv.Key] = kv.Value;
        await Start(more);                                                                        // p2 (no consent step) AND p4 are available
        Assert.True(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p2"));
        var x = await Xml(await Answer("p2", "1"));                                               // someone presses 1 against a forged or edited address naming p2
        Assert.Equal("p1", (await S.Row()).Tier);
        Assert.Contains(PipelineScripts.ConsentDeclined, x.Descendants("Say").Select(e => e.Value));
    }

    [Fact]
    public async Task A_number_that_is_not_on_the_test_list_is_never_offered_the_consent_either()
    {
        await Start();
        var x = await Xml(await Answer("p4", "1", from: "+15550000000"));
        Assert.Contains(VoiceEndpoints.AfterStreamPath, x.Element("Redirect")!.Value);
        Assert.NotEqual("p4", (await S.Row()).Tier);
    }

    [Fact]
    public async Task The_other_options_are_unchanged_there_is_no_consent_for_p2()
    {
        var more = VoiceP2PipelineTests.P2Settings(); foreach (var kv in P4Settings()) more[kv.Key] = kv.Value;
        await Start(more);
        var x = await Xml(await Menu("2"));
        Assert.Null(x.Element("Gather"));
        Assert.NotNull(x.Descendants("Stream").SingleOrDefault());
        Assert.Equal("p2", (await S.Row()).Tier);
    }

    // ---------- availability and start-up rules ----------

    [Fact]
    public async Task P4_is_offered_only_with_its_settings_and_the_consent_wording()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig());
        Assert.False(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p4"));
        _s.Dispose();
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P4Settings()));
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.True(registry.IsAvailable("p4")); Assert.False(registry.IsAvailable("p5"));
        await Task.CompletedTask;
    }

    private static VoiceOptions V(Action<VoiceOptions>? f = null)
    {
        var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
        o.Pipelines.P4 = new PipelineProviders { Stt = "yiddishlabs", Tts = "yiddishlabs" }; o.Pipelines.Llm.Model = "m"; o.Pipelines.Llm.Provider = "openai";
        o.YiddishLabs.FlashApiKey = "k"; o.YiddishLabs.TtsVoiceId = "v1"; o.YiddishLabs.ConsentLine = "consent";
        o.PipelineMenu.Enabled = true;                                           // invalid values are only validated when the menu is on
        f?.Invoke(o); return o;
    }
    private static IReadOnlyList<string> Problems(Action<VoiceOptions>? f = null) => VoiceConfig.Problems(V(f), new SignalWireOptions { SigningKey = "sw" }, true);

    [Fact]
    public void Start_up_refuses_nonsense_settings_of_p4_but_not_missing_ones()
    {
        Assert.Empty(Problems());
        Assert.Contains(Problems(o => o.YiddishLabs.TtsVoiceId = "../x"), p => p.Contains("TtsVoiceId"));
        Assert.Contains(Problems(o => o.YiddishLabs.ConsentLine = new string('x', 601)), p => p.Contains("ConsentLine"));
        Assert.Contains(Problems(o => o.YiddishLabs.SttLanguage = "fr"), p => p.Contains("SttLanguage"));
        Assert.Contains(Problems(o => o.YiddishLabs.UploadSampleRate = 44100), p => p.Contains("UploadSampleRate"));
        Assert.Contains(Problems(o => o.YiddishLabs.VadSilenceMs = 50), p => p.Contains("VadSilenceMs"));
        Assert.Contains(Problems(o => o.YiddishLabs.VadEnergy = 9000), p => p.Contains("VadEnergy"));
        Assert.Contains(Problems(o => o.YiddishLabs.MaxConcurrentTtsSessions = 0), p => p.Contains("MaxConcurrentTtsSessions"));
        Assert.Contains(Problems(o => o.YiddishLabs.SttPrompt = new string('x', 501)), p => p.Contains("SttPrompt"));
        Assert.Contains(Problems(o => o.Pipelines.P4 = new PipelineProviders { Stt = "yiddishlabs", Tts = "elevenlabs" }), p => p.Contains("no adapter"));
        Assert.Empty(Problems(o => { o.PipelineMenu.Enabled = false; o.YiddishLabs.SttLanguage = "fr"; }));          // the menu off: nothing here stops the start
        // missing settings (key, voice, model, consent wording): start-up goes on, the option is just unavailable
        foreach (var blank in new Action<VoiceOptions>[] { o => o.YiddishLabs.FlashApiKey = "", o => o.YiddishLabs.TtsVoiceId = "", o => o.YiddishLabs.ConsentLine = "", o => o.YiddishLabs.ConsentLine = "   ", o => o.YiddishLabs.SttModel = "" })
            Assert.Empty(Problems(blank));
        Assert.DoesNotContain("yl_flash", string.Join("", Problems(o => { o.YiddishLabs.FlashApiKey = "yl_flash_secret"; o.YiddishLabs.SttLanguage = "fr"; })));
    }

    [Fact]
    public void Without_the_consent_wording_p4_is_unavailable_with_ONE_warning_that_names_only_the_setting()
    {
        var lines = new ListLogger();
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(lines)).BuildServiceProvider();
        var source = new VendorPartsSource(sp);
        var secret = V(o => { o.YiddishLabs.ConsentLine = ""; o.YiddishLabs.FlashApiKey = "yl_flash_secret"; });
        Assert.Empty(source.Create(secret));                                                          // no parts, so the registry answers "not available on this line"
        var warnings = lines.Lines.Where(l => l.Contains("p4")).ToList();
        Assert.Single(warnings);
        Assert.Contains("Voice:YiddishLabs:ConsentLine", warnings[0]);
        Assert.DoesNotContain("yl_flash", warnings[0]);
        Assert.Contains(VoicePipelineVendors.Missing(V(o => o.YiddishLabs.ConsentLine = " "), "p4"), m => m == "Voice:YiddishLabs:ConsentLine");
        Assert.Empty(VoicePipelineVendors.Missing(V(), "p4"));
    }

    private sealed class OneLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_host_started_without_the_consent_wording_runs_and_sends_the_menu_choice_to_p1()
    {
        await Start(P4Settings(d => d.Remove("Voice:YiddishLabs:ConsentLine")));                       // the whole API starts
        Assert.False(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p4"));
        var x = await Xml(await Menu("4"));
        Assert.Equal(PipelineScripts.Unavailable, x.Element("Say")!.Value);                              // "not available on this line", and no consent question
        Assert.Null(x.Element("Gather"));
        var row = await S.Row();
        Assert.Equal(("p1", "model-full"), (row.Tier, row.Model));                                         // the call stays on the Realtime line
    }

    [Fact]
    public void Only_the_options_that_use_the_vendor_need_the_consent()
    {
        var o = V(); o.Pipelines.P2 = new PipelineProviders { Stt = "deepgram", Tts = "elevenlabs" };
        Assert.True(o.Pipelines.UsesYiddishLabs("p4")); Assert.False(o.Pipelines.UsesYiddishLabs("p2")); Assert.False(o.Pipelines.UsesYiddishLabs("p3")); Assert.False(o.Pipelines.UsesYiddishLabs("p1"));
        Assert.False(new VoicePipelinesOptions().UsesYiddishLabs("p4"));                            // not configured: not "using" anything
    }

    // ---------- a Yiddish turn through the real adapters ----------

    private async Task<(FakeSocketConnector Sockets, FakeLlmHandler Llm, FakeFlashHandler Flash)> Call(FakeFlashHandler? flash = null, FakeSocketConnector? sockets = null)
    {
        sockets ??= new FakeSocketConnector(); flash ??= new FakeFlashHandler(FlashAnswer.Text("איך וויל א חלה"));
        var llm = new FakeLlmHandler { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
        await Start(services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
            svc.AddHttpClient(VoicePipelineVendors.YiddishLabsClient).ConfigurePrimaryHttpMessageHandler(() => flash);
        });
        _ = Task.Run(() => YiddishLabsVendor(sockets, _stop.Token));
        var (token, _) = await VoiceScenario.StreamFrom(await Answer("p4", "1"));
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return (sockets, llm, flash);
    }

    /// <summary>The fake Yiddish Labs voice: for every sentence, session.ready at 16 kHz, two chunks of 1600 samples (100 ms each, all 1000) and done.</summary>
    internal static async Task YiddishLabsVendor(FakeSocketConnector sockets, CancellationToken ct)
    {
        var seen = 0;
        while (!ct.IsCancellationRequested)
        {
            var all = sockets.Connections;
            while (seen < all.Count)
            {
                var c = all[seen++];
                if (!c.Uri.Host.Contains("yiddishlabs")) continue;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 2; i++) await MemorySocketPair.ReceiveAsync(c.Server);
                        await MemorySocketPair.SendTextAsync(c.Server, """{"type":"session.ready","sample_rate":16000,"format":"pcm_s16le","channels":1}""");
                        var pcm = new byte[3200]; for (var i = 0; i < 1600; i++) BitConverter.GetBytes((short)1000).CopyTo(pcm, i * 2);
                        await c.Server.SendAsync(pcm, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
                        await c.Server.SendAsync(pcm, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
                        await MemorySocketPair.SendTextAsync(c.Server, """{"type":"done","billed_words":5}""");
                    }
                    catch { /* the adapter closed first */ }
                });
            }
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);
    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);
    private static string Media(byte b, int frames) => Convert.ToBase64String(Enumerable.Repeat(b, 160 * frames).ToArray());

    [Fact]
    public async Task A_greeting_then_a_Yiddish_turn_goes_audio_to_Flash_then_the_words_to_the_text_model_then_the_voice_back_with_no_second_silence_wait()
    {
        var (sockets, llm, flash) = await Call();
        await Until(() => Frames("media").Count() >= 4);                                           // two sentences, two 100 ms chunks each, as 8 kHz mu-law
        Assert.All(Frames("media"), m => Assert.Equal(Enumerable.Repeat(PhoneAudio.PcmToMulaw(1000), 800), Convert.FromBase64String(m["media"]!["payload"]!.GetValue<string>())));
        Assert.Equal(("p4", "pipeline:p4"), ((await S.Row()).Tier, (await S.Row()).Model));
        Assert.DoesNotContain(sockets.Connections, c => c.Uri.Host.Contains("deepgram") || c.Uri.Host.Contains("gladia") || c.Uri.Host.Contains("elevenlabs"));   // only Yiddish Labs, and the text model

        var before = llm.Requests.Count;
        await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(PhoneAudio.PcmToMulaw(8000), 30) } });         // the caller speaks (600 ms)
        await _phone.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(0xFF, 30) } });                                   // and stops (600 ms of silence)
        await Until(() => llm.Requests.Count > before);                                            // no fake time was advanced: the turn ended when the transcript arrived
        var input = JsonNode.Parse(llm.Requests[^1].Body)!["input"]!.AsArray();
        Assert.Contains(input, i => i!["role"]?.GetValue<string>() == "user" && i["content"]!.GetValue<string>() == "איך וויל א חלה");
        Assert.Equal("https://flash.yiddishlabs.com/v1/audio/transcriptions", flash.Requests.Single().Uri!.ToString());
        await Until(() => Frames("media").Count() >= 8);                                           // and the answer is spoken
    }

    [Fact]
    public async Task When_Flash_refuses_the_credits_mid_call_the_platform_apologises_and_the_call_ends_as_vendor_lost()
    {
        var (_, _, _) = await Call(new FakeFlashHandler(new FlashAnswer(HttpStatusCode.PaymentRequired, "insufficient_credits")));
        await Until(() => Frames("media").Count() >= 4);
        await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(PhoneAudio.PcmToMulaw(8000), 30) } });
        await _phone.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(0xFF, 30) } });
        await Until(() => S.Row().Result.EndedAt is not null);
        Assert.Equal(VoiceOutcome.VendorLost, (await S.Row()).Outcome);
        Assert.Contains(PipelineScripts.VendorLost, (await S.AfterStream()).Descendants("Say").Select(e => e.Value));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("insufficient_credits") || l.Contains("yl_flash"));
    }

    [Fact]
    public async Task When_the_voice_cannot_be_opened_during_the_call_the_platform_apologises_in_its_own_words()
    {
        var sockets = new FakeSocketConnector { FailWhen = u => u.Host.Contains("yiddishlabs") };
        var (_, _, _) = await Call(sockets: sockets);
        await Until(() => S.Row().Result.EndedAt is not null);
        Assert.Equal(VoiceOutcome.VendorLost, (await S.Row()).Outcome);
        Assert.Contains(PipelineScripts.VendorLost, (await S.AfterStream()).Descendants("Say").Select(e => e.Value));
    }
}
