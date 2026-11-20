using System.Net;
using System.Net.WebSockets;
using System.Text;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The Yiddish Labs adapters (decision V-22, slice P4) against fakes: the recogniser's own end-of-speech detection and its one-request-per-turn upload, and the realtime voice.</summary>
public class YiddishLabsFlashSttTests
{
    private const string Key = "yl_flash_not_real";
    private static YiddishLabsOptions Opts(Action<YiddishLabsOptions>? f = null) { var o = new YiddishLabsOptions { FlashApiKey = Key, TtsVoiceId = "v1", ConsentLine = "c" }; f?.Invoke(o); return o; }

    private static byte[] Frames(int count, bool loud) { var b = new byte[count * 160]; Array.Fill(b, loud ? PhoneAudio.PcmToMulaw(8000) : (byte)0xFF); return b; }
    private static byte[] Audio(int silenceBefore, int loud, int silenceAfter) => [.. Frames(silenceBefore, false), .. Frames(loud, true), .. Frames(silenceAfter, false)];

    private static async Task<(List<SttEvent> Events, FakeFlashHandler Http, ListLogger Log)> Run(byte[] audio, FakeFlashHandler http, YiddishLabsOptions? o = null, int chunk = 0)
    {
        var log = new ListLogger();
        var session = await new YiddishLabsFlashStt(o ?? Opts(), new HttpClient(http), log).OpenAsync(CancellationToken.None);
        if (chunk <= 0) await session.SendAudioAsync(audio, CancellationToken.None);
        else for (var i = 0; i < audio.Length; i += chunk) await session.SendAudioAsync(audio.AsMemory(i, Math.Min(chunk, audio.Length - i)), CancellationToken.None);
        await Task.Delay(300);                                                                 // the upload worker runs on its own
        await session.DisposeAsync();
        var events = new List<SttEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in session.Events(cts.Token)) events.Add(e);
        return (events, http, log);
    }

    [Fact]
    public async Task One_spoken_turn_is_one_upload_to_the_fixed_address_with_the_bearer_key_and_one_final_transcript()
    {
        var (events, http, _) = await Run(Audio(10, 25, 25), new FakeFlashHandler(FlashAnswer.Text("איך וויל א חלה")));
        Assert.Equal([SttEventKind.SpeechStarted, SttEventKind.SpeechEnded, SttEventKind.Final], events.Select(e => e.Kind));
        Assert.Equal(("איך וויל א חלה", "yi"), (events[2].Text, events[2].Language));
        var req = http.Requests.Single();
        Assert.Equal("https://flash.yiddishlabs.com/v1/audio/transcriptions", req.Uri!.ToString());
        Assert.Equal("Bearer " + Key, req.Auth);
        Assert.Equal(("yiddishlabs-3-turbo", "yi", "json"), (req.Fields["model"], req.Fields["language"], req.Fields["response_format"]));
        Assert.False(req.Fields.ContainsKey("prompt"));
        Assert.DoesNotContain(Key, string.Join("", req.Fields.Values));
    }

    [Fact]
    public async Task The_uploaded_recording_is_an_8k_mono_wav_with_a_little_before_the_speech_and_the_speech_itself()
    {
        var (_, http, _) = await Run(Audio(10, 25, 25), new FakeFlashHandler(FlashAnswer.Text("x")));
        var wav = http.Requests.Single().File!;
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal((1, 16, 8000), (BitConverter.ToInt16(wav, 22), BitConverter.ToInt16(wav, 34), BitConverter.ToInt32(wav, 24)));
        var samples = (wav.Length - 44) / 2;
        // the 3 frames that proved the speech started are inside it, plus up to 15 frames of pre-roll, the speech and the silence that ended it (450 ms rounds up to 23 frames)
        Assert.InRange(samples, 160 * (10 + 25 + 23 - 2), 160 * (10 + 25 + 25));
    }

    [Fact]
    public async Task A_16k_upload_is_the_same_recording_up_sampled()
    {
        var (_, http, _) = await Run(Audio(10, 25, 25), new FakeFlashHandler(FlashAnswer.Text("x")), Opts(o => o.UploadSampleRate = 16000));
        var wav = http.Requests.Single().File!;
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.True((wav.Length - 44) / 2 > 160 * 2 * 40);
    }

    [Fact]
    public async Task A_click_shorter_than_three_frames_starts_nothing_and_a_blip_shorter_than_200_ms_is_not_sent()
    {
        var (click, http1, _) = await Run(Audio(10, 2, 30), new FakeFlashHandler());
        Assert.Empty(click); Assert.Empty(http1.Requests);
        var (blip, http2, _) = await Run(Audio(10, 5, 30), new FakeFlashHandler());               // 100 ms of sound: heard as speech, too short to upload
        Assert.Equal([SttEventKind.SpeechStarted, SttEventKind.SpeechEnded], blip.Select(e => e.Kind));
        Assert.Empty(http2.Requests);
    }

    [Fact]
    public async Task The_biasing_prompt_and_other_settings_are_sent_when_set()
    {
        var (_, http, _) = await Run(Audio(5, 25, 25), new FakeFlashHandler(FlashAnswer.Text("x")), Opts(o => { o.SttPrompt = "challah, babka"; o.SttModel = "yiddishlabs-3-pro"; o.SttLanguage = "auto"; }));
        var f = http.Requests.Single().Fields;
        Assert.Equal(("challah, babka", "yiddishlabs-3-pro", "auto"), (f["prompt"], f["model"], f["language"]));
    }

    [Fact]
    public async Task Two_turns_are_two_uploads_in_order_and_an_empty_transcript_says_nothing()
    {
        var a = Audio(5, 25, 25);
        var (events, http, _) = await Run([.. a, .. a, .. a], new FakeFlashHandler(FlashAnswer.Text("first"), FlashAnswer.Text("  "), FlashAnswer.Text("third")));
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal(["first", "third"], events.Where(e => e.Kind == SttEventKind.Final).Select(e => e.Text));
    }

    [Fact]
    public async Task A_turn_that_never_stops_is_cut_at_15_seconds()
    {
        var (events, http, _) = await Run([.. Frames(5, false), .. Frames(800, true)], new FakeFlashHandler(FlashAnswer.Text("long")));      // 16 s of speech
        Assert.Contains(events, e => e.Kind == SttEventKind.SpeechEnded);
        Assert.Single(http.Requests);
        Assert.InRange((http.Requests[0].File!.Length - 44) / 2, 160 * 740, 160 * 760);                                                      // about 15 s of 8 kHz samples
    }

    [Fact]
    public async Task Chunk_boundaries_do_not_change_what_is_heard()
    {
        var audio = Audio(10, 25, 25);
        var whole = (await Run(audio, new FakeFlashHandler(FlashAnswer.Text("x")))).Events.Select(e => e.Kind).ToList();
        foreach (var chunk in new[] { 1, 7, 100, 159, 161, 1000 })
            Assert.Equal(whole, (await Run(audio, new FakeFlashHandler(FlashAnswer.Text("x")), chunk: chunk)).Events.Select(e => e.Kind).ToList());
    }

    [Fact]
    public async Task A_busy_vendor_or_a_rate_limit_is_retried_once_and_then_the_text_arrives()
    {
        var (e1, h1, _) = await Run(Audio(5, 25, 25), new FakeFlashHandler(new FlashAnswer(HttpStatusCode.ServiceUnavailable, "no_capacity"), FlashAnswer.Text("after retry")));
        Assert.Equal(2, h1.Requests.Count); Assert.Contains(e1, e => e.Text == "after retry");
        var (e2, h2, _) = await Run(Audio(5, 25, 25), new FakeFlashHandler(new FlashAnswer(HttpStatusCode.TooManyRequests, "slow down", 0), FlashAnswer.Text("after wait")));
        Assert.Equal(2, h2.Requests.Count); Assert.Contains(e2, e => e.Text == "after wait");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.PaymentRequired)] [InlineData(HttpStatusCode.RequestEntityTooLarge)] [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Any_other_failure_ends_the_recogniser_without_a_retry_and_without_logging_what_the_vendor_said(HttpStatusCode status)
    {
        var (events, http, log) = await Run(Audio(5, 25, 25), new FakeFlashHandler(new FlashAnswer(status, "my secret words " + Key)));
        Assert.Single(http.Requests);
        Assert.DoesNotContain(events, e => e.Kind == SttEventKind.Final);
        Assert.Contains(log.Lines, l => l.Contains("recogniser stopped") && l.Contains("HttpRequestException"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("secret") || l.Contains(Key));
    }

    [Fact]
    public async Task A_rate_limit_that_asks_for_a_long_wait_ends_the_recogniser_at_once_it_is_not_waited_for()
    {
        var http = new FakeFlashHandler(new FlashAnswer(HttpStatusCode.TooManyRequests, "x", 60));
        var session = await new YiddishLabsFlashStt(Opts(), new HttpClient(http), new ListLogger()).OpenAsync(CancellationToken.None);
        await session.SendAudioAsync(Audio(5, 25, 25), CancellationToken.None);
        var drained = Task.Run(async () => { await foreach (var _ in session.Events(CancellationToken.None)) { } });
        await drained.WaitAsync(TimeSpan.FromSeconds(3));                                      // the event stream ENDS (the pipeline then apologises) without anyone disposing the session or waiting a minute
        Assert.Single(http.Requests);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task What_the_caller_said_is_never_logged()
    {
        var (_, _, log) = await Run(Audio(5, 25, 25), new FakeFlashHandler(FlashAnswer.Text("my private sentence")));
        Assert.DoesNotContain(log.Lines, l => l.Contains("private") || l.Contains(Key));
    }
}

public class YiddishLabsTtsTests
{
    private const string Key = "yl_flash_not_real";
    private static YiddishLabsOptions Opts(Action<YiddishLabsOptions>? f = null) { var o = new YiddishLabsOptions { FlashApiKey = Key, TtsVoiceId = "voice 1/x", ConsentLine = "c" }; f?.Invoke(o); return o; }
    private static byte[] Pcm(params short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }
    private static Task Binary(WebSocket ws, byte[] data) => ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<List<byte[]>> Speak(YiddishLabsTts tts, string text, FakeSocketConnector sockets, Func<WebSocket, Task> vendor)
    {
        var script = Task.Run(async () => { var conn = await sockets.NextAsync(); await vendor(conn.Server); });
        var chunks = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var c in tts.SynthesizeAsync(text, cts.Token)) chunks.Add(c);
        await script;
        return chunks;
    }

    [Fact]
    public async Task It_opens_the_voice_with_the_bearer_key_in_the_header_and_the_hd_flag_only_when_asked()
    {
        var sockets = new FakeSocketConnector();
        await Speak(new YiddishLabsTts(Opts(), sockets, new ListLogger()), "Hello.", sockets, s => MemorySocketPair.SendTextAsync(s, """{"type":"done"}"""));
        var conn = sockets.Connections.Single();
        Assert.Equal(("rt.yiddishlabs.com", "/v1/tts/realtime"), (conn.Uri.Host, conn.Uri.AbsolutePath));
        Assert.Contains("voice=voice%201%2Fx", conn.Uri.Query); Assert.DoesNotContain("hd=", conn.Uri.Query);
        Assert.Equal("Bearer " + Key, conn.Headers["Authorization"]);
        Assert.DoesNotContain(Key, conn.Uri.ToString());
        Assert.Contains("hd=true", YiddishLabsTts.AddressFor(Opts(o => o.TtsHd = true)).Query);
    }

    [Fact]
    public async Task It_sends_the_text_then_input_done_and_turns_the_16k_pcm_it_gets_into_8k_mulaw_in_order_until_done()
    {
        var sockets = new FakeSocketConnector(); var got = new List<string>();
        var chunks = await Speak(new YiddishLabsTts(Opts(), sockets, new ListLogger()), "  A sentence.  ", sockets, async s =>
        {
            for (var i = 0; i < 2; i++) got.Add(Encoding.UTF8.GetString((await MemorySocketPair.ReceiveAsync(s))!.Value.Data));
            await MemorySocketPair.SendTextAsync(s, """{"type":"session.ready","session_id":"s","sample_rate":16000,"format":"pcm_s16le","channels":1}""");
            await Binary(s, Pcm(1000, 3000, -2000, -4000));
            await MemorySocketPair.SendTextAsync(s, """{"type":"segment.start","index":0}""");      // ignored
            await Binary(s, Pcm(500, 500));
            await MemorySocketPair.SendTextAsync(s, "not json");                                    // ignored
            await MemorySocketPair.SendTextAsync(s, """{"type":"done","billed_words":2}""");
        });
        Assert.Equal(new[] { """{"type":"input.text","text":"A sentence."}""", """{"type":"input.done"}""" }, got);
        Assert.Equal([[PhoneAudio.PcmToMulaw(2000), PhoneAudio.PcmToMulaw(-3000)], [PhoneAudio.PcmToMulaw(500)]], chunks);
    }

    [Fact]
    public async Task HD_48k_audio_is_averaged_down_too()
    {
        var sockets = new FakeSocketConnector();
        var chunks = await Speak(new YiddishLabsTts(Opts(o => o.TtsHd = true), sockets, new ListLogger()), "x.", sockets, async s =>
        {
            for (var i = 0; i < 2; i++) await MemorySocketPair.ReceiveAsync(s);
            await MemorySocketPair.SendTextAsync(s, """{"type":"session.ready","sample_rate":48000}""");
            await Binary(s, Pcm(100, 300, 500, 700, 900, 1100));
            await MemorySocketPair.SendTextAsync(s, """{"type":"done"}""");
        });
        Assert.Equal([[PhoneAudio.PcmToMulaw(600)]], chunks);
    }

    [Fact]
    public async Task A_cancelled_sentence_tells_the_vendor_to_stop_and_closes()
    {
        var sockets = new FakeSocketConnector();
        var tts = new YiddishLabsTts(Opts(), sockets, new ListLogger());
        using var cts = new CancellationTokenSource(); var first = new TaskCompletionSource(); var cancelSeen = new TaskCompletionSource<string>();
        var run = Task.Run(async () => { await foreach (var _ in tts.SynthesizeAsync("A long sentence being spoken.", cts.Token)) first.TrySetResult(); });
        var conn = await sockets.NextAsync();
        for (var i = 0; i < 2; i++) await MemorySocketPair.ReceiveAsync(conn.Server);
        await MemorySocketPair.SendTextAsync(conn.Server, """{"type":"session.ready","sample_rate":16000}""");
        await Binary(conn.Server, Pcm(1000, 1000));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();                                                                           // the caller talked over it
        var m = await MemorySocketPair.ReceiveAsync(conn.Server);
        Assert.Equal("""{"type":"cancel"}""", Encoding.UTF8.GetString(m!.Value.Data));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_error_frame_audio_before_ready_or_an_odd_rate_is_a_failure_with_no_vendor_text()
    {
        var sockets = new FakeSocketConnector();
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => Speak(new YiddishLabsTts(Opts(), sockets, new ListLogger()), "x.", sockets, s => MemorySocketPair.SendTextAsync(s, """{"type":"error","message":"my secret words"}""")));
        Assert.DoesNotContain("secret", ex1.Message);
        var s2 = new FakeSocketConnector();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Speak(new YiddishLabsTts(Opts(), s2, new ListLogger()), "x.", s2, s => Binary(s, Pcm(1, 2))));
        var s3 = new FakeSocketConnector();
        await Assert.ThrowsAsync<NotSupportedException>(() => Speak(new YiddishLabsTts(Opts(), s3, new ListLogger()), "x.", s3, s => MemorySocketPair.SendTextAsync(s, """{"type":"session.ready","sample_rate":22050}""")));
    }

    [Fact]
    public async Task At_most_the_configured_number_of_sessions_run_at_once_and_the_next_is_refused_not_queued_for_long()
    {
        var sockets = new FakeSocketConnector();
        var tts = new YiddishLabsTts(Opts(o => o.MaxConcurrentTtsSessions = 1), sockets, new ListLogger());
        using var hold = new CancellationTokenSource();
        var first = Task.Run(async () => { try { await foreach (var _ in tts.SynthesizeAsync("one.", hold.Token)) { } } catch (OperationCanceledException) { } });
        await sockets.NextAsync();                                                              // the first holds the only session
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in tts.SynthesizeAsync("two.", CancellationToken.None)) { } });
        Assert.Contains("No free speech session", ex.Message);
        Assert.Single(sockets.Connections);                                                     // the refused one never connected
        hold.Cancel(); await first;
        // and the slot is free again afterwards
        var again = Task.Run(async () => { await foreach (var _ in tts.SynthesizeAsync("three.", CancellationToken.None)) { } });
        var conn = await sockets.NextAsync(skip: 1);
        for (var i = 0; i < 2; i++) await MemorySocketPair.ReceiveAsync(conn.Server);
        await MemorySocketPair.SendTextAsync(conn.Server, """{"type":"done"}""");
        await again.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Empty_text_opens_nothing_and_a_vendor_that_closes_early_ends_the_sentence_quietly_and_the_text_is_never_logged()
    {
        var sockets = new FakeSocketConnector(); var log = new ListLogger();
        await foreach (var _ in new YiddishLabsTts(Opts(), sockets, log).SynthesizeAsync("  ", CancellationToken.None)) { }
        Assert.Empty(sockets.Connections);
        await Speak(new YiddishLabsTts(Opts(), sockets, log), "my private sentence.", sockets, s => s.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None));
        Assert.DoesNotContain(log.Lines, l => l.Contains("private") || l.Contains(Key));
    }
}
