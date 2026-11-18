using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Opens the upstream real-time socket for one call. A seam: tests return a socket to a scripted fake (no OpenAI calls in CI).</summary>
public interface IRealtimeConnector
{
    Task<WebSocket> ConnectAsync(string model, CancellationToken ct);
}

public sealed class OpenAiRealtimeConnector(IOptions<VoiceOptions> options) : IRealtimeConnector
{
    public async Task<WebSocket> ConnectAsync(string model, CancellationToken ct)
    {
        var o = options.Value;
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + o.OpenAiApiKey);
        await ws.ConnectAsync(new Uri(o.OpenAiRealtimeUrl + "?model=" + Uri.EscapeDataString(model)), ct);
        return ws;
    }
}

/// <summary>
/// Bridges one phone call (SignalWire bidirectional stream, G.711 mu-law 8 kHz, passed through without transcoding) to one OpenAI Realtime session (decision V-01).
/// What crosses: caller audio up, agent audio down, the model's tool calls and our tool results. What never crosses: DTMF digits (dropped here, not even logged),
/// anything a keypad step produces, payload text in logs. Nothing is stored but counters.
/// </summary>
public sealed class RealtimeBridge(WebSocket phone, WebSocket upstream, VoiceCall call, VoiceCallState state, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools,
    TimeProvider time, ILogger logger)
{
    private const int BytesPerMs = 8;   // mu-law, 8000 samples per second, one byte each
    private readonly SemaphoreSlim _phoneSend = new(1, 1), _upSend = new(1, 1);
    private string? _streamSid, _itemId;
    private long _speechStopped;
    private bool _endAfterResponse, _dtmfTransfer, _responseActive;
    /// <summary>What the PHONE is playing (marks SignalWire echoes), not what the model has generated: the model is "done" seconds before the phone is (V-12).</summary>
    private readonly PhonePlayback _playback = new(time);
    /// <summary>True when the stream ended for a keypad step: the call goes on and the next stream resumes it. Decided BEFORE either socket is closed, because SignalWire's next instruction can arrive the moment the phone socket closes and clear the step.</summary>
    public bool KeypadPause { get; private set; }
    private TaskCompletionSource? _fillerDone;
    private CancellationTokenSource _cts = new();
    private Task _calls = Task.CompletedTask;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);   // the phone's start event has been handled
    private TaskCompletionSource _markEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VoiceOptions _o = options.Value;

    /// <summary>Runs until the caller hangs up, the model's session ends, a transfer is done or the call is too long. Returns the fallback outcome.</summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _o.MaxCallSeconds)));   // per-call limit: call length
        var token = cts.Token;
        string outcome = VoiceOutcome.Abandoned;
        Task phoneLoop = Task.CompletedTask, upLoop = Task.CompletedTask;
        try
        {
            await SendUpAsync(SessionUpdate(), token);
            if (state.Connections > 1)
            {
                // a keypad step ended the earlier stream: same voice, same prompt, plus the server's summary of where things stand
                await SendUpAsync(new JsonObject { ["type"] = "conversation.item.create", ["item"] = new JsonObject { ["type"] = "message", ["role"] = "system", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = state.Summary() }) } }, token);
                await SendUpAsync(new JsonObject { ["type"] = "response.create", ["response"] = new JsonObject { ["instructions"] = VoiceScripts.Resume } }, token);
            }
            else await SendUpAsync(new JsonObject { ["type"] = "response.create", ["response"] = new JsonObject { ["instructions"] = VoiceScripts.Greeting } }, token);
            _responseActive = true;
            phoneLoop = PumpAsync(phone, HandlePhoneAsync, token);
            upLoop = PumpAsync(upstream, HandleUpAsync, token);
            await Task.WhenAny(phoneLoop, upLoop);
            if (_dtmfTransfer) outcome = VoiceOutcome.Transferred;
        }
        catch (OperationCanceledException) { logger.LogInformation("Voice call {Call} reached its time limit or was cancelled.", call.Id); }
        catch (WebSocketException) { logger.LogWarning("Voice call {Call}: a socket closed abnormally.", call.Id); }
        finally
        {
            KeypadPause = state.KeypadStep is not null;
            cts.Cancel();
            try { await Task.WhenAll(phoneLoop, upLoop, _calls); } catch (Exception) { /* both loops end by cancellation or a closed socket */ }
            // The call's record is closed FIRST: it must not wait for a close handshake with a peer that may answer slowly or never (that wait once made the end of a call
            // visible seconds late, and a test of it flaky). Then both sockets are closed without waiting for the peer's acknowledgement.
            if (!KeypadPause) await store.EndAsync(call.Id, outcome, CancellationToken.None);   // a keypad step pauses the call: the next stream resumes it
            await CloseQuietly(upstream);
            await CloseQuietly(phone);
        }
        return outcome;
    }

    private JsonObject SessionUpdate()
    {
        var turn = new JsonObject { ["type"] = _o.TurnDetection, ["create_response"] = true, ["interrupt_response"] = true };
        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["output_modalities"] = new JsonArray("audio"),
                ["instructions"] = VoiceScripts.Instructions,
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcmu" }, ["turn_detection"] = turn },
                    ["output"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcmu" }, ["voice"] = _o.OpenAiVoice },
                },
                ["tools"] = tools.Definitions(),
                ["tool_choice"] = "auto",
            },
        };
    }

    // ---- phone (SignalWire) -> bridge ----
    private async Task HandlePhoneAsync(JsonElement m, CancellationToken ct)
    {
        switch (Str(m, "event"))
        {
            case "start":
                _streamSid = m.TryGetProperty("start", out var s) ? Str(s, "streamSid") : null;
                _streamSid ??= Str(m, "streamSid");
                _started.TrySetResult();
                break;
            case "media":
                if (m.TryGetProperty("media", out var media) && Str(media, "payload") is { Length: > 0 } audio && Str(media, "track") is null or "inbound")
                    await SendUpAsync(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = audio }, ct);
                break;
            case "mark":
                if (m.TryGetProperty("mark", out var mk) && Str(mk, "name") is { } markName)
                {
                    if (markName == "end") _markEnd.TrySetResult();   // the keypad / goodbye hand-off
                    else _playback.Echoed(markName);                  // an audio mark: that part of the audio has played (or was cleared)
                }
                break;
            case "dtmf":
                // Keypad digits never go upstream and are never logged. Zero asks for a person; every other digit is ignored here (keypad steps run outside the stream).
                if (m.TryGetProperty("dtmf", out var d) && Str(d, "digit") == "0") await TransferNowAsync(ct);
                break;
            case "stop":
                throw new OperationCanceledException();
        }
    }

    private async Task TransferNowAsync(CancellationToken ct)
    {
        if (await VoiceTransfer.RequestAsync(store, options, time, call.Id, ct)) { _dtmfTransfer = true; throw new OperationCanceledException(); }
        // no person available: carry on; the model is told by the tool the next time it is asked
    }

    // ---- OpenAI -> bridge ----
    private async Task HandleUpAsync(JsonElement m, CancellationToken ct)
    {
        switch (Str(m, "type"))
        {
            case "response.created":
                _responseActive = true;
                break;
            case "response.output_audio.delta" or "response.audio.delta":
                // The two sockets are read concurrently: model audio can win the race with the phone's start event. Wait for it (briefly) instead of dropping the audio.
                if (_streamSid is null) await Task.WhenAny(_started.Task, Task.Delay(TimeSpan.FromSeconds(3), time, ct));
                if (Str(m, "delta") is { Length: > 0 } delta && _streamSid is not null)
                {
                    if (_speechStopped != 0) { await store.AddLatencyAsync(call.Id, (long)time.GetElapsedTime(_speechStopped).TotalMilliseconds, ct); _speechStopped = 0; }
                    _itemId = Str(m, "item_id") ?? _itemId;
                    var bytes = delta.Length * 3L / 4 - (delta.EndsWith("==") ? 2 : delta.EndsWith('=') ? 1 : 0);
                    var markName = _playback.NoteSent(bytes, _itemId);
                    await SendPhoneAsync(new JsonObject { ["event"] = "media", ["streamSid"] = _streamSid, ["media"] = new JsonObject { ["payload"] = delta } }, ct);
                    // SignalWire echoes this mark when the audio before it has played, or when it was cleared: that is how we know the phone is still playing after the model is done
                    await SendPhoneAsync(new JsonObject { ["event"] = "mark", ["streamSid"] = _streamSid, ["mark"] = new JsonObject { ["name"] = markName } }, ct);
                }
                break;
            case "input_audio_buffer.speech_started":
                // Barge-in: the caller talks while the PHONE still has audio queued, whether or not the model is done generating it (it usually is: it generates much faster than real time).
                // Stop what the phone has queued, tell the model how much of the item was actually heard, and stop the response if it is still going.
                if (_streamSid is not null && _playback.TryBargeIn() is { } cut)
                {
                    await SendPhoneAsync(new JsonObject { ["event"] = "clear", ["streamSid"] = _streamSid }, ct);
                    if (cut.ItemId is not null)
                        await SendUpAsync(new JsonObject { ["type"] = "conversation.item.truncate", ["item_id"] = cut.ItemId, ["content_index"] = 0, ["audio_end_ms"] = (int)cut.PlayedMs }, ct);
                    if (_responseActive) await SendUpAsync(new JsonObject { ["type"] = "response.cancel" }, ct);
                    logger.LogInformation("Voice call {Call}: the caller talked over the agent; {Queued} ms of audio were still queued on the phone, {Played} ms of the item had played.", call.Id, cut.QueuedMs, cut.PlayedMs);
                    _itemId = null;
                }
                break;
            case "input_audio_buffer.speech_stopped":
                _speechStopped = time.GetTimestamp();
                break;
            case "response.done":
                await HandleResponseDoneAsync(m, ct);
                break;
            case "error":
                logger.LogWarning("Voice call {Call}: the model session reported an error ({Code}).", call.Id, m.TryGetProperty("error", out var e) ? Str(e, "code") : null);
                break;
        }
    }

    private async Task HandleResponseDoneAsync(JsonElement m, CancellationToken ct)
    {
        _responseActive = false;   // the model is done; the PHONE may still be playing (see _playback)
        _fillerDone?.TrySetResult();
        if (!m.TryGetProperty("response", out var r)) return;
        if (r.TryGetProperty("usage", out var u)) await store.AddUsageAsync(call.Id, call.Tier, ReadUsage(u), ct);
        var pending = new List<(string Name, string? Args, string? CallId)>();
        if (r.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray())
                if (Str(item, "type") == "function_call") pending.Add((Str(item, "name") ?? "", Str(item, "arguments"), Str(item, "call_id")));
        if (pending.Count > 0)
        {
            // off the read loop: a slow tool must not stop audio, barge-in or the filler's own events from being handled
            var previous = _calls;
            _calls = Task.Run(async () =>
            {
                try { await previous; await RunCallsAsync(pending, ct); }
                catch (OperationCanceledException) { /* the call ended */ }
                catch (WebSocketException) { /* a socket closed */ }
            }, CancellationToken.None);
            return;
        }
        if (_endAfterResponse) await FinishAfterGoodbyeAsync(ct);
    }

    private async Task RunCallsAsync(List<(string Name, string? Args, string? CallId)> pending, CancellationToken ct)
    {
        foreach (var (name, args, callId) in pending)
        {
            var result = await RunToolWithFillerAsync(name, args, ct);
            _endAfterResponse |= result.EndCall;
            // the server ends the AI part of the call (the off-topic limit): the model is not asked for another answer; the phone plays what is already queued, then the stream ends and the platform speaks
            if (result.EndNow) { await FinishAfterGoodbyeAsync(ct); return; }
            await SendUpAsync(new JsonObject { ["type"] = "conversation.item.create", ["item"] = new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = result.Output } }, ct);
        }
        _responseActive = true;
        await SendUpAsync(new JsonObject { ["type"] = "response.create" }, ct);
    }

    /// <summary>A tool that takes longer than <c>Voice:ToolFillerMs</c> makes the agent say a short natural filler first, so the caller never hears silence; the answer waits for the filler to finish.</summary>
    private async Task<VoiceToolResult> RunToolWithFillerAsync(string name, string? args, CancellationToken ct)
    {
        var run = tools.RunAsync(new VoiceToolContext(call.Id, call.Tier, state), name, args, ct);
        var fast = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, _o.ToolFillerMs)), time, ct));
        if (fast != run && !_responseActive)
        {
            _fillerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _responseActive = true;
            await SendUpAsync(new JsonObject { ["type"] = "response.create", ["response"] = new JsonObject { ["instructions"] = VoiceScripts.Filler, ["tool_choice"] = "none" } }, ct);
            var result = await run;
            await Task.WhenAny(_fillerDone.Task, Task.Delay(TimeSpan.FromSeconds(4), time, ct));   // its response.done is handled by the read loop, which keeps running
            return result;
        }
        return await run;
    }

    /// <summary>The agent has said goodbye: wait (bounded) until the phone has played it, then end the stream so SignalWire continues with the next instruction (the transfer).</summary>
    private async Task FinishAfterGoodbyeAsync(CancellationToken ct)
    {
        if (_streamSid is not null) await SendPhoneAsync(new JsonObject { ["event"] = "mark", ["streamSid"] = _streamSid, ["mark"] = new JsonObject { ["name"] = "end" } }, ct);
        await Task.WhenAny(_markEnd.Task, Task.Delay(TimeSpan.FromSeconds(6), time, ct));
        _cts.Cancel();
    }

    internal static VoiceUsage ReadUsage(JsonElement u)
    {
        long N(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var i = u.TryGetProperty("input_token_details", out var id) ? id : default;
        var o = u.TryGetProperty("output_token_details", out var od) ? od : default;
        return new VoiceUsage(N(i, "audio_tokens"), N(o, "audio_tokens"), N(i, "text_tokens"), N(o, "text_tokens"));
    }

    // ---- plumbing ----
    private async Task PumpAsync(WebSocket ws, Func<JsonElement, CancellationToken, Task> handle, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 1_000_000) return;   // no message of ours is that large: drop the connection
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(ms.ToArray()); } catch (JsonException) { continue; }
            using (doc) { if (doc.RootElement.ValueKind == JsonValueKind.Object) await handle(doc.RootElement, ct); }
        }
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private Task SendUpAsync(JsonObject o, CancellationToken ct) => SendAsync(upstream, _upSend, o, ct);
    private Task SendPhoneAsync(JsonObject o, CancellationToken ct) => SendAsync(phone, _phoneSend, o, ct);

    private static async Task SendAsync(WebSocket ws, SemaphoreSlim gate, JsonObject o, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { gate.Release(); }
    }

    private static async Task CloseQuietly(WebSocket ws)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", t.Token);   // our close frame; the peer's reply is not awaited
        }
        catch (Exception) { /* already gone */ }
    }
}
