using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Infrastructure;

/// <summary>
/// Every buyer request reaches the API from the web server (decision O-02), so the socket address says nothing about
/// the end user. The web server forwards the end-user IP in three headers signed with its own key (<c>Web:ForwardingKey</c>,
/// never a portal secret):
/// <code>
/// X-Solvelt-Client-Ip:        203.0.113.7            (IPv4 or IPv6 literal)
/// X-Solvelt-Client-Timestamp: unix seconds
/// X-Solvelt-Client-Signature: v1=hex HMAC-SHA256(key, "&lt;timestamp&gt;\n&lt;ip&gt;")
/// </code>
/// A missing, malformed, stale (&gt; 300 s) or wrongly signed set is ignored, so a browser or attacker cannot choose its own
/// bucket: such a request is keyed by the socket address (shared by everything that comes through the same peer).
/// </summary>
public sealed class ClientIpResolver(IOptions<WebOptions> options, TimeProvider time)
{
    public const string IpHeader = "X-Solvelt-Client-Ip", TimestampHeader = "X-Solvelt-Client-Timestamp", SignatureHeader = "X-Solvelt-Client-Signature";
    public const int WindowSeconds = 300;

    /// <summary>A stable, non-reversible-looking bucket key for the end user ("fwd:…" if signed by the web server, else "peer:…").</summary>
    public string Resolve(HttpContext ctx)
    {
        var forwarded = TryForwarded(ctx.Request.Headers);
        if (forwarded is not null) return "fwd:" + Normalize(forwarded);
        return "peer:" + (ctx.Connection.RemoteIpAddress is { } peer ? Normalize(peer) : "unknown");
    }

    private IPAddress? TryForwarded(IHeaderDictionary h)
    {
        var key = options.Value.ForwardingKey;
        if (string.IsNullOrEmpty(key)) return null;
        string ip = h[IpHeader].ToString(), ts = h[TimestampHeader].ToString(), sig = h[SignatureHeader].ToString();
        if (ip.Length is 0 or > 45 || ts.Length == 0 || sig.Length == 0) return null;
        if (!IPAddress.TryParse(ip, out var addr) || !long.TryParse(ts, out var unix)) return null;
        if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - unix) > WindowSeconds) return null;
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{ts}\n{ip}")));
        var given = Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), given) ? addr : null;
    }

    /// <summary>IPv4-mapped IPv6 becomes IPv4; native IPv6 collapses to its /64 so one subscriber cannot rotate through its range.</summary>
    internal static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}

/// <summary>
/// In-memory fixed-window limiter for keys the partitioned rate limiter cannot see before the body is read (an email on a
/// sign-in or reset request, an account id). Keys are hashed so addresses are not kept in memory. It is per instance;
/// durable lockout lives in the auth tables.
/// </summary>
public sealed class AttemptLimiter(TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Start, int Count)> _windows = new();
    private const int PruneAbove = 10_000;

    /// <summary>Counts one attempt; false means the limit for this window is exceeded.</summary>
    public bool TryAcquire(string bucket, string key, int limit, TimeSpan window)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = (long)window.TotalMilliseconds;
        var id = bucket + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant())));
        var w = _windows.AddOrUpdate(id, _ => (now, 1), (_, old) => now - old.Start >= windowMs ? (now, 1) : (old.Start, old.Count + 1));
        if (_windows.Count > PruneAbove)
            foreach (var kv in _windows.Where(kv => now - kv.Value.Start >= windowMs).ToList()) _windows.TryRemove(kv);
        return w.Count <= limit;
    }
}
