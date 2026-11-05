using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// Compares a database with the model and says what is missing (decision M1-83). <c>EnsureCreated</c> never alters an existing database, so a database made by an earlier build lacks the tables and
/// columns added since, and the first query that touches one fails with a 500. <see cref="LogAtStartup"/> makes that cause visible with one critical log line; <see cref="FixScript"/> prints the
/// SQL that adds what is missing (tools/SchemaTool). Reads only; never changes a database. What it compares: tables, columns (PostgreSQL: store type and nullability; SQLite:
/// nullability, SQLite types are only affinities), and on PostgreSQL also primary keys, foreign keys, unique constraints and indexes by name; on SQLite indexes by name.
/// </summary>
public static class SchemaCheck
{
    private sealed record Actual(Dictionary<string, Dictionary<string, (string Type, bool NotNull)>> Tables, HashSet<string> Constraints, HashSet<string> Indexes);

    /// <summary>One critical line naming the first differences, or nothing when the database matches the model. Never throws: a failure to look is logged as a warning.</summary>
    public static void LogAtStartup(MarketplaceDb db, ILogger log)
    {
        try
        {
            var problems = Differences(db);
            if (problems.Count == 0) return;
            var shown = string.Join("; ", problems.Take(15)) + (problems.Count > 15 ? $"; and {problems.Count - 15} more" : "");
            log.LogCritical("The database schema does not match the model ({Count} differences), so requests that use the missing parts will fail with 500. EnsureCreated never alters an existing database. Differences: {Differences}. " +
                "Print the SQL that fixes it with: MARKETPLACE_DB=\"<connection string>\" dotnet run --project tools/SchemaTool -- <postgres|sqlite>  (see README, Database schema)", problems.Count, shown);
        }
        catch (Exception ex) { log.LogWarning("The schema could not be compared with the model ({ExceptionType}: {Reason}); it is not known whether it matches", ex.GetType().Name, Sanitise(ex.Message)); }
    }

    /// <summary>One column checked for NFC: how many values it holds and how many are not in normalisation form C.</summary>
    public sealed record NfcCount(string Column, int Values, int NotNfc);

    /// <summary>
    /// Read-only: how many stored email addresses are not in NFC (decision M1-81). Before the normaliser an address typed with combining marks and the same address typed precomposed were two
    /// different values; one stored in the decomposed form is no longer found by a sign-in or a guest lookup (those normalise to NFC). Counts only, never the addresses. A table or column that is
    /// not there is skipped. Works on both providers (the check runs in the application, with <see cref="Text.UnicodeNormalizer"/>).
    /// </summary>
    public static List<NfcCount> NfcReport(MarketplaceDb db)
    {
        var result = new List<NfcCount>();
        void Check(string label, Func<List<string>> read)
        {
            try
            {
                var values = read();
                result.Add(new NfcCount(label, values.Count, values.Count(v => !Text.UnicodeNormalizer.IsNfc(v))));
            }
            catch (Exception) { /* the table or column does not exist (an older database): the schema differences say so */ }
        }
        Check("buyer_accounts.Email", () => db.Database.SqlQueryRaw<string>("SELECT \"Email\" AS \"Value\" FROM buyer_accounts WHERE \"Email\" IS NOT NULL").ToList());
        Check("marketplace_orders.GuestEmail", () => db.Database.SqlQueryRaw<string>("SELECT \"GuestEmail\" AS \"Value\" FROM marketplace_orders WHERE \"GuestEmail\" IS NOT NULL").ToList());
        return result;
    }

    /// <summary>
    /// The reason of a failure, safe for a log line: any <c>key=value</c> pair of a connection string (Password, Pwd, User ID, Host, Server, Data Source, ...) is replaced, line breaks are dropped and the
    /// text is cut at 300 characters, so "cannot connect" and "permission denied" can be told apart without the connection string reaching the log.
    /// </summary>
    public static string Sanitise(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "no detail";
        var clean = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd|user id|uid|username|host|server|data source|database|port|connection string)\s*=\s*[^;\r\n]*", "$1=***");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length <= 300 ? clean : clean[..300] + "...";
    }

    /// <summary>
    /// A context for reading only (decision M1-83): SQLite is opened with <c>Mode=ReadOnly</c>, so a mistyped path fails instead of creating an empty database; PostgreSQL gets
    /// <c>default_transaction_read_only=on</c> for the session. <paramref name="provider"/> is <c>postgres</c> or <c>sqlite</c>.
    /// </summary>
    public static MarketplaceDb OpenReadOnly(string provider, string connectionString)
    {
        var options = new DbContextOptionsBuilder<MarketplaceDb>();
        if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            options.UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Options = "-c default_transaction_read_only=on" }.ConnectionString);
        else
            options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString) { Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ConnectionString);
        return new MarketplaceDb(options.Options);
    }

    public static List<string> Differences(MarketplaceDb db)
    {
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var actual = Read(db);
        var problems = new List<string>();
        var pg = db.Database.IsNpgsql();
        foreach (var t in model.Tables)
        {
            if (!actual.Tables.TryGetValue(t.Name, out var cols)) { problems.Add($"table {t.Name} is missing"); continue; }
            foreach (var c in t.Columns)
            {
                if (!cols.TryGetValue(c.Name, out var have)) { problems.Add($"column {t.Name}.{c.Name} is missing"); continue; }
                if (pg && !string.Equals(have.Type, c.StoreType, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"column {t.Name}.{c.Name} is {have.Type}, the model says {c.StoreType}");
                else if (!c.IsNullable && !have.NotNull && !IsKeyColumn(t, c))
                    problems.Add($"column {t.Name}.{c.Name} allows null, the model says not null");
                else if (pg && c.IsNullable && have.NotNull)
                    problems.Add($"column {t.Name}.{c.Name} is not null, the model says null");
            }
            if (pg)
            {
                if (t.PrimaryKey is { } pk && !actual.Constraints.Contains(pk.Name)) problems.Add($"primary key {pk.Name} is missing");
                foreach (var fk in t.ForeignKeyConstraints) if (!actual.Constraints.Contains(fk.Name)) problems.Add($"foreign key {fk.Name} is missing");
                foreach (var u in t.UniqueConstraints) if (!actual.Constraints.Contains(u.Name)) problems.Add($"unique constraint {u.Name} is missing");
            }
            foreach (var i in t.Indexes) if (!actual.Indexes.Contains(i.Name)) problems.Add($"index {i.Name} is missing");
        }
        return problems;
    }

    private static bool IsKeyColumn(ITable t, IColumn c) => t.PrimaryKey?.Columns.Any(k => k.Name == c.Name) == true;   // SQLite reports a key column as nullable in some declarations

    /// <summary>
    /// SQL that adds what <see cref="Differences"/> reports missing: whole tables (with keys, foreign keys), columns (a NOT NULL column gets the type's default so existing rows can take it: review it),
    /// and indexes. Types, nullability and missing keys on existing tables are listed as comments for a person to decide. Run it in a transaction on a COPY first.
    /// </summary>
    public static string FixScript(MarketplaceDb db)
    {
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var actual = Read(db);
        var all = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
        var ops = new List<MigrationOperation>();
        var missingTables = model.Tables.Where(t => !actual.Tables.ContainsKey(t.Name)).Select(t => t.Name).ToHashSet();
        foreach (var op in all)
        {
            switch (op)
            {
                case CreateTableOperation ct when missingTables.Contains(ct.Name):
                    ops.Add(ct);
                    break;
                case CreateTableOperation ct when actual.Tables.TryGetValue(ct.Name, out var cols):
                    foreach (var col in ct.Columns.Where(c => !cols.ContainsKey(c.Name)))
                    {
                        if (!col.IsNullable && col.DefaultValue is null && col.DefaultValueSql is null && !col.IsRowVersion) col.DefaultValue = DefaultFor(col.ClrType);
                        ops.Add(col);
                    }
                    break;
            }
        }
        foreach (var op in all.OfType<CreateIndexOperation>().Where(i => !actual.Indexes.Contains(i.Name) && (missingTables.Contains(i.Table) || actual.Tables.ContainsKey(i.Table))))
            ops.Add(op);
        var sql = db.GetService<IMigrationsSqlGenerator>().Generate(ops, db.GetService<IDesignTimeModel>().Model);
        var script = new System.Text.StringBuilder();
        script.AppendLine("-- Generated by tools/SchemaTool. Review, and run it in a transaction on a copy first.");
        foreach (var d in Differences(db).Where(d => !d.Contains("is missing", StringComparison.Ordinal) || d.StartsWith("primary key", StringComparison.Ordinal) || d.StartsWith("foreign key", StringComparison.Ordinal) || d.StartsWith("unique constraint", StringComparison.Ordinal)))
            script.AppendLine("-- MANUAL: " + d);
        foreach (var c in sql) script.AppendLine(c.CommandText.TrimEnd().EndsWith(';') ? c.CommandText.TrimEnd() : c.CommandText.TrimEnd() + ";");
        return script.ToString();
    }

    private static object? DefaultFor(Type? type)
    {
        var t = Nullable.GetUnderlyingType(type ?? typeof(string)) ?? type ?? typeof(string);
        if (t == typeof(string)) return "";
        if (t == typeof(byte[])) return Array.Empty<byte>();
        if (t == typeof(DateTimeOffset)) return DateTimeOffset.UnixEpoch;
        if (t == typeof(DateTime)) return DateTime.UnixEpoch;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static Actual Read(MarketplaceDb db)
    {
        var tables = new Dictionary<string, Dictionary<string, (string, bool)>>(StringComparer.Ordinal);
        var constraints = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            void Run(string sql, Action<IDataRecord> each)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) each(r);
            }
            if (db.Database.IsNpgsql())
            {
                Run("""
                    SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                    FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = current_schema() AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
                    """, r => Add(tables, r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
                Run("SELECT con.conname FROM pg_constraint con JOIN pg_namespace n ON n.oid = con.connamespace WHERE n.nspname = current_schema()", r => constraints.Add(r.GetString(0)));
                Run("SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()", r => indexes.Add(r.GetString(0)));
            }
            else
            {
                Run("SELECT m.name, p.name, p.type, p.\"notnull\" FROM sqlite_master m, pragma_table_info(m.name) p WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'",
                    r => Add(tables, r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
                Run("SELECT name FROM sqlite_master WHERE type = 'index'", r => indexes.Add(r.GetString(0)));
            }
        }
        finally { if (!wasOpen) conn.Close(); }
        return new Actual(tables, constraints, indexes);

        static void Add(Dictionary<string, Dictionary<string, (string, bool)>> t, string table, string column, string type, bool notNull)
        {
            if (!t.TryGetValue(table, out var cols)) t[table] = cols = new(StringComparer.Ordinal);
            cols[column] = (type, notNull);
        }
    }
}
