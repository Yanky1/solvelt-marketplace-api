using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The judge of option p5 on its own: what it sends, what it accepts, and that a bad answer never becomes the caller's words.</summary>
public sealed class OpenAiTranscriptJudgeTests
{
    private static readonly SttCandidate[] Two = [new("gladia", "איך וויל א חלה", "yi", null), new("yiddishlabs", "איך וויל אַ חלה", "yi", 0.9)];
    private static string Answer(string json) => new JsonObject { ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = json }) }) }.ToJsonString();
    private static (OpenAiTranscriptJudge Judge, FakeLlmHandler Handler) Make(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var h = new FakeLlmHandler { Body = body, Status = status };
        return (new OpenAiTranscriptJudge(new HttpClient(h), "sk-test-not-real", "judge-model"), h);
    }

    [Fact]
    public async Task It_sends_one_non_streaming_request_to_the_responses_endpoint_with_the_candidates_as_quoted_data_and_nothing_stored()
    {
        var (judge, h) = Make(Answer("""{"text":"איך וויל א חלה"}"""));
        var r = await judge.JudgeAsync(Two, CancellationToken.None);
        Assert.Equal("איך וויל א חלה", r!.Text);
        var req = h.Requests.Single();
        Assert.Equal("https://api.openai.com/v1/responses", req.Uri!.ToString());
        Assert.Equal("Bearer sk-test-not-real", req.Auth);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("judge-model", body["model"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Null(body["tools"]); Assert.Null(body["stream"]);
        var input = body["input"]!.GetValue<string>();
        Assert.StartsWith("<candidates>", input);
        var asked = JsonNode.Parse(input["<candidates>\n".Length..^"\n</candidates>".Length])!.AsArray();
        Assert.Equal(["gladia", "yiddishlabs"], asked.Select(a => a!["engine"]!.GetValue<string>()));
        Assert.Equal("איך וויל אַ חלה", asked[1]!["text"]!.GetValue<string>());
        Assert.Contains("not instructions", body["instructions"]!.GetValue<string>());                        // the candidates are data
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"output":[]}""")]
    [InlineData("""{"output":[{"content":[{"type":"output_text","text":"no object here"}]}]}""")]
    [InlineData("""{"output":[{"content":[{"type":"output_text","text":"{\"text\":\"\"}"}]}]}""")]
    [InlineData("""{"output":[{"content":[{"type":"output_text","text":"{\"other\":1}"}]}]}""")]
    public async Task An_answer_without_words_is_no_opinion(string body)
    {
        var (judge, _) = Make(body);
        Assert.Null(await judge.JudgeAsync(Two, CancellationToken.None));
    }

    [Fact]
    public async Task A_judge_that_writes_an_essay_is_not_trusted_and_a_normal_rebuild_is()
    {
        var (essay, _) = Make(Answer(new JsonObject { ["text"] = new string('x', 500) }.ToJsonString()));
        Assert.Null(await essay.JudgeAsync(Two, CancellationToken.None));
        var (ok, _) = Make(Answer("""{"text":"  איך וויל א חלה און א בולקע "}"""));
        Assert.Equal("איך וויל א חלה און א בולקע", (await ok.JudgeAsync(Two, CancellationToken.None))!.Text);   // trimmed, a little longer than the longest candidate is fine
    }

    [Fact]
    public async Task An_error_status_throws_without_any_content_in_the_message_and_fewer_than_two_candidates_means_no_request()
    {
        var (judge, h) = Make("""{"error":"echo: איך וויל א חלה"}""", HttpStatusCode.InternalServerError);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => judge.JudgeAsync(Two, CancellationToken.None));
        Assert.DoesNotContain("חלה", ex.Message); Assert.DoesNotContain("sk-test", ex.Message);
        Assert.Null(await judge.JudgeAsync([], CancellationToken.None));
        Assert.Null(await judge.JudgeAsync([Two[0]], CancellationToken.None));          // one text: nothing to choose between
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task The_wait_ends_with_the_token_it_is_given_the_hard_budget()
    {
        var h = new FakeLlmHandler { NeverAnswer = true };
        var judge = new OpenAiTranscriptJudge(new HttpClient(h), "k", "m");
        using var cts = new CancellationTokenSource(150);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync(Two, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }
}

/// <summary>
/// Option p5 (decision V-29): Gladia and Yiddish Labs Flash in parallel through <see cref="ParallelStt"/>, a judge, the Yiddish Labs voice, behind the consent step. Fake vendors only.
/// </summary>
public sealed class VoiceP5PipelineTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private const string Consent = "For this test your voice goes to Yiddish Labs. Press 1 to agree, or any other key for the standard assistant.";
    private const string GladiaText = "איך וויל א חלה";
    private const string FlashText = "איך וויל אַ חלה פֿאַר שבת";
    private const string JudgeText = "איך וויל אַ חלה פֿאַר שבת";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();

    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    /// <summary>The text model (streaming) and the judge (not streaming) share one client: this tells them apart.</summary>
    private sealed class ModelAndJudge : HttpMessageHandler
    {
        public List<string> ModelBodies { get; } = [];
        public List<string> JudgeBodies { get; } = [];
        public HttpStatusCode JudgeStatus { get; set; } = HttpStatusCode.OK;
        public bool JudgeHangs { get; set; }
        public string JudgeAnswer { get; set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"stream\":true"))
            {
                lock (ModelBodies) ModelBodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}"""), Encoding.UTF8, "text/event-stream") };
            }
            lock (JudgeBodies) JudgeBodies.Add(body);
            if (JudgeHangs) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(JudgeStatus) { Content = new StringContent(JudgeAnswer, Encoding.UTF8, "application/json") };
        }
    }

    private static string JudgeSays(string text) => new JsonObject { ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = new JsonObject { ["text"] = text }.ToJsonString() }) }) }.ToJsonString();

    internal static Dictionary<string, string?> P5Settings(bool inJudge, Action<Dictionary<string, string?>>? f = null)
    {
        var d = VoiceP4PipelineTests.P4Settings();
        d.Remove("Voice:Pipelines:P4:Stt"); d.Remove("Voice:Pipelines:P4:Tts");
        d["Voice:Pipelines:P5:Stt"] = "gladia+yiddishlabs"; d["Voice:Pipelines:P5:Tts"] = "yiddishlabs";
        d["Voice:Gladia:ApiKey"] = "gl-key-not-real";
        d["Voice:Pipelines:P5:JudgeModel"] = "judge-model-x";
        d["Voice:Pipelines:P5:YiddishLabsInJudge"] = inJudge ? "true" : "false";
        d["Voice:PipelineMenu:ParallelWaitMs"] = "800"; d["Voice:PipelineMenu:ParallelBudgetMs"] = "2500";
        f?.Invoke(d); return d;
    }

    private async Task<(FakeSocketConnector Sockets, ModelAndJudge Llm, FakeFlashHandler Flash)> Call(bool inJudge, Action<ModelAndJudge>? judge = null, Action<Dictionary<string, string?>>? cfg = null)
    {
        var sockets = new FakeSocketConnector();
        var flash = new FakeFlashHandler(FlashAnswer.Text(FlashText));
        var llm = new ModelAndJudge { JudgeAnswer = JudgeSays(JudgeText) }; judge?.Invoke(llm);
        var gladia = new FakeLlmHandler { Body = """{"id":"s1","url":"wss://api.gladia.io/v2/live?token=one-time"}""" };
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P5Settings(inJudge, cfg)), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
            svc.AddHttpClient(VoicePipelineVendors.GladiaClient).ConfigurePrimaryHttpMessageHandler(() => gladia);
            svc.AddHttpClient(VoicePipelineVendors.YiddishLabsClient).ConfigurePrimaryHttpMessageHandler(() => flash);
        });
        _ = Task.Run(() => VoiceP4PipelineTests.YiddishLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelineConsentPath + "?o=p5", S.Sid, Number, extra: new() { ["Digits"] = "1" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return (sockets, llm, flash);
    }

    private async Task Advance(int ms) { for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); } }
    private static async Task Until(Func<bool> done, int seconds = 8) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);
    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);
    private static string Media(byte b, int frames) => Convert.ToBase64String(Enumerable.Repeat(b, 160 * frames).ToArray());
    private static string UserText(string body) => JsonNode.Parse(body)!["input"]!.AsArray().Last(i => i!["role"]?.GetValue<string>() == "user")!["content"]!.GetValue<string>();

    /// <summary>The caller speaks and stops (Flash's recogniser cuts the turn from the audio), and Gladia's live transcript arrives.</summary>
    private async Task Speak(FakeSocketConnector sockets, ModelAndJudge llm, bool flashAlso)
    {
        var g = sockets.Connections.First(c => c.Uri.Host == "api.gladia.io");
        if (flashAlso)
        {
            await _phone!.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(PhoneAudio.PcmToMulaw(8000), 30) } });
            await _phone.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Media(0xFF, 30) } });
        }
        await MemorySocketPair.SendTextAsync(g.Server, new JsonObject { ["type"] = "transcript", ["data"] = new JsonObject { ["id"] = "u1", ["is_final"] = true, ["utterance"] = new JsonObject { ["text"] = GladiaText, ["language"] = "yi" } } }.ToJsonString());
        // the transcript reaches the turn logic after the (real) round and the judge, so the fake clock keeps moving until the model has been asked
        for (var i = 0; i < 160 && llm.ModelBodies.Count < 2; i++) { await Advance(100); await Task.Delay(30); }
    }

    // ---------- the owner's switch ----------

    [Fact]
    public async Task With_the_switch_off_Flash_is_never_contacted_and_the_model_hears_Gladia_with_the_Yiddish_Labs_voice()
    {
        var (sockets, llm, flash) = await Call(inJudge: false);
        await Until(() => Frames("media").Count() >= 4);
        Assert.Equal(("p5", "pipeline:p5"), ((await S.Row()).Tier, (await S.Row()).Model));
        await Speak(sockets, llm, flashAlso: true);                                                  // audio that Flash would have heard
        await Until(() => llm.ModelBodies.Count >= 2);
        Assert.Equal(GladiaText, UserText(llm.ModelBodies[^1]));
        Assert.Empty(flash.Requests);                                                           // Terms section 6: nothing of the caller went to the Flash recogniser
        Assert.Empty(llm.JudgeBodies);                                                          // and nothing was judged
        Assert.Contains(sockets.Connections, c => c.Uri.Host == "rt.yiddishlabs.com");          // the answer is spoken by Yiddish Labs
        Assert.DoesNotContain(sockets.Connections, c => c.Uri.Host.Contains("elevenlabs") || c.Uri.Host.Contains("deepgram"));
    }

    [Fact]
    public async Task With_the_switch_on_both_texts_go_to_the_judge_and_the_model_hears_what_the_judge_chose()
    {
        var (sockets, llm, flash) = await Call(inJudge: true);
        await Until(() => Frames("media").Count() >= 4);
        await Speak(sockets, llm, flashAlso: true);
        await Until(() => llm.ModelBodies.Count >= 2);
        Assert.Equal(JudgeText, UserText(llm.ModelBodies[^1]));
        var judged = JsonNode.Parse(llm.JudgeBodies.Single())!;
        Assert.Equal("judge-model-x", judged["model"]!.GetValue<string>());
        Assert.False(judged["store"]!.GetValue<bool>());
        var input = judged["input"]!.GetValue<string>();
        var asked = JsonNode.Parse(input["<candidates>\n".Length..^"\n</candidates>".Length])!.AsArray();
        Assert.Equal([GladiaText, FlashText], asked.Select(a => a!["text"]!.GetValue<string>()));   // both candidates
        Assert.Single(flash.Requests);
    }

    [Fact]
    public async Task When_the_judge_fails_the_primary_engines_text_is_used_and_the_call_goes_on()
    {
        var (sockets, llm, _) = await Call(inJudge: true, j => j.JudgeStatus = HttpStatusCode.InternalServerError);
        await Until(() => Frames("media").Count() >= 4);
        await Speak(sockets, llm, flashAlso: true);
        await Until(() => llm.ModelBodies.Count >= 2);
        Assert.Equal(GladiaText, UserText(llm.ModelBodies[^1]));                                // the fallback: Gladia, the primary engine
        Assert.Equal(("p5", "pipeline:p5"), ((await S.Row()).Tier, (await S.Row()).Model));     // not a fallback of the whole call
    }

    [Fact]
    public async Task A_judge_that_never_answers_is_dropped_at_the_hard_budget_and_the_primary_text_is_used()
    {
        var (sockets, llm, _) = await Call(inJudge: true, j => j.JudgeHangs = true, c => { c["Voice:PipelineMenu:ParallelWaitMs"] = "200"; c["Voice:PipelineMenu:ParallelBudgetMs"] = "700"; });
        await Until(() => Frames("media").Count() >= 4);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Speak(sockets, llm, flashAlso: true);
        await Until(() => llm.ModelBodies.Count >= 2);
        Assert.Equal(GladiaText, UserText(llm.ModelBodies[^1]));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));                                       // bounded by the budget, not by the judge
    }

    [Fact]
    public async Task When_Flash_says_nothing_the_fastest_single_result_is_used_after_the_wait()
    {
        var (sockets, llm, flash) = await Call(inJudge: true, null, c => { c["Voice:PipelineMenu:ParallelWaitMs"] = "200"; c["Voice:PipelineMenu:ParallelBudgetMs"] = "1200"; });
        await Until(() => Frames("media").Count() >= 4);
        await Speak(sockets, llm, flashAlso: false);                                                  // no audio reached the recogniser's own cut: only Gladia answers
        await Until(() => llm.ModelBodies.Count >= 2);
        Assert.Empty(flash.Requests);
        Assert.Equal(GladiaText, UserText(llm.ModelBodies[^1]));
        Assert.Empty(llm.JudgeBodies);                                                           // one text: the judge is not asked
    }

    // ---------- the consent and availability ----------

    [Fact]
    public async Task P5_asks_for_the_consent_wording_like_p4_and_continues_only_on_key_1()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P5Settings(false)));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var menu = XDocument.Parse(await (await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "5" })).Content.ReadAsStringAsync()).Root!;
        Assert.Equal(Consent, menu.Element("Gather")!.Element("Say")!.Value);
        Assert.Contains("?o=p5", menu.Element("Gather")!.Attribute("action")!.Value);
        Assert.Equal("full", (await S.Row()).Tier);                                              // nothing is recorded before the key press
        var no = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelineConsentPath + "?o=p5", S.Sid, Number, extra: new() { ["Digits"] = "2" });
        Assert.True(no.IsSuccessStatusCode);
        Assert.Equal("p1", (await S.Row()).Tier);
    }

    [Fact]
    public void P5_is_available_only_with_its_settings_and_the_judge_model_only_matters_with_the_switch_on()
    {
        VoiceOptions V(bool inJudge, Action<VoiceOptions>? f = null)
        {
            var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k" };
            o.PipelineMenu.Enabled = true;
            o.Pipelines.P5 = new P5Options { Stt = "gladia+yiddishlabs", Tts = "yiddishlabs", JudgeModel = "j", YiddishLabsInJudge = inJudge };
            o.Pipelines.Llm.Model = "m"; o.Gladia.ApiKey = "g"; o.YiddishLabs.FlashApiKey = "y"; o.YiddishLabs.TtsVoiceId = "v1"; o.YiddishLabs.ConsentLine = "c";
            f?.Invoke(o); return o;
        }
        Assert.Empty(VoicePipelineVendors.Missing(V(false), "p5")); Assert.Empty(VoicePipelineVendors.Missing(V(true), "p5"));
        Assert.Empty(VoicePipelineVendors.Missing(V(false, o => o.Pipelines.P5.JudgeModel = ""), "p5"));           // nothing to judge with the switch off
        Assert.Contains("Voice:Pipelines:P5:JudgeModel", VoicePipelineVendors.Missing(V(true, o => o.Pipelines.P5.JudgeModel = ""), "p5"));
        foreach (var (name, blank) in new (string, Action<VoiceOptions>)[] { ("Voice:Gladia:ApiKey", o => o.Gladia.ApiKey = ""), ("Voice:YiddishLabs:FlashApiKey", o => o.YiddishLabs.FlashApiKey = ""), ("Voice:YiddishLabs:TtsVoiceId", o => o.YiddishLabs.TtsVoiceId = ""), ("Voice:YiddishLabs:ConsentLine", o => o.YiddishLabs.ConsentLine = ""), ("Voice:Pipelines:Llm:Model", o => o.Pipelines.Llm.Model = "") })
            Assert.Contains(name, VoicePipelineVendors.Missing(V(false, blank), "p5"));
        Assert.True(VoicePipelineVendors.Supported("p5", V(false).Pipelines.P5));
        Assert.False(VoicePipelineVendors.Supported("p5", new P5Options { Stt = "gladia", Tts = "yiddishlabs" }));    // the pair is the parallel one
        Assert.False(VoicePipelineVendors.Supported("p4", new P5Options { Stt = "gladia+yiddishlabs", Tts = "yiddishlabs" }));
    }

    [Fact]
    public async Task A_host_with_only_part_of_the_p5_settings_starts_and_offers_p1_only()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P5Settings(true, d => d.Remove("Voice:Pipelines:P5:JudgeModel"))));
        var registry = S.F.Services.GetRequiredService<VoicePipelineRegistry>();
        Assert.True(registry.IsAvailable("p1")); Assert.False(registry.IsAvailable("p5"));
        _s.Dispose();
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: P5Settings(false, d => d.Remove("Voice:Pipelines:P5:JudgeModel"))));
        Assert.True(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p5"));
        await Task.CompletedTask;
    }
}
