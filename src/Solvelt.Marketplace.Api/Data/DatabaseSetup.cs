using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// How the schema comes to exist (decisions M1-03, M1-26, M1-84). Development and Testing (SQLite or PostgreSQL) build it from the model with EnsureCreated plus the search script.
/// Production on PostgreSQL applies the EF migrations at start (<c>Database:MigrateOnStart</c>, default true; EF takes a database lock, so several instances starting together are safe;
/// set it to false to apply them from a pipeline with <c>dotnet ef database update</c> or a migration bundle instead). Production never uses EnsureCreated, and nothing here drops a table.
/// <para>A database that already has the tables but no migration history (made earlier by EnsureCreated, e.g. a test site) is BASELINED, never re-created: when every table, column (with its
/// store type and nullability), primary key, foreign key and index of the model is present, the InitialCreate history row is inserted and the search script (idempotent) is run; when anything is
/// missing or different the start-up stops with the list of differences and changes nothing, so a half-matching database is never marked as migrated.</para>
/// </summary>
public static class DatabaseSetup
{
    public static void Prepare(MarketplaceDb db, bool devLike, bool migrateOnStart, ILogger? log = null)
    {
        if (devLike)
        {
            db.Database.EnsureCreated();
            SearchSchema.Ensure(db);
        }
        else if (migrateOnStart && db.Database.IsNpgsql())
        {
            if (!db.Database.GetAppliedMigrations().Any() && HasTables(db))
            {
                var problems = SchemaProblems(db);
                if (problems.Count > 0)
                {
                    var shown = string.Join("; ", problems.Take(20)) + (problems.Count > 20 ? $"; and {problems.Count - 20} more" : "");
                    log?.LogCritical("The database has tables but no migration history, and its schema does not match the model, so it was NOT baselined and nothing was changed. {Count} differences: {Problems}", problems.Count, shown);
                    throw new InvalidOperationException($"The database has tables but no migration history and does not match the model ({problems.Count} differences, first: {problems[0]}). Nothing was changed. Bring the schema in line by hand (or restore an empty database) and start again; see README, Database schema.");
                }
                var first = db.Database.GetMigrations().First();
                var history = db.GetService<IHistoryRepository>();
                db.Database.ExecuteSqlRaw(history.GetCreateIfNotExistsScript());
                db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(first, ProductInfo.GetVersion())));
                SearchSchema.Ensure(db);
                log?.LogWarning("The database was made without migrations and matches the model: baselined at {Migration}", first);
            }
            db.Database.Migrate();
        }
    }

    private static bool HasTables(MarketplaceDb db)
        => Query(db, "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory'").Count > 0;

    private static List<string> Query(MarketplaceDb db, string sql)
    {
        var rows = new List<string>();
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(r.GetString(0));
        }
        finally { if (!wasOpen) conn.Close(); }
        return rows;
    }

    /// <summary>What the model has and the database lacks or has differently (the same comparison as the start-up schema check, <see cref="SchemaCheck"/>). Empty when the database can be baselined.</summary>
    public static List<string> SchemaProblems(MarketplaceDb db) => SchemaCheck.Differences(db);
}
