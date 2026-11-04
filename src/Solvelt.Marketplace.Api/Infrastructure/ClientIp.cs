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
/// A missing, malformed, stale (&gt; 60 s) or wrongly signed set is ignored, so a browser or attacker cannot choose its own
/// bucket: such a request is keyed by the socket address (shared by everything that comes through the same peer). When headers
/// are present but invalid (a key mismatch or clock skew would silently put every user into one bucket) the failure is counted,
/// exposed on <c>/health</c> and logged as a rate-limited warning with the reason only (never an address or a signature).
/// The window is short because a captured header set can be replayed to frame a victim (decision M1-28); the API must be reachable
/// only from the web tier, over TLS.
/// </summary>
public sealed class ClientIpResolver(IOptions<WebOptions> options, TimeProvider time, ILogger<ClientIpResolver> log)
{
    public const string IpHeader = "X-Solvelt-Client-Ip", TimestampHeader = "X-Solvelt-Client-Timestamp", SignatureHeader = "X-Solvelt-Client-Signature";
    public const int WindowSeconds = 60;
    private const string CacheKey = "solvelt.client-bucket";
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);
    private long _invalid;
    private long _lastWarnTicks;

    /// <summary>Requests that carried forwarding headers which did not verify, since start.</summary>
    public long InvalidCount => Interlocked.Read(ref _invalid);

    /// <summary>A stable bucket key for the end user ("fwd:…" if signed by the web server, else "peer:…"), computed once per request.</summary>
    public string Resolve(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(CacheKey, out var cached) && cached is string key) return key;
        var forwarded = TryForwarded(ctx.Request.Headers);
        key = forwarded is not null
            ? "fwd:" + Normalize(forwarded)
            : "peer:" + (ctx.Connection.RemoteIpAddress is { } peer ? Normalize(peer) : "unknown");
        ctx.Items[CacheKey] = key;
        return key;
    }

    private IPAddress? TryForwarded(IHeaderDictionary h)
    {
        string ip = h[IpHeader].ToString(), ts = h[TimestampHeader].ToString(), sig = h[SignatureHeader].ToString();
        if (ip.Length == 0 && ts.Length == 0 && sig.Length == 0) return null;   // not forwarded at all: not a failure
        var reason = Verify(ip, ts, sig, out var addr);
        if (reason is null) return addr;
        Interlocked.Increment(ref _invalid);
        WarnRateLimited(reason);
        return null;
    }

    private string? Verify(string ip, string ts, string sig, out IPAddress? addr)
    {
        addr = null;
        var key = options.Value.ForwardingKey;
        if (string.IsNullOrEmpty(key)) return "not_configured";
        if (ip.Length == 0 || ts.Length == 0 || sig.Length == 0) return "incomplete";
        if (ip.Length > 45 || !IPAddress.TryParse(ip, out var parsed) || !long.TryParse(ts, out var unix)) return "malformed";
        if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - unix) > WindowSeconds) return "stale";
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{ts}\n{ip}")));
        var given = Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), given)) return "bad_signature";
        addr = parsed;
        return null;
    }

    private void WarnRateLimited(string reason)
    {
        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarnTicks);
        if (last != 0 && now - last < WarnEvery.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWarnTicks, now, last) != last) return;
        log.LogWarning("Client-IP forwarding headers present but not valid ({Reason}); {Total} such requests since start. " +
                       "Those requests share one rate-limit bucket per peer: check Web:ForwardingKey and clock skew between the web server and the API.", reason, InvalidCount);
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
/// durable lockout lives in the auth tables. Expired windows are pruned at most once per <see cref="PruneInterval"/> (not per call);
/// the table is hard-capped at <c>maxKeys</c>: beyond it a tenth of the entries is evicted in one batch, <b>lowest hit count first</b>
/// (then oldest window), so a spray of distinct single-hit keys can never evict, and so reset, a key that is being brute-forced.
/// The number of keys is an interlocked counter: <c>ConcurrentDictionary.Count</c> takes every lock, which a limiter must not do per call.
/// </summary>
public sealed class AttemptLimiter(TimeProvider time, int maxKeys)
{
    public AttemptLimiter(TimeProvider time) : this(time, 100_000) { }

    public static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);
    private readonly record struct Entry(long Start, int Count, long WindowMs);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _windows = new();
    private int _keys;
    private long _lastPruneMs;
    private int _evicting;
    internal int TrackedKeys => Volatile.Read(ref _keys);
    internal int ActualKeys => _windows.Count;   // for tests: the counter must always agree with the table
    internal int PruneRuns;
    internal int Evictions;

    /// <summary>Counts one attempt; false means the limit for this window is exceeded.</summary>
    public bool TryAcquire(string bucket, string key, int limit, TimeSpan window)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = (long)window.TotalMilliseconds;
        var id = bucket + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant())));
        Entry current;
        while (true)
        {
            if (_windows.TryGetValue(id, out var old))
            {
                current = now - old.Start >= windowMs ? new Entry(now, 1, windowMs) : old with { Count = old.Count + 1 };
                if (_windows.TryUpdate(id, current, old)) break;
            }
            else
            {
                current = new Entry(now, 1, windowMs);
                if (_windows.TryAdd(id, current)) { Interlocked.Increment(ref _keys); break; }
            }
        }
        MaybePrune(now);
        if (Volatile.Read(ref _keys) > maxKeys) Evict();
        return current.Count <= limit;
    }

    private void MaybePrune(long now)
    {
        var last = Interlocked.Read(ref _lastPruneMs);
        if (now - last < PruneInterval.TotalMilliseconds || Interlocked.CompareExchange(ref _lastPruneMs, now, last) != last) return;
        Interlocked.Increment(ref PruneRuns);
        foreach (var kv in _windows)
            if (now - kv.Value.Start >= kv.Value.WindowMs && _windows.TryRemove(kv)) Interlocked.Decrement(ref _keys);
    }

    private void Evict()
    {
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0) return;
        try
        {
            var target = maxKeys - Math.Max(1, maxKeys / 10);
            var excess = Volatile.Read(ref _keys) - target;
            if (excess <= 0) return;
            // Snapshot by enumerating: LINQ (OrderBy, ToList) would copy through ICollection.CopyTo, which throws ArgumentException when another thread
            // adds a key between sizing the array and filling it. The enumerator is safe under concurrent writes.
            var snapshot = new List<KeyValuePair<string, Entry>>(Volatile.Read(ref _keys) + 16);
            foreach (var kv in _windows) snapshot.Add(kv);
            foreach (var kv in snapshot.OrderBy(kv => kv.Value.Count).ThenBy(kv => kv.Value.Start).Take(excess))
                if (_windows.TryRemove(kv)) Interlocked.Decrement(ref _keys);
            Interlocked.Increment(ref Evictions);
        }
        finally { Volatile.Write(ref _evicting, 0); }
    }
}
