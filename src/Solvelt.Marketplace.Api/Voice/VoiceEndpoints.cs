using System.Net.WebSockets;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

public static class VoiceEndpoints
{
    public const string InboundPath = "/api/v1/voice/signalwire/inbound", AfterStreamPath = "/api/v1/voice/signalwire/after-stream", StreamPath = "/api/v1/voice/stream";

    public static IEndpointRouteBuilder MapVoice(this IEndpointRouteBuilder app)
    {
        app.MapPost(InboundPath, Inbound);
        app.MapPost(AfterStreamPath, AfterStream);
        app.Map(StreamPath, Stream);
        return app;
    }

    private static IResult Cxml(XElement response) => Results.Content(new XDocument(new XDeclaration("1.0", "UTF-8", null), response).ToString(), "text/xml");

    /// <summary>The signed URL is built from configuration, never from request headers (a forwarded Host must not decide what was signed).</summary>
    private static async Task<(bool Ok, IReadOnlyDictionary<string, string> Form)> VerifyAsync(HttpContext ctx, VoiceOptions v, SignalWireOptions s)
    {
        if (!ctx.Request.HasFormContentType) return (false, new Dictionary<string, string>());
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var fields = form.ToDictionary(k => k.Key, k => k.Value.ToString());
        var url = v.PublicBaseUrl!.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString;
        return (SignalWireSignature.Verify(s.SigningKey ?? "", url, fields, ctx.Request.Headers[SignalWireSignature.Header].ToString()), fields);
    }

    private static async Task<IResult> Inbound(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, StreamTokens tokens, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled) return Results.NotFound();
        var (ok, form) = await VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        if (!form.TryGetValue("CallSid", out var sid) || string.IsNullOrWhiteSpace(sid) || sid.Length > 64) return Results.BadRequest();
        form.TryGetValue("From", out var from);
        var call = await store.StartAsync(sid, from, ct);
        var token = tokens.Issue(call.Id);
        var afterUrl = v.PublicBaseUrl!.TrimEnd('/') + AfterStreamPath;
        var wss = new UriBuilder(v.PublicBaseUrl!) { Scheme = new Uri(v.PublicBaseUrl!).Scheme == "https" ? "wss" : "ws", Path = StreamPath, Query = "token=" + Uri.EscapeDataString(token) };
        return Cxml(new XElement("Response",
            new XElement("Say", VoiceScripts.Disclosure),
            // authBearerToken: SignalWire sends it as "Authorization: Bearer" on the upgrade (documented); the same token is also in the URL. The Connect page does not document an
            // "action" attribute, so the Redirect after the stream ends is what moves the call on; both point to the same idempotent endpoint.
            new XElement("Connect", new XAttribute("action", afterUrl), new XElement("Stream", new XAttribute("url", wss.Uri.ToString()), new XAttribute("authBearerToken", token))),
            new XElement("Redirect", afterUrl)));
    }

    /// <summary>SignalWire calls this when the stream ends: a person was asked for and is reachable, so dial; otherwise hang up.</summary>
    private static async Task<IResult> AfterStream(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled) return Results.NotFound();
        var (ok, form) = await VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        var call = form.TryGetValue("CallSid", out var sid) ? await store.FindBySidAsync(sid, ct) : null;
        if (call?.TransferRequestedAt is not null && !string.IsNullOrWhiteSpace(v.SupportNumber))
            return Cxml(new XElement("Response", new XElement("Say", VoiceScripts.Connecting), new XElement("Dial", v.SupportNumber)));
        return Cxml(new XElement("Response", new XElement("Hangup")));
    }

    private static async Task Stream(HttpContext ctx, IOptions<VoiceOptions> vo, StreamTokens tokens, VoiceCallStore store, VoiceStateStore states, IRealtimeConnector connector, VoiceToolRunner tools,
        TimeProvider time, ILoggerFactory logs)
    {
        if (!vo.Value.Enabled) { ctx.Response.StatusCode = 404; return; }
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var auth = ctx.Request.Headers.Authorization.ToString();
        var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        var id = tokens.Validate(!string.IsNullOrEmpty(bearer) ? bearer : ctx.Request.Query["token"].ToString());
        var call = id is { } g ? await store.FindAsync(g, ctx.RequestAborted) : null;
        if (call is null || call.EndedAt is not null) { ctx.Response.StatusCode = 401; return; }
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice");
        WebSocket upstream;
        try { upstream = await connector.ConnectAsync(call.Model, ctx.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Voice call {Call}: the model session could not be opened ({Type}).", call.Id, ex.GetType().Name);
            await store.EndAsync(call.Id, Data.VoiceOutcome.Abandoned, CancellationToken.None);
            ctx.Response.StatusCode = 503;
            return;
        }
        using var phone = await ctx.WebSockets.AcceptWebSocketAsync();
        log.LogInformation("Voice call {Call} started: caller {Caller}, model tier {Tier}.", call.Id, call.CallerMasked, call.Tier);
        var state = states.GetOrCreate(call.Id);
        state.Connected(time.GetUtcNow());
        await new RealtimeBridge(phone, upstream, call, state, vo, store, tools, time, log).RunAsync(ctx.RequestAborted);
        if (state.KeypadStep is null) states.Remove(call.Id);   // the call is over: nothing of it stays in memory
        upstream.Dispose();
    }
}
