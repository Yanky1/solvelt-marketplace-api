using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// InitialCreate (decisions M1-26, M1-84): the migrations apply on PostgreSQL and produce exactly the schema the model produces (EnsureCreated plus the search DDL), tables, columns, defaults,
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

    [PostgresFact]
    public async Task The_migrations_apply_and_give_exactly_the_schema_of_the_model()
    {
        var admin = Admin!;
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

    [PostgresFact]
    public async Task The_model_has_no_change_the_migrations_do_not_carry()
    {
        var admin = Admin!;
        await using var t = await TempDb.Create(admin);
        using var db = t.Context();
        Assert.False(db.Database.HasPendingModelChanges(), "the model changed since InitialCreate was generated: run tools/migrations/regenerate-initial-create.sh (before the first release) or add a migration");
        Assert.Single(db.Database.GetMigrations());
    }

    [PostgresFact]
    public async Task Migrating_twice_changes_nothing_and_the_search_script_is_a_no_op_afterwards()
    {
        var admin = Admin!;
        await using var t = await TempDb.Create(admin);
        using var db = t.Context();
        await db.Database.MigrateAsync();
        var before = await Describe(t.ConnectionString);
        await db.Database.MigrateAsync();
        SearchSchema.Ensure(db);                   // what a Development start does: nothing new on a migrated database
        Assert.Equal(before, await Describe(t.ConnectionString));
    }

    [PostgresFact]
    public async Task Down_removes_everything_and_up_applies_again()
    {
        var admin = Admin!;
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

/// <summary>Which of EnsureCreated and Migrate runs at start-up (M1-84).</summary>
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

    [PostgresFact]
    public async Task Production_on_PostgreSQL_applies_the_migrations_unless_told_not_to()
    {
        var admin = Admin!;
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

    // ---------- a database made earlier by EnsureCreated (a live test site) ----------

    private static async Task<(MarketplaceDb Db, string Name)> OldDatabase(string admin)
    {
        var name = "mp_old_" + Guid.NewGuid().ToString("N");
        using (var c = new NpgsqlConnection(admin)) { c.Open(); using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c); cmd.ExecuteNonQuery(); }
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(cs).Options);
        await db.Database.EnsureCreatedAsync();                       // what the test site ran: tables from the model, no history, no search objects
        db.BuyerAccounts.Add(new BuyerAccount { Id = Guid.NewGuid(), Email = "keep@example.test", DisplayName = "Keep", RelayEmail = "relay-keep@relay.test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (db, name);
    }

    private static void Drop(string admin, string name)
    {
        NpgsqlConnection.ClearAllPools();
        using var c = new NpgsqlConnection(admin); c.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", c); cmd.ExecuteNonQuery();
    }

    private static async Task<int> Scalar(MarketplaceDb db, string sql)
        => await db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    [PostgresFact]
    public async Task A_database_that_matches_the_model_but_has_no_history_is_baselined_and_keeps_its_data()
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
            Assert.Equal(1, await db.BuyerAccounts.CountAsync(a => a.Email == "keep@example.test"));            // nothing dropped or recreated
            Assert.Equal(1, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\""));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(1, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_catalog_search_text_trgm'"));   // the search objects the old database lacked
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);                                  // a second start changes nothing
            Assert.Equal(1, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\""));
        }
        finally { Drop(admin, name); }
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE buyer_accounts DROP COLUMN \"Phone\"", "Phone")]
    [InlineData("ALTER TABLE buyer_accounts ALTER COLUMN \"DisplayName\" TYPE character varying(5)", "DisplayName")]
    [InlineData("DROP INDEX \"IX_buyer_passkeys_AccountId\"", "IX_buyer_passkeys_AccountId")]
    [InlineData("DROP TABLE webauthn_challenges", "webauthn_challenges")]
    public async Task A_database_that_differs_from_the_model_stops_the_start_and_is_left_alone(string damage, string expectedInMessage)
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            await db.Database.ExecuteSqlRawAsync(damage);
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            Assert.Contains("Nothing was changed", ex.Message);
            Assert.Contains(expectedInMessage, string.Join(";", DatabaseSetup.SchemaProblems(db)));
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"));   // not marked as migrated
            Assert.Equal(1, await db.BuyerAccounts.CountAsync());                                                // data intact
        }
        finally { Drop(admin, name); }
    }

    // ---------- review of the baseline (M1-84): what the names-only check missed, concurrency, and which migration is recorded ----------

    [PostgresTheory]
    [InlineData("DROP INDEX ux_buyer_addresses_one_default; CREATE INDEX ux_buyer_addresses_one_default ON buyer_addresses (\"AccountId\")", "ux_buyer_addresses_one_default")]       // unique and filtered made plain
    [InlineData("DROP INDEX \"IX_marketplace_orders_CartId\"; CREATE UNIQUE INDEX \"IX_marketplace_orders_CartId\" ON marketplace_orders (\"CartId\")", "IX_marketplace_orders_CartId")]   // partial made total
    [InlineData("ALTER TABLE cart_lines ALTER COLUMN \"IsDemo\" DROP DEFAULT", "cart_lines.IsDemo")]                                                                                        // the default gone: every cart add would fail NOT NULL
    [InlineData("ALTER TABLE buyer_accounts ADD CONSTRAINT extra_check CHECK (\"DisplayName\" <> 'x')", "extra_check")]                                                                      // an extra constraint
    [InlineData("CREATE INDEX extra_idx ON buyer_accounts (\"Phone\")", "extra_idx")]                                                                                                     // an extra index
    [InlineData("ALTER TABLE cart_lines DROP CONSTRAINT \"FK_cart_lines_carts_CartId\"; ALTER TABLE cart_lines ADD CONSTRAINT \"FK_cart_lines_carts_CartId\" FOREIGN KEY (\"CartId\") REFERENCES carts (\"Id\")", "FK_cart_lines_carts_CartId")]
    public async Task A_database_whose_indexes_defaults_or_constraints_differ_is_not_baselined(string damage, string expectedInMessage)
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            await db.Database.ExecuteSqlRawAsync(damage);
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            Assert.Contains("Nothing was changed", ex.Message);
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"));      // not marked as migrated
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM pg_namespace WHERE nspname LIKE 'mp_baseline_%'"));                          // the temporary schema left nothing behind
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_catalog_search_text_trgm'"));              // and the search script did not run
            Assert.Equal(1, await db.BuyerAccounts.CountAsync());                                                                                                  // data intact
            Assert.Contains(expectedInMessage, ex.Message + string.Join(";", SchemaDescription.Compare(DescribeActual(db), ExpectedForInitial(db), true)));
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task Four_instances_starting_together_on_an_old_database_baseline_it_once_and_none_fails()
    {
        var admin = Admin!;
        var (first, name) = await OldDatabase(admin);
        try
        {
            using var _ = first;
            var cs = first.Database.GetConnectionString()!;
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try
                {
                    using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(cs).Options);
                    DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
                    return (string?)null;
                }
                catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            })));
            Assert.All(results, r => Assert.Null(r));
            Assert.Equal(1, await Scalar(first, "SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\""));
            Assert.Equal(1, await first.BuyerAccounts.CountAsync());
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task A_baselined_database_is_exactly_what_the_migrations_make_search_objects_included()
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
            var problems = SchemaDescription.Compare(DescribeActual(db), ExpectedForInitial(db), ignoreSearchObjects: false);
            Assert.Empty(problems);
        }
        finally { Drop(admin, name); }
    }

    private static List<string> DescribeActual(MarketplaceDb db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        return SchemaDescription.Describe(conn, tx, "public");
    }

    private static List<string> ExpectedForInitial(MarketplaceDb db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var script = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db)
            .GenerateScript(null, db.Database.GetMigrations().First(), Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.NoTransactions);
        var schema = "mp_expected_" + Guid.NewGuid().ToString("N")[..8];
        using var tx = conn.BeginTransaction();
        void Run(string sql) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = sql; c.ExecuteNonQuery(); }
        Run($"CREATE SCHEMA {schema}"); Run($"SET LOCAL search_path TO {schema}, public"); Run(script);
        var lines = SchemaDescription.Describe(conn, tx, schema);
        tx.Rollback();
        return lines;
    }

    // --- which migration is recorded: the newest one the database equals (a pure test of the choice, with made-up migrations) ---

    [Fact]
    public void The_baseline_is_recorded_at_the_oldest_migration_the_schema_equals()
    {
        string[] ids = ["001_Initial", "002_AddA", "003_AddB"];
        var m1 = new[] { "table t" };
        var m2 = new[] { "table t", "column t.a" };
        var m3 = new[] { "table t", "column t.a", "column t.b" };
        IReadOnlyCollection<string> ExpectedFor(string id) => id switch { "001_Initial" => m1, "002_AddA" => m2, _ => m3 };

        Assert.Equal(0, SchemaDescription.Choose(ids, ExpectedFor, m1).Index);            // made by the first migration: only the first is recorded, the others will be applied
        Assert.Equal(1, SchemaDescription.Choose(ids, ExpectedFor, m2).Index);            // already has A: the first two are recorded, B is applied
        Assert.Equal(2, SchemaDescription.Choose(ids, ExpectedFor, m3).Index);            // current: all three recorded
        var (none, problems) = SchemaDescription.Choose(ids, ExpectedFor, ["table t", "column t.zzz"]);
        Assert.Equal(-1, none);
        Assert.Contains(problems, p => p.Contains("column t.zzz") && p.Contains("not expected"));   // the differences are against the newest migration
        Assert.Contains(problems, p => p.Contains("column t.b"));
    }

    [Fact]
    public void Among_migrations_that_leave_the_same_schema_the_oldest_is_chosen_so_a_data_only_migration_still_runs()
    {
        string[] ids = ["001_Initial", "002_SeedData", "003_BackfillData"];                // 002 and 003 change data only: the description cannot tell the three apart
        var same = new[] { "table t", "column t.a" };
        Assert.Equal(0, SchemaDescription.Choose(ids, _ => same, same).Index);            // recorded at 001: 002 and 003 are applied by Migrate(), not marked as done unseen
        // and a schema change in the middle still decides
        IReadOnlyCollection<string> ExpectedFor(string id) => id == "001_Initial" ? ["table t"] : same;
        Assert.Equal(1, SchemaDescription.Choose(ids, ExpectedFor, same).Index);
    }

    [Fact]
    public void The_search_objects_are_left_out_of_the_first_comparison_only()
    {
        string[] expected = ["table t", "column catalog_search.document tsvector generated=s", "index catalog_search ix_catalog_search_text_trgm CREATE INDEX", "function catalog_tokens_tsvector (t text) x"];
        Assert.Empty(SchemaDescription.Compare(["table t"], expected, ignoreSearchObjects: true));
        Assert.Equal(3, SchemaDescription.Compare(["table t"], expected, ignoreSearchObjects: false).Count);
    }

    // ---------- a history row from another build (InitialCreate regenerated, or a rolled-back deploy): refuse clearly, one-line fix ----------

    private static async Task<(MarketplaceDb Db, string Name)> MigratedDatabase(string admin)
    {
        var name = "mp_hist_" + Guid.NewGuid().ToString("N");
        using (var c = new NpgsqlConnection(admin)) { c.Open(); using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c); cmd.ExecuteNonQuery(); }
        var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString).Options);
        DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
        return (db, name);
    }

    [PostgresFact]
    public async Task A_history_row_this_build_does_not_have_stops_the_start_with_the_ids_and_the_fix_and_changes_nothing()
    {
        var admin = Admin!;
        var (db, name) = await MigratedDatabase(admin);
        try
        {
            using var _ = db;
            await db.Database.ExecuteSqlRawAsync("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '20260101000000_InitialCreate'");   // the id an earlier build recorded
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            Assert.Contains("20260101000000_InitialCreate", ex.Message);
            Assert.Contains("Nothing was changed", ex.Message);
            Assert.Contains("DELETE FROM \"__EFMigrationsHistory\"", ex.Message);
            Assert.DoesNotContain("already exists", ex.Message);                                                                   // not a database error about creating tables again
            Assert.Equal(1, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260101000000_InitialCreate'"));
            Assert.Equal(0, await db.BuyerAccounts.CountAsync());                                                                  // the tables are untouched
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task The_documented_fix_works_the_history_is_cleared_and_the_database_is_baselined_again()
    {
        var admin = Admin!;
        var (db, name) = await MigratedDatabase(admin);
        try
        {
            using var _ = db;
            await db.Database.ExecuteSqlRawAsync("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '20260101000000_InitialCreate'");
            Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";");                                         // the one-line fix
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);                                                       // equal to what the migrations make: baselined
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations().First(), await db.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"").SingleAsync());
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task After_the_fix_a_database_made_by_the_older_InitialCreate_lists_what_differs_and_the_tool_script_repairs_it()
    {
        var admin = Admin!;
        var (db, name) = await MigratedDatabase(admin);
        try
        {
            using var _ = db;
            // the earlier InitialCreate had no buyer_accounts.DemoVerifiedAt
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE buyer_accounts DROP COLUMN \"DemoVerifiedAt\"");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '20260101000000_InitialCreate'");
            Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));        // 1: the stranger in the history
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";");
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));  // 2: the schema differs: listed, nothing changed
            Assert.Contains("DemoVerifiedAt", ex.Message);
            var fix = SchemaCheck.FixScript(db);                                                                                   // 3: the tool's SQL adds the column
            Assert.Contains("DemoVerifiedAt", fix);
            await db.Database.OpenConnectionAsync();
            using (var cmd = db.Database.GetDbConnection().CreateCommand()) { cmd.CommandText = fix; await cmd.ExecuteNonQueryAsync(); }
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);                                                       // 4: baselined
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { Drop(admin, name); }
    }

    // ---------- review of the baseline, round 2 (M1-89) ----------

    [PostgresFact]
    public async Task The_search_script_runs_before_the_history_so_when_it_fails_no_history_row_is_written_and_the_next_start_retries()
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            // a function of the same name and arguments with another return type: the search script (CREATE OR REPLACE) cannot replace it. The comparison ignores search objects, so it passes and the script is what fails.
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION catalog_tokens_tsvector(t text, w \"char\") RETURNS int LANGUAGE sql AS 'SELECT 1'");
            Assert.ThrowsAny<Exception>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"));         // nothing marked as done
            await db.Database.ExecuteSqlRawAsync("DROP FUNCTION catalog_tokens_tsvector(text, \"char\")");
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);                                                                                           // the next start retries and baselines
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task The_history_table_and_its_rows_are_written_in_one_transaction()
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        try
        {
            using var _ = db;
            DatabaseSetup.HistoryRowWritten = _ => throw new InvalidOperationException("crash after the first history row");
            try { Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true)); }
            finally { DatabaseSetup.HistoryRowWritten = null; }
            // the crash left neither the history table nor a row (before this, a partial history made every later start fail)
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"));
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { Drop(admin, name); }
    }

    [PostgresFact]
    public async Task A_start_waiting_for_another_baselining_instance_gives_up_after_the_deadline_and_changes_nothing()
    {
        var admin = Admin!;
        var (db, name) = await OldDatabase(admin);
        var holder = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()!) { Pooling = false }.ConnectionString);
        var wait = DatabaseSetup.LockWait;
        try
        {
            using var _ = db;
            await holder.OpenAsync();
            using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(" + 0x4D50_4241_5345L + ")", holder)) await cmd.ExecuteScalarAsync();   // "another instance" holds the baseline lock
            DatabaseSetup.LockWait = TimeSpan.FromSeconds(1);
            var ex = Assert.Throws<TimeoutException>(() => DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true));
            Assert.Contains("changed nothing", ex.Message);
            Assert.Equal(0, await Scalar(db, "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'"));
            await holder.CloseAsync();                                                                                                           // the other instance is done (its session ends, the lock goes)
            DatabaseSetup.LockWait = TimeSpan.FromSeconds(30);
            DatabaseSetup.Prepare(db, devLike: false, migrateOnStart: true);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { DatabaseSetup.LockWait = wait; await holder.DisposeAsync(); Drop(admin, name); }
    }
}
