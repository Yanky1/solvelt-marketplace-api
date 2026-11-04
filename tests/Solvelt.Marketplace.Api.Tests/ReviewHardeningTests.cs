using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Orchestrator review of slice 1: logging of URLs, validator guards, per-end-user rate limiting, test provider.</summary>
public class ReviewHardeningTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
    private static IConfiguration Config(Dictionary<string, string?> d) => new ConfigurationBuilder().AddInMemoryCollection(d).Build();

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40),
        ["Portal:WebhookSecret"] = new string('b', 40),
        ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40),
        ["Portal:BaseUrl"] = "https://portal.example",
        ["Marketplace:RelayDomain"] = "relay.example",
        ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres",
        ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };
    private static IReadOnlyList<string> Prod(Dictionary<string, string?> d, string env = "Production") => StartupValidator.Validate(Config(d), new Env(env));

    // ---------- 1. no URLs, query strings, emails or tokens in logs ----------
    [Fact]
    public async Task Logs_never_contain_query_strings_emails_or_tokens()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        await c.GetAsync("/api/v1/ping?email=jane.doe@example.com&token=SECRETTOKEN123");
        await c.GetAsync("/verify/jane.doe@example.com/SECRETTOKEN123");
        await c.PostAsJsonAsync("/api/v1/nope?email=jane.doe@example.com", new { email = "jane.doe@example.com", password = "hunter2hunter2" });
        var lines = f.Logs.Lines;
        Assert.Contains(lines, l => l.Contains("/api/v1/ping"));   // not vacuous: our own route log line exists
        foreach (var banned in new[] { "jane.doe", "example.com", "SECRETTOKEN123", "hunter2", "email=" })
        {
            var hits = lines.Where(l => l.Contains(banned, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(hits.Count == 0, $"'{banned}' leaked into logs:\n" + string.Join("\n", hits));
        }
    }

    // ---------- 2/4. every start-up guard has a test that fails without it ----------
    [Theory]
    [InlineData("Portal:InboundSecret")]
    [InlineData("Portal:WebhookSecret")]
    [InlineData("Web:ForwardingKey")]
    public void Each_secret_must_be_at_least_32_chars(string key)
    {
        var d = GoodProd(); d[key] = new string('x', 31);
        Assert.Contains(Prod(d), p => p.Contains(key) && p.Contains("at least 32"));
        d[key] = new string('x', 32);
        Assert.DoesNotContain(Prod(d), p => p.Contains("at least 32"));
    }

    [Theory]
    [InlineData("Portal:InboundSecret", DevDefaults.PortalInboundSecret)]
    [InlineData("Portal:InboundSecret", DevDefaults.PortalWebhookSecret)]     // swapped defaults
    [InlineData("Portal:InboundSecret", DevDefaults.WebForwardingKey)]
    [InlineData("Portal:WebhookSecret", DevDefaults.PortalInboundSecret)]     // swapped defaults
    [InlineData("Portal:WebhookSecret", DevDefaults.PortalWebhookSecret)]
    [InlineData("Portal:WebhookSecret", DevDefaults.WebForwardingKey)]
    [InlineData("Web:ForwardingKey", DevDefaults.PortalInboundSecret)]
    [InlineData("Web:ForwardingKey", DevDefaults.PortalWebhookSecret)]
    [InlineData("Web:ForwardingKey", DevDefaults.WebForwardingKey)]
    public void No_dev_default_is_accepted_in_any_slot_in_production(string key, string devDefault)
    {
        var d = GoodProd(); d[key] = devDefault;
        Assert.Contains(Prod(d), p => p.Contains(key) && p.Contains("Development default"));
    }

    [Fact]
    public void Dev_defaults_are_long_enough_to_pass_only_the_dev_check()
        => Assert.All(new[] { DevDefaults.PortalInboundSecret, DevDefaults.PortalWebhookSecret, DevDefaults.WebForwardingKey }, v => Assert.True(v.Length >= 32));

    [Theory]
    [InlineData("Portal:InboundSecret", "Portal:WebhookSecret")]
    [InlineData("Portal:InboundSecret", "Web:ForwardingKey")]
    [InlineData("Portal:WebhookSecret", "Web:ForwardingKey")]
    public void No_two_keys_may_be_equal(string a, string b)
    {
        var d = GoodProd(); d[b] = d[a];
        Assert.Contains(Prod(d), p => p.Contains(a) && p.Contains(b) && p.Contains("must differ"));
    }

    [Theory]
    [InlineData("Portal:InboundSecret")]
    [InlineData("Portal:WebhookSecret")]
    [InlineData("Web:ForwardingKey")]
    [InlineData("Portal:BaseUrl")]
    [InlineData("Marketplace:RelayDomain")]
    [InlineData("Marketplace:PublicBaseUrl")]
    [InlineData("Database:Provider")]
    [InlineData("ConnectionStrings:Default")]
    public void Every_required_value_is_required_in_production(string key)
    {
        var d = GoodProd(); d.Remove(key);
        Assert.Contains(Prod(d), p => p.Contains(key == "ConnectionStrings:Default" ? "ConnectionStrings:Default" : key));
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("")]
    public void Production_requires_the_postgres_provider(string provider)
    {
        var d = GoodProd(); d["Database:Provider"] = provider;
        Assert.Contains(Prod(d), p => p.Contains("must be Postgres"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("")]
    public void Only_Development_and_Testing_skip_validation(string env)
        => Assert.NotEmpty(Prod([], env));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_Testing_need_no_configuration(string env)
        => Assert.Empty(Prod([], env));

    [Fact]
    public void Dev_fallbacks_apply_only_when_dev_like_and_never_override_configured_values()
    {
        var empty = Config([]);
        PortalOptions P(bool dev) { var o = new PortalOptions(); OptionsSetup.Portal(o, empty, dev); return o; }
        Assert.Equal(DevDefaults.PortalInboundSecret, P(true).InboundSecret);
        Assert.Equal(DevDefaults.PortalWebhookSecret, P(true).WebhookSecret);
        Assert.Null(P(false).InboundSecret);
        Assert.Null(P(false).WebhookSecret);

        var w = new WebOptions(); OptionsSetup.Web(w, empty, false); Assert.Null(w.ForwardingKey);
        w = new WebOptions(); OptionsSetup.Web(w, empty, true); Assert.Equal(DevDefaults.WebForwardingKey, w.ForwardingKey);
        var m = new MarketplaceOptions(); OptionsSetup.Marketplace(m, empty, false); Assert.Null(m.RelayDomain);
        m = new MarketplaceOptions(); OptionsSetup.Marketplace(m, empty, true); Assert.Equal(DevDefaults.RelayDomain, m.RelayDomain);

        var set = Config(new() { ["Portal:InboundSecret"] = "configured", ["Web:ForwardingKey"] = "k" });
        var o = new PortalOptions(); OptionsSetup.Portal(o, set, true); Assert.Equal("configured", o.InboundSecret);
        w = new WebOptions(); OptionsSetup.Web(w, set, true); Assert.Equal("k", w.ForwardingKey);
    }

    // ---------- 3. rate limits are per end user, not per web server ----------
    private static string Sig(string key, string ts, string ip)
        => "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{ts}\n{ip}")));

    private static HttpRequestMessage Ping(TestFactory f, string? ip, string? key = null, long? ts = null, string? sig = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        if (ip is null) return r;
        var t = (ts ?? f.Clock.GetUtcNow().ToUnixTimeSeconds()).ToString();
        r.Headers.Add(ClientIpResolver.IpHeader, ip);
        r.Headers.Add(ClientIpResolver.TimestampHeader, t);
        r.Headers.Add(ClientIpResolver.SignatureHeader, sig ?? Sig(key ?? DevDefaults.WebForwardingKey, t, ip));
        return r;
    }

    [Fact]
    public async Task Signed_forwarded_end_user_ips_get_separate_buckets()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "2" });
        var c = f.CreateClient();
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Ping(f, "203.0.113.7"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.SendAsync(Ping(f, "203.0.113.7"))).StatusCode);   // user A is locked out ...
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Ping(f, "203.0.113.8"))).StatusCode);                // ... user B is not
    }

    [Fact]
    public async Task Forged_or_unsigned_forwarding_headers_cannot_choose_a_bucket()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "2" });
        var c = f.CreateClient();
        var now = f.Clock.GetUtcNow().ToUnixTimeSeconds();
        // each attempt claims a different IP but is signed wrongly / stale / not at all: all fall into the one peer bucket
        var attempts = new[]
        {
            Ping(f, "198.51.100.1", key: "some-other-key-that-is-not-the-forwarding-key"),
            Ping(f, "198.51.100.2", sig: "v1=deadbeef"),
            Ping(f, "198.51.100.3", ts: now - 301),
            Ping(f, "198.51.100.4", ts: now + 301),
            Ping(f, null),
        };
        var codes = new List<HttpStatusCode>();
        foreach (var a in attempts) codes.Add((await c.SendAsync(a)).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task A_signature_for_one_ip_does_not_authorise_another_ip()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "1" });
        var c = f.CreateClient();
        var t = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var sigForA = Sig(DevDefaults.WebForwardingKey, t, "203.0.113.7");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Ping(f, "203.0.113.7", sig: sigForA))).StatusCode);
        // same signature, different claimed IP: rejected as unsigned -> peer bucket (fresh), then exhausted
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Ping(f, "203.0.113.99", sig: sigForA))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.SendAsync(Ping(f, "203.0.113.98", sig: sigForA))).StatusCode);
    }

    private static (ClientIpResolver Resolver, FakeTimeProvider Clock) Resolver(string? key = "k-forwarding-key-for-unit-tests-0123456789")
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        return (new ClientIpResolver(Options.Create(new WebOptions { ForwardingKey = key }), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientIpResolver>.Instance), clock);
    }

    private static DefaultHttpContext Ctx(ClientIpResolver r, string? peer, params (string, string)[] headers)
    {
        var services = new ServiceCollection().AddSingleton(r).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        if (peer is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;
        return ctx;
    }

    /// <summary>Cross-repo vector shared with the web server (W1, web b9faea5): both sides must produce exactly this signature.</summary>
    [Fact]
    public void Client_ip_forwarding_cross_repo_vector()
    {
        const string key = DevDefaults.WebForwardingKey;
        Assert.Equal("dev-only-web-forwarding-key-not-for-production", key);
        Assert.Equal("v1=eaf9dade01862226a6cb91574596e068996969fe53eb32f05d4ec102df81a4e8", Sig(key, "1700000000", "203.0.113.9"));   // independent test-side HMAC
        // ... and the production resolver accepts exactly that signature at that timestamp, and rejects it for another ip
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var r = new ClientIpResolver(Options.Create(new WebOptions { ForwardingKey = key }), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientIpResolver>.Instance);
        const string sig = "v1=eaf9dade01862226a6cb91574596e068996969fe53eb32f05d4ec102df81a4e8";
        Assert.Equal("fwd:203.0.113.9", r.Resolve(Ctx(r, "10.0.0.1", (ClientIpResolver.IpHeader, "203.0.113.9"), (ClientIpResolver.TimestampHeader, "1700000000"), (ClientIpResolver.SignatureHeader, sig))));
        Assert.Equal("peer:10.0.0.1", r.Resolve(Ctx(r, "10.0.0.1", (ClientIpResolver.IpHeader, "203.0.113.10"), (ClientIpResolver.TimestampHeader, "1700000000"), (ClientIpResolver.SignatureHeader, sig))));
    }

    [Fact]
    public void A_null_remote_address_is_handled_and_partitions_do_not_share_between_policies()
    {
        var (r, _) = Resolver();
        var ctx = Ctx(r, null);
        Assert.Equal("peer:unknown", r.Resolve(ctx));
        var auth = RateLimitPolicies.PartitionKey(ctx, RateLimitPolicies.Auth);
        var pub = RateLimitPolicies.PartitionKey(ctx, RateLimitPolicies.Public);
        Assert.NotEqual(auth, pub);
        Assert.StartsWith("auth:", auth);
        Assert.StartsWith("public:", pub);
        Assert.NotEqual(auth, RateLimitPolicies.PartitionKey(Ctx(r, "10.0.0.1"), RateLimitPolicies.Auth));   // distinct peers, distinct buckets
    }

    [Fact]
    public void Resolver_with_no_configured_key_never_trusts_headers()
    {
        var (r, clock) = Resolver(key: null);
        var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var ctx = Ctx(r, "10.0.0.5", (ClientIpResolver.IpHeader, "203.0.113.7"), (ClientIpResolver.TimestampHeader, ts), (ClientIpResolver.SignatureHeader, Sig("", ts, "203.0.113.7")));
        Assert.Equal("peer:10.0.0.5", r.Resolve(ctx));
    }

    [Fact]
    public void Ipv6_collapses_to_its_64_and_mapped_ipv4_to_ipv4()
    {
        var (r, clock) = Resolver();
        const string key = "k-forwarding-key-for-unit-tests-0123456789";
        string Key(string ip)
        {
            var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
            return r.Resolve(Ctx(r, "10.0.0.1", (ClientIpResolver.IpHeader, ip), (ClientIpResolver.TimestampHeader, ts), (ClientIpResolver.SignatureHeader, Sig(key, ts, ip))));
        }
        Assert.Equal(Key("2001:db8:1:2:aaaa:bbbb:cccc:dddd"), Key("2001:db8:1:2:1:2:3:4"));      // same /64
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));                         // different /64
        Assert.Equal(Key("203.0.113.7"), Key("::ffff:203.0.113.7"));
        Assert.Equal("fwd:203.0.113.7", Key("203.0.113.7"));
        Assert.StartsWith("peer:", r.Resolve(Ctx(r, "10.0.0.1", (ClientIpResolver.IpHeader, "not-an-ip"))));
    }

    [Fact]
    public void Attempt_limiter_counts_per_key_resets_after_the_window_and_ignores_case()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var l = new AttemptLimiter(clock);
        var w = TimeSpan.FromMinutes(15);
        for (var i = 0; i < 5; i++) Assert.True(l.TryAcquire("signin", "Jane@Example.com", 5, w));
        Assert.False(l.TryAcquire("signin", "jane@example.com", 5, w));            // same key, different case
        Assert.True(l.TryAcquire("signin", "other@example.com", 5, w));            // other key unaffected
        Assert.True(l.TryAcquire("reset", "jane@example.com", 5, w));              // other bucket unaffected
        clock.Advance(w);
        Assert.True(l.TryAcquire("signin", "jane@example.com", 5, w));             // window passed
    }

    // ---------- optional: exception messages never reach the log ----------
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? "" : " ex:" + exception));
    }

    [Fact]
    public async Task Unhandled_exceptions_log_type_and_correlation_id_not_the_message()
    {
        var log = new ListLogger<ProblemExceptionMiddleware>();
        var mw = new ProblemExceptionMiddleware(_ => throw new InvalidOperationException("duplicate key for jane.doe@example.com"), log);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await mw.InvokeAsync(ctx);
        Assert.Equal(500, ctx.Response.StatusCode);
        var id = ctx.Response.Headers["X-Correlation-Id"].ToString();
        Assert.Matches("^[0-9a-f]{32}$", id);
        var line = Assert.Single(log.Lines);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains(id, line);
        Assert.DoesNotContain("jane.doe", line);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("internal_error", body);
        Assert.DoesNotContain("jane.doe", body);
    }

    // ---------- 5. the test database provider cannot fall back silently ----------
    [Theory]
    [InlineData("postgresql")]
    [InlineData("Postgres15")]
    [InlineData("sqllite")]
    [InlineData("mysql")]
    public void Unknown_test_providers_throw(string value)
        => Assert.Throws<InvalidOperationException>(() => TestFactory.ResolveProvider(value));

    [Theory]
    [InlineData(null, "sqlite")]
    [InlineData("", "sqlite")]
    [InlineData("SQLite", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Known_test_providers_resolve(string? value, string expected) => Assert.Equal(expected, TestFactory.ResolveProvider(value));

    [Fact]
    public void The_app_really_runs_on_the_requested_provider()
    {
        using var f = new TestFactory();
        using var scope = f.Services.CreateScope();
        var name = scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Database.ProviderName;
        Assert.Equal(f.Provider == "postgres" ? "Npgsql.EntityFrameworkCore.PostgreSQL" : "Microsoft.EntityFrameworkCore.Sqlite", name);
    }
}

/// <summary>Orchestrator review of slice 2a, item 7: Portal:BaseUrl is validated at start-up.</summary>
public class PortalBaseUrlValidationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IReadOnlyList<string> Validate(string baseUrl, string env = "Production")
    {
        var d = new Dictionary<string, string?>
        {
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40),
            ["Portal:BaseUrl"] = baseUrl, ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
            ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp",
        };
        return StartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env(env));
    }

    [Theory]
    [InlineData("https://portal.example")]
    [InlineData("https://portal.example/")]
    [InlineData("https://portal.example:8443")]
    public void Plain_https_origins_are_accepted(string url) => Assert.Empty(Validate(url));

    [Theory]
    [InlineData("http://portal.example", "https")]
    [InlineData("portal.example", "absolute")]
    [InlineData("/relative", "Portal:BaseUrl")]   // a file: URI on Linux, "not absolute" elsewhere: rejected either way
    [InlineData("ftp://portal.example", "https")]
    [InlineData("https://portal.example/api", "path")]
    [InlineData("https://portal.example/api/internal/v1", "path")]
    [InlineData("https://portal.example/?x=1", "query")]
    [InlineData("https://portal.example/#frag", "query or fragment")]
    [InlineData("https://user:pw@portal.example", "credentials")]
    public void Anything_but_an_https_origin_is_rejected_in_production(string url, string fragmentOfProblem)
        => Assert.Contains(Validate(url), p => p.Contains("Portal:BaseUrl") && p.Contains(fragmentOfProblem));

    [Theory]
    [InlineData("http://localhost:5000", true)]
    [InlineData("https://portal.test", true)]
    [InlineData("http://localhost/api", false)]
    [InlineData("ftp://portal.test", false)]
    [InlineData("https://portal.test/?a=b", false)]
    public void Development_clients_may_use_http_but_never_a_path_or_query(string url, bool ok)
        => Assert.Equal(ok, StartupValidator.BaseUrlProblem(url, requireHttps: false) is null);

    [Fact]
    public void Dev_and_testing_do_not_require_a_base_url() => Assert.Empty(Validate("", "Development"));

    [Theory]
    [InlineData("https://portal.test/api")]
    [InlineData("https://portal.test/?x=1")]
    public void The_client_registration_refuses_a_base_url_with_a_path_or_query_even_in_development(string url)
    {
        using var f = new TestFactory(new() { ["Portal:BaseUrl"] = url });
        using var scope = f.Services.CreateScope();
        var ex = Assert.ThrowsAny<Exception>(() => scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Portal.ISignedPortalClient>());
        Assert.Contains("Portal:BaseUrl", ex.ToString());
    }
}

