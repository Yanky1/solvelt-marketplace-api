using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
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
    private readonly string _provider = Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")?.ToLowerInvariant() ?? "sqlite";
    private readonly string _dbName = "mp_test_" + Guid.NewGuid().ToString("N");
    private readonly string _sqlitePath;
    private readonly Dictionary<string, string?> _extra = new();

    public TestFactory(Dictionary<string, string?>? config = null)
    {
        Portal = new FakePortal(DevDefaults.PortalInboundSecret, Clock);
        _sqlitePath = Path.Combine(Path.GetTempPath(), _dbName + ".db");
        if (config != null) foreach (var kv in config) _extra[kv.Key] = kv.Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.AddHttpClient<ISignedPortalClient, SignedPortalClient>().ConfigurePrimaryHttpMessageHandler(() => Portal);
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
