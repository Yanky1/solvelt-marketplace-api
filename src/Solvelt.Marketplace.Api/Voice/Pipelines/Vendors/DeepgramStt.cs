using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Deepgram streaming speech to text (option p2). One WebSocket per call to <c>wss://api.deepgram.com/v1/listen</c>; the telephone's G.711 mu-law 8 kHz goes in as it comes (<c>encoding=mulaw</c>, <c>sample_rate=8000</c>),
/// so nothing is converted. The key travels in the <c>Authorization</c> header only, never in the address. What the caller says is held in memory for the turn: this class logs counts and exception types, never text.
/// </summary>
public sealed class DeepgramStt(DeepgramOptions options, IVendorSocketConnector connector, ILogger log, IVoiceVocabulary? vocabulary = null) : IStreamingStt
{
    /// <summary>Total characters of keyterms we send. Deepgram limits keyterms to 500 TOKENS and does not say how it counts them; at roughly four characters a token this stays far under it.</summary>
    internal const int KeytermCharBudget = 1200;

    /// <summary>Keyterms exist only for Nova-3 and Flux (Deepgram's keyterm page); other models would need the legacy <c>keywords</c>, which we do not send.</summary>
    internal static bool SupportsKeyterms(string model) => model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase) || model.StartsWith("flux", StringComparison.OrdinalIgnoreCase);

    internal static Uri Address(DeepgramOptions o, IReadOnlyList<string>? terms = null)
    {
        var url = $"wss://{VendorHosts.Deepgram}/v1/listen?model={Uri.EscapeDataString(o.Model)}&language={Uri.EscapeDataString(o.Language)}"
            + "&encoding=mulaw&sample_rate=8000&channels=1&interim_results=true&vad_events=true&utterance_end_ms=1000";
        if (o.EndpointingMs > 0) url += "&endpointing=" + Math.Clamp(o.EndpointingMs, 10, 2000);
        if (terms is { Count: > 0 } && SupportsKeyterms(o.Model))
        {
            // one parameter per term, never joined by commas or semicolons (those silently boost nothing); a phrase is one term, escaped with %20
            var used = 0; var n = 0;
            foreach (var t in terms)
            {
                if (n >= Math.Max(0, o.KeytermMax) || used + t.Length > KeytermCharBudget) break;
                url += "&keyterm=" + Uri.EscapeDataString(t); used += t.Length; n++;
            }
            if (n > 0 && o.RedactUsage) url += "&redact_usage=true";   // otherwise the keyterms land in Deepgram's usage records
        }
        return new Uri(url);
    }

    public async Task<ISttSession> OpenAsync(CancellationToken ct)
    {
        var ws = await connector.ConnectAsync(Address(options, vocabulary?.Terms), new Dictionary<string, string> { ["Authorization"] = "Token " + options.ApiKey }, ct);
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
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { _events.Writer.TryComplete(); }   // the vendor went away: the events end and the pipeline falls back
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
            catch (Exception ex) { _log.LogWarning("Deepgram stream ended ({Type}) after {Count} messages.", ex.GetType().Name, _messages); }
            finally { _events.Writer.TryComplete(); }
        }

        /// <summary>Maps one Deepgram message: <c>Results</c> (interim or final), <c>SpeechStarted</c>, <c>UtteranceEnd</c>; everything else (metadata, errors) is ignored here.</summary>
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
                    case "SpeechStarted": yield return new SttEvent(SttEventKind.SpeechStarted); break;
                    case "UtteranceEnd": yield return new SttEvent(SttEventKind.SpeechEnded); break;
                    case "Results":
                        if (!root.TryGetProperty("channel", out var ch) || !ch.TryGetProperty("alternatives", out var alts) || alts.ValueKind != JsonValueKind.Array || alts.GetArrayLength() == 0) yield break;
                        var alt = alts[0];
                        var text = alt.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(text)) yield break;
                        double? conf = alt.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                        var final = root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
                        yield return new SttEvent(final ? SttEventKind.Final : SttEventKind.Partial, text.Trim(), null, conf);
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_ws.State == WebSocketState.Open) { using var c = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _ws.SendAsync("{\"type\":\"CloseStream\"}"u8.ToArray(), WebSocketMessageType.Text, true, c.Token); } } catch { /* already gone */ }
            _cts.Cancel();
            await VendorSocketIo.CloseQuietlyAsync(_ws);
            try { await _reader; } catch { /* ended by cancellation */ }
            _cts.Dispose(); _send.Dispose();
        }
    }
}
