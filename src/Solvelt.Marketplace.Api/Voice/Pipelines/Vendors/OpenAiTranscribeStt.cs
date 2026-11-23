using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// OpenAI Realtime transcription as a streaming recogniser (decision V-47), built against docs/agent-briefs/asr-vendor-facts.md (OpenAI's docs, read 2026-11-22). One WebSocket per call to
/// <c>{Voice:OpenAiRealtimeUrl}?intent=transcription</c> (no model in the address), then a <c>session.update</c> with <c>session.type "transcription"</c>, the G.711 mu-law input format the telephone already carries
/// (<c>audio/pcmu</c>, as the Realtime line uses), the model (default <c>gpt-live-transcribe</c>), <c>languages</c> (the plural field that model takes; never the singular), <c>keywords</c> from the catalog
/// vocabulary, and <c>turn_detection</c> null. THAT last point is why this class has its own end-of-speech detection: "<c>gpt-live-transcribe</c> ... require[s] turn detection to be omitted or set to null.
/// Send <c>input_audio_buffer.commit</c> to finish each audio turn", so a simple energy detector decides when the caller has stopped and sends the commit. Events are matched by <c>item_id</c> ("Ordering between
/// completion events from different speech turns isn't guaranteed").
/// <para>
/// WHAT THIS RECOGNISER DOES NOT GIVE, said plainly: one hypothesis per turn, no confidence ("gpt-live-transcribe doesn't return word-level timestamps, speaker labels, or transcription confidence scores"), no
/// alternatives, so the n-best and doubt notes of V-42 and V-43 never have anything from it. Its speech start and end are OUR detector's, not the model's.
/// </para>
/// The key travels in the Authorization header only. What the caller says is held in memory for the turn; logs carry counts, error codes and exception types, never words.
/// </summary>
public sealed class OpenAiTranscribeStt(OpenAiTranscribeOptions options, string apiKey, string realtimeUrl, IVendorSocketConnector connector, ILogger log, IVoiceVocabulary? vocabulary = null) : IStreamingStt
{
    private const int FrameBytes = 160, FrameMs = 20, PreRollFrames = 15, StartFrames = 3;
    /// <summary>Characters a keyword may hold: the API refuses some characters and its list was not in the facts given to this build, so only plain letters, digits, spaces, hyphens and apostrophes are sent (and if the API still refuses the update, the keywords are dropped and the session carries on).</summary>
    internal static bool PlainKeyword(string t) => t.Length is > 0 and <= 60 && t.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'');

    internal static Uri Address(string realtimeUrl) => new(realtimeUrl.TrimEnd('/', '?') + "?intent=transcription");

    internal static IReadOnlyList<string> Languages(OpenAiTranscribeOptions o) => (o.Languages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The first message: the transcription session, with or without keywords.</summary>
    internal static string SessionUpdate(OpenAiTranscribeOptions o, IReadOnlyList<string>? keywords)
    {
        var transcription = new JsonObject { ["model"] = o.Model };
        var langs = Languages(o);
        if (langs.Count > 0) transcription["languages"] = new JsonArray(langs.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
        if (keywords is { Count: > 0 }) transcription["keywords"] = new JsonArray(keywords.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject { ["input"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcmu" }, ["transcription"] = transcription, ["turn_detection"] = null } },
            },
        }.ToJsonString();
    }

    internal IReadOnlyList<string> KeywordsToSend()
        => (vocabulary?.Terms ?? []).Where(PlainKeyword).Take(Math.Max(0, options.KeywordMax)).ToList();

    public async Task<ISttSession> OpenAsync(CancellationToken ct)
    {
        var ws = await connector.ConnectAsync(Address(realtimeUrl), new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey }, ct);
        var keywords = KeywordsToSend();
        var session = new Session(ws, options, keywords, log);
        await session.StartAsync(ct);
        return session;
    }

    private sealed class Session : ISttSession
    {
        private readonly WebSocket _ws;
        private readonly OpenAiTranscribeOptions _o;
        private readonly ILogger _log;
        private readonly Channel<SttEvent> _events = Channel.CreateUnbounded<SttEvent>();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly Task _reader;
        private readonly Dictionary<string, string> _partials = [];
        private IReadOnlyList<string> _keywords;
        private volatile bool _ready;
        private int _failures, _messages;
        // our end-of-speech detector (the model's own is off)
        private readonly Queue<byte[]> _ring = new();
        private readonly List<byte> _pending = [];
        private double _floor = 100;
        private int _voicedRun, _silenceMs, _turnMs;
        private bool _inSpeech;

        public Session(WebSocket ws, OpenAiTranscribeOptions o, IReadOnlyList<string> keywords, ILogger log)
        { _ws = ws; _o = o; _keywords = keywords; _log = log; _reader = Task.Run(ReadAsync); }

        public Task StartAsync(CancellationToken ct) => SendTextAsync(SessionUpdate(_o, _keywords), ct);

        private async Task SendTextAsync(string json, CancellationToken ct)
        {
            if (_ws.State != WebSocketState.Open) return;
            await _send.WaitAsync(ct);
            try { await _ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { _events.Writer.TryComplete(); }   // the vendor went away: the events end and the pipeline falls back
            finally { _send.Release(); }
        }

        private Task AppendAsync(byte[] frame, CancellationToken ct)
            => SendTextAsync("{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(frame) + "\"}", ct);

        public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
        {
            if (_ws.State != WebSocketState.Open || mulaw8k.IsEmpty) return;
            _pending.AddRange(mulaw8k.ToArray());
            while (_pending.Count >= FrameBytes)
            {
                var frame = _pending.GetRange(0, FrameBytes).ToArray();
                _pending.RemoveRange(0, FrameBytes);
                await OnFrameAsync(frame, ct);
            }
        }

        private double Rms(byte[] frame)
        {
            double sum = 0;
            foreach (var b in frame) { var s = PhoneAudio.MulawToPcm(b); sum += (double)s * s; }
            return Math.Sqrt(sum / frame.Length);
        }

        private async Task OnFrameAsync(byte[] frame, CancellationToken ct)
        {
            var rms = Rms(frame);
            var threshold = Math.Max(_o.MinEnergy, _floor * 2.5);
            var voiced = rms > threshold;
            if (!_inSpeech)
            {
                if (!voiced) _floor = _floor * 0.95 + rms * 0.05;   // the line's own noise, followed slowly
                _ring.Enqueue(frame); while (_ring.Count > PreRollFrames) _ring.Dequeue();
                _voicedRun = voiced ? _voicedRun + 1 : 0;
                if (_voicedRun < StartFrames) return;
                _inSpeech = true; _silenceMs = 0; _turnMs = _ring.Count * FrameMs;
                _events.Writer.TryWrite(new SttEvent(SttEventKind.SpeechStarted));
                while (_ring.Count > 0) await AppendAsync(_ring.Dequeue(), ct);   // the half second before the caller was heard goes with it
                return;
            }
            await AppendAsync(frame, ct);
            _turnMs += FrameMs;
            _silenceMs = voiced ? 0 : _silenceMs + FrameMs;
            if (_silenceMs < _o.EndpointSilenceMs && _turnMs < _o.MaxTurnSeconds * 1000) return;
            _inSpeech = false; _voicedRun = 0; _silenceMs = 0; _turnMs = 0;
            await SendTextAsync("{\"type\":\"input_audio_buffer.commit\"}", ct);
            _events.Writer.TryWrite(new SttEvent(SttEventKind.SpeechEnded));
        }

        public async IAsyncEnumerable<SttEvent> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct)) yield return e;
        }

        private async Task ReadAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var m = await VendorSocketIo.ReceiveAsync(_ws, _cts.Token);
                    if (m is null) break;
                    if (m.Value.Type != WebSocketMessageType.Text) continue;
                    _messages++;
                    await HandleAsync(m.Value.Data);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning("OpenAI transcription stream ended ({Type}) after {Count} messages.", ex.GetType().Name, _messages); }
            finally { _events.Writer.TryComplete(); }
        }

        private async Task HandleAsync(byte[] json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); } catch (JsonException) { return; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
                string? Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                switch (t.GetString())
                {
                    case "session.updated": _ready = true; break;
                    case "conversation.item.input_audio_transcription.delta":
                        if (Str("item_id") is { } di && Str("delta") is { } delta)
                        {
                            var text = (_partials.GetValueOrDefault(di) ?? "") + delta;
                            _partials[di] = text;
                            if (!string.IsNullOrWhiteSpace(text)) _events.Writer.TryWrite(new SttEvent(SttEventKind.Partial, text.Trim()));
                        }
                        break;
                    case "conversation.item.input_audio_transcription.completed":
                        if (Str("item_id") is { } ci) _partials.Remove(ci);
                        if (Str("transcript") is { } transcript && !string.IsNullOrWhiteSpace(transcript)) _events.Writer.TryWrite(new SttEvent(SttEventKind.Final, transcript.Trim()));   // one hypothesis: no confidence, no alternatives
                        break;
                    case "conversation.item.input_audio_transcription.failed":
                        if (Str("item_id") is { } fi) _partials.Remove(fi);
                        if (++_failures <= 3) _log.LogWarning("OpenAI could not transcribe a turn (failure {Count}).", _failures);
                        break;
                    case "error":
                        var code = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        if (!_ready && _keywords.Count > 0)
                        {
                            // the API refused the session update: the keyword list is the one thing in it that can be refused for its characters, so the update is sent again without it, once
                            _keywords = [];
                            _log.LogWarning("OpenAI refused the transcription session settings ({Code}); trying again without the catalog keywords.", code ?? "no code");
                            await SendTextAsync(SessionUpdate(_o, null), _cts.Token);
                        }
                        else _log.LogWarning("OpenAI transcription reported an error ({Code}).", code ?? "no code");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await VendorSocketIo.CloseQuietlyAsync(_ws);
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ended or timed out */ }
            _events.Writer.TryComplete();
        }
    }
}
