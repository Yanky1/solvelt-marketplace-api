using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Solvelt.Marketplace.Api.Catalog;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// How the schema comes to exist (decisions M1-03, M1-26, M1-84). Development and Testing (SQLite or PostgreSQL) build it from the model with EnsureCreated plus the search script.
/// Production on PostgreSQL applies the EF migrations at start (<c>Database:MigrateOnStart</c>, default true; EF takes a database lock, so several instances starting together are safe;
/// set it to false to apply them from a pipeline with <c>dotnet ef database update</c> or a migration bundle instead). Production never uses EnsureCreated, and nothing here drops a table.
/// <para>A database that already has tables but no migration history (made earlier by EnsureCreated, e.g. a test site) is BASELINED, never re-created, and only when it is EXACTLY what the
/// migrations would have made: the migrations are replayed from scratch into a temporary schema inside a transaction that is always rolled back, and the two schemas are compared object by object
/// (<see cref="SchemaDescription"/>: columns with type, nullability, default and generated expression, every constraint and index as PostgreSQL prints it, so unique, partial and filtered indexes, defaults
/// and checks all count). The NEWEST migration whose result the database equals is the one it is baselined at: the history rows of it and of every older migration are written, and the newer ones
/// are applied by the normal Migrate(). When none matches, the start stops with the list of differences and changes nothing. The search objects EF cannot model are added first (the idempotent
/// search script), the history last, so a failure leaves nothing marked as done. The whole check runs under a PostgreSQL advisory lock and is repeated after taking it, so instances starting
/// together do not race: one baselines, the others find the history and carry on. The role needs permission to create a schema (the same level of right it needs to migrate).</para>
/// </summary>
public static class DatabaseSetup
{
    private const long BaselineLockKey = 0x4D50_4241_5345;   // "MPBASE"

    public static void Prepare(MarketplaceDb db, bool devLike, bool migrateOnStart, ILogger? log = null)
    {
        if (devLike)
        {
            db.Database.EnsureCreated();
            SearchSchema.Ensure(db);
        }
        else if (migrateOnStart && db.Database.IsNpgsql())
        {
            RefuseUnknownHistory(db, log);
            BaselineIfNeeded(db, log);
            db.Database.Migrate();
        }
    }

    /// <summary>
    /// A history row for a migration this build does not have means the database was migrated by another build: an older one whose migrations were later regenerated (InitialCreate is regenerated until the
    /// first release, decision M1-84; its id is a timestamp, so a regenerated one is a different id), or a NEWER one (this build was rolled back). Migrate() would ignore the stranger and try to create
    /// tables that already exist, ending in a database error nobody can read. Instead the start stops with the ids and the fix, and changes nothing.
    /// </summary>
    private static void RefuseUnknownHistory(MarketplaceDb db, ILogger? log)
    {
        var applied = db.Database.GetAppliedMigrations().ToList();
        if (applied.Count == 0) return;
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = applied.Where(a => !known.Contains(a)).ToList();
        if (unknown.Count == 0) return;
        var ids = string.Join(", ", unknown);
        log?.LogCritical("The migration history lists migration(s) this build does not have: {Ids}. The database was migrated by another build (an older one whose migrations were regenerated, or a newer one), so nothing was changed. See README, Database schema (migrations), for the one-line fix.", ids);
        throw new InvalidOperationException($"The migration history lists migration(s) this build does not have: {ids}. The database was migrated by another build: an older one whose InitialCreate was regenerated before the first release, or a NEWER build (this one was rolled back). Nothing was changed. If it was an older build, clear the history with DELETE FROM \"__EFMigrationsHistory\"; and start again: the start then compares the schema with the migrations and baselines it, or lists what differs (tools/SchemaTool prints the SQL that adds what is missing). If it was a newer build, deploy that build again. See README, Database schema (migrations).");
    }

    private static void BaselineIfNeeded(MarketplaceDb db, ILogger? log)
    {
        // a cheap look first, without the lock: the normal case (history exists, or an empty database) leaves at once
        if (db.Database.GetAppliedMigrations().Any() || !HasTables(db)) return;
        db.Database.OpenConnection();                                    // one connection for the lock, the checks and the writes
        var conn = db.Database.GetDbConnection();
        try
        {
            Scalar(conn, null, $"SELECT pg_advisory_lock({BaselineLockKey})");
            try
            {
                if (db.Database.GetAppliedMigrations().Any() || !HasTables(db)) return;   // another instance got there first, or the database is empty after all
                var ids = db.Database.GetMigrations().ToList();
                var actual = DescribeCurrent(conn);
                var (index, problems) = SchemaDescription.Choose(ids, id => ExpectedAfter(db, conn, id), actual);
                if (index < 0)
                {
                    var shown = string.Join("; ", problems.Take(20)) + (problems.Count > 20 ? $"; and {problems.Count - 20} more" : "");
                    log?.LogCritical("The database has tables but no migration history, and its schema is not what the migrations would have made, so it was NOT baselined and nothing was changed. {Count} differences: {Problems}", problems.Count, shown);
                    throw new InvalidOperationException($"The database has tables but no migration history and is not what the migrations would have made ({problems.Count} differences, first: {problems[0]}). Nothing was changed. Bring the schema in line by hand (tools/SchemaTool prints the SQL for what is missing) or restore an empty database, and start again; see README, Database schema.");
                }
                SearchSchema.Ensure(db);                                  // the objects EF cannot model; before the history, so a failure here is retried at the next start
                var history = db.GetService<IHistoryRepository>();
                Scalar(conn, null, history.GetCreateIfNotExistsScript());
                foreach (var id in ids.Take(index + 1))
                    Scalar(conn, null, history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion())).TrimEnd().TrimEnd(';') + " ON CONFLICT DO NOTHING;");
                log?.LogWarning("The database was made without migrations and equals what the migrations make: baselined at {Migration} ({Count} migration(s) recorded)", ids[index], index + 1);
            }
            finally { Scalar(conn, null, $"SELECT pg_advisory_unlock({BaselineLockKey})"); }
        }
        finally { db.Database.CloseConnection(); }
    }

    /// <summary>The database's own schema (the current one), described inside a read-only transaction.</summary>
    private static List<string> DescribeCurrent(DbConnection conn)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            var schema = (string)Scalar(conn, tx, "SELECT current_schema()")!;
            return SchemaDescription.Describe(conn, tx, schema);
        }
        finally { tx.Rollback(); }
    }

    /// <summary>
    /// What the schema is after the migrations up to <paramref name="migrationId"/>: they are replayed from scratch (EF's own script) into a temporary schema inside a transaction that is rolled back,
    /// so nothing is left behind, not even after a crash.
    /// </summary>
    private static List<string> ExpectedAfter(MarketplaceDb db, DbConnection conn, string migrationId)
    {
        var script = db.GetService<IMigrator>().GenerateScript(null, migrationId, MigrationsSqlGenerationOptions.NoTransactions);
        var schema = "mp_baseline_" + Guid.NewGuid().ToString("N")[..12];
        using var tx = conn.BeginTransaction();
        try
        {
            Scalar(conn, tx, $"CREATE SCHEMA {SchemaDescription.Quote(schema)}");
            Scalar(conn, tx, $"SET LOCAL search_path TO {SchemaDescription.Quote(schema)}, public");
            Scalar(conn, tx, script);                                     // a raw command, not ExecuteSqlRaw: the script may contain braces
            return SchemaDescription.Describe(conn, tx, schema);
        }
        finally { tx.Rollback(); }
    }

    private static object? Scalar(DbConnection conn, DbTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static bool HasTables(MarketplaceDb db)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try { return Convert.ToInt64(Scalar(conn, null, "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory'")) > 0; }
        finally { if (!wasOpen) conn.Close(); }
    }

    /// <summary>What the model has and the database lacks or has differently (the start-up schema check, <see cref="SchemaCheck"/>): names, types and nullability, for the log and the tool.</summary>
    public static List<string> SchemaProblems(MarketplaceDb db) => SchemaCheck.Differences(db);
}
