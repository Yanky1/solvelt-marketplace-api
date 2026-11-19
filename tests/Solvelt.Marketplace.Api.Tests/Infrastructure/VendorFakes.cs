using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>Two ends of a WebSocket held in memory: the adapter gets <see cref="Client"/>, the test plays the vendor with <see cref="Server"/>. No network is touched.</summary>
public sealed class MemorySocketPair : IDisposable
{
    public WebSocket Client { get; }
    public WebSocket Server { get; }

    public MemorySocketPair()
    {
        var toServer = new Pipe(); var toClient = new Pipe();
        Client = WebSocket.CreateFromStream(new Duplex(toClient.Reader.AsStream(), toServer.Writer.AsStream()), false, null, TimeSpan.FromSeconds(30));
        Server = WebSocket.CreateFromStream(new Duplex(toServer.Reader.AsStream(), toClient.Writer.AsStream()), true, null, TimeSpan.FromSeconds(30));
    }

    public void Dispose() { Client.Dispose(); Server.Dispose(); }

    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken c) => write.FlushAsync(c);
        public override int Read(byte[] b, int o, int c) => read.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken c = default) => read.ReadAsync(m, c);
        public override void Write(byte[] b, int o, int c) => write.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken c = default) => write.WriteAsync(m, c);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { read.Dispose(); write.Dispose(); } base.Dispose(disposing); }
    }

    /// <summary>One message from the other side as text (or the binary frame as a base64 string prefixed "bin:"); null when closed.</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(WebSocket ws, int seconds = 5)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buffer = new byte[16 * 1024]; using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    public static Task SendTextAsync(WebSocket ws, string json) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

/// <summary>Hands the adapter one end of a fresh in-memory socket per connection and keeps what it was asked for (address, headers).</summary>
public sealed class FakeSocketConnector : IVendorSocketConnector
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Connection> _connections = new();
    public bool Fail { get; set; }
    public IReadOnlyList<Connection> Connections => _connections.ToArray();

    public sealed record Connection(Uri Uri, IReadOnlyDictionary<string, string> Headers, MemorySocketPair Pair)
    {
        public WebSocket Server => Pair.Server;
    }

    public Task<WebSocket> ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (Fail) throw new WebSocketException("vendor refused");
        var pair = new MemorySocketPair();
        _connections.Enqueue(new Connection(uri, new Dictionary<string, string>(headers), pair));
        return Task.FromResult(pair.Client);
    }

    public async Task<Connection> NextAsync(int seconds = 5, int skip = 0)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { var all = Connections; if (all.Count > skip) return all[skip]; await Task.Delay(5); }
        throw new TimeoutException("the adapter did not connect");
    }
}

/// <summary>Answers the text model's HTTP request with a scripted event stream and keeps what was sent.</summary>
public sealed class FakeLlmHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = "";
    public bool NeverAnswer { get; set; }
    public List<(Uri? Uri, string? Auth, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        if (NeverAnswer) await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "text/event-stream") };
    }

    public static string Sse(params string[] events) => string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n";
}

/// <summary>Keeps every log line (message and exception type) so a test can prove what was and was not written.</summary>
public sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { lock (Lines) Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception.GetType().Name)); }
}
