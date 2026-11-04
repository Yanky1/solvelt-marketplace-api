using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

public sealed record ReceivedCall(string Method, string PathAndQuery, byte[] Body, string Timestamp, string Nonce, string Signature, bool Verified, int AnswerStatus)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// In-process stand-in for the seller portal's internal API. It verifies every request with the portal's rules
/// (docs/ORDERS.md §3.1) using its OWN implementation, deliberately not sharing code with the client's signer:
/// headers present, timestamp inside the window, body cap, signature over the exact bytes, nonce replay.
/// After verification, scripted responders (queued per call, in order) produce the answer; the default is 200 {}.
/// </summary>
public sealed class FakePortal(string secret, TimeProvider clock, int windowSeconds = 300) : HttpMessageHandler
{
    public const int MaxBodyBytes = 262144;
    private readonly object _lock = new();
    private readonly HashSet<string> _nonces = [];
    private readonly List<ReceivedCall> _calls = [];
    private readonly Queue<Func<ReceivedCall, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private Func<ReceivedCall, HttpResponseMessage>? _always;

    /// <summary>Answers every call the script does not cover (the script is one answer per call, then this, then an empty 200). For stateless routes such as shipping settings.</summary>
    public FakePortal RespondAlways(Func<ReceivedCall, HttpResponseMessage> router) { lock (_lock) _always = router; return this; }

    public IReadOnlyList<ReceivedCall> Calls { get { lock (_lock) return _calls.ToList(); } }
    /// <summary>Only requests that passed signature verification.</summary>
    public IReadOnlyList<ReceivedCall> VerifiedCalls => Calls.Where(c => c.Verified).ToList();

    public FakePortal Respond(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null)
        => Respond(_ => Json(status, json, configure));

    public FakePortal Respond(Func<ReceivedCall, HttpResponseMessage> responder) => RespondAsync((c, _) => Task.FromResult(responder(c)));

    public FakePortal RespondAsync(Func<ReceivedCall, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        lock (_lock) _script.Enqueue(responder);
        return this;
    }

    /// <summary>The portal never answers: the call ends only when the request token is cancelled (the client's deadline).</summary>
    public FakePortal RespondHang(int times = 1)
    {
        for (var i = 0; i < times; i++)
            RespondAsync(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.OK, "{}"); });
        return this;
    }

    public FakePortal RespondTransportError() => Respond(_ => throw new HttpRequestException("simulated network failure"));

    public static HttpResponseMessage Json(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        configure?.Invoke(r);
        return r;
    }

    private static HttpResponseMessage Problem(HttpStatusCode s, string code, string message)
        => Json(s, $"{{\"code\":\"{code}\",\"message\":\"{message}\"}}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        string H(string n) => request.Headers.TryGetValues(n, out var v) ? v.First() : "";
        var ts = H("X-Solvelt-Timestamp");
        var nonce = H("X-Solvelt-Nonce");
        var sig = H("X-Solvelt-Signature");
        var target = request.RequestUri!.PathAndQuery;

        var (verified, failure) = Verify(request.Method.Method, target, ts, nonce, sig, body);
        var call = new ReceivedCall(request.Method.Method, target, body, ts, nonce, sig, verified, failure?.StatusCode is { } sc ? (int)sc : 0);
        if (!verified)
        {
            lock (_lock) _calls.Add(call);
            return failure!;
        }

        Func<ReceivedCall, CancellationToken, Task<HttpResponseMessage>>? responder;
        Func<ReceivedCall, HttpResponseMessage>? always;
        lock (_lock) { _calls.Add(call); _script.TryDequeue(out responder); always = _always; }
        if (responder is not null) return await responder(call, ct);
        return always is null ? Json(HttpStatusCode.OK, "{}") : always(call);
    }

    private (bool Ok, HttpResponseMessage? Failure) Verify(string method, string target, string ts, string nonce, string sig, byte[] body)
    {
        if (ts.Length == 0 || nonce.Length is 0 or > 100 || sig.Length == 0) return (false, Problem(HttpStatusCode.Unauthorized, "unsigned", "Missing signature headers."));
        if (!long.TryParse(ts, out var unix) || unix < 0 || unix > 4_102_444_800L) return (false, Problem(HttpStatusCode.Unauthorized, "bad_timestamp", "Invalid timestamp."));
        if (Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - unix) > windowSeconds) return (false, Problem(HttpStatusCode.Unauthorized, "stale", "Outside the allowed window."));
        if (body.Length > MaxBodyBytes) return (false, Problem(HttpStatusCode.RequestEntityTooLarge, "too_large", "Request body too large."));

        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n", ts, nonce, method.ToUpperInvariant(), target, bodyHash);
        var expected = "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant())))
            return (false, Problem(HttpStatusCode.Unauthorized, "bad_signature", "Signature mismatch."));

        // The nonce is recorded only for correctly signed requests (as the portal does).
        lock (_lock) { if (!_nonces.Add(nonce)) return (false, Problem(HttpStatusCode.Unauthorized, "replayed", "Nonce already used.")); }
        return (true, null);
    }
}
