using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Claude as the text model of the chained options and as the judge (decision V-33), against a fake local endpoint: no network, no key.</summary>
public sealed class VoiceAnthropicTests : IDisposable
{
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Url { get; }
        public List<(string Path, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];
        public List<Func<HttpListenerResponse, CancellationToken, Task>> Scripts { get; } = [];
        public volatile bool ClientDropped;
        private readonly CancellationTokenSource _stop = new();

        public FakeApi()
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                try { _l.Prefixes.Clear(); _l.Prefixes.Add($"http://127.0.0.1:{port}/"); _l.Start(); Url = $"http://127.0.0.1:{port}"; break; }
                catch (HttpListenerException) when (attempt < 20) { }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var n = 0;
                    while (!_stop.IsCancellationRequested)
                    {
                        var c = await _l.GetContextAsync();
                        using var r = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                        var body = await r.ReadToEndAsync();
                        var headers = c.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => c.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
                        int idx; lock (Requests) { idx = n++; Requests.Add((c.Request.Url!.AbsolutePath, body, headers)); }
                        var script = idx < Scripts.Count ? Scripts[idx] : Scripts[^1];
                        _ = Task.Run(async () => { try { await script(c.Response, _stop.Token); } catch (Exception) { ClientDropped = true; } });
                    }
                }
                catch (Exception) { /* closed */ }
            });
        }
        public void Dispose() { _stop.Cancel(); try { _l.Close(); } catch (Exception) { } }
    }

    private readonly List<FakeApi> _apis = [];
    public void Dispose() { foreach (var a in _apis) a.Dispose(); }
    private FakeApi Api(params Func<HttpListenerResponse, CancellationToken, Task>[] scripts) { var a = new FakeApi(); a.Scripts.AddRange(scripts); _apis.Add(a); return a; }

    private static AnthropicClient Client(FakeApi api) => new() { ApiKey = "sk-ant-test-not-real", BaseUrl = api.Url, MaxRetries = 0 };

    // ---------- scripted answers (the provider's server-sent events) ----------
    private static string Sse(string type, string json) => $"event: {type}\ndata: {json}\n\n";
    private static string Start(int input = 40) => Sse("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":@IN@,"output_tokens":1}}}""".Replace("@IN@", input.ToString()));
    private static string TextStart(int i) => Sse("content_block_start", """{"type":"content_block_start","index":@I@,"content_block":{"type":"text","text":""}}""".Replace("@I@", i.ToString()));
    private static string TextDelta(int i, string t) => Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = t } }.ToJsonString());
    private static string Stop(int i) => Sse("content_block_stop", """{"type":"content_block_stop","index":@I@}""".Replace("@I@", i.ToString()));
    private static string End(string reason, int output = 12) => Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"@R@","stop_sequence":null},"usage":{"output_tokens":@O@}}""".Replace("@R@", reason).Replace("@O@", output.ToString())) + Sse("message_stop", """{"type":"message_stop"}""");
    private static string ToolStart(int i, string id, string name) => Sse("content_block_start", """{"type":"content_block_start","index":@I@,"content_block":{"type":"tool_use","id":"@ID@","name":"@N@","input":{}}}""".Replace("@I@", i.ToString()).Replace("@ID@", id).Replace("@N@", name));
    private static string JsonDelta(int i, string part) => Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = part } }.ToJsonString());
    private static string ThinkStart(int i) => Sse("content_block_start", """{"type":"content_block_start","index":@I@,"content_block":{"type":"thinking","thinking":"","signature":""}}""".Replace("@I@", i.ToString()));
    private static string ThinkDelta(int i, string t) => Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = t } }.ToJsonString());
    private static string SigDelta(int i, string sig) => Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = sig } }.ToJsonString());

    private static Func<HttpListenerResponse, CancellationToken, Task> Stream(params string[] chunks) => async (r, ct) =>
    {
        r.ContentType = "text/event-stream"; r.SendChunked = true;
        foreach (var c in chunks) { var b = Encoding.UTF8.GetBytes(c); await r.OutputStream.WriteAsync(b, ct); await r.OutputStream.FlushAsync(ct); }
        r.Close();
    };
    private static Func<HttpListenerResponse, CancellationToken, Task> Json(string body, int status = 200) => async (r, ct) =>
    { r.StatusCode = status; r.ContentType = "application/json"; var b = Encoding.UTF8.GetBytes(body); await r.OutputStream.WriteAsync(b, ct); r.Close(); };

    private static JsonArray Tools() => new()
    {
        new JsonObject { ["type"] = "function", ["name"] = "search_products", ["description"] = "Search the catalog.", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("query") } },
        new JsonObject { ["type"] = "function", ["name"] = "off_topic", ["description"] = "The caller asked for something else.", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } },
    };
    private static LlmRequest Req(params LlmMessage[] m) => new("Be a shop assistant.", m, Tools());
    private static async Task<List<LlmEvent>> Run(IVoiceLlm llm, LlmRequest r, CancellationToken ct = default) { var l = new List<LlmEvent>(); await foreach (var e in llm.StreamAsync(r, ct)) l.Add(e); return l; }
    private static AnthropicVoiceLlm Llm(FakeApi api, int timeoutSeconds = 5, string model = "claude-sonnet-5-5") => new(Client(api), model, 512, timeoutSeconds);

    [Fact]
    public async Task A_streamed_answer_reaches_the_voice_delta_by_delta_with_the_request_shaped_for_speed_and_caching()
    {
        var api = Api(Stream(Start(), TextStart(0), TextDelta(0, "Hello and "), TextDelta(0, "welcome."), Stop(0), End("end_turn", 9)));
        var events = await Run(Llm(api), Req(new LlmMessage("user", "hi")));
        Assert.Equal(["Hello and ", "welcome."], events.OfType<LlmTextDelta>().Select(e => e.Text));
        var done = Assert.IsType<LlmCompleted>(events[^1]);
        Assert.Equal((40L, 9L), (done.InputTokens, done.OutputTokens));

        var req = api.Requests.Single();
        Assert.Equal("/v1/messages", req.Path);
        Assert.Contains("sk-ant-test-not-real", req.Headers["Authorization"]);                              // the key travels in a header only (the SDK's own header)
        Assert.DoesNotContain("sk-ant", req.Path);
        var b = JsonNode.Parse(req.Body)!;
        Assert.True(b["stream"]!.GetValue<bool>());
        Assert.Equal("claude-sonnet-5-5", b["model"]!.GetValue<string>());
        Assert.Equal("Be a shop assistant.", b["system"]!.GetValue<string>());                           // the same instructions as the Realtime line
        Assert.Equal("disabled", b["thinking"]!["type"]!.GetValue<string>());                             // thinking only delays the first word
        Assert.NotNull(b["cache_control"]);                                                               // top-level automatic caching
        var tools = b["tools"]!.AsArray();
        Assert.Equal(["search_products", "off_topic"], tools.Select(t => t!["name"]!.GetValue<string>()));  // the same tools, off_topic included
        Assert.Null(tools[0]!["cache_control"]); Assert.NotNull(tools[1]!["cache_control"]);              // the breakpoint is on the last tool
        Assert.Equal("query", tools[0]!["input_schema"]!["required"]![0]!.GetValue<string>());
        Assert.DoesNotContain("sk-ant", req.Body);
    }

    [Fact]
    public async Task A_tool_call_is_assembled_from_the_streamed_json_and_the_next_request_replays_the_assistant_turn_unchanged_with_all_results_in_one_user_turn()
    {
        var api = Api(
            Stream(Start(), ThinkStart(0), ThinkDelta(0, "the caller wants a mug"), SigDelta(0, "SIG-ABC123"), Stop(0), TextStart(1), TextDelta(1, "One moment."), Stop(1),
                   ToolStart(2, "toolu_A", "search_products"), JsonDelta(2, "{\"que"), JsonDelta(2, "ry\":\"mug\"}"), Stop(2),
                   ToolStart(3, "toolu_B", "off_topic"), Stop(3), End("tool_use")),
            Stream(Start(), TextStart(0), TextDelta(0, "I found a mug."), Stop(0), End("end_turn")));
        var llm = Llm(api);
        var first = await Run(llm, Req(new LlmMessage("user", "a mug please")));
        var calls = first.OfType<LlmToolCallEvent>().Select(e => e.Call).ToList();
        Assert.Equal([("toolu_A", "search_products", "{\"query\":\"mug\"}"), ("toolu_B", "off_topic", "{}")], calls.Select(c => (c.CallId, c.Name, c.Arguments)));
        Assert.Equal(["One moment."], first.OfType<LlmTextDelta>().Select(e => e.Text));                   // thinking is never spoken

        // the bridge's conversation: it knows only text and tool calls, not the thinking block
        var second = await Run(llm, Req(new LlmMessage("user", "a mug please"), new LlmMessage("assistant", "One moment.", calls), new LlmMessage("tool", "{\"found\":3}", ToolCallId: "toolu_A"), new LlmMessage("tool", "{\"ok\":true}", ToolCallId: "toolu_B")));
        Assert.Equal(["I found a mug."], second.OfType<LlmTextDelta>().Select(e => e.Text));
        var msgs = JsonNode.Parse(api.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], msgs.Select(m => m!["role"]!.GetValue<string>()));
        var assistant = msgs[1]!["content"]!.AsArray();
        Assert.Equal(["thinking", "text", "tool_use", "tool_use"], assistant.Select(c => c!["type"]!.GetValue<string>()));
        Assert.Equal("SIG-ABC123", assistant[0]!["signature"]!.GetValue<string>());                        // replayed unchanged
        Assert.Equal("the caller wants a mug", assistant[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("mug", assistant[2]!["input"]!["query"]!.GetValue<string>());
        var results = msgs[2]!["content"]!.AsArray();
        Assert.Equal(["tool_result", "tool_result"], results.Select(c => c!["type"]!.GetValue<string>()));
        Assert.Equal(["toolu_A", "toolu_B"], results.Select(c => c!["tool_use_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Without_a_remembered_turn_the_assistant_turn_is_rebuilt_from_the_text_and_tool_calls()
    {
        var api = Api(Stream(Start(), TextStart(0), TextDelta(0, "Done."), Stop(0), End("end_turn")));
        var calls = new[] { new LlmToolCall("toolu_X", "search_products", "{\"query\":\"cup\"}") };
        await Run(Llm(api), Req(new LlmMessage("user", "a cup"), new LlmMessage("assistant", "Looking.", calls), new LlmMessage("tool", "{}", ToolCallId: "toolu_X"), new LlmMessage("system", "keypad done")));
        var msgs = JsonNode.Parse(api.Requests.Single().Body)!["messages"]!.AsArray();
        Assert.Equal(["text", "tool_use"], msgs[1]!["content"]!.AsArray().Select(c => c!["type"]!.GetValue<string>()));
        var last = msgs[2]!["content"]!.AsArray();                                                         // the tool result and the platform note are one user turn
        Assert.Equal("tool_result", last[0]!["type"]!.GetValue<string>());
        Assert.Contains("keypad done", last[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_the_read_and_drops_the_connection()
    {
        var gate = new TaskCompletionSource();
        var api = Api(async (r, ct) =>
        {
            r.ContentType = "text/event-stream"; r.SendChunked = true;
            foreach (var c in new[] { Start(), TextStart(0), TextDelta(0, "First words. ") }) { var b = Encoding.UTF8.GetBytes(c); await r.OutputStream.WriteAsync(b, ct); await r.OutputStream.FlushAsync(ct); }
            gate.SetResult();
            for (var i = 0; i < 200; i++) { var b = Encoding.UTF8.GetBytes(TextDelta(0, "more ")); await r.OutputStream.WriteAsync(b, ct); await r.OutputStream.FlushAsync(ct); await Task.Delay(50, ct); }   // a long answer the caller interrupts
            r.Close();
        });
        using var cts = new CancellationTokenSource();
        var got = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in Llm(api).StreamAsync(Req(new LlmMessage("user", "tell me everything")), cts.Token))
                if (e is LlmTextDelta d) { got.Add(d.Text); cts.Cancel(); }
        });
        Assert.Equal("First words. ", got[0]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
        await FakeRealtime.Poll(() => api.ClientDropped ? "x" : null, 8);                                  // the server saw the client leave: the provider stops generating
    }

    [Fact]
    public async Task A_refusal_and_a_cut_off_answer_end_with_a_polite_line_in_the_platforms_words()
    {
        var refusal = Api(Stream(Start(), Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":0}}"""), Sse("message_stop", """{"type":"message_stop"}""")));
        var r1 = await Run(Llm(refusal), Req(new LlmMessage("user", "x")));
        Assert.Equal([PipelineScripts.ModelRefused], r1.OfType<LlmTextDelta>().Select(e => e.Text));       // nothing raw, no leading space
        Assert.IsType<LlmCompleted>(r1[^1]);

        var cut = Api(Stream(Start(), TextStart(0), TextDelta(0, "The mug costs"), Stop(0), End("max_tokens")));
        var r2 = await Run(Llm(cut), Req(new LlmMessage("user", "x")));
        Assert.Equal(["The mug costs", " " + PipelineScripts.ModelRanOut], r2.OfType<LlmTextDelta>().Select(e => e.Text));
    }

    [Fact]
    public async Task A_provider_error_throws_a_fixed_message_without_any_content_and_a_silent_provider_times_out()
    {
        var bad = Api(Json("""{"type":"error","error":{"type":"api_error","message":"echo: a mug for Ronald sk-ant-test-not-real"}}""", 500));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Run(Llm(bad), Req(new LlmMessage("user", "a mug for Ronald"))));
        Assert.DoesNotContain("Ronald", ex.Message); Assert.DoesNotContain("sk-ant", ex.Message); Assert.Null(ex.InnerException);

        var silent = Api(async (r, ct) => { await Task.Delay(Timeout.Infinite, ct); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => Run(Llm(silent, timeoutSeconds: 1), Req(new LlmMessage("user", "x"))));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task The_real_voice_tools_convert_including_off_topic_and_the_default_host_is_Anthropics_only()
    {
        using var s = new VoiceScenario("+15551234567");
        var defs = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions();
        var llm = new AnthropicVoiceLlm(new AnthropicClient { ApiKey = "k" }, "claude-sonnet-5-5", 256, 5);
        var built = llm.BuildRequest(new LlmRequest("i", [new LlmMessage("user", "hi")], defs));
        var names = built.Tools!.Select(t => t.TryPickTool(out var x) ? x.Name : "?").ToList();
        Assert.Equal(defs.Select(d => d!["name"]!.GetValue<string>()), names);                              // every tool the Realtime line has
        Assert.Contains("off_topic", names);
        var prod = AnthropicVoiceLlm.NewClient("k");
        Assert.Equal("https://api.anthropic.com", prod.BaseUrl);                                              // the host is fixed in code: production never points the key anywhere else
        Assert.Equal(0, prod.MaxRetries);
        await Task.CompletedTask;
    }

    // ---------- the judge ----------

    private static readonly SttCandidate[] Two = [new("gladia", "איך וויל א חלה", "yi", null), new("yiddishlabs", "איך וויל אַ חלה", "yi", 0.9)];
    private static string Message(string text) => new JsonObject { ["id"] = "msg_j", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-haiku-5-5", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["stop_reason"] = "end_turn", ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 30, ["output_tokens"] = 8 } }.ToJsonString();

    [Fact]
    public async Task The_Claude_judge_sends_one_non_streaming_request_with_the_candidates_as_quoted_data_and_returns_the_words()
    {
        var api = Api(Json(Message("איך וויל אַ חלה")));
        var r = await new AnthropicTranscriptJudge(Client(api), "claude-haiku-5-5").JudgeAsync(Two, CancellationToken.None);
        Assert.Equal("איך וויל אַ חלה", r!.Text);
        var b = JsonNode.Parse(api.Requests.Single().Body)!;
        Assert.Equal("claude-haiku-5-5", b["model"]!.GetValue<string>());
        Assert.Null(b["stream"]); Assert.Null(b["tools"]);
        Assert.Equal("disabled", b["thinking"]!["type"]!.GetValue<string>());
        Assert.Contains("not instructions", b["system"]!.GetValue<string>());
        var input = b["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("<candidates>", input);
        var asked = JsonNode.Parse(input["<candidates>\n".Length..^"\n</candidates>".Length])!.AsArray();
        Assert.Equal(["gladia", "yiddishlabs"], asked.Select(a => a!["engine"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_Claude_judge_refuses_an_essay_and_an_empty_answer_needs_two_candidates_and_an_error_throws_without_content()
    {
        Assert.Null(await new AnthropicTranscriptJudge(Client(Api(Json(Message(new string('x', 600))))), "m").JudgeAsync(Two, CancellationToken.None));
        Assert.Null(await new AnthropicTranscriptJudge(Client(Api(Json(Message("   ")))), "m").JudgeAsync(Two, CancellationToken.None));
        var none = Api(Json(Message("x")));
        Assert.Null(await new AnthropicTranscriptJudge(Client(none), "m").JudgeAsync([Two[0]], CancellationToken.None));
        Assert.Empty(none.Requests);                                                                       // one text: no request at all
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new AnthropicTranscriptJudge(Client(Api(Json("""{"type":"error","error":{"type":"api_error","message":"echo חלה"}}""", 500))), "m").JudgeAsync(Two, CancellationToken.None));
        Assert.DoesNotContain("חלה", ex.Message);
    }

    // ---------- configuration: the provider is a setting ----------

    private static VoiceOptions V(Action<VoiceOptions>? f = null)
    {
        var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "oa", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
        o.PipelineMenu.Enabled = true;
        o.Pipelines.P2 = new PipelineProviders { Stt = "deepgram", Tts = "elevenlabs" }; o.Deepgram.ApiKey = "dg"; o.ElevenLabs.ApiKey = "el"; o.ElevenLabs.VoiceId = "v1"; o.ElevenLabs.TtsModel = "t";
        f?.Invoke(o); return o;
    }

    [Fact]
    public void Claude_is_the_default_provider_needs_its_own_key_and_a_missing_key_is_unavailable_never_a_start_up_failure()
    {
        Assert.Equal("anthropic", new VoiceLlmOptions().Provider);
        var noKey = V();
        Assert.Contains("Voice:Anthropic:ApiKey", VoicePipelineVendors.Missing(noKey, "p2"));
        Assert.DoesNotContain("Voice:Pipelines:Llm:Model", VoicePipelineVendors.Missing(noKey, "p2"));       // the model has a built-in default with Claude
        Assert.DoesNotContain("Voice:OpenAiApiKey", VoicePipelineVendors.Missing(V(o => o.OpenAiApiKey = ""), "p2"));   // and OpenAI is not needed for it
        Assert.Empty(VoicePipelineVendors.Missing(V(o => o.Anthropic.ApiKey = "ak"), "p2"));
        Assert.Empty(VoiceConfig.Problems(noKey, new SignalWireOptions { SigningKey = "sw" }, true));          // start-up goes on
        // OpenAI stays selectable: its key and model id are then what is needed
        Assert.Equal(["Voice:Pipelines:Llm:Model"], VoicePipelineVendors.Missing(V(o => o.Pipelines.Llm.Provider = "openai"), "p2"));
        Assert.Empty(VoicePipelineVendors.Missing(V(o => { o.Pipelines.Llm.Provider = "OpenAI"; o.Pipelines.Llm.Model = "m"; }), "p2"));
        Assert.Contains("Voice:OpenAiApiKey", VoicePipelineVendors.Missing(V(o => { o.Pipelines.Llm.Provider = "openai"; o.Pipelines.Llm.Model = "m"; o.OpenAiApiKey = ""; }), "p2"));
        // an unknown provider: unavailable, and an invalid value when the menu is on
        var typo = V(o => { o.Pipelines.Llm.Provider = "gemini"; o.Anthropic.ApiKey = "ak"; });
        Assert.Contains("Voice:Pipelines:Llm:Provider", VoicePipelineVendors.Missing(typo, "p2"));
        Assert.Contains(VoiceConfig.Problems(typo, new SignalWireOptions { SigningKey = "sw" }, true), p => p.Contains("Llm:Provider"));
        Assert.Contains(VoiceConfig.Problems(V(o => o.Pipelines.Llm.MaxTokens = 5), new SignalWireOptions { SigningKey = "sw" }, true), p => p.Contains("MaxTokens"));
    }

    [Fact]
    public void The_judge_follows_the_conversation_provider_unless_it_has_its_own_and_needs_a_model_only_for_OpenAI()
    {
        VoiceOptions P5(Action<VoiceOptions>? f = null) => V(o =>
        {
            o.Pipelines.P2 = new PipelineProviders(); o.Pipelines.P5 = new P5Options { Stt = "gladia+yiddishlabs", Tts = "yiddishlabs", YiddishLabsInJudge = true };
            o.Gladia.ApiKey = "g"; o.YiddishLabs.FlashApiKey = "y"; o.YiddishLabs.TtsVoiceId = "v1"; o.YiddishLabs.ConsentLine = "c"; o.Anthropic.ApiKey = "ak";
            f?.Invoke(o);
        });
        Assert.Empty(VoicePipelineVendors.Missing(P5(), "p5"));                                               // Claude judges with its built-in judge model
        Assert.Equal("anthropic", VoicePipelineVendors.JudgeProvider(P5()));
        Assert.Equal("openai", VoicePipelineVendors.JudgeProvider(P5(o => o.Pipelines.P5.JudgeProvider = "openai")));
        Assert.Contains("Voice:Pipelines:P5:JudgeModel", VoicePipelineVendors.Missing(P5(o => o.Pipelines.P5.JudgeProvider = "openai"), "p5"));
        Assert.Empty(VoicePipelineVendors.Missing(P5(o => { o.Pipelines.P5.JudgeProvider = "openai"; o.Pipelines.P5.JudgeModel = "jm"; }), "p5"));
        Assert.Contains("Voice:Pipelines:P5:JudgeProvider", VoicePipelineVendors.Missing(P5(o => o.Pipelines.P5.JudgeProvider = "x"), "p5"));
        Assert.Contains(VoiceConfig.Problems(P5(o => o.Pipelines.P5.JudgeProvider = "x"), new SignalWireOptions { SigningKey = "sw" }, true), p => p.Contains("JudgeProvider"));
    }

    private sealed class OneLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    [Fact]
    public void Claude_is_the_agent_by_default_everywhere_and_an_OpenAI_key_alone_never_makes_an_option_available_or_becomes_its_brain()
    {
        // decision V-40: OpenAI is for audio; the defaults of options 2 to 6 never use an OpenAI text model
        var d = new VoiceOptions();
        Assert.Equal("anthropic", d.Pipelines.Llm.Provider);
        Assert.Equal("anthropic", VoicePipelineVendors.JudgeProvider(d));                                          // the judge follows the conversation, which is Claude
        Assert.Equal("", d.Pipelines.Llm.Model); Assert.Equal("", d.Pipelines.P5.JudgeModel); Assert.Equal("", d.Pipelines.P5.JudgeProvider);   // no OpenAI model id is a default anywhere
        VoiceOptions OnlyOpenAi() { var o = new VoiceOptions { OpenAiApiKey = "sk-only-openai" }; o.Pipelines.P6.Enabled = true; o.Pipelines.P2 = new PipelineProviders { Stt = "deepgram", Tts = "elevenlabs" }; o.Deepgram.ApiKey = "dg"; o.ElevenLabs = new ElevenLabsOptions { ApiKey = "el", VoiceId = "v", TtsModel = "m" }; return o; }
        foreach (var option in new[] { "p2", "p6" })
            Assert.Contains("Voice:Anthropic:ApiKey", VoicePipelineVendors.Missing(OnlyOpenAi(), option));        // an OpenAI key does not stand in for Claude's
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(new Infrastructure.ListLogger())).AddHttpClient().AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>().BuildServiceProvider();
        Assert.Empty(new VendorPartsSource(sp).Create(OnlyOpenAi()));                                              // so with only an OpenAI key no chained option exists at all
        var withClaude = OnlyOpenAi(); withClaude.Anthropic.ApiKey = "ak";
        Assert.All(new VendorPartsSource(sp).Create(withClaude), p => Assert.IsType<AnthropicVoiceLlm>(p.Llm));    // both keys: Claude, not OpenAI
    }

    [Fact]
    public void The_OpenAI_text_model_is_an_explicit_override_and_says_so_once_in_the_log_and_Claude_does_not()
    {
        VoiceOptions P6(Action<VoiceOptions>? f = null) { var o = new VoiceOptions { OpenAiApiKey = "sk", Anthropic = new AnthropicVoiceOptions { ApiKey = "ak" } }; o.Pipelines.P6.Enabled = true; f?.Invoke(o); return o; }
        string[] Warnings(VoiceOptions o)
        {
            var log = new Infrastructure.ListLogger();
            var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(log)).AddHttpClient().AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>().BuildServiceProvider();
            _ = new VendorPartsSource(sp).Create(o).ToList();
            return log.Lines.Where(l => l.Contains("V-40")).ToArray();
        }
        Assert.Empty(Warnings(P6()));
        Assert.Single(Warnings(P6(o => { o.Pipelines.Llm.Provider = "openai"; o.Pipelines.Llm.Model = "m"; })));
    }

    [Fact]
    public void The_option_gets_the_provider_that_is_configured_and_the_models_default_to_the_documented_ids()
    {
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(new OneLoggerFactory(new Infrastructure.ListLogger())).AddHttpClient().AddSingleton<IVendorSocketConnector, ClientWebSocketConnector>().BuildServiceProvider();
        var source = new VendorPartsSource(sp);
        var claude = source.Create(V(o => o.Anthropic.ApiKey = "ak")).Single();
        Assert.IsType<AnthropicVoiceLlm>(claude.Llm);
        var openai = source.Create(V(o => { o.Pipelines.Llm.Provider = "openai"; o.Pipelines.Llm.Model = "m"; })).Single();
        Assert.IsType<OpenAiVoiceLlm>(openai.Llm);
        Assert.Empty(source.Create(V()));                                                                      // Claude without its key: no option, not a crash
        Assert.Equal(("claude-sonnet-5-5", "claude-haiku-5-5"), (AnthropicDefaults.ConversationModel, AnthropicDefaults.JudgeModel));
    }
}
