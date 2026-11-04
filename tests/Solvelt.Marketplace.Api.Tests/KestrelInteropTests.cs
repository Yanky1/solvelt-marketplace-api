using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The in-process FakePortal reads <c>RequestUri.PathAndQuery</c>; the real portal reads Kestrel's raw request target
/// (<c>UriHelper.GetEncodedPathAndQuery</c>). This hosts a real Kestrel server and a real socket HttpClient, with a verifier that
/// mirrors the portal's (docs/ORDERS.md §3.1: canonical string over the encoded path+query as received and the raw body bytes),
/// so what we sign is proven to be what the portal reconstructs.
/// </summary>
public class KestrelInteropTests
{
    private const string Secret = "interop-test-inbound-secret-0123456789abcdef";
    private static readonly Guid Seller = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Brand = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed record Seen(string Target, string Method, bool Verified, string Code, string Nonce, byte[] Body);

    private sealed class KestrelPortal : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HashSet<string> _nonces = [];
        public ConcurrentQueue<Seen> Requests { get; } = new();
        public Func<int, int> StatusFor { get; set; } = _ => 200;
        public Uri BaseAddress { get; }

        private KestrelPortal(WebApplication app, Uri baseAddress) { _app = app; BaseAddress = baseAddress; }

        public static async Task<KestrelPortal> StartAsync()
        {
            var b = WebApplication.CreateBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = b.Build();
            KestrelPortal? self = null;
            app.Run(async ctx =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var body = ms.ToArray();
                var target = UriHelper.GetEncodedPathAndQuery(ctx.Request);   // exactly what the portal signs against
                var (ok, code) = Verify(self!, ctx, target, body);
                self!.Requests.Enqueue(new Seen(target, ctx.Request.Method, ok, code, ctx.Request.Headers["X-Solvelt-Nonce"].ToString(), body));
                ctx.Response.ContentType = "application/json";
                if (!ok) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync($"{{\"code\":\"{code}\"}}"); return; }
                var n = self.Requests.Count;
                ctx.Response.StatusCode = self.StatusFor(n);
                await ctx.Response.WriteAsync(ctx.Request.Method == "POST" && target.EndsWith("/orders") ? "{\"created\":false,\"orders\":[]}" : "{}");
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            self = new KestrelPortal(app, new Uri(address));
            return self;
        }

        private static (bool, string) Verify(KestrelPortal self, HttpContext ctx, string target, byte[] body)
        {
            string H(string n) => ctx.Request.Headers[n].ToString();
            var ts = H("X-Solvelt-Timestamp"); var nonce = H("X-Solvelt-Nonce"); var sig = H("X-Solvelt-Signature");
            if (ts.Length == 0 || nonce.Length is 0 or > 100 || sig.Length == 0) return (false, "unsigned");
            if (!long.TryParse(ts, out var unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300) return (false, "stale");
            var canonical = $"{ts}\n{nonce}\n{ctx.Request.Method.ToUpperInvariant()}\n{target}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}";
            var expected = "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()))) return (false, "bad_signature");
            lock (self._nonces) { if (!self._nonces.Add(nonce)) return (false, "replayed"); }
            return (true, "ok");
        }

        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    private static SignedPortalClient Client(KestrelPortal portal) => new(
        new HttpClient(new SocketsHttpHandler()) { BaseAddress = portal.BaseAddress, Timeout = Timeout.InfiniteTimeSpan },
        Options.Create(new PortalOptions { BaseUrl = portal.BaseAddress.GetLeftPart(UriPartial.Authority), InboundSecret = Secret, RetryBaseDelayMs = 0 }),
        TimeProvider.System);

    private static CreateOrderRequest Order(string name, string? gift) => new("MP-10045", DateTimeOffset.UtcNow, "USD",
        new PortalBuyer(name, "relay-8f3a@relay.solvelt.test"),
        new PortalAddress("Zoë Müller", "1 Hauptstraße", null, "Zürich", null, "8001", "CH"),
        "Standard", "standard", gift,
        [new OrderLineRequest("L-1", Seller, Brand, Guid.NewGuid(), null, 1, 1999)],
        [new ShippingChargeRequest(Seller, Brand, 599)]);

    [Fact]
    public async Task Unicode_and_escaped_json_bodies_verify_against_the_raw_bytes_over_a_real_socket()
    {
        await using var portal = await KestrelPortal.StartAsync();
        var res = await Client(portal).CreateOrderAsync(Order("Zoë Müller 🙂 日本語", "line1\nline2 \"quoted\" \\ back\u2028slash"));
        Assert.True(res.IsSuccess, res.Error?.Code);
        var seen = Assert.Single(portal.Requests);
        Assert.True(seen.Verified, seen.Code);
        // System.Text.Json escapes non-ASCII by default, so the signed bytes are pure ASCII with \uXXXX escapes (valid JSON for the portal).
        var wire = Encoding.UTF8.GetString(seen.Body);
        Assert.Contains("\\ud83d\\ude42", wire, StringComparison.OrdinalIgnoreCase);
        Assert.All(seen.Body, b => Assert.True(b < 0x80));
    }

    [Fact]
    public async Task Escaped_query_strings_verify_against_the_target_kestrel_received()
    {
        await using var portal = await KestrelPortal.StartAsync();
        var from = new DateTimeOffset(2026, 12, 9, 0, 0, 0, TimeSpan.Zero);
        var svc = Guid.NewGuid();
        var res = await Client(portal).GetServiceAvailabilityAsync(new ServiceAvailabilityQuery(svc, from, from.AddDays(1), "customer", "11201 ä",
            AddOns: ["deep clean", "a/b", "c&d=e", "ü+1"]));
        Assert.True(res.IsSuccess, res.Error?.Code);
        var seen = Assert.Single(portal.Requests);
        Assert.True(seen.Verified, seen.Code);
        Assert.Contains("from=2026-12-09T00%3A00%3A00Z", seen.Target);
        Assert.Contains("a%2Fb", seen.Target);
        Assert.Contains("c%26d%3De", seen.Target);
    }

    [Fact]
    public async Task Retries_over_a_real_socket_use_fresh_nonces_and_are_all_accepted()
    {
        await using var portal = await KestrelPortal.StartAsync();
        portal.StatusFor = n => n < 3 ? 503 : 200;
        var res = await Client(portal).CreateOrderAsync(Order("Jane B.", null));
        Assert.True(res.IsSuccess);
        Assert.Equal(3, res.Attempts);
        Assert.All(portal.Requests, r => Assert.True(r.Verified, r.Code));
        Assert.Equal(3, portal.Requests.Select(r => r.Nonce).Distinct().Count());
        Assert.Single(portal.Requests.Select(r => Convert.ToHexString(r.Body)).Distinct());
    }
}
