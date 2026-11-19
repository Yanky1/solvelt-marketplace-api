using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// ElevenLabs streaming text to speech (options p2 and p3). One WebSocket per spoken sentence to <c>wss://{host}/v1/text-to-speech/{voice}/stream-input</c>, asking for <c>ulaw_8000</c> so the audio is already what the
/// telephone wants. The key travels in the <c>xi-api-key</c> header only. Cancelling (a caller talking over the assistant) closes the socket at once. The text is never logged.
/// </summary>
public sealed class ElevenLabsTts(ElevenLabsOptions options, IVendorSocketConnector connector, ILogger log) : ITts
{
    internal static Uri AddressFor(ElevenLabsOptions o)
    {
        var host = VendorHosts.ElevenLabs(o.Region) ?? throw new InvalidOperationException("unknown region");
        return new($"wss://{host}/v1/text-to-speech/{Uri.EscapeDataString(o.VoiceId)}/stream-input?model_id={Uri.EscapeDataString(o.TtsModel)}&output_format=ulaw_8000&enable_logging={(o.EnableLogging ? "true" : "false")}");
    }

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var ws = await connector.ConnectAsync(AddressFor(options), new Dictionary<string, string> { ["xi-api-key"] = options.ApiKey }, ct);
        var chunks = 0;
        try
        {
            await SendAsync(ws, new { text = " " }, ct);                                        // the opening blank
            await SendAsync(ws, new { text = text.Trim() + " ", flush = true }, ct);            // the sentence, generated now
            await SendAsync(ws, new { text = "" }, ct);                                         // end of input
            while (true)
            {
                var m = await VendorSocketIo.ReceiveAsync(ws, ct);
                if (m is null) yield break;
                if (m.Value.Type != WebSocketMessageType.Text) continue;
                byte[]? audio = null; var final = false;
                try
                {
                    using var doc = JsonDocument.Parse(m.Value.Data);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("audio", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } b64) audio = Convert.FromBase64String(b64);
                    final = root.TryGetProperty("isFinal", out var f) && f.ValueKind == JsonValueKind.True;
                }
                catch (Exception ex) when (ex is JsonException or FormatException) { continue; }   // a frame that is not what we expect is skipped, never logged
                if (audio is { Length: > 0 }) { chunks++; yield return audio; }
                if (final) yield break;
            }
        }
        finally
        {
            log.LogDebug("ElevenLabs speech ended after {Chunks} audio chunks.", chunks);
            await VendorSocketIo.CloseQuietlyAsync(ws);
        }
    }

    private static Task SendAsync(WebSocket ws, object message, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, ct);
}
