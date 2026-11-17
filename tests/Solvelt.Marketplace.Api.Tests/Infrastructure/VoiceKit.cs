using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>Two connected WebSockets in memory (no network): the client end goes to the app as its "OpenAI" socket, the server end is the scripted fake.</summary>
public static class SocketPair
{
    private sealed class Duplex(PipeReader r, PipeWriter w) : Stream
    {
        private readonly Stream _in = r.AsStream(), _out = w.AsStream();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => _in.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => _in.ReadAsync(m, ct);
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => _out.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) => _out.WriteAsync(m, ct);
        protected override void Dispose(bool d) { if (d) { r.Complete(); w.Complete(); } base.Dispose(d); }
    }

    public static (WebSocket Client, WebSocket Server) Create()
    {
        var a = new Pipe(); var b = new Pipe();
        var client = WebSocket.CreateFromStream(new Duplex(a.Reader, b.Writer), new WebSocketCreationOptions { IsServer = false });
        var server = WebSocket.CreateFromStream(new Duplex(b.Reader, a.Writer), new WebSocketCreationOptions { IsServer = true });
        return (client, server);
    }
}

/// <summary>A scripted OpenAI Realtime server: records every event the app sends and lets a test push model events (audio, function calls, usage).</summary>
public sealed class FakeRealtime : IRealtimeConnector, IDisposable
{
    private WebSocket? _server;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _received = new();
    public IReadOnlyList<string> Received => _received.ToArray();
    public string? Model { get; private set; }
    public bool Fail { get; set; }
    public bool Closed => _server is { State: not WebSocketState.Open };

    public Task<WebSocket> ConnectAsync(string model, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("upstream down");
        Model = model;
        var (client, server) = SocketPair.Create();
        _server = server;
        _ = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            try
            {
                while (server.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await server.ReceiveAsync(buf, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                    _received.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch (Exception) { /* the test ended */ }
        });
        _connected.TrySetResult();
        return Task.FromResult(client);
    }

    public async Task Send(object evt)
    {
        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _server!.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt)), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public Task<JsonNode> Wait(Func<JsonNode, bool> match, int seconds = 5) => Poll(() => _received.Select(s => JsonNode.Parse(s)!).FirstOrDefault(match), seconds);
    public Task<JsonNode> Wait(string type, int seconds = 5) => Wait(n => n["type"]?.GetValue<string>() == type, seconds);
    public IEnumerable<JsonNode> Of(string type) => _received.Select(s => JsonNode.Parse(s)!).Where(n => n["type"]?.GetValue<string>() == type);

    internal static async Task<T> Poll<T>(Func<T?> f, int seconds) where T : class
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (f() is { } v) return v; await Task.Delay(10); }
        throw new TimeoutException("the expected event did not arrive");
    }

    public async Task CloseAsync() { if (_server is { State: WebSocketState.Open }) await _server.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
    public void Dispose() => _server?.Dispose();
}

/// <summary>The phone side: a fake SignalWire stream talking to the app's WebSocket endpoint over the test server.</summary>
public sealed class FakePhone(WebSocket ws) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<JsonNode> _got = new();
    private Task? _reader;
    public WebSocket Socket => ws;
    public IReadOnlyList<JsonNode> Got => _got.ToArray();

    public static async Task<FakePhone> Connect(TestFactory f, string token)
    {
        var c = f.Server.CreateWebSocketClient();
        var p = new FakePhone(await c.ConnectAsync(new Uri("ws://localhost/api/v1/voice/stream?token=" + Uri.EscapeDataString(token)), CancellationToken.None));
        p.StartReading();
        return p;
    }

    private void StartReading() => _reader = Task.Run(async () =>
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                _got.Enqueue(JsonNode.Parse(ms.ToArray())!);
            }
        }
        catch (Exception) { /* closed */ }
    });

    public Task Send(object evt) => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt)), WebSocketMessageType.Text, true, CancellationToken.None);
    public Task Start(string sid = "MZ1") => Send(new { @event = "start", start = new { streamSid = sid, callSid = "CA1" }, streamSid = sid });
    public Task Media(string b64) => Send(new { @event = "media", streamSid = "MZ1", media = new { track = "inbound", payload = b64 } });
    public Task Dtmf(string digit) => Send(new { @event = "dtmf", streamSid = "MZ1", dtmf = new { digit } });
    public Task<JsonNode> Wait(string @event, int seconds = 5) => FakeRealtime.Poll(() => _got.FirstOrDefault(n => n["event"]?.GetValue<string>() == @event), seconds);
    public async Task<bool> WaitClosed(int seconds = 5)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (ws.State != WebSocketState.Open) return true; await Task.Delay(10); }
        return false;
    }
    public void Dispose() => ws.Dispose();
}

public static class VoiceFactory
{
    public const string SigningKey = "sw-test-signing-key", BaseUrl = "https://voice.test";

    public static Dictionary<string, string?> Config(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["Voice:Enabled"] = "true", ["Voice:OpenAiApiKey"] = "test-key-not-real", ["Voice:StreamTokenKey"] = new string('t', 40), ["Voice:PublicBaseUrl"] = BaseUrl,
            ["Voice:OpenAiModel"] = "model-full", ["Voice:OpenAiModelMini"] = "model-mini", ["SignalWire:SigningKey"] = SigningKey,
        };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    public static TestFactory Create(FakeRealtime fake, Dictionary<string, string?>? extra = null)
        => new(Config(extra), s => { s.RemoveAll<IRealtimeConnector>(); s.AddSingleton<IRealtimeConnector>(fake); });

    /// <summary>POSTs a signed SignalWire webhook and returns the response.</summary>
    public static async Task<HttpResponseMessage> Hook(HttpClient c, string path, string callSid, string from = "+15551234567", string? signature = null)
    {
        var form = new Dictionary<string, string> { ["CallSid"] = callSid, ["From"] = from, ["To"] = "+15559876543" };
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add(SignalWireSignature.Header, signature ?? SignalWireSignature.Compute(SigningKey, BaseUrl + path, form));
        return await c.SendAsync(req);
    }

    /// <summary>Runs the inbound webhook and returns the call's stream token from the cXML.</summary>
    public static async Task<string> Answer(HttpClient c, string callSid = "CA1", string from = "+15551234567")
    {
        var r = await Hook(c, VoiceEndpoints.InboundPath, callSid, from);
        r.EnsureSuccessStatusCode();
        var url = System.Xml.Linq.XDocument.Parse(await r.Content.ReadAsStringAsync()).Descendants("Stream").Single().Attribute("url")!.Value;
        return System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["token"]!;
    }
}
