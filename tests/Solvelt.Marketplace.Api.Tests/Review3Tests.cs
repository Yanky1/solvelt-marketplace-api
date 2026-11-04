using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Orchestrator review 3 (of 0ca712b): logging independent of appsettings.json, forwarding observability and window, limiter bounds.</summary>
public class Review3LoggingTests
{
    private static readonly string[] Banned = ["jane.doe", "example.com", "SECRETTOKEN123", "email="];

    private static async Task HitUrls(HttpClient c)
    {
        await c.GetAsync("/api/v1/ping?email=jane.doe@example.com&token=SECRETTOKEN123");
        await c.GetAsync("/verify/jane.doe@example.com/SECRETTOKEN123");
    }

    private static void AssertClean(IEnumerable<string> lines)
    {
        foreach (var banned in Banned)
        {
            var hits = lines.Where(l => l.Contains(banned, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            Assert.True(hits.Count == 0, $"'{banned}' leaked into logs:\n" + string.Join("\n", hits));
        }
    }

    [Fact]
    public async Task Configuration_cannot_loosen_the_log_filters()
    {
        // Everything at Trace in configuration (as if appsettings.json were missing or edited): the filters set in code still win.
        using var f = new TestFactory(new()
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Routing"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Routing.Matching.DfaMatcher"] = "Trace",
        });
        await HitUrls(f.CreateClient());
        Assert.NotEmpty(f.Logs.Lines);
        AssertClean(f.Logs.Lines);
    }

    [Fact]
    public void The_enforcement_removes_every_looser_rule_under_the_sensitive_prefixes_and_keeps_unrelated_ones()
    {
        var o = new LoggerFilterOptions();
        o.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.Routing.Matching.DfaMatcher", LogLevel.Trace, null));   // more specific than any coarse rule
        o.Rules.Add(new LoggerFilterRule("Console", "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Trace, null));        // provider-specific
        o.Rules.Add(new LoggerFilterRule(null, "Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information, null));
        o.Rules.Add(new LoggerFilterRule(null, "System.Net.Http.HttpClient.ISignedPortalClient.ClientHandler", LogLevel.Debug, null));
        o.Rules.Add(new LoggerFilterRule(null, "Solvelt.Marketplace", LogLevel.Debug, null));
        o.Rules.Add(new LoggerFilterRule(null, null, LogLevel.Trace, null));                                                    // the Default rule
        SafeLogging.Enforce(o);
        var byCategory = o.Rules.Where(r => r.CategoryName is not null).ToLookup(r => r.CategoryName!);
        Assert.Single(byCategory["Microsoft.AspNetCore.Hosting.Diagnostics"]);
        Assert.Equal(LogLevel.None, byCategory["Microsoft.AspNetCore.Hosting.Diagnostics"].Single().LogLevel);
        Assert.Equal(LogLevel.Warning, byCategory["Microsoft.AspNetCore"].Single().LogLevel);
        Assert.Equal(LogLevel.Warning, byCategory["Microsoft.EntityFrameworkCore"].Single().LogLevel);
        Assert.Equal(LogLevel.Warning, byCategory["System.Net.Http.HttpClient"].Single().LogLevel);
        Assert.DoesNotContain(o.Rules, r => r.CategoryName is "Microsoft.AspNetCore.Routing.Matching.DfaMatcher" or "Microsoft.EntityFrameworkCore.Database.Command"
                                            or "System.Net.Http.HttpClient.ISignedPortalClient.ClientHandler");
        Assert.Equal(LogLevel.Debug, byCategory["Solvelt.Marketplace"].Single().LogLevel);   // unrelated rules are left alone
        Assert.Contains(o.Rules, r => r.CategoryName is null && r.LogLevel == LogLevel.Trace);
    }

    [Theory]
    [InlineData("everything-at-trace")]            // the original scenario
    [InlineData("console-provider-default-trace")] // a) a provider-specific rule beats provider-less ones
    [InlineData("wildcard-dfamatcher")]            // b) a wildcard rule does not start with the prefix, and its longer name wins
    [InlineData("uppercase-routing")]              // c) removal was case sensitive, category matching is not
    public async Task Running_the_published_dll_from_another_directory_does_not_log_urls_whatever_the_logging_configuration_says(string scenario)
    {
        // The review's scenario: `dotnet <path>/Solvelt.Marketplace.Api.dll` from a directory with no appsettings.json, plus ONE logging env var.
        var dll = Path.Combine(AppContext.BaseDirectory, "Solvelt.Marketplace.Api.dll");
        Assert.True(File.Exists(dll));
        var cwd = Directory.CreateTempSubdirectory("solvelt-cwd-").FullName;
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:0")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Default"] = $"Data Source={Path.Combine(cwd, "cwd.db")}";
        switch (scenario)
        {
            case "everything-at-trace": psi.Environment["Logging__LogLevel__Default"] = "Trace"; psi.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Trace"; break;
            case "console-provider-default-trace": psi.Environment["Logging__Console__LogLevel__Default"] = "Trace"; break;
            case "wildcard-dfamatcher": psi.Environment["Logging__LogLevel__*Routing.Matching.DfaMatcher"] = "Trace"; break;
            case "uppercase-routing": psi.Environment["Logging__LogLevel__MICROSOFT.ASPNETCORE.ROUTING"] = "Trace"; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        using var p = Process.Start(psi)!;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var listening = new TaskCompletionSource<string>();
        void OnLine(string? line)
        {
            if (line is null) return;
            output.Enqueue(line);
            const string marker = "Now listening on: ";
            var i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) listening.TrySetResult(line[(i + marker.Length)..].Trim());
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try
        {
            var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(60));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(url) };
            await HitUrls(http);
            await Task.Delay(500);
        }
        finally { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); try { Directory.Delete(cwd, true); } catch { } }
        Assert.Contains(output, l => l.Contains("Now listening"));   // not vacuous
        AssertClean(output);
    }

    [Theory]
    [InlineData("Logging:CaptureLoggerProvider:LogLevel:Default", "Trace")]                                     // a) provider-specific default
    [InlineData("Logging:CaptureLoggerProvider:LogLevel:Microsoft.AspNetCore.Routing.Matching.DfaMatcher", "Trace")]
    [InlineData("Logging:LogLevel:*Routing.Matching.DfaMatcher", "Trace")]                                      // b) wildcard
    [InlineData("Logging:LogLevel:MICROSOFT.ASPNETCORE.ROUTING", "Trace")]                                      // c) different casing
    [InlineData("Logging:LogLevel:microsoft.aspnetcore.hosting.diagnostics", "Trace")]
    [InlineData("Logging:CaptureLoggerProvider:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "Trace")]
    public async Task No_filter_configuration_can_bring_back_the_url_logging(string key, string level)
    {
        using var f = new TestFactory(new() { ["Logging:LogLevel:Default"] = "Trace", [key] = level });
        await HitUrls(f.CreateClient());
        Assert.NotEmpty(f.Logs.Lines);
        AssertClean(f.Logs.Lines);
    }

    private sealed class Recorder : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public int Scopes;
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }
        private sealed class L(Recorder r, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { r.Scopes++; return null; }
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => r.Lines.Add($"{category}|{logLevel}|{formatter(state, exception)}");
        }
    }

    private static (ILoggerFactory Factory, Recorder Rec) Guarded()
    {
        var rec = new Recorder();
        var real = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(rec));
        return (new SafeLoggerFactory(real), rec);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("microsoft.aspnetcore.hosting.diagnostics")]
    [InlineData("Microsoft.AspNetCore.Routing")]
    [InlineData("Microsoft.AspNetCore.Routing.Matching.DfaMatcher")]
    [InlineData("MICROSOFT.ASPNETCORE.ROUTING.EndpointMiddleware")]
    public void Silent_categories_log_nothing_at_any_level_and_open_no_scope(string category)
    {
        var (factory, rec) = Guarded();
        var log = factory.CreateLogger(category);
        foreach (var level in Enum.GetValues<LogLevel>().Where(l => l != LogLevel.None))
        {
            Assert.False(log.IsEnabled(level));
            log.Log(level, "request /verify/jane.doe@example.com/SECRETTOKEN123");
        }
        using (log.BeginScope(new Dictionary<string, object> { ["RequestPath"] = "/verify/jane.doe@example.com" })) { }   // the no-op scope: nothing reaches a provider
        Assert.Empty(rec.Lines);
        Assert.Equal(0, rec.Scopes);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel")]
    [InlineData("microsoft.aspnetcore.cors")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient.ISignedPortalClient.ClientHandler")]
    public void Broad_categories_log_warnings_and_above_only_and_open_no_scope(string category)
    {
        var (factory, rec) = Guarded();
        var log = factory.CreateLogger(category);
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information })
        {
            Assert.False(log.IsEnabled(level));
            log.Log(level, "below warning");
        }
        Assert.True(log.IsEnabled(LogLevel.Warning));
        log.Log(LogLevel.Warning, "a warning");
        log.Log(LogLevel.Error, "an error");
        Assert.Equal([$"{category}|Warning|a warning", $"{category}|Error|an error"], rec.Lines);
        Assert.Null(log.BeginScope("scope"));
        Assert.Equal(0, rec.Scopes);
    }

    [Fact]
    public void Our_own_and_unrelated_categories_are_untouched()
    {
        var (factory, rec) = Guarded();
        var ours = factory.CreateLogger("Solvelt.Marketplace.Api.Catalog.CatalogSyncService");
        ours.Log(LogLevel.Information, "hello");
        ours.Log(LogLevel.Debug, "debug");
        using (ours.BeginScope("scope")) { }
        Assert.Equal(["Solvelt.Marketplace.Api.Catalog.CatalogSyncService|Information|hello", "Solvelt.Marketplace.Api.Catalog.CatalogSyncService|Debug|debug"], rec.Lines);
        Assert.Equal(1, rec.Scopes);
        Assert.Same(factory.CreateLogger("Solvelt.X"), factory.CreateLogger("Solvelt.X"));   // loggers are cached per category
        Assert.Same(factory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel"), factory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel"));   // including the wrapped ones (the inner factory caches only its own)
    }

    [Fact]
    public void The_enforcement_also_removes_wildcard_and_differently_cased_rules()
    {
        var o = new LoggerFilterOptions();
        o.Rules.Add(new LoggerFilterRule(null, "*Routing.Matching.DfaMatcher", LogLevel.Trace, null));
        o.Rules.Add(new LoggerFilterRule(null, "MICROSOFT.ASPNETCORE.ROUTING", LogLevel.Trace, null));
        o.Rules.Add(new LoggerFilterRule("Console", "microsoft.aspnetcore.hosting.diagnostics", LogLevel.Trace, null));
        o.Rules.Add(new LoggerFilterRule(null, "Solvelt.*", LogLevel.Debug, null));
        SafeLogging.Enforce(o);
        Assert.DoesNotContain(o.Rules, r => r.CategoryName is "*Routing.Matching.DfaMatcher" or "MICROSOFT.ASPNETCORE.ROUTING" or "microsoft.aspnetcore.hosting.diagnostics" or "Solvelt.*");
        Assert.Contains(o.Rules, r => r.CategoryName == "Microsoft.AspNetCore.Routing" && r.LogLevel == LogLevel.None);
    }

}

public class Review3ForwardingTests
{
    private const string Key = "k-forwarding-key-for-unit-tests-0123456789";

    private sealed class ListLogger : ILogger<ClientIpResolver>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static (ClientIpResolver R, FakeTimeProvider Clock, ListLogger Log) Rig(string? key = Key)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var log = new ListLogger();
        return (new ClientIpResolver(Options.Create(new WebOptions { ForwardingKey = key }), clock, log), clock, log);
    }

    private static string Sig(string key, string ts, string ip) => ReviewSig(key, ts, ip);
    private static string ReviewSig(string key, string ts, string ip)
        => "v1=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes($"{ts}\n{ip}"))).ToLowerInvariant();

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Ctx(ClientIpResolver r, string? ip, string? ts, string? sig)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(r).BuildServiceProvider() };
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        if (ip is not null) ctx.Request.Headers[ClientIpResolver.IpHeader] = ip;
        if (ts is not null) ctx.Request.Headers[ClientIpResolver.TimestampHeader] = ts;
        if (sig is not null) ctx.Request.Headers[ClientIpResolver.SignatureHeader] = sig;
        return ctx;
    }

    private static string Ts(FakeTimeProvider c, int skew = 0) => (c.GetUtcNow().ToUnixTimeSeconds() + skew).ToString();

    // ---------- F4 / O-09: 60 s window ----------
    [Fact]
    public void The_forwarding_window_is_60_seconds()
        => Assert.Equal(60, ClientIpResolver.WindowSeconds);

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(-60, true)]
    [InlineData(61, false)]
    [InlineData(-61, false)]
    [InlineData(299, false)]
    public void The_window_boundary_is_exactly_60_seconds(int skew, bool accepted)
    {
        var (r, clock, _) = Rig();
        var ts = Ts(clock, skew);
        var key = r.Resolve(Ctx(r, "203.0.113.9", ts, Sig(Key, ts, "203.0.113.9")));
        Assert.Equal(accepted ? "fwd:203.0.113.9" : "peer:10.0.0.1", key);
        Assert.Equal(accepted ? 0 : 1, r.InvalidCount);
    }

    // ---------- F1: failures are counted and logged (reason only), absent headers are not failures ----------
    [Fact]
    public void Requests_without_forwarding_headers_and_valid_ones_are_not_failures()
    {
        var (r, clock, log) = Rig();
        r.Resolve(Ctx(r, null, null, null));
        var ts = Ts(clock);
        r.Resolve(Ctx(r, "203.0.113.9", ts, Sig(Key, ts, "203.0.113.9")));
        Assert.Equal(0, r.InvalidCount);
        Assert.Empty(log.Lines);
    }

    [Theory]
    [InlineData("bad_signature")]
    [InlineData("stale")]
    [InlineData("incomplete")]
    [InlineData("malformed")]
    [InlineData("not_configured")]
    public void Each_failure_reason_is_counted_and_logged_without_values(string reason)
    {
        var (r, clock, log) = Rig(key: reason == "not_configured" ? null : Key);
        var ts = Ts(clock);
        var ctx = reason switch
        {
            "bad_signature" => Ctx(r, "203.0.113.77", ts, "v1=" + new string('0', 64)),
            "stale" => Ctx(r, "203.0.113.77", Ts(clock, -120), Sig(Key, Ts(clock, -120), "203.0.113.77")),
            "incomplete" => Ctx(r, "203.0.113.77", null, null),
            "malformed" => Ctx(r, "not-an-ip-9999", ts, Sig(Key, ts, "not-an-ip-9999")),
            _ => Ctx(r, "203.0.113.77", ts, Sig(Key, ts, "203.0.113.77")),
        };
        Assert.Equal("peer:10.0.0.1", r.Resolve(ctx));
        Assert.Equal(1, r.InvalidCount);
        var line = Assert.Single(log.Lines);
        Assert.StartsWith("Warning:", line);
        Assert.Contains($"({reason})", line);
        Assert.DoesNotContain("203.0.113.77", line);
        Assert.DoesNotContain("not-an-ip", line);
        Assert.DoesNotContain("v1=", line);
    }

    [Fact]
    public void The_warning_is_rate_limited_to_one_a_minute_but_every_failure_is_counted()
    {
        var (r, clock, log) = Rig();
        for (var i = 0; i < 50; i++) r.Resolve(Ctx(r, "203.0.113.9", Ts(clock), "v1=bad"));
        Assert.Equal(50, r.InvalidCount);
        Assert.Single(log.Lines);
        clock.Advance(TimeSpan.FromSeconds(59));
        r.Resolve(Ctx(r, "203.0.113.9", Ts(clock), "v1=bad"));
        Assert.Single(log.Lines);
        clock.Advance(TimeSpan.FromSeconds(2));
        r.Resolve(Ctx(r, "203.0.113.9", Ts(clock), "v1=bad"));
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("52 such requests", log.Lines[1]);
    }

    [Fact]
    public void A_request_is_resolved_and_counted_once_however_often_it_is_asked()
    {
        var (r, _, log) = Rig();
        var ctx = Ctx(r, "203.0.113.9", "1", "v1=bad");
        Assert.Equal(r.Resolve(ctx), r.Resolve(ctx));
        Assert.Equal(1, r.InvalidCount);
        Assert.Single(log.Lines);
    }

    [Fact]
    public async Task Health_exposes_the_count_and_nothing_personal()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        async Task<long> Invalid() => JsonDocument.Parse(await (await c.GetAsync("/health")).Content.ReadAsStringAsync()).RootElement.GetProperty("forwarding").GetProperty("invalid").GetInt64();
        Assert.Equal(0, await Invalid());
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        req.Headers.Add(ClientIpResolver.IpHeader, "203.0.113.9");
        req.Headers.Add(ClientIpResolver.TimestampHeader, f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString());
        req.Headers.Add(ClientIpResolver.SignatureHeader, "v1=wrong");
        await c.SendAsync(req);
        await c.GetAsync("/api/v1/ping");   // no headers: not a failure
        Assert.Equal(1, await Invalid());
        var body = await (await c.GetAsync("/health")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("203.0.113.9", body);
        Assert.DoesNotContain("wrong", body);
    }
}

public class Review3AttemptLimiterTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Expired_windows_are_pruned_at_most_every_30s_not_on_every_call()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock);
        for (var i = 0; i < 1000; i++) l.TryAcquire("signin", $"user{i}@example.com", 5, TimeSpan.FromSeconds(10));
        Assert.Equal(1, l.PruneRuns);          // once, not 1000 times
        clock.Advance(TimeSpan.FromSeconds(29));
        l.TryAcquire("signin", "x@example.com", 5, TimeSpan.FromSeconds(10));
        Assert.Equal(1, l.PruneRuns);          // all 1000 are expired but the prune interval has not passed
        Assert.Equal(1001, l.TrackedKeys);
        clock.Advance(TimeSpan.FromSeconds(2));
        l.TryAcquire("signin", "y@example.com", 5, TimeSpan.FromSeconds(10));
        Assert.Equal(2, l.PruneRuns);
        Assert.Equal(2, l.TrackedKeys);        // the 1000 expired windows are gone; x (started at 29 s) and y are still inside their 10 s windows
    }

    [Fact]
    public void The_table_is_hard_capped_and_evicts_in_batches()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 100);
        for (var i = 0; i < 99; i++) { l.TryAcquire("signin", $"filler{i}", 5, TimeSpan.FromMinutes(15)); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        // an account under attack: its window is recent, so it must survive the eviction
        Assert.True(l.TryAcquire("signin", "victim", 2, TimeSpan.FromMinutes(15)));
        Assert.True(l.TryAcquire("signin", "victim", 2, TimeSpan.FromMinutes(15)));
        Assert.False(l.TryAcquire("signin", "victim", 2, TimeSpan.FromMinutes(15)));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 150; i++) { l.TryAcquire("signin", $"spray{i}", 5, TimeSpan.FromMinutes(15)); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.InRange(l.TrackedKeys, 1, 100);
        Assert.True(l.Evictions >= 1);
        // the sprayed keys are the newest: the oldest fillers went first, the victim (older than the spray) may go too;
        // what matters is the bound and that the newest key is tracked
        Assert.True(l.TryAcquire("signin", "newest", 1, TimeSpan.FromMinutes(15)));
        Assert.False(l.TryAcquire("signin", "newest", 1, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void A_recently_attacked_key_survives_an_eviction_that_removes_older_ones()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 100);
        for (var i = 0; i < 95; i++) { l.TryAcquire("signin", $"old{i}", 5, TimeSpan.FromMinutes(15)); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        l.TryAcquire("signin", "victim", 1, TimeSpan.FromMinutes(15));
        Assert.False(l.TryAcquire("signin", "victim", 1, TimeSpan.FromMinutes(15)));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 6; i++) l.TryAcquire("signin", $"new{i}", 5, TimeSpan.FromMinutes(15));   // 102 keys: forces one eviction of the oldest tenth
        Assert.True(l.Evictions >= 1);
        Assert.False(l.TryAcquire("signin", "victim", 1, TimeSpan.FromMinutes(15)));   // still blocked: its window survived
    }

    [Fact]
    public void Eviction_is_batched_so_a_spray_does_not_sort_the_table_on_every_insert()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 100);
        for (var i = 0; i < 1000; i++) { l.TryAcquire("signin", $"spray{i}", 5, TimeSpan.FromMinutes(15)); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.InRange(l.TrackedKeys, 85, 100);
        Assert.InRange(l.Evictions, 1, 150);   // about one eviction per tenth of the cap (one per insert would be ~900)
    }

    [Fact]
    public void A_key_under_brute_force_survives_a_spray_of_more_keys_than_the_table_holds_even_when_it_is_the_oldest()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 100);
        var w = TimeSpan.FromMinutes(15);
        // the victim is attacked FIRST, so its window is the oldest in the table, and it reaches its limit
        for (var i = 0; i < 6; i++) l.TryAcquire("signin", "victim@example.com", 5, w);
        Assert.False(l.TryAcquire("signin", "victim@example.com", 5, w));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        // then a spray of ten times the table size in distinct single-hit keys
        for (var i = 0; i < 1000; i++) { l.TryAcquire("signin", $"spray{i}@example.com", 5, w); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.True(l.Evictions > 1);
        Assert.False(l.TryAcquire("signin", "victim@example.com", 5, w));          // still blocked: its count was not reset by an eviction
    }

    [Fact]
    public void Among_equal_counts_the_oldest_window_goes_first()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 100);
        var w = TimeSpan.FromMinutes(15);
        void Hit2(string key) { l.TryAcquire("signin", key, 5, w); l.TryAcquire("signin", key, 5, w); clock.Advance(TimeSpan.FromMilliseconds(1)); }
        Hit2("oldest");
        for (var i = 0; i < 9; i++) Hit2($"a{i}");
        Hit2("newer");                                              // the 11th oldest window ...
        for (var i = 0; i < 88; i++) Hit2($"b{i}");                 // ... 99 keys in all, each with count 2
        l.TryAcquire("signin", "trigger1", 5, w);                   // 100 keys: at the cap
        Assert.Equal(0, l.Evictions);
        l.TryAcquire("signin", "trigger2", 5, w);                   // 101: evicts down to 90, i.e. 11 keys: the two count-1 triggers, then the 9 oldest of the count-2 keys
        Assert.Equal(1, l.Evictions);
        Assert.Equal(90, l.TrackedKeys);
        // "oldest" was evicted (its count starts again), "newer" was not (its count continues from 2)
        for (var i = 0; i < 3; i++) Assert.True(l.TryAcquire("signin", "oldest", 3, w));   // counts 1, 2, 3
        Assert.False(l.TryAcquire("signin", "oldest", 3, w));                              // count 4
        Assert.True(l.TryAcquire("signin", "newer", 3, w));                                // count 3: continued from 2
        Assert.False(l.TryAcquire("signin", "newer", 3, w));                               // count 4
    }

    [Fact]
    public void The_key_counter_always_agrees_with_the_table_through_adds_prunes_and_evictions()
    {
        var clock = Clock();
        var l = new AttemptLimiter(clock, maxKeys: 50);
        for (var i = 0; i < 40; i++) l.TryAcquire("a", $"k{i}", 5, TimeSpan.FromSeconds(10));
        Assert.Equal(l.ActualKeys, l.TrackedKeys);
        Assert.Equal(40, l.TrackedKeys);
        for (var i = 0; i < 40; i++) l.TryAcquire("a", $"k{i}", 5, TimeSpan.FromSeconds(10));    // updating existing keys does not count them again
        Assert.Equal(40, l.TrackedKeys);
        for (var i = 40; i < 120; i++) { clock.Advance(TimeSpan.FromMilliseconds(1)); l.TryAcquire("a", $"k{i}", 5, TimeSpan.FromSeconds(10)); }
        Assert.True(l.Evictions >= 1);
        Assert.Equal(l.ActualKeys, l.TrackedKeys);
        Assert.InRange(l.TrackedKeys, 1, 50);
        clock.Advance(TimeSpan.FromMinutes(1));                                                  // everything expires, the next call prunes
        l.TryAcquire("a", "fresh", 5, TimeSpan.FromSeconds(10));
        Assert.Equal(1, l.TrackedKeys);
        Assert.Equal(l.ActualKeys, l.TrackedKeys);
    }

    [Fact]
    public void The_key_counter_stays_exact_under_parallel_load_and_eviction_never_throws()
    {
        // Eviction snapshots a table that other threads are adding to (LINQ over the dictionary throws ArgumentException when a key lands between sizing
        // and filling its buffer): a high cap-to-spray ratio and many threads starting together make that collision near certain.
        var l = new AttemptLimiter(Clock(), maxKeys: 200);
        const int threads = 16, perThread = 6000;
        using var start = new Barrier(threads);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            try { for (var i = 0; i < perThread; i++) l.TryAcquire("signin", $"t{t}-{i}@example.com", 5, TimeSpan.FromMinutes(15)); }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        Assert.True(failures.IsEmpty, failures.TryPeek(out var first) ? first.ToString() : "");
        Assert.Equal(l.ActualKeys, l.TrackedKeys);                                               // no add or remove was lost or counted twice
        Assert.InRange(l.TrackedKeys, 1, 200 + threads * 4);                                     // bounded (a few adds can land while an eviction runs)
        Assert.True(l.Evictions >= 1);
    }

    [Fact]
    public void The_same_key_hit_from_many_threads_loses_no_count()
    {
        // 16 threads x 5000 hits on ONE key, started together. A compare-and-swap loop loses nothing: after N hits the (N+1)th is the first to exceed a
        // limit of N. An unconditional write would lose increments under contention and let it through.
        var l = new AttemptLimiter(Clock());
        const int threads = 16, perThread = 5000, total = threads * perThread;
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perThread; i++) l.TryAcquire("signin", "same@example.com", total, TimeSpan.FromMinutes(15));
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        Assert.False(l.TryAcquire("signin", "same@example.com", total, TimeSpan.FromMinutes(15)));   // hit number total + 1
        Assert.Equal(1, l.TrackedKeys);
    }

    [Fact]
    public void The_limit_is_enforced_exactly_for_a_key_hit_concurrently()
    {
        var l = new AttemptLimiter(Clock());
        var allowed = 0;
        using var start = new Barrier(16);
        var workers = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 200; i++) if (l.TryAcquire("signin", "same@example.com", 50, TimeSpan.FromMinutes(15))) Interlocked.Increment(ref allowed);
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        Assert.Equal(50, allowed);                                                               // exactly the limit, whatever the interleaving
    }
}

