using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// One phone call on a CHAINED pipeline (decision V-19): the caller's audio goes to streaming speech-to-text, an end-of-turn engine decides when they have finished, OUR LLM answers with the same instructions
/// and the same tools as the Realtime line, and the answer is spoken by streaming text-to-speech back to the phone. The phone side is the Realtime bridge's own: SignalWire marks tell what the phone has played
/// (<see cref="PhonePlayback"/>), barge-in clears the phone's queue and cuts the assistant's answer to what was HEARD, a tool that starts a keypad step ends the stream after the goodbye, and DTMF digits never go
/// anywhere (keypad payment, codes and PINs stay outside every pipeline).
/// What it keeps: the conversation in memory for the call and counters in the call's row (tokens, an estimated cost, latency). What it never keeps: audio, the caller's words or the assistant's words are not
/// stored and not logged (log lines carry counts and timings only).
/// </summary>
public sealed class ChainedBridge(WebSocket phone, VoiceCall call, VoiceCallState state, IChainedPipelineParts parts, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools,
    TimeProvider time, ILogger logger, ISttSession? opened = null)
{
    private bool _vendorLost;
    private const int BytesPerMs = 8;   // mu-law, 8000 samples per second, one byte each
    private readonly VoiceOptions _o = options.Value;
    private readonly SemaphoreSlim _phoneSend = new(1, 1);
    private readonly PhonePlayback _playback = new(time);
    private readonly List<LlmMessage> _history = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _markEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _turnCts;
    private Task _turn = Task.CompletedTask;
    private string? _streamSid;
    private long _turnSeq, _audioBytesIn, _ttsChars, _llmIn, _llmOut, _lastUserEvent;
    private long? _cutPlayedMs;
    private LlmMessage? _lastAssistant;                      // the last answer added to the history, and what was spoken of it: a barge-in right after it was added still cuts it to what was heard
    private List<SpokenSentence> _lastSpoken = [];
    private bool _endAfterResponse, _dtmfTransfer, _speaking;

    public bool KeypadPause { get; private set; }

    public async Task<string> RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _o.MaxCallSeconds)));   // per-call limit: call length
        var token = cts.Token;
        var outcome = VoiceOutcome.Abandoned;
        ISttSession? stt = null;
        Task phoneLoop = Task.CompletedTask, sttLoop = Task.CompletedTask, tickLoop = Task.CompletedTask;
        try
        {
            try { stt = opened ?? await parts.Stt.OpenAsync(token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Voice call {Call}: the speech recogniser could not be opened ({Type}).", call.Id, ex.GetType().Name);   // never the message; the call ends as Abandoned below (there is no fallback to option 1 yet, V-23)
                return outcome;
            }
            var detector = parts.NewTurnDetector();
            // the opening turn: a first greeting, or (after a keypad step) where things stand
            if (state.Connections > 1) { _history.Add(new LlmMessage("system", state.Summary())); StartTurn(VoiceScripts.Resume); }
            else StartTurn(VoiceScripts.Greeting);
            phoneLoop = PumpPhoneAsync(stt, token);
            sttLoop = ReadSttAsync(stt, detector, token);
            tickLoop = TickAsync(detector, token);
            await Task.WhenAny(phoneLoop, sttLoop);
            if (_dtmfTransfer) outcome = VoiceOutcome.Transferred;
            // a vendor failed DURING the call: a turn could not be spoken or written, or the recogniser went away while the caller was still on the line (not a hang-up, not a keypad step, not an end we asked for)
            else if (_vendorLost || (sttLoop.IsCompleted && !phoneLoop.IsCompleted && !cts.IsCancellationRequested && state.KeypadStep is null && phone.State == WebSocketState.Open))
            {
                outcome = VoiceOutcome.VendorLost;
                logger.LogWarning("Voice call {Call}: ended on a vendor failure ({Where}); the platform apologises.", call.Id, _vendorLost ? "a turn" : "the speech recogniser");
            }
        }
        catch (OperationCanceledException) { logger.LogInformation("Voice call {Call} reached its time limit or was cancelled.", call.Id); }
        catch (WebSocketException) { logger.LogWarning("Voice call {Call}: the phone socket closed abnormally.", call.Id); }
        catch (Exception ex) { logger.LogWarning("Voice call {Call}: the pipeline could not start or stopped ({Type}).", call.Id, ex.GetType().Name); }   // a vendor that refuses: the call ends as Abandoned below; never the message, it could hold what was said
        finally
        {
            KeypadPause = state.KeypadStep is not null;
            cts.Cancel();
            _turnCts?.Cancel();
            try { await Task.WhenAll(phoneLoop, sttLoop, tickLoop, _turn); } catch (Exception) { /* every loop ends by cancellation or a closed socket */ }
            if (stt is not null) { try { await stt.DisposeAsync(); } catch (Exception) { /* already gone */ } }
            await RecordUsageAsync();
            if (!KeypadPause) await store.EndAsync(call.Id, outcome, CancellationToken.None);   // a keypad step pauses the call: the next stream resumes it
            await CloseQuietly(phone);
        }
        return outcome;
    }

    // ---- phone (SignalWire) -> pipeline ----
    private async Task PumpPhoneAsync(ISttSession stt, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && phone.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await phone.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 1_000_000) return;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(ms.ToArray()); } catch (JsonException) { continue; }
            using (doc) { if (doc.RootElement.ValueKind == JsonValueKind.Object) await HandlePhoneAsync(doc.RootElement, stt, ct); }
        }
    }

    private async Task HandlePhoneAsync(JsonElement m, ISttSession stt, CancellationToken ct)
    {
        switch (Str(m, "event"))
        {
            case "start":
                _streamSid = m.TryGetProperty("start", out var s) ? Str(s, "streamSid") : null;
                _streamSid ??= Str(m, "streamSid");
                _started.TrySetResult();
                break;
            case "media":
                if (m.TryGetProperty("media", out var media) && Str(media, "payload") is { Length: > 0 } payload && Str(media, "track") is null or "inbound")
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(payload); } catch (FormatException) { break; }
                    Interlocked.Add(ref _audioBytesIn, bytes.Length);
                    await stt.SendAudioAsync(bytes, ct);
                }
                break;
            case "mark":
                if (m.TryGetProperty("mark", out var mk) && Str(mk, "name") is { } markName)
                {
                    if (markName == "end") _markEnd.TrySetResult();   // the keypad / goodbye hand-off
                    else _playback.Echoed(markName);
                }
                break;
            case "dtmf":
                // Keypad digits never go to any stage and are never logged. Zero asks for a person; every other digit is ignored here (keypad steps run outside the stream).
                if (m.TryGetProperty("dtmf", out var d) && Str(d, "digit") == "0" && await VoiceTransfer.RequestAsync(store, options, time, call.Id, ct)) { _dtmfTransfer = true; throw new OperationCanceledException(); }
                break;
            case "stop":
                throw new OperationCanceledException();
        }
    }

    // ---- speech-to-text -> turns ----
    private async Task ReadSttAsync(ISttSession stt, ITurnDetector detector, CancellationToken ct)
    {
        await foreach (var e in stt.Events(ct))
        {
            _lastUserEvent = time.GetTimestamp();
            Handle(detector.OnEvent(e, time.GetUtcNow()));
        }
    }

    private async Task TickAsync(ITurnDetector detector, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), time);
        while (await timer.WaitForNextTickAsync(ct)) Handle(detector.OnTick(time.GetUtcNow()));
    }

    private void Handle(TurnSignal signal)
    {
        switch (signal.Kind)
        {
            case TurnSignalKind.UserSpeaking:
                _ = BargeInAsync();
                break;
            case TurnSignalKind.TurnEnded when !string.IsNullOrWhiteSpace(signal.Text):
                lock (_history) _history.Add(new LlmMessage("user", signal.Text));
                StartTurn(null);
                break;
        }
    }

    /// <summary>The caller talks while the PHONE still has audio queued: clear it, remember how much was played, and stop the answer (V-12, the same marks).</summary>
    private async Task BargeInAsync()
    {
        if (_streamSid is null) return;
        if (_playback.TryBargeIn() is { } cut)
        {
            _cutPlayedMs = cut.PlayedMs;
            TrimLastAssistant(cut.PlayedMs);
            try { await SendPhoneAsync(new JsonObject { ["event"] = "clear", ["streamSid"] = _streamSid }, _cts.Token); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { return; }
            _turnCts?.Cancel();
            logger.LogInformation("Voice call {Call}: the caller talked over the agent; {Queued} ms of audio were still queued on the phone, {Played} ms of the answer had played.", call.Id, cut.QueuedMs, cut.PlayedMs);
        }
        else if (_speaking) _turnCts?.Cancel();   // an answer still being written or fetched: the caller is talking, drop it (what they say next starts a new one)
    }

    /// <summary>The answer was already in the history when the caller talked over it: cut it to what was heard (or drop it when nothing was).</summary>
    private void TrimLastAssistant(long playedMs)
    {
        lock (_history)
        {
            var at = _lastAssistant is null ? -1 : _history.FindLastIndex(m => ReferenceEquals(m, _lastAssistant));
            if (at < 0) return;
            List<(string Text, long Ms)> spoken;
            lock (_lastSpoken) spoken = _lastSpoken.Select(x => (x.Text, x.Ms)).ToList();
            var heard = HeardPart(spoken, playedMs);
            if (heard.Length == 0) _history.RemoveAt(at); else _history[at] = new LlmMessage("assistant", heard);
            _lastAssistant = null;
        }
    }

    // ---- one assistant turn: LLM -> sentences -> TTS -> phone, with tools between ----
    private void StartTurn(string? instruction)
    {
        var previous = _turn;
        _turnCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _turnCts = cts;
        _turn = Task.Run(async () =>
        {
            try { await previous; } catch (Exception) { /* the earlier turn ended by cancellation */ }
            try { await RunTurnAsync(instruction, cts.Token); }
            catch (OperationCanceledException) { /* barge-in, or the call ended */ }
            catch (VendorFailure vf) { logger.LogWarning("Voice call {Call}: the speech vendor failed ({Type}).", call.Id, vf.Kind); _vendorLost = true; _cts.Cancel(); }
            catch (WebSocketException) { /* the phone closed */ }
            catch (Exception ex) { logger.LogWarning("Voice call {Call}: a turn failed ({Type}).", call.Id, ex.GetType().Name); }   // never the message: it could hold what was said; a failed text-model turn does not end the call (V-19)
        }, CancellationToken.None);
    }

    private async Task RunTurnAsync(string? instruction, CancellationToken tc)
    {
        var id = "t" + Interlocked.Increment(ref _turnSeq);
        _cutPlayedMs = null;
        _speaking = true;
        var spoken = new List<SpokenSentence>();   // sentences handed to the phone, in order, with their audio length: to cut the answer to what was heard
        var firstAudio = true;
        try
        {
            for (var round = 0; round < 6; round++)
            {
                var chunker = new SentenceChunker();
                var assistantText = new StringBuilder();
                var calls = new List<LlmToolCall>();
                var request = BuildRequest(round == 0 ? instruction : null);
                var queue = Channel.CreateUnbounded<string>();
                var speaker = Task.Run(async () =>
                {
                    await foreach (var sentence in queue.Reader.ReadAllAsync(tc))
                        await SpeakAsync(sentence, id, spoken, () => { var f = firstAudio; firstAudio = false; return f; }, tc);
                }, CancellationToken.None);
                try
                {
                    await foreach (var ev in parts.Llm.StreamAsync(request, tc))
                    {
                        switch (ev)
                        {
                            case LlmTextDelta d:
                                assistantText.Append(d.Text);
                                foreach (var sentence in chunker.Add(d.Text)) queue.Writer.TryWrite(sentence);
                                break;
                            case LlmToolCallEvent t: calls.Add(t.Call); break;
                            case LlmCompleted c: _llmIn += c.InputTokens; _llmOut += c.OutputTokens; break;
                        }
                    }
                    if (chunker.Flush() is { } rest) queue.Writer.TryWrite(rest);
                }
                finally { queue.Writer.TryComplete(); }
                await speaker;   // everything handed to the phone, or cancelled (barge-in)

                if (calls.Count == 0)
                {
                    var heard = _cutPlayedMs is { } played ? HeardPart(spoken.Select(x => (x.Text, x.Ms)).ToList(), played) : string.Join(' ', spoken.Select(x => x.Text));
                    if (assistantText.Length > 0 && (_cutPlayedMs is null || heard.Length > 0))
                        lock (_history) { _lastAssistant = new LlmMessage("assistant", heard.Length > 0 ? heard : assistantText.ToString()); _lastSpoken = spoken; _history.Add(_lastAssistant); }
                    if (_endAfterResponse && !tc.IsCancellationRequested) await FinishAfterGoodbyeAsync(_cts.Token);
                    return;
                }
                // tools: the assistant message with the calls, then one tool message per call (every call gets an answer, even when the turn is cut)
                lock (_history) _history.Add(new LlmMessage("assistant", assistantText.Length > 0 ? assistantText.ToString() : null, calls));
                foreach (var call_ in calls)
                {
                    VoiceToolResult result;
                    if (tc.IsCancellationRequested) result = new("""{"error":"cancelled"}""");
                    else result = await RunToolWithFillerAsync(call_, id, spoken, tc);
                    lock (_history) _history.Add(new LlmMessage("tool", result.Output, null, call_.CallId));
                    _endAfterResponse |= result.EndCall;
                    // the server ends the AI part of the call (the off-topic limit): no further answer; the phone plays what is queued, then the stream ends and the platform speaks
                    if (result.EndNow) { await FinishAfterGoodbyeAsync(_cts.Token); return; }
                }
                tc.ThrowIfCancellationRequested();
            }
        }
        finally { _speaking = false; }
    }

    private LlmRequest BuildRequest(string? instruction)
    {
        List<LlmMessage> messages;
        lock (_history) messages = [.. _history];
        if (instruction is not null) messages.Add(new LlmMessage("system", instruction));
        return new LlmRequest(VoiceScripts.Instructions, messages, tools.Definitions());
    }

    /// <summary>Speaks one sentence: synthesises it and sends the audio to the phone, each chunk followed by a mark (the same marks as the Realtime bridge).</summary>
    private async Task SpeakAsync(string sentence, string itemId, List<SpokenSentence> spoken, Func<bool> isFirstAudio, CancellationToken ct)
    {
        if (_streamSid is null) await Task.WhenAny(_started.Task, Task.Delay(TimeSpan.FromSeconds(3), time, ct));
        if (_streamSid is null) return;
        Interlocked.Add(ref _ttsChars, sentence.Length);
        var entry = new SpokenSentence(sentence);
        lock (spoken) spoken.Add(entry);   // from the first chunk on: a barge-in that cuts it half way still finds what was being said
        await using var speech = parts.Tts.SynthesizeAsync(sentence, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await speech.MoveNextAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new VendorFailure(ex.GetType().Name); }   // the vendor, not the phone: told apart so a failing voice is not mistaken for a hang-up
            if (!more) break;
            var chunk = speech.Current;
            ct.ThrowIfCancellationRequested();
            if (chunk.Length == 0) continue;
            if (isFirstAudio() && _lastUserEvent != 0) { await store.AddLatencyAsync(call.Id, (long)time.GetElapsedTime(_lastUserEvent).TotalMilliseconds, ct); _lastUserEvent = 0; }
            var markName = _playback.NoteSent(chunk.Length, itemId);
            await SendPhoneAsync(new JsonObject { ["event"] = "media", ["streamSid"] = _streamSid, ["media"] = new JsonObject { ["payload"] = Convert.ToBase64String(chunk) } }, ct);
            await SendPhoneAsync(new JsonObject { ["event"] = "mark", ["streamSid"] = _streamSid, ["mark"] = new JsonObject { ["name"] = markName } }, ct);
            entry.Ms += chunk.Length / BytesPerMs;
        }
    }

    /// <summary>The part of the answer the caller HEARD when they spoke: whole sentences that had played, and the played share of the sentence that was cut (by words).</summary>
    internal static string HeardPart(IReadOnlyList<(string Text, long Ms)> spoken, long playedMs)
    {
        var sb = new StringBuilder();
        long at = 0;
        foreach (var (text, ms) in spoken)
        {
            if (at + ms <= playedMs) { if (sb.Length > 0) sb.Append(' '); sb.Append(text); at += ms; continue; }
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var share = ms <= 0 ? 0 : Math.Clamp((double)(playedMs - at) / ms, 0, 1);
            var n = (int)Math.Floor(words.Length * share);
            if (n > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(string.Join(' ', words.Take(n))); }
            break;
        }
        return sb.ToString();
    }

    /// <summary>A tool that takes longer than <c>Voice:ToolFillerMs</c> makes the platform say a short filler phrase first, so the caller never hears silence. Tools run on the CALL's token: a barge-in never cuts a tool in the middle.</summary>
    private async Task<VoiceToolResult> RunToolWithFillerAsync(LlmToolCall tool, string itemId, List<SpokenSentence> spoken, CancellationToken tc)
    {
        var run = tools.RunAsync(new VoiceToolContext(call.Id, call.Tier, state), tool.Name, tool.Arguments, _cts.Token);
        var fast = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, _o.ToolFillerMs)), time, tc));
        if (fast != run && !_playback.Playing && !tc.IsCancellationRequested)
        {
            try { await SpeakAsync(PipelineScripts.Filler, itemId, spoken, () => false, tc); } catch (OperationCanceledException) { /* the caller talked: the answer still arrives */ }
        }
        return await run;
    }

    /// <summary>The agent has said goodbye: wait (bounded) until the phone has played it, then end the stream so SignalWire continues with the next instruction.</summary>
    private async Task FinishAfterGoodbyeAsync(CancellationToken ct)
    {
        if (_streamSid is not null) await SendPhoneAsync(new JsonObject { ["event"] = "mark", ["streamSid"] = _streamSid, ["mark"] = new JsonObject { ["name"] = "end" } }, ct);
        await Task.WhenAny(_markEnd.Task, Task.Delay(TimeSpan.FromSeconds(6), time, ct));
        _cts.Cancel();
    }

    /// <summary>Counters only: LLM tokens, and the cost of what is priced (speech-to-text minutes, text-to-speech characters, LLM tokens). Written once when the stream ends.</summary>
    private async Task RecordUsageAsync()
    {
        var p = _o.PipelineMenu.Prices;
        var minutes = (decimal)Interlocked.Read(ref _audioBytesIn) / BytesPerMs / 1000m / 60m;
        var micros = (long)Math.Round((minutes * p.SttPerMinute + _ttsChars / 1_000_000m * p.TtsPerMillionChars + _llmIn / 1_000_000m * p.LlmInPerMillionTokens + _llmOut / 1_000_000m * p.LlmOutPerMillionTokens) * 1_000_000m);
        try { await store.AddPipelineUsageAsync(call.Id, _llmIn, _llmOut, micros, CancellationToken.None); } catch (Exception) { /* the call's row is gone (purged): nothing to add to */ }
        logger.LogInformation("Voice call {Call} on {Option}: {AudioSeconds} s of caller audio, {Chars} characters spoken, {In}/{Out} model tokens.", call.Id, parts.OptionId, (long)(minutes * 60), _ttsChars, _llmIn, _llmOut);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task SendPhoneAsync(JsonObject o, CancellationToken ct)
    {
        await _phoneSend.WaitAsync(ct);
        try { if (phone.State == WebSocketState.Open) await phone.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { _phoneSend.Release(); }
    }

    private static async Task CloseQuietly(WebSocket ws)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", t.Token);
        }
        catch (Exception) { /* already gone */ }
    }
}

/// <summary>A sentence handed to the phone and how much audio of it has been sent so far.</summary>
public sealed class SpokenSentence(string text) { public string Text { get; } = text; public long Ms { get; set; } }

/// <summary>A speech vendor failed while the phone was fine. Only the exception type travels (<see cref="Kind"/>), never a message.</summary>
internal sealed class VendorFailure(string kind) : Exception("A speech vendor failed.") { public string Kind { get; } = kind; }

/// <summary>Option p2 to p5 at run time: the stages of one option behind the pipeline seam.</summary>
public sealed class ChainedPipeline(IChainedPipelineParts parts, RealtimePipeline realtime, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools, TimeProvider time, ILoggerFactory logs) : IVoicePipeline
{
    public string Id => parts.OptionId;

    /// <summary>
    /// Opens the speech recogniser BEFORE the phone socket is accepted. When the vendor refuses, the call is handed to the Realtime line on the same call (decision V-25): the tier and model the call started with
    /// are put back, the option that failed is recorded in <c>FallbackFrom</c>, and the Realtime pipeline opens as for any normal call.
    /// </summary>
    public async Task<IVoicePipelineSession?> OpenAsync(VoiceCall call, CancellationToken ct)
    {
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice");
        ISttSession stt;
        try { stt = await parts.Stt.OpenAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Voice call {Call}: option {Option} could not open its speech recogniser ({Type}); handing the call to the Realtime line.", call.Id, Id, ex.GetType().Name);   // never the message
            var fallback = await store.FallBackToRealtimeAsync(call.Id, CancellationToken.None);
            if (fallback is null) { await store.EndAsync(call.Id, VoiceOutcome.Abandoned, CancellationToken.None); return null; }     // nothing to fall back to: the call ends like a failing Realtime connector (503)
            return await realtime.OpenAsync(fallback, ct);
        }
        return new Session(call, parts, stt, options, store, tools, time, log);
    }

    private sealed class Session(VoiceCall call, IChainedPipelineParts parts, ISttSession stt, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools, TimeProvider time, ILogger log) : IVoicePipelineSession
    {
        private bool _handedOver;

        public async Task<VoicePipelineResult> RunAsync(WebSocket phone, VoiceCallState state, CancellationToken ct)
        {
            _handedOver = true;        // the bridge owns the recogniser from here and disposes it when the stream ends
            var bridge = new ChainedBridge(phone, call, state, parts, options, store, tools, time, log, stt);
            await bridge.RunAsync(ct);
            return new VoicePipelineResult(bridge.KeypadPause);
        }

        public async ValueTask DisposeAsync() { if (!_handedOver) { try { await stt.DisposeAsync(); } catch (Exception) { /* already gone */ } } }
    }
}
