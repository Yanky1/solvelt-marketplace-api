using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Gladia live speech to text (options p3 and, later, p5): the only documented live Yiddish recognizer (Solaria-1). A session is opened with <c>POST https://api.gladia.io/v2/live</c> (the key in <c>x-gladia-key</c>) and
/// answers a one-time WebSocket address, which is only followed when it is <c>wss</c> on <c>api.gladia.io</c>. The telephone's mu-law 8 kHz goes in as it comes (<c>wav/ulaw</c>, 8000 Hz, 8 bit): no conversion.
/// What the caller says is held in memory for the turn; this class logs counts and exception types, never text.
/// </summary>
public sealed class GladiaStt(GladiaOptions options, HttpClient http, IVendorSocketConnector connector, ILogger log) : IStreamingStt
{
    internal static readonly Uri InitAddress = new("https://api.gladia.io/v2/live");

    internal static JsonObject InitBody(GladiaOptions o) => new()
    {
        ["model"] = o.Model, ["encoding"] = "wav/ulaw", ["bit_depth"] = 8, ["sample_rate"] = 8000, ["channels"] = 1,
        ["language_config"] = new JsonObject { ["languages"] = new JsonArray(o.Language), ["code_switching"] = false },
        ["messages_config"] = new JsonObject
        {
            ["receive_partial_transcripts"] = true, ["receive_final_transcripts"] = true, ["receive_speech_events"] = true, ["receive_pre_processing_events"] = false,
            ["receive_realtime_processing_events"] = false, ["receive_post_processing_events"] = false, ["receive_acknowledgments"] = false, ["receive_errors"] = true, ["receive_lifecycle_events"] = false,
        },
    };

    /// <summary>The address the vendor answered, accepted only when it is a <c>wss</c> address on the vendor's own host.</summary>
    internal static Uri Checked(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "wss" || !string.Equals(u.Host, "api.gladia.io", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recogniser answered an address that is not its own.");
        return u;
    }

    public async Task<ISttSession> OpenAsync(CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, InitAddress) { Content = new StringContent(InitBody(options).ToJsonString(), Encoding.UTF8, "application/json") };
        msg.Headers.Add("x-gladia-key", options.ApiKey);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await http.SendAsync(msg, wait.Token);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"The recogniser answered {(int)resp.StatusCode}.", null, resp.StatusCode);   // the body is not read or logged
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(wait.Token));
        var address = Checked(doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
        var ws = await connector.ConnectAsync(address, new Dictionary<string, string>(), ct);
        return new Session(ws, log);
    }

    private sealed class Session : ISttSession
    {
        private readonly WebSocket _ws;
        private readonly ILogger _log;
        private readonly Channel<SttEvent> _events = Channel.CreateUnbounded<SttEvent>();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly Task _reader;
        private int _messages;

        public Session(WebSocket ws, ILogger log) { _ws = ws; _log = log; _reader = Task.Run(ReadAsync); }

        public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
        {
            if (_ws.State != WebSocketState.Open || mulaw8k.IsEmpty) return;
            await _send.WaitAsync(ct);
            try { await _ws.SendAsync(mulaw8k, WebSocketMessageType.Binary, true, ct); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { _events.Writer.TryComplete(); }
            finally { _send.Release(); }
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
                    foreach (var e in Parse(m.Value.Data)) _events.Writer.TryWrite(e);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning("Gladia stream ended ({Type}) after {Count} messages.", ex.GetType().Name, _messages); }
            finally { _events.Writer.TryComplete(); }
        }

        /// <summary>Maps one Gladia message: <c>transcript</c> (partial or final), <c>speech_start</c>, <c>speech_end</c>; the rest is ignored.</summary>
        internal static IEnumerable<SttEvent> Parse(byte[] json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); } catch (JsonException) { yield break; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) yield break;
                switch (t.GetString())
                {
                    case "speech_start": yield return new SttEvent(SttEventKind.SpeechStarted); break;
                    case "speech_end": yield return new SttEvent(SttEventKind.SpeechEnded); break;
                    case "transcript":
                        if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("utterance", out var u) || u.ValueKind != JsonValueKind.Object) yield break;
                        var text = u.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(text)) yield break;
                        var lang = u.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                        double? conf = u.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                        var final = d.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
                        yield return new SttEvent(final ? SttEventKind.Final : SttEventKind.Partial, text.Trim(), lang, conf);
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_ws.State == WebSocketState.Open) { using var c = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _ws.SendAsync("{\"type\":\"stop_recording\"}"u8.ToArray(), WebSocketMessageType.Text, true, c.Token); } } catch { /* already gone */ }
            _cts.Cancel();
            await VendorSocketIo.CloseQuietlyAsync(_ws);
            try { await _reader; } catch { /* ended by cancellation */ }
            _cts.Dispose(); _send.Dispose();
        }
    }
}
