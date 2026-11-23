using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.TestSet;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>OpenAI Realtime transcription as a recogniser (decision V-47), against a fake socket: the session it asks for, its own end-of-speech detection and commit, matching by item id, and honesty about what it does not give.</summary>
public sealed class VoiceOpenAiTranscribeTests
{
    private const string Url = "wss://api.openai.com/v1/realtime";

    private sealed class FakeVocabulary(params string[] terms) : IVoiceVocabulary
    {
        public IReadOnlyList<string> Terms { get; } = terms;
        public string Hints(string? staticHints, int maxChars) => string.Join(", ", Terms);
    }

    private static OpenAiTranscribeOptions Opts(Action<OpenAiTranscribeOptions>? f = null) { var o = new OpenAiTranscribeOptions(); f?.Invoke(o); return o; }

    private static byte[] Frame(double amplitude, int phase = 0)
    {
        var b = new byte[160];
        for (var i = 0; i < b.Length; i++) b[i] = PhoneAudio.PcmToMulaw((short)(amplitude * Math.Sin((i + phase) * 0.4)));
        return b;
    }
    private static byte[] Loud(int phase = 0) => Frame(9000, phase);
    private static byte[] Quiet() => Frame(15);

    /// <summary>Reads the fake vendor's end of the socket in the background (a read that times out would abort the socket), so a test can take the next message or wait for quiet.</summary>
    private sealed class Pump
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> _q = new();
        public Pump(WebSocket server) => _ = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    using var ms = new MemoryStream(); WebSocketReceiveResult r;
                    do { r = await server.ReceiveAsync(buffer, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
                    _q.Enqueue(JsonDocument.Parse(ms.ToArray()).RootElement.Clone());
                }
            }
            catch (Exception) { }
        });
        public async Task<JsonElement> NextAsync(int seconds = 5)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) { if (_q.TryDequeue(out var m)) return m; await Task.Delay(5); }
            throw new TimeoutException("the adapter sent nothing");
        }
        /// <summary>Everything that arrives until nothing has arrived for a moment.</summary>
        public async Task<List<JsonElement>> DrainAsync(int quietMs = 250)
        {
            var all = new List<JsonElement>(); var lastAt = DateTime.UtcNow;
            while ((DateTime.UtcNow - lastAt).TotalMilliseconds < quietMs) { if (_q.TryDequeue(out var m)) { all.Add(m); lastAt = DateTime.UtcNow; } else await Task.Delay(5); }
            return all;
        }
    }

    private static async Task<(ISttSession Session, Pump Conn, ListLogger Log, FakeSocketConnector.Connection Raw)> Open(OpenAiTranscribeOptions? o = null, IVoiceVocabulary? vocab = null)
    {
        var connector = new FakeSocketConnector(); var log = new ListLogger();
        var stt = new OpenAiTranscribeStt(o ?? Opts(), "sk-test-key-not-real", Url, connector, log, vocab);
        var session = await stt.OpenAsync(CancellationToken.None);
        var raw = await connector.NextAsync();
        return (session, new Pump(raw.Server), log, raw);
    }

    private static async Task<List<SttEvent>> TakeAsync(ISttSession s, int count, int seconds = 5)
    {
        var list = new List<SttEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await foreach (var e in s.Events(cts.Token)) { list.Add(e); if (list.Count >= count) break; }
        return list;
    }

    // ---------- the session ----------

    [Fact]
    public async Task It_connects_for_transcription_with_no_model_in_the_address_the_key_in_the_header_and_asks_for_a_transcription_session()
    {
        var (s, conn, _, raw) = await Open(vocab: new FakeVocabulary("Challah Co", "Zabar's", "Honey Cake"));
        await using var _s = s;
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", raw.Uri.ToString());
        Assert.DoesNotContain("model", raw.Uri.Query);
        Assert.Equal("Bearer sk-test-key-not-real", raw.Headers["Authorization"]);
        Assert.DoesNotContain("sk-test", raw.Uri.ToString());
        var u = await conn.NextAsync();
        Assert.Equal("session.update", u.GetProperty("type").GetString());
        var session = u.GetProperty("session");
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        var input = session.GetProperty("audio").GetProperty("input");
        Assert.Equal("audio/pcmu", input.GetProperty("format").GetProperty("type").GetString());                              // what the telephone carries, no conversion
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);                                       // gpt-live-transcribe takes none: we detect the end of speech
        var tr = input.GetProperty("transcription");
        Assert.Equal("gpt-live-transcribe", tr.GetProperty("model").GetString());
        Assert.Equal(["en"], tr.GetProperty("languages").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.False(tr.TryGetProperty("language", out _));                                                                    // never both: the model takes the plural
        Assert.Equal(["Challah Co", "Zabar's", "Honey Cake"], tr.GetProperty("keywords").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Keywords_are_plain_bounded_and_left_out_when_there_are_none_and_the_languages_are_configurable()
    {
        var (s, conn, _, raw) = await Open(Opts(o => { o.KeywordMax = 2; o.Languages = "en, yi"; o.Model = "gpt-live-transcribe-x"; }), new FakeVocabulary("Honey Cake", "Bagel & Bean", "<b>x</b>", "Rye", "Babka"));
        await using var _s = s;
        var tr = (await conn.NextAsync()).GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.Equal("gpt-live-transcribe-x", tr.GetProperty("model").GetString());
        Assert.Equal(["en", "yi"], tr.GetProperty("languages").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["Honey Cake", "Rye"], tr.GetProperty("keywords").EnumerateArray().Select(x => x.GetString()!).ToArray());   // the & and the tags are not sent, the cap applies after
        await s.DisposeAsync();
        var (s2, conn2, _, raw2) = await Open(Opts(o => o.Languages = ""));
        await using var _s2 = s2;
        var tr2 = (await conn2.NextAsync()).GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.False(tr2.TryGetProperty("keywords", out _)); Assert.False(tr2.TryGetProperty("languages", out _));
    }

    [Fact]
    public async Task When_the_api_refuses_the_session_update_the_keywords_are_dropped_and_it_is_sent_once_more()
    {
        var (s, conn, log, raw) = await Open(vocab: new FakeVocabulary("Challah Co"));
        await using var _s = s;
        await conn.NextAsync();                                                                                          // the first update, with keywords
        await MemorySocketPair.SendTextAsync(raw.Server, """{"type":"error","error":{"type":"invalid_request_error","code":"invalid_value","message":"keyword rejected"}}""");
        var again = await conn.NextAsync();
        Assert.False(again.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription").TryGetProperty("keywords", out _));
        Assert.Contains(log.Lines, l => l.Contains("invalid_value") && l.Contains("without the catalog keywords"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("keyword rejected"));                                                  // the message is never logged
        await MemorySocketPair.SendTextAsync(raw.Server, """{"type":"error","error":{"code":"invalid_value"}}""");            // a second refusal: no endless loop
        Assert.Empty(await conn.DrainAsync());
    }

    // ---------- the end of speech is ours ----------

    [Fact]
    public async Task Speech_starts_a_turn_with_a_half_second_before_it_and_silence_ends_it_with_a_commit()
    {
        var (s, conn, _, raw) = await Open(Opts(o => o.EndpointSilenceMs = 200));
        await using var _s = s;
        await conn.NextAsync();                                                                                          // session.update
        for (var i = 0; i < 20; i++) await s.SendAudioAsync(Quiet(), default);                                                // a quiet line: nothing is sent
        Assert.Empty(await conn.DrainAsync());
        for (var i = 0; i < 10; i++) await s.SendAudioAsync(Loud(i), default);                                                // the caller speaks
        for (var i = 0; i < 12; i++) await s.SendAudioAsync(Quiet(), default);                                                // and stops (200 ms is ten frames)
        var sent = await conn.DrainAsync();
        var types = sent.Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Equal("input_audio_buffer.commit", types.Last());
        Assert.Equal(1, types.Count(t => t == "input_audio_buffer.commit"));
        var appends = sent.Where(m => m.GetProperty("type").GetString() == "input_audio_buffer.append").ToList();
        Assert.InRange(appends.Count, 20, 40);                                                                                  // the pre-roll, the speech and the trailing silence; not the 20 quiet frames before
        Assert.All(appends, a => Assert.Equal(160, Convert.FromBase64String(a.GetProperty("audio").GetString()!).Length));
        var kinds = (await TakeAsync(s, 2)).Select(e => e.Kind).ToList();
        Assert.Equal([SttEventKind.SpeechStarted, SttEventKind.SpeechEnded], kinds);
    }

    [Fact]
    public async Task A_turn_that_never_pauses_is_committed_at_the_limit_and_a_short_click_starts_nothing()
    {
        var (s, conn, _, raw) = await Open(Opts(o => { o.MaxTurnSeconds = 5; o.EndpointSilenceMs = 3000; }));
        await using var _s = s;
        await conn.NextAsync();
        await s.SendAudioAsync(Loud(), default); await s.SendAudioAsync(Quiet(), default); await s.SendAudioAsync(Quiet(), default);   // one loud frame: a click, not speech
        Assert.Empty(await conn.DrainAsync());
        for (var i = 0; i < 260; i++) await s.SendAudioAsync(Loud(i), default);                                                // 5.2 seconds of speech
        var types = (await conn.DrainAsync()).Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Contains("input_audio_buffer.commit", types);
    }

    [Fact]
    public async Task Audio_in_odd_sized_pieces_is_cut_into_20_millisecond_frames()
    {
        var (s, conn, _, raw) = await Open(Opts(o => o.EndpointSilenceMs = 200));
        await using var _s = s;
        await conn.NextAsync();
        var all = Enumerable.Range(0, 12).SelectMany(i => Loud(i)).Concat(Enumerable.Range(0, 12).SelectMany(_ => Quiet())).ToArray();
        for (var i = 0; i < all.Length; i += 97) await s.SendAudioAsync(all.AsMemory(i, Math.Min(97, all.Length - i)), default);
        var sent = await conn.DrainAsync();
        Assert.All(sent.Where(m => m.GetProperty("type").GetString() == "input_audio_buffer.append"), a => Assert.Equal(160, Convert.FromBase64String(a.GetProperty("audio").GetString()!).Length));
        Assert.Contains(sent, m => m.GetProperty("type").GetString() == "input_audio_buffer.commit");
    }

    // ---------- what it reports ----------

    [Fact]
    public async Task Deltas_become_growing_partials_and_the_completed_text_is_the_final_matched_by_item_id_in_any_order()
    {
        var (s, conn, _, raw) = await Open();
        await using var _s = s;
        await conn.NextAsync();
        var send = (string j) => MemorySocketPair.SendTextAsync(raw.Server, j);
        await send("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"A","delta":"do you "}""");
        await send("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"B","delta":"two "}""");
        await send("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"A","delta":"have"}""");
        await send("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"B","transcript":"two cups"}""");   // the later turn finishes first
        await send("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"A","transcript":" do you have a kiddush cup "}""");
        var e = await TakeAsync(s, 5);
        Assert.Equal(["do you", "two", "do you have"], e.Take(3).Select(x => x.Text).ToArray());
        Assert.Equal([SttEventKind.Partial, SttEventKind.Partial, SttEventKind.Partial, SttEventKind.Final, SttEventKind.Final], e.Select(x => x.Kind).ToArray());
        Assert.Equal(["two cups", "do you have a kiddush cup"], e.Skip(3).Select(x => x.Text).ToArray());
    }

    [Fact]
    public async Task One_hypothesis_only_no_confidence_no_alternatives_no_words()
    {
        var (s, conn, _, raw) = await Open();
        await using var _s = s;
        await conn.NextAsync();
        await MemorySocketPair.SendTextAsync(raw.Server, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"A","transcript":"hello there","logprobs":[{"token":"hello","logprob":-0.1}]}""");
        var f = (await TakeAsync(s, 1)).Single();
        Assert.Equal((SttEventKind.Final, "hello there"), (f.Kind, f.Text));
        Assert.Null(f.Confidence); Assert.Null(f.Candidates); Assert.Null(f.Words); Assert.Null(f.Language);                    // so the doubt and alternatives notes (V-42, V-43) have nothing from this recogniser
    }

    [Fact]
    public async Task A_failed_or_empty_transcription_is_no_final_and_noise_is_ignored_and_the_words_are_never_logged()
    {
        var (s, conn, log, raw) = await Open();
        await using var _s = s;
        await conn.NextAsync();
        var send = (string j) => MemorySocketPair.SendTextAsync(raw.Server, j);
        await send("not json"); await send("""{"type":"session.created"}"""); await send("""{"type":"input_audio_buffer.committed","item_id":"A"}""");
        await send("""{"type":"conversation.item.input_audio_transcription.failed","item_id":"A","error":{"message":"my secret words"}}""");
        await send("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"B","transcript":"   "}""");
        await send("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"C","transcript":"after all that"}""");
        var f = (await TakeAsync(s, 1)).Single();
        Assert.Equal("after all that", f.Text);
        Assert.Contains(log.Lines, l => l.Contains("could not transcribe"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("secret") || l.Contains("after all that") || l.Contains("sk-test"));
    }

    [Fact]
    public async Task When_the_vendor_closes_the_events_end_and_a_vendor_that_cannot_be_reached_throws_on_open()
    {
        var (s, conn, _, raw) = await Open();
        await raw.Server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in s.Events(cts.Token)) { }                                                                       // completes
        await s.DisposeAsync();
        var down = new FakeSocketConnector { Fail = true };
        await Assert.ThrowsAsync<WebSocketException>(() => new OpenAiTranscribeStt(Opts(), "k", Url, down, new ListLogger()).OpenAsync(default));
    }

    // ---------- which options can use it ----------

    private static VoiceOptions V(Action<VoiceOptions>? f = null)
    {
        var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
        o.PipelineMenu.Enabled = true; f?.Invoke(o); return o;
    }
    private static IReadOnlyList<string> Problems(Action<VoiceOptions>? f = null) => VoiceConfig.Problems(V(f), new SignalWireOptions { SigningKey = "sw" }, true);
    private static void ElevenLabs(VoiceOptions o) { o.Anthropic.ApiKey = "a"; o.ElevenLabs.ApiKey = "k"; o.ElevenLabs.VoiceId = "voice_1"; o.ElevenLabs.TtsModel = "t"; o.Pipelines.Llm.Model = "m"; }

    [Fact]
    public void OpenAi_is_a_recogniser_of_options_2_3_and_4_and_needs_only_the_existing_openai_key()
    {
        Assert.True(VoicePipelineVendors.Supported("p2", new PipelineProviders { Stt = "openai", Tts = "elevenlabs" }));
        Assert.True(VoicePipelineVendors.Supported("p3", new PipelineProviders { Stt = "OpenAI", Tts = "elevenlabs" }));
        Assert.True(VoicePipelineVendors.Supported("p4", new PipelineProviders { Stt = "openai", Tts = "yiddishlabs" }));
        Assert.False(VoicePipelineVendors.Supported("p2", new PipelineProviders { Stt = "openai", Tts = "somewhere" }));
        var v = V(o => { ElevenLabs(o); o.Pipelines.P2 = new PipelineProviders { Stt = "openai", Tts = "elevenlabs" }; });
        Assert.Empty(VoicePipelineVendors.Missing(v, "p2"));                                                                    // no Deepgram settings, no new secret
        v.OpenAiApiKey = "";
        Assert.Equal(["Voice:OpenAiApiKey"], VoicePipelineVendors.Missing(v, "p2"));                                            // missing = unavailable, never a start-up failure (V-26)
        v.OpenAiApiKey = "k"; v.OpenAiTranscribe.Model = "";
        Assert.Contains("Voice:OpenAiTranscribe:Model", VoicePipelineVendors.Missing(v, "p2"));
    }

    [Fact]
    public void Its_values_are_checked_at_start_up_only_when_an_option_uses_it()
    {
        void On(VoiceOptions o) { ElevenLabs(o); o.Pipelines.P2 = new PipelineProviders { Stt = "openai", Tts = "elevenlabs" }; }
        Assert.Empty(Problems(On));
        Assert.Empty(Problems(o => o.OpenAiTranscribe.MinEnergy = 1));                                                          // not used: not checked
        foreach (var bad in new Action<VoiceOptions>[] { o => o.OpenAiTranscribe.MinEnergy = 1, o => o.OpenAiTranscribe.EndpointSilenceMs = 50, o => o.OpenAiTranscribe.MaxTurnSeconds = 1, o => o.OpenAiTranscribe.KeywordMax = 500, o => o.OpenAiTranscribe.Languages = "english", o => o.OpenAiTranscribe.Model = "a b" })
            Assert.NotEmpty(Problems(o => { On(o); bad(o); }));
    }

    [Fact]
    public void Option_5_can_add_it_as_a_third_candidate_only_on_request_and_then_needs_the_judge()
    {
        void P5(VoiceOptions o)
        {
            o.Pipelines.P5 = new P5Options { Stt = "gladia+yiddishlabs", Tts = "yiddishlabs" }; o.Pipelines.Llm.Model = "m";
            o.Gladia.ApiKey = "g"; o.Gladia.Model = "m"; o.Gladia.Language = "en"; o.YiddishLabs.FlashApiKey = "f"; o.YiddishLabs.TtsVoiceId = "v"; o.YiddishLabs.ConsentLine = "c"; o.YiddishLabs.SttModel = "s";
        }
        var off = V(P5);
        off.Anthropic.ApiKey = "a";
        Assert.Empty(VoicePipelineVendors.Missing(off, "p5"));                                                                  // off by default: OpenAI is not part of option 5
        var on = V(o => { P5(o); o.Anthropic.ApiKey = "a"; o.Pipelines.P5.OpenAiInJudge = true; });
        Assert.Empty(VoicePipelineVendors.Missing(on, "p5"));                                                                   // the judge is Claude by default: its key is the one the options already need
        on.Pipelines.P5.JudgeProvider = "openai";
        Assert.Contains("Voice:Pipelines:P5:JudgeModel", VoicePipelineVendors.Missing(on, "p5"));                                // an OpenAI judge needs its model, as the Flash switch does
        on.OpenAiApiKey = "";
        Assert.Contains("Voice:OpenAiApiKey", VoicePipelineVendors.Missing(on, "p5"));
    }

    [Fact]
    public void The_scoring_tool_can_replay_recordings_through_it()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voice:Pipelines:P2:Stt"] = "openai", ["Voice:Pipelines:P2:Tts"] = "elevenlabs", ["Voice:OpenAiApiKey"] = "k",
        }).Build();
        Assert.Equal(["p2"], ScoringSetup.ReplayableOptions(cfg));
        using var setup = new ScoringSetup();
        Assert.IsType<OpenAiTranscribeStt>(setup.Build(cfg, new VariantSpec("openai-live", "p2", new Dictionary<string, string> { ["Voice:OpenAiTranscribe:EndpointSilenceMs"] = "900" }, ["challah"])).Stt);
    }
}

/// <summary>Option 2 end to end with OpenAI transcription as its recogniser (decision V-47): the telephone's audio reaches the fake OpenAI socket, our silence detection commits, the final words reach the text model, and the answer is spoken.</summary>
public sealed class VoiceOpenAiTranscribeCallTests : IDisposable
{
    private const string Number = "+15551234567", Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();
    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    private static byte[] Frame(double amplitude, int phase = 0)
    {
        var b = new byte[160];
        for (var i = 0; i < b.Length; i++) b[i] = PhoneAudio.PcmToMulaw((short)(amplitude * Math.Sin((i + phase) * 0.4)));
        return b;
    }
    private System.Collections.Generic.IEnumerable<System.Text.Json.Nodes.JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);

    [Fact]
    public async Task A_caller_turn_goes_audio_to_OpenAi_then_the_final_words_to_the_text_model_and_speech_back()
    {
        var sockets = new FakeSocketConnector();
        var llm = new FakeLlmHandler { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
        var settings = VoiceP2PipelineTests.P2Settings();
        settings["Voice:Pipelines:P2:Stt"] = "openai"; settings.Remove("Voice:Deepgram:ApiKey");                       // no Deepgram anywhere
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: settings), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
        });
        Assert.True(S.F.Services.GetRequiredService<VoicePipelineRegistry>().IsAvailable("p2"));
        _ = Task.Run(() => VoiceP2PipelineTests.ElevenLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "2" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        await FakeRealtime.Poll(() => Frames("media").Count() >= 4 ? "x" : null, 5);                                  // the greeting
        var openai = sockets.Connections.Single(c => c.Uri.Host == "api.openai.com");
        Assert.Equal("/v1/realtime", openai.Uri.AbsolutePath); Assert.Equal("?intent=transcription", openai.Uri.Query);
        Assert.DoesNotContain(sockets.Connections, c => c.Uri.Host == "api.deepgram.com");
        var update = System.Text.Json.JsonDocument.Parse((await MemorySocketPair.ReceiveAsync(openai.Server))!.Value.Data).RootElement;
        Assert.Equal("session.update", update.GetProperty("type").GetString());

        // the caller speaks, then is silent: the phone's frames reach OpenAI as appends and a commit follows
        for (var i = 0; i < 12; i++) await _phone.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(Frame(9000, i)) } });
        for (var i = 0; i < 40; i++) await _phone.Send(new { @event = "media", streamSid = "MZ1", media = new { payload = Convert.ToBase64String(Frame(15)) } });
        var types = new List<string>();
        while (!types.Contains("input_audio_buffer.commit"))
            types.Add(System.Text.Json.JsonDocument.Parse((await MemorySocketPair.ReceiveAsync(openai.Server))!.Value.Data).RootElement.GetProperty("type").GetString()!);
        Assert.Contains("input_audio_buffer.append", types);

        var before = llm.Requests.Count;
        await MemorySocketPair.SendTextAsync(openai.Server, """{"type":"conversation.item.input_audio_transcription.completed","item_id":"A","transcript":"find me a challah"}""");
        await FakeRealtime.Poll(() => llm.Requests.Count > before ? "x" : null, 5);                                   // the final words end the turn at once: no clock to advance
        var input = System.Text.Json.Nodes.JsonNode.Parse(llm.Requests[^1].Body)!["input"]!.AsArray();
        Assert.Contains(input, i => i!["role"]?.GetValue<string>() == "user" && i["content"]!.GetValue<string>() == "find me a challah");
        await FakeRealtime.Poll(() => Frames("media").Count() >= 8 ? "x" : null, 5);                                  // and the answer is spoken
        Assert.Equal(("p2", "pipeline:p2"), ((await S.Row()).Tier, (await S.Row()).Model));
    }
}
