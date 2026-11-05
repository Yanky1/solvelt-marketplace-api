using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Decision M1-83: a database made by an earlier build (EnsureCreated never alters) is compared with the model; the cause of the 500s is logged once and the fix script repairs it.</summary>
public class SchemaCheckTests
{
    private static string? Admin => TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres" ? Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") : null;

    private sealed class Old : IDisposable
    {
        public MarketplaceDb Db { get; }
        private readonly Action _drop;
        public Old(string provider)
        {
            if (provider == "postgres")
            {
                var admin = Admin!;
                var name = "mp_chk_" + Guid.NewGuid().ToString("N");
                using (var c = new NpgsqlConnection(admin)) { c.Open(); using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c); cmd.ExecuteNonQuery(); }
                Db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString).Options);
                _drop = () => { NpgsqlConnection.ClearAllPools(); using var c = new NpgsqlConnection(admin); c.Open(); using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", c); cmd.ExecuteNonQuery(); };
            }
            else
            {
                var path = Path.Combine(Path.GetTempPath(), "mp_chk_" + Guid.NewGuid().ToString("N") + ".db");
                Db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseSqlite($"Data Source={path}").Options);
                _drop = () => { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); };
            }
            Db.Database.EnsureCreated();
        }
        public void Run(string sql) => Db.Database.ExecuteSqlRaw(sql);
        public void Dispose() { Db.Dispose(); _drop(); }
    }

    [Theory]
    [InlineData("sqlite")]
    public void A_database_made_from_the_current_model_has_no_differences(string provider)
    {
        using var o = new Old(provider);
        Assert.Empty(SchemaCheck.Differences(o.Db));
    }

    [Theory]
    [InlineData("postgres")]
    public void A_database_made_from_the_current_model_has_no_differences_on_PostgreSQL(string provider)
    {
        if (Admin is null) return;                                   // PostgreSQL only: CI's PostgreSQL job runs it (README, Test)
        A_database_made_from_the_current_model_has_no_differences(provider);
    }

    [Theory]
    [InlineData("sqlite")]
    public void A_database_from_an_earlier_build_is_described_and_the_fix_script_repairs_it(string provider)
    {
        using var o = new Old(provider);
        var q = provider == "postgres" ? "\"" : "\"";
        o.Run($"ALTER TABLE buyer_accounts DROP COLUMN {q}SessionEpoch{q}");                       // a column added later
        o.Run($"ALTER TABLE buyer_sessions DROP COLUMN {q}Epoch{q}");
        o.Run($"DROP TABLE webauthn_challenges");                                                  // a table added later
        o.Run($"DROP TABLE buyer_passkeys");
        o.Run($"DROP INDEX {q}IX_buyer_sessions_AccountId{q}");                                     // an index
        var diffs = SchemaCheck.Differences(o.Db);
        Assert.Contains("column buyer_accounts.SessionEpoch is missing", diffs);
        Assert.Contains("column buyer_sessions.Epoch is missing", diffs);
        Assert.Contains("table buyer_passkeys is missing", diffs);
        Assert.Contains("table webauthn_challenges is missing", diffs);
        Assert.Contains("index IX_buyer_sessions_AccountId is missing", diffs);

        var script = SchemaCheck.FixScript(o.Db);
        Assert.Contains("SessionEpoch", script);
        Assert.Contains("buyer_passkeys", script);
        o.Run(script);
        Assert.Empty(SchemaCheck.Differences(o.Db));
    }

    [Theory]
    [InlineData("postgres")]
    public void A_database_from_an_earlier_build_is_described_and_the_fix_script_repairs_it_on_PostgreSQL(string provider)
    {
        if (Admin is null) return;
        A_database_from_an_earlier_build_is_described_and_the_fix_script_repairs_it(provider);
    }

    [Fact]
    public void A_missing_column_on_a_table_with_rows_gets_the_types_default_so_existing_rows_take_it()
    {
        using var o = new Old("sqlite");
        o.Db.BuyerAccounts.Add(new BuyerAccount { Id = Guid.NewGuid(), Email = "a@example.test", DisplayName = "A", RelayEmail = "r@relay.test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        o.Db.SaveChanges();
        o.Run("ALTER TABLE buyer_accounts DROP COLUMN \"SessionEpoch\"");
        o.Run(SchemaCheck.FixScript(o.Db));
        Assert.Equal(0L, o.Db.BuyerAccounts.AsNoTracking().Single().SessionEpoch);                 // the row survived and has the default
    }

    [Fact]
    public void A_column_of_the_wrong_type_or_nullability_is_listed_as_manual_on_postgres()
    {
        if (Admin is null) return;                                   // PostgreSQL only: CI's PostgreSQL job runs it (README, Test)
        using var o = new Old("postgres");
        o.Run("ALTER TABLE buyer_accounts ALTER COLUMN \"DisplayName\" TYPE character varying(5)");
        var diffs = SchemaCheck.Differences(o.Db);
        Assert.Contains(diffs, d => d.StartsWith("column buyer_accounts.DisplayName is character varying(5)", StringComparison.Ordinal));
        Assert.Contains("-- MANUAL: column buyer_accounts.DisplayName", SchemaCheck.FixScript(o.Db));
    }

    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Start_up_logs_one_critical_line_with_the_differences_and_nothing_when_all_is_well()
    {
        using var o = new Old("sqlite");
        var log = new Capture();
        SchemaCheck.LogAtStartup(o.Db, log);
        Assert.Empty(log.Lines);
        o.Run("ALTER TABLE buyer_accounts DROP COLUMN \"SessionEpoch\"");
        SchemaCheck.LogAtStartup(o.Db, log);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Contains("column buyer_accounts.SessionEpoch is missing", line.Text);
        Assert.Contains("tools/SchemaTool", line.Text);
    }

    [Fact]
    public void Start_up_never_throws_when_the_database_cannot_be_read()
    {
        var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseSqlite("Data Source=/nonexistent-dir/none.db").Options);
        var log = new Capture();
        SchemaCheck.LogAtStartup(db, log);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Lines).Level);
    }

    // ---------- review lows (M1-83) ----------

    [Fact]
    public void A_mistyped_SQLite_path_is_not_created_when_reading()
    {
        var path = Path.Combine(Path.GetTempPath(), "mp_typo_" + Guid.NewGuid().ToString("N") + ".db");
        using (var db = SchemaCheck.OpenReadOnly("sqlite", $"Data Source={path}"))
            Assert.ThrowsAny<Exception>(() => SchemaCheck.Differences(db));
        Assert.False(File.Exists(path));                                                       // a read-write open would have made an empty database here
    }

    [Fact]
    public void A_read_only_context_cannot_change_a_SQLite_database()
    {
        using var o = new Old("sqlite");
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(o.Db.Database.GetConnectionString()).DataSource;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var ro = SchemaCheck.OpenReadOnly("sqlite", $"Data Source={path}");
        Assert.Empty(SchemaCheck.Differences(ro));
        Assert.ThrowsAny<Exception>(() => ro.Database.ExecuteSqlRaw("CREATE TABLE should_not_exist (x int)"));
    }

    [Fact]
    public void A_read_only_context_cannot_change_a_PostgreSQL_database()
    {
        if (Admin is null) return;                                   // PostgreSQL only: CI's PostgreSQL job runs it (README, Test)
        using var o = new Old("postgres");
        using var ro = SchemaCheck.OpenReadOnly("postgres", o.Db.Database.GetConnectionString()!);
        Assert.Empty(SchemaCheck.Differences(ro));
        Assert.ThrowsAny<Exception>(() => ro.Database.ExecuteSqlRaw("CREATE TABLE should_not_exist (x int)"));
    }

    [Theory]
    [InlineData("Failed to connect. Host=db.internal;Port=5432;Password=hunter2;Username=app", "hunter2")]
    [InlineData("login failed for Pwd = s3cret; User ID=sa", "s3cret")]
    [InlineData("unable to open Data Source=/srv/secret/marketplace.db", "/srv/secret")]
    public void The_failure_reason_in_the_log_carries_no_connection_string_values(string message, string secret)
    {
        var text = SchemaCheck.Sanitise(message);
        Assert.DoesNotContain(secret, text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void The_failure_reason_is_kept_short_and_on_one_line_and_tells_the_causes_apart()
    {
        Assert.Equal("permission denied for table x", SchemaCheck.Sanitise("permission denied\nfor table x"));
        Assert.Contains("connection refused", SchemaCheck.Sanitise("Failed to connect: connection refused"));
        Assert.True(SchemaCheck.Sanitise(new string('a', 5000)).Length <= 303);
        Assert.Equal("no detail", SchemaCheck.Sanitise(null));
    }

    [Fact]
    public void The_start_up_warning_says_why_the_database_could_not_be_read_without_the_connection_string()
    {
        var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseSqlite("Data Source=/nonexistent-dir/none.db;Password=hunter2").Options);
        var log = new Capture();
        SchemaCheck.LogAtStartup(db, log);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("SqliteException", line.Text);
        Assert.DoesNotContain("hunter2", line.Text);
        Assert.DoesNotContain("/nonexistent-dir", line.Text);
    }

}
