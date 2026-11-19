using System.Net.WebSockets;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Opens the client WebSocket to a vendor. The seam the adapters are tested through: tests hand back one end of an in-memory socket pair, so no network is touched.</summary>
public interface IVendorSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

public sealed class ClientWebSocketConnector : IVendorSocketConnector
{
    public async Task<WebSocket> ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            foreach (var (k, v) in headers) ws.Options.SetRequestHeader(k, v);
            await ws.ConnectAsync(uri, ct);
            return ws;
        }
        catch { ws.Dispose(); throw; }
    }
}

internal static class VendorSocketIo
{
    /// <summary>One whole message (text or binary) from the socket; null when the peer closed.</summary>
    public static async Task<(System.Net.WebSockets.WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("message too large");
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    public static async Task CloseQuietlyAsync(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch { /* already gone */ }
        finally { ws.Dispose(); }
    }
}
