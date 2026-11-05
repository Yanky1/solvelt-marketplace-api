using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real app. Real SQLite (a temp file per factory) by default; set TEST_DB_PROVIDER=postgres and
/// TEST_POSTGRES_CONNECTION to run the same tests against PostgreSQL (a fresh database per factory).
/// Time comes from <see cref="Clock"/> so tests control it.
/// </summary>
public sealed class TestFactory : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    /// <summary>In-process seller portal; the app's <c>ISignedPortalClient</c> talks to it. Verifies signatures with the portal's rules.</summary>
    public FakePortal Portal { get; }
    /// <summary>Captures every log line (message, state values, scopes) written while the app runs.</summary>
    public CaptureLoggerProvider Logs { get; } = new();
    /// <summary>Answers the unsigned public GETs the app makes to the portal (proof-of-delivery images). Calls are recorded in <see cref="PublicCalls"/>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? PublicResponder { get; set; }
    /// <summary>The attachment scanner seam (decision M5-22): null allows every file, as the default does.</summary>
    public Func<byte[], string, bool>? ScannerVerdict { get; set; }
    /// <summary>Order reconcile observers (M3 compares refunds): what they were called with, and whether they should throw.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Order, Solvelt.Marketplace.Api.Portal.PortalOrderState State)> Observed { get; } = new();
    public bool ObserverThrows { get; set; }
    private sealed class TestObserver(TestFactory f) : Solvelt.Marketplace.Api.Fulfilment.IOrderReconcileObserver
    {
        public Task ObserveAsync(string marketplaceOrderId, Solvelt.Marketplace.Api.Portal.PortalOrderState state, CancellationToken ct)
        {
            f.Observed.Enqueue((marketplaceOrderId, state));
            return f.ObserverThrows ? throw new InvalidOperationException("observer boom") : Task.CompletedTask;
        }
    }
    private sealed class TestScanner(TestFactory f) : Solvelt.Marketplace.Api.Messaging.IAttachmentScanner
    {
        public Task<bool> AllowsAsync(byte[] bytes, string contentType, CancellationToken ct) => Task.FromResult(f.ScannerVerdict?.Invoke(bytes, contentType) ?? true);
    }
    public System.Collections.Concurrent.ConcurrentQueue<Uri> PublicCalls { get; } = new();
    private readonly string _provider = ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER"));

    /// <summary>"sqlite" (default when unset) or "postgres". Anything else throws: a typo must not silently run on SQLite.</summary>
    public static string ResolveProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sqlite" => "sqlite",
        "postgres" => "postgres",
        _ => throw new InvalidOperationException($"Unknown TEST_DB_PROVIDER '{value}'. Use 'sqlite' or 'postgres'."),
    };
    public string Provider => _provider;
    private readonly string _dbName = "mp_test_" + Guid.NewGuid().ToString("N");
    private readonly string _sqlitePath;
    private readonly Dictionary<string, string?> _extra = new();
    private readonly Action<IServiceCollection>? _services;

    public TestFactory(Dictionary<string, string?>? config = null, Action<IServiceCollection>? services = null)
    {
        _services = services;
        Portal = new FakePortal(DevDefaults.PortalInboundSecret, Clock);
        _sqlitePath = Path.Combine(Path.GetTempPath(), _dbName + ".db");
        if (config != null) foreach (var kv in config) _extra[kv.Key] = kv.Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(l => l.AddProvider(Logs));
        var settings = new Dictionary<string, string?> { ["Portal:BaseUrl"] = "https://portal.test", ["Portal:RetryBaseDelayMs"] = "0" };
        foreach (var kv in _extra) settings[kv.Key] = kv.Value;
        if (_provider == "postgres")
        {
            var admin = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
                ?? throw new InvalidOperationException("TEST_POSTGRES_CONNECTION is required when TEST_DB_PROVIDER=postgres");
            using (var c = new NpgsqlConnection(admin))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
                cmd.ExecuteNonQuery();
            }
            settings["Database:Provider"] = "Postgres";
            settings["ConnectionStrings:Default"] = new NpgsqlConnectionStringBuilder(admin) { Database = _dbName }.ConnectionString;
        }
        else
        {
            settings["Database:Provider"] = "Sqlite";
            settings["ConnectionStrings:Default"] = $"Data Source={_sqlitePath}";
        }
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        builder.ConfigureServices(s =>
        {
            _services?.Invoke(s);
            s.RemoveAll<Solvelt.Marketplace.Api.Messaging.IAttachmentScanner>();
            s.AddSingleton<Solvelt.Marketplace.Api.Messaging.IAttachmentScanner>(new TestScanner(this));
            s.AddSingleton<Solvelt.Marketplace.Api.Fulfilment.IOrderReconcileObserver>(new TestObserver(this));
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.AddHttpClient<ISignedPortalClient, SignedPortalClient>().ConfigurePrimaryHttpMessageHandler(() => Portal);
            s.AddHttpClient<Solvelt.Marketplace.Api.Fulfilment.ProofFetcher>().ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler(r => { PublicCalls.Enqueue(r.RequestUri!); return PublicResponder?.Invoke(r) ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (_provider == "postgres")
            {
                NpgsqlConnection.ClearAllPools();
                using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"));
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
                cmd.ExecuteNonQuery();
            }
            else foreach (var f in new[] { _sqlitePath, _sqlitePath + "-shm", _sqlitePath + "-wal" }) File.Delete(f);
        }
        catch { /* best-effort cleanup of test databases */ }
    }
}

public sealed class CaptureLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public IReadOnlyList<string> Lines => _lines.ToArray();
    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class Capture(CaptureLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(category).Append(" | ").Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs) foreach (var kv in kvs) sb.Append(" | ").Append(kv.Key).Append('=').Append(kv.Value);
            owner._scopes.ForEachScope((scope, sbArg) =>
            {
                sbArg.Append(" | scope:").Append(scope);
                if (scope is IEnumerable<KeyValuePair<string, object?>> sk) foreach (var kv in sk) sbArg.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            }, sb);
            if (exception is not null) sb.Append(" | ex:").Append(exception);
            owner._lines.Enqueue(sb.ToString());
        }
    }
}

public sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(f(request));
}
