using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// InitialCreate (decisions M1-26, M1-83): the migrations apply on PostgreSQL and produce exactly the schema the model produces (EnsureCreated plus the search DDL), tables, columns, defaults,
/// keys, foreign keys, unique and partial indexes included. PostgreSQL only: without TEST_POSTGRES_CONNECTION (or on SQLite) these tests return at once; CI runs them in the PostgreSQL job.
/// </summary>
public class MigrationTests
{
    private static string? Admin => TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres" ? Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") : null;

    private sealed class TempDb : IAsyncDisposable
    {
        private readonly string _admin;
        public string Name { get; } = "mp_mig_" + Guid.NewGuid().ToString("N");
        public string ConnectionString { get; }
        private TempDb(string admin) { _admin = admin; ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = Name }.ConnectionString; }
        public static async Task<TempDb> Create(string admin)
        {
            var d = new TempDb(admin);
            await using var c = new NpgsqlConnection(admin);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{d.Name}\"", c);
            await cmd.ExecuteNonQueryAsync();
            return d;
        }
        public MarketplaceDb Context() => new(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(ConnectionString).Options);
        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(_admin);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>A normalised description of everything in the public schema that the model decides, one string per object, sorted.</summary>
    private static async Task<List<string>> Describe(string connectionString)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        var lines = new List<string>();
        async Task Add(string prefix, string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) lines.Add(prefix + " " + r.GetString(0));
        }
        const string notHistory = "c.relname <> '__EFMigrationsHistory'";
        await Add("column", $"""
            SELECT c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod) || ' notnull=' || a.attnotnull || ' identity=' || a.attidentity::text || ' generated=' || a.attgenerated::text
                   || ' default=' || coalesce(pg_get_expr(d.adbin, d.adrelid), '') || ' collation=' || coalesce(a.attcollation::regcollation::text, '') || ' comment=' || coalesce(col_description(c.oid, a.attnum), '')
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped AND {notHistory}
            """);
        await Add("constraint", $"""
            SELECT c.relname || ' ' || con.conname || ' ' || pg_get_constraintdef(con.oid)
            FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND {notHistory}
            """);
        await Add("index", """
            SELECT tablename || ' ' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory'
            """);
        await Add("function", """
            SELECT p.proname || ' ' || pg_get_function_arguments(p.oid) || ' ' || p.provolatile::text || p.proparallel::text || ' ' || md5(p.prosrc)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')   -- not the extension's own
            """);
        await Add("extension", "SELECT extname FROM pg_extension WHERE extname = 'pg_trgm'");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Fact]
    public async Task The_migrations_apply_and_give_exactly_the_schema_of_the_model()
    {
        if (Admin is not { } admin) return;
        await using var migrated = await TempDb.Create(admin);
        await using var created = await TempDb.Create(admin);
        using (var db = migrated.Context()) await db.Database.MigrateAsync();
        using (var db = created.Context()) { await db.Database.EnsureCreatedAsync(); SearchSchema.Ensure(db); }

        var a = await Describe(migrated.ConnectionString);
        var b = await Describe(created.ConnectionString);
        Assert.True(a.Count > 300, $"the description should cover the whole schema, saw {a.Count} objects");
        Assert.Empty(a.Except(b).Take(10).Select(x => "only in the migrated database: " + x));
        Assert.Empty(b.Except(a).Take(10).Select(x => "only in the model-created database: " + x));
        // the objects EF cannot model are in the description too, so a missing one cannot hide
        Assert.Contains(a, x => x.StartsWith("index catalog_search ix_catalog_search_document ", StringComparison.Ordinal));
        Assert.Contains(a, x => x.StartsWith("index catalog_search ix_catalog_search_text_trgm ", StringComparison.Ordinal));
        Assert.Contains(a, x => x.StartsWith("function catalog_tokens_tsvector ", StringComparison.Ordinal));
        Assert.Contains(a, x => x.StartsWith("column catalog_search.document tsvector", StringComparison.Ordinal) && x.Contains("generated=s") && x.Contains("comment=v3"));
    }

    [Fact]
    public async Task The_model_has_no_change_the_migrations_do_not_carry()
    {
        if (Admin is not { } admin) return;
        await using var t = await TempDb.Create(admin);
        using var db = t.Context();
        Assert.False(db.Database.HasPendingModelChanges(), "the model changed since InitialCreate was generated: run tools/migrations/regenerate-initial-create.sh (before the first release) or add a migration");
        Assert.Single(db.Database.GetMigrations());
    }

    [Fact]
    public async Task Migrating_twice_changes_nothing_and_the_search_script_is_a_no_op_afterwards()
    {
        if (Admin is not { } admin) return;
        await using var t = await TempDb.Create(admin);
        using var db = t.Context();
        await db.Database.MigrateAsync();
        var before = await Describe(t.ConnectionString);
        await db.Database.MigrateAsync();
        SearchSchema.Ensure(db);                   // what a Development start does: nothing new on a migrated database
        Assert.Equal(before, await Describe(t.ConnectionString));
    }

    [Fact]
    public async Task Down_removes_everything_and_up_applies_again()
    {
        if (Admin is not { } admin) return;
        await using var t = await TempDb.Create(admin);
        using var db = t.Context();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync("0");
        var left = (await Describe(t.ConnectionString)).Where(x => !x.StartsWith("extension ", StringComparison.Ordinal)).ToList();
        Assert.Empty(left);
        await migrator.MigrateAsync();
        Assert.True((await Describe(t.ConnectionString)).Count > 300);
    }
}

/// <summary>Which of EnsureCreated and Migrate runs at start-up (M1-83).</summary>
public class DatabaseSetupTests
{
    private static string? Admin => TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres" ? Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") : null;

    private static MarketplaceDb Sqlite(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "mp_setup_" + Guid.NewGuid().ToString("N") + ".db");
        return new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseSqlite($"Data Source={path}").Options);
    }

    [Fact]
    public void Development_builds_the_schema_from_the_model()
    {
        using var db = Sqlite(out var path);
        try
        {
            DatabaseSetup.Prepare(db, devLike: true, migrateOnStart: true);
            Assert.True(db.BuyerAccounts.Count() == 0);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void Production_never_builds_a_schema_from_the_model()
    {
        using var db = Sqlite(out var path);
        try
        {
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);       // SQLite has no migrations: nothing is created
            Assert.ThrowsAny<Exception>(() => db.BuyerAccounts.Count());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task Production_on_PostgreSQL_applies_the_migrations_unless_told_not_to()
    {
        if (Admin is not { } admin) return;
        string Create(string name) { using var c = new NpgsqlConnection(admin); c.Open(); using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c); cmd.ExecuteNonQuery(); return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString; }
        var n1 = "mp_set_" + Guid.NewGuid().ToString("N"); var n2 = "mp_set_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(Create(n1)).Options))
            {
                DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
                Assert.Equal(0, await db.BuyerAccounts.CountAsync());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
            using (var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(Create(n2)).Options))
            {
                DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: false);
                Assert.Single(await db.Database.GetPendingMigrationsAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.BuyerAccounts.CountAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var n in new[] { n1, n2 }) { using var c = new NpgsqlConnection(admin); c.Open(); using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{n}\" WITH (FORCE)", c); cmd.ExecuteNonQuery(); }
        }
    }
}
