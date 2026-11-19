using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Yiddish Labs realtime text to speech (options p4 and p5; a PREVIEW service). One WebSocket per spoken sentence to <c>wss://rt.yiddishlabs.com/v1/tts/realtime</c>: the text, <c>input.done</c>, then raw PCM s16le at the rate
/// the session announces (16 kHz, 48 kHz in HD), converted to the telephone's mu-law 8 kHz as it arrives. A barge-in cancels the token: the adapter sends the vendor's <c>cancel</c> (nothing more is billed) and closes. The vendor
/// allows five concurrent sessions per account; a gate keeps this process to <see cref="YiddishLabsOptions.MaxConcurrentTtsSessions"/> and refuses (rather than queues) when none is free. The key travels in the
/// <c>Authorization</c> header only; the text is never logged.
/// </summary>
public sealed class YiddishLabsTts(YiddishLabsOptions options, IVendorSocketConnector connector, ILogger log) : ITts
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.MaxConcurrentTtsSessions));

    internal static Uri AddressFor(YiddishLabsOptions o)
        => new($"wss://{VendorHosts.YiddishLabsRealtime}/v1/tts/realtime?voice={Uri.EscapeDataString(o.TtsVoiceId)}{(o.TtsHd ? "&hd=true" : "")}");

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(1), ct)) throw new InvalidOperationException("No free speech session.");
        WebSocket? ws = null; var chunks = 0;
        using var recv = new CancellationTokenSource();
        CancellationTokenRegistration reg = default;
        try
        {
            ws = await connector.ConnectAsync(AddressFor(options), new Dictionary<string, string> { ["Authorization"] = "Bearer " + options.FlashApiKey }, ct);
            // a barge-in: the vendor is told to stop (and not to bill the rest) FIRST, and only then is the pending receive interrupted (an interrupted receive aborts the socket, and nothing could be sent after it)
            var socket = ws;
            reg = ct.Register(() => _ = Task.Run(async () =>
            {
                try { using var c = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await SendAsync(socket, new { type = "cancel" }, c.Token); } catch { /* already gone */ }
                finally { try { recv.Cancel(); } catch (ObjectDisposedException) { } }
            }));
            await SendAsync(ws, new { type = "input.text", text = text.Trim() }, ct);
            await SendAsync(ws, new { type = "input.done" }, ct);
            PhoneAudio.PcmToMulaw8k? convert = null;
            while (true)
            {
                (WebSocketMessageType Type, byte[] Data)? m;
                try { m = await VendorSocketIo.ReceiveAsync(ws, recv.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                if (m is null) yield break;
                if (m.Value.Type == WebSocketMessageType.Binary)
                {
                    if (convert is null) throw new InvalidOperationException("Audio before the session was ready.");
                    var mulaw = convert.Add(m.Value.Data);
                    if (mulaw.Length > 0) { chunks++; yield return mulaw; }
                    continue;
                }
                string? type = null; int rate = 0;
                try
                {
                    using var doc = JsonDocument.Parse(m.Value.Data);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (type == "session.ready" && root.TryGetProperty("sample_rate", out var sr) && sr.TryGetInt32(out var r)) rate = r;
                }
                catch (JsonException) { continue; }
                switch (type)
                {
                    case "session.ready": convert = new PhoneAudio.PcmToMulaw8k(rate); break;     // a rate that is not a whole multiple of 8 kHz throws: a vendor failure, not a wrong-sounding voice
                    case "done": yield break;
                    case "cancelled": ct.ThrowIfCancellationRequested(); yield break;
                    case "error": throw new InvalidOperationException("The speech vendor reported an error.");    // its message is not read: it could hold the text
                }
            }
        }
        finally
        {
            reg.Dispose();
            if (ws is not null) await VendorSocketIo.CloseQuietlyAsync(ws);
            _gate.Release();
            log.LogDebug("Yiddish Labs speech ended after {Chunks} audio chunks.", chunks);
        }
    }

    private static Task SendAsync(WebSocket ws, object message, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, ct);
}
