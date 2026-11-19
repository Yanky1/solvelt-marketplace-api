using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The vendor adapters of option p2 (decision V-22) against in-memory sockets and a scripted HTTP handler: no network and no key exist in these tests.</summary>
public class DeepgramSttTests
{
    private const string Key = "dg-key-not-real";
    private static DeepgramOptions Opts() => new() { ApiKey = Key, Model = "nova-3", Language = "en" };

    private static async Task<(DeepgramStt Stt, FakeSocketConnector Sockets, ListLogger Log, ISttSession Session, FakeSocketConnector.Connection Conn)> Open()
    {
        var sockets = new FakeSocketConnector(); var log = new ListLogger();
        var stt = new DeepgramStt(Opts(), sockets, log);
        var session = await stt.OpenAsync(CancellationToken.None);
        return (stt, sockets, log, session, await sockets.NextAsync());
    }

    [Fact]
    public async Task It_asks_for_the_telephones_own_mulaw_8k_and_keeps_the_key_in_the_header_not_the_address()
    {
        var (_, _, _, session, conn) = await Open();
        await using var _s = session;
        Assert.Equal("api.deepgram.com", conn.Uri.Host);
        Assert.Equal("wss", conn.Uri.Scheme);
        Assert.Equal("/v1/listen", conn.Uri.AbsolutePath);
        Assert.Contains("encoding=mulaw", conn.Uri.Query); Assert.Contains("sample_rate=8000", conn.Uri.Query); Assert.Contains("channels=1", conn.Uri.Query);
        Assert.Contains("model=nova-3", conn.Uri.Query); Assert.Contains("language=en", conn.Uri.Query); Assert.Contains("interim_results=true", conn.Uri.Query);
        Assert.DoesNotContain(Key, conn.Uri.ToString());
        Assert.Equal("Token " + Key, conn.Headers["Authorization"]);
    }

    [Fact]
    public async Task Caller_audio_goes_out_as_binary_frames_in_order_and_empty_audio_is_not_sent()
    {
        var (_, _, _, session, conn) = await Open();
        await using var _s = session;
        await session.SendAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await session.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await session.SendAudioAsync(new byte[] { 4, 5 }, CancellationToken.None);
        var a = await MemorySocketPair.ReceiveAsync(conn.Server); var b = await MemorySocketPair.ReceiveAsync(conn.Server);
        Assert.Equal(WebSocketMessageType.Binary, a!.Value.Type); Assert.Equal(new byte[] { 1, 2, 3 }, a.Value.Data);
        Assert.Equal(WebSocketMessageType.Binary, b!.Value.Type); Assert.Equal(new byte[] { 4, 5 }, b.Value.Data);
    }

    [Fact]
    public async Task Deepgram_messages_become_the_pipelines_events_and_everything_else_is_ignored()
    {
        var (_, _, _, session, conn) = await Open();
        await using var _s = session;
        foreach (var m in new[]
        {
            """{"type":"SpeechStarted","timestamp":1.2}""",
            """{"type":"Results","is_final":false,"channel":{"alternatives":[{"transcript":"I would like","confidence":0.8}]}}""",
            """{"type":"Results","is_final":true,"speech_final":false,"channel":{"alternatives":[{"transcript":"I would like a challah","confidence":0.97}]}}""",
            """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"   ","confidence":0.1}]}}""",      // empty: nothing to say
            """{"type":"Metadata","request_id":"x"}""",
            "this is not json",
            """{"type":"UtteranceEnd","last_word_end":3.4}""",
            """{"no":"type"}""",
        }) await MemorySocketPair.SendTextAsync(conn.Server, m);
        await conn.Server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        var events = new List<SttEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in session.Events(cts.Token)) events.Add(e);
        Assert.Equal([SttEventKind.SpeechStarted, SttEventKind.Partial, SttEventKind.Final, SttEventKind.SpeechEnded], events.Select(e => e.Kind));
        Assert.Equal("I would like", events[1].Text);
        Assert.Equal(("I would like a challah", 0.97), (events[2].Text, events[2].Confidence));
    }

    [Fact]
    public async Task When_the_vendor_goes_away_the_event_stream_ends_so_the_pipeline_can_fall_back()
    {
        var (_, _, _, session, conn) = await Open();
        await using var _s = session;
        conn.Pair.Server.Abort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in session.Events(cts.Token)) { }                              // returns: the stream completed
        await session.SendAudioAsync(new byte[] { 1 }, CancellationToken.None);            // and sending afterwards neither throws nor hangs
    }

    [Fact]
    public async Task Closing_the_session_tells_the_vendor_to_close_the_stream()
    {
        var (_, _, _, session, conn) = await Open();
        var close = Task.Run(async () => { var m = await MemorySocketPair.ReceiveAsync(conn.Server); return Encoding.UTF8.GetString(m!.Value.Data); });
        await session.DisposeAsync();
        Assert.Contains("CloseStream", await close);
    }

    [Fact]
    public async Task What_the_caller_says_is_never_logged()
    {
        var (_, _, log, session, conn) = await Open();
        await MemorySocketPair.SendTextAsync(conn.Server, """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"my secret words","confidence":0.9}]}}""");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in session.Events(cts.Token)) { Assert.Equal("my secret words", e.Text); break; }
        conn.Pair.Server.Abort();
        await session.DisposeAsync();
        Assert.DoesNotContain(log.Lines, l => l.Contains("secret") || l.Contains(Key));
    }

    [Fact]
    public async Task A_vendor_that_refuses_the_connection_throws_and_leaks_nothing()
    {
        var sockets = new FakeSocketConnector { Fail = true };
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => new DeepgramStt(Opts(), sockets, new ListLogger()).OpenAsync(CancellationToken.None));
        Assert.DoesNotContain(Key, ex.Message);
    }
}

public class ElevenLabsTtsTests
{
    private const string Key = "el-key-not-real";
    private static ElevenLabsOptions Opts(string region = "") => new() { ApiKey = Key, VoiceId = "voice_ABC-1", TtsModel = "model-x", Region = region };

    private static string Audio(params byte[] b) => $"{{\"audio\":\"{Convert.ToBase64String(b)}\"}}";

    private static async Task<List<byte[]>> Speak(ElevenLabsTts tts, string text, FakeSocketConnector sockets, Func<WebSocket, Task> vendor)
    {
        var script = Task.Run(async () => { var conn = await sockets.NextAsync(); await vendor(conn.Server); });
        var chunks = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var c in tts.SynthesizeAsync(text, cts.Token)) chunks.Add(c);
        await script;
        return chunks;
    }

    [Fact]
    public async Task It_asks_for_ulaw_8000_from_the_named_voice_with_the_key_in_the_header_and_the_vendors_logging_off()
    {
        var sockets = new FakeSocketConnector();
        await Speak(new ElevenLabsTts(Opts(), sockets, new ListLogger()), "Hello there.", sockets, async s => await MemorySocketPair.SendTextAsync(s, """{"isFinal":true}"""));
        var conn = sockets.Connections.Single();
        Assert.Equal("api.elevenlabs.io", conn.Uri.Host);
        Assert.Equal("/v1/text-to-speech/voice_ABC-1/stream-input", conn.Uri.AbsolutePath);
        Assert.Contains("output_format=ulaw_8000", conn.Uri.Query); Assert.Contains("model_id=model-x", conn.Uri.Query); Assert.Contains("enable_logging=false", conn.Uri.Query);
        Assert.DoesNotContain(Key, conn.Uri.ToString());
        Assert.Equal(Key, conn.Headers["xi-api-key"]);
    }

    [Theory]
    [InlineData("", "api.elevenlabs.io")] [InlineData("us", "api.us.elevenlabs.io")] [InlineData("eu", "api.eu.residency.elevenlabs.io")]
    [InlineData("in", "api.in.residency.elevenlabs.io")] [InlineData("sg", "api.sg.residency.elevenlabs.io")]
    public void The_region_picks_one_of_the_fixed_hosts(string region, string host) => Assert.Equal(host, ElevenLabsTts.AddressFor(Opts(region)).Host);

    [Fact]
    public void An_unknown_region_is_never_turned_into_a_host() => Assert.Throws<InvalidOperationException>(() => ElevenLabsTts.AddressFor(Opts("evil.example.com")));

    [Fact]
    public async Task It_sends_the_opening_blank_then_the_sentence_with_flush_then_the_end_and_yields_the_audio_in_order_until_the_final_frame()
    {
        var sockets = new FakeSocketConnector(); var got = new List<string>();
        var chunks = await Speak(new ElevenLabsTts(Opts(), sockets, new ListLogger()), "  How can I help?  ", sockets, async s =>
        {
            for (var i = 0; i < 3; i++) got.Add(Encoding.UTF8.GetString((await MemorySocketPair.ReceiveAsync(s))!.Value.Data));
            await MemorySocketPair.SendTextAsync(s, Audio(1, 2, 3));
            await MemorySocketPair.SendTextAsync(s, "not json at all");                 // skipped
            await MemorySocketPair.SendTextAsync(s, """{"audio":null}""");              // nothing to play
            await MemorySocketPair.SendTextAsync(s, Audio(4, 5));
            await MemorySocketPair.SendTextAsync(s, """{"isFinal":true}""");
        });
        Assert.Equal(new[] { """{"text":" "}""", """{"text":"How can I help? ","flush":true}""", """{"text":""}""" }, got);
        Assert.Equal([new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }], chunks);
    }

    [Fact]
    public async Task Empty_text_opens_no_connection()
    {
        var sockets = new FakeSocketConnector();
        await foreach (var _ in new ElevenLabsTts(Opts(), sockets, new ListLogger()).SynthesizeAsync("   ", CancellationToken.None)) { }
        Assert.Empty(sockets.Connections);
    }

    [Fact]
    public async Task Cancelling_a_barge_in_stops_at_once_and_closes_the_vendor_socket()
    {
        var sockets = new FakeSocketConnector();
        var tts = new ElevenLabsTts(Opts(), sockets, new ListLogger());
        using var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource();
        var run = Task.Run(async () => { await foreach (var _ in tts.SynthesizeAsync("A long sentence that is being spoken.", cts.Token)) first.TrySetResult(); });
        var conn = await sockets.NextAsync();
        for (var i = 0; i < 3; i++) await MemorySocketPair.ReceiveAsync(conn.Server);
        await MemorySocketPair.SendTextAsync(conn.Server, Audio(9));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();                                                                     // the caller talked over it
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(conn.Pair.Client.State is WebSocketState.Closed or WebSocketState.Aborted or WebSocketState.CloseSent);
    }

    [Fact]
    public async Task The_text_is_never_logged()
    {
        var sockets = new FakeSocketConnector(); var log = new ListLogger();
        await Speak(new ElevenLabsTts(Opts(), sockets, log), "my private sentence", sockets, async s => await MemorySocketPair.SendTextAsync(s, """{"isFinal":true}"""));
        Assert.DoesNotContain(log.Lines, l => l.Contains("private") || l.Contains(Key));
    }
}

public class OpenAiVoiceLlmTests
{
    private const string Key = "sk-test-not-real";
    private static readonly VoiceLlmOptions Opts = new() { Model = "model-x", TimeoutSeconds = 2 };

    private static async Task<List<LlmEvent>> Run(OpenAiVoiceLlm llm, LlmRequest r)
    {
        var l = new List<LlmEvent>();
        await foreach (var e in llm.StreamAsync(r, CancellationToken.None)) l.Add(e);
        return l;
    }

    private static LlmRequest Req(params LlmMessage[] m) => new("THE INSTRUCTIONS", m, new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "search_products" }));

    [Fact]
    public async Task The_request_carries_the_same_instructions_and_tools_stores_nothing_and_goes_to_the_fixed_address_with_the_key_in_the_header()
    {
        var h = new FakeLlmHandler { Body = FakeLlmHandler.Sse("""{"type":"response.completed","response":{"usage":{"input_tokens":1,"output_tokens":1}}}""") };
        var llm = new OpenAiVoiceLlm(new HttpClient(h), Key, Opts);
        await Run(llm, Req(new LlmMessage("user", "hello")));
        var (uri, auth, body) = h.Requests.Single();
        Assert.Equal("https://api.openai.com/v1/responses", uri!.ToString());
        Assert.Equal("Bearer " + Key, auth);
        var j = JsonNode.Parse(body)!;
        Assert.Equal("model-x", j["model"]!.GetValue<string>());
        Assert.Equal("THE INSTRUCTIONS", j["instructions"]!.GetValue<string>());
        Assert.True(j["stream"]!.GetValue<bool>());
        Assert.False(j["store"]!.GetValue<bool>());                                          // OpenAI keeps no stored response
        Assert.Equal("search_products", j["tools"]![0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(Key, body);
    }

    [Fact]
    public async Task The_conversation_is_sent_as_user_assistant_function_call_and_function_call_output_items()
    {
        var h = new FakeLlmHandler { Body = FakeLlmHandler.Sse("""{"type":"response.completed","response":{}}""") };
        await Run(new OpenAiVoiceLlm(new HttpClient(h), Key, Opts), Req(
            new LlmMessage("system", "Greet the caller."),
            new LlmMessage("user", "Find challah"),
            new LlmMessage("assistant", "Sure.", [new LlmToolCall("c1", "search_products", """{"query":"challah"}""")]),
            new LlmMessage("tool", """{"found":3}""", ToolCallId: "c1"),
            new LlmMessage("assistant", "I found three.")));
        var input = JsonNode.Parse(h.Requests.Single().Body)!["input"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "function_call", "function_call_output", "assistant"],
            input.Select(i => i!["type"]?.GetValue<string>() ?? i["role"]!.GetValue<string>()));
        Assert.Equal(("c1", "search_products", """{"query":"challah"}"""), (input[3]!["call_id"]!.GetValue<string>(), input[3]!["name"]!.GetValue<string>(), input[3]!["arguments"]!.GetValue<string>()));
        Assert.Equal(("c1", """{"found":3}"""), (input[4]!["call_id"]!.GetValue<string>(), input[4]!["output"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_event_stream_becomes_text_deltas_a_tool_call_and_the_token_counts()
    {
        var h = new FakeLlmHandler { Body = FakeLlmHandler.Sse(
            """{"type":"response.created","response":{}}""",
            """{"type":"response.output_text.delta","delta":"Hello "}""",
            """{"type":"response.output_text.delta","delta":"there."}""",
            """{"type":"response.output_text.delta","delta":""}""",
            "not json",
            """{"type":"response.output_item.done","item":{"type":"message"}}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c9","name":"off_topic","arguments":"{\"category\":\"news\"}"}}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c10","name":"view_cart","arguments":""}}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":120,"output_tokens":17}}}""") };
        var ev = await Run(new OpenAiVoiceLlm(new HttpClient(h), Key, Opts), Req(new LlmMessage("user", "x")));
        Assert.Equal(["Hello ", "there."], ev.OfType<LlmTextDelta>().Select(d => d.Text));
        var calls = ev.OfType<LlmToolCallEvent>().Select(c => c.Call).ToList();
        Assert.Equal(("c9", "off_topic", """{"category":"news"}"""), (calls[0].CallId, calls[0].Name, calls[0].Arguments));
        Assert.Equal(("c10", "view_cart", "{}"), (calls[1].CallId, calls[1].Name, calls[1].Arguments));       // empty arguments become an empty object
        Assert.Equal((120L, 17L), (ev.OfType<LlmCompleted>().Single().InputTokens, ev.OfType<LlmCompleted>().Single().OutputTokens));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.TooManyRequests)] [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_request_throws_without_reading_or_leaking_the_body(HttpStatusCode status)
    {
        var h = new FakeLlmHandler { Status = status, Body = "the body echoes: my secret words and " + Key };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Run(new OpenAiVoiceLlm(new HttpClient(h), Key, Opts), Req(new LlmMessage("user", "my secret words"))));
        Assert.DoesNotContain("secret", ex.Message); Assert.DoesNotContain(Key, ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Theory]
    [InlineData("response.failed")] [InlineData("response.incomplete")] [InlineData("error")]
    public async Task A_model_that_does_not_finish_throws_a_message_with_no_content(string type)
    {
        var h = new FakeLlmHandler { Body = FakeLlmHandler.Sse($"{{\"type\":\"{type}\",\"response\":{{\"error\":{{\"message\":\"my secret words\"}}}}}}") };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new OpenAiVoiceLlm(new HttpClient(h), Key, Opts), Req(new LlmMessage("user", "x"))));
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task A_model_that_never_answers_is_cut_off_after_the_timeout()
    {
        var h = new FakeLlmHandler { NeverAnswer = true };
        var llm = new OpenAiVoiceLlm(new HttpClient(h), Key, new VoiceLlmOptions { Model = "m", TimeoutSeconds = 1 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(llm, Req(new LlmMessage("user", "x"))).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
