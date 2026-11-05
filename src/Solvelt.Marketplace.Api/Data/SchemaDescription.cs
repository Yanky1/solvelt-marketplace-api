using System.Data;
using System.Data.Common;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A normalised description of everything in one PostgreSQL schema that the model (and so the migrations) decides, one string per object, sorted: every column with its type, nullability,
/// identity, generated expression, default and collation; every constraint and index as PostgreSQL itself prints it (<c>pg_get_constraintdef</c>, <c>indexdef</c>: so unique, partial and filtered
/// indexes, check constraints and foreign key actions all count); and the functions of the schema. Two databases with equal descriptions have the same schema as far as the application can tell.
/// The baseline of a database made without migrations (decision M1-84) compares such descriptions.
/// </summary>
public static class SchemaDescription
{
    /// <summary>The schema's description. Reads inside the given transaction, with the search path set to <paramref name="schema"/> so both sides print names the same way.</summary>
    public static List<string> Describe(DbConnection conn, DbTransaction tx, string schema)
    {
        Exec(conn, tx, $"SET LOCAL search_path TO {Quote(schema)}, public", null);
        var lines = new List<string>();
        void Add(string prefix, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            var p = cmd.CreateParameter(); p.ParameterName = "s"; p.Value = schema; cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            while (r.Read()) lines.Add(prefix + " " + r.GetString(0));
        }
        Add("column", """
            SELECT c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod) || ' notnull=' || a.attnotnull::text || ' identity=' || a.attidentity::text || ' generated=' || a.attgenerated::text
                   || ' default=' || coalesce(pg_get_expr(d.adbin, d.adrelid), '') || ' collation=' || coalesce(a.attcollation::regcollation::text, '') || ' comment=' || coalesce(col_description(c.oid, a.attnum), '')
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = @s AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped AND c.relname <> '__EFMigrationsHistory'
            """);
        Add("constraint", """
            SELECT c.relname || ' ' || con.conname || ' ' || pg_get_constraintdef(con.oid)
            FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @s AND c.relname <> '__EFMigrationsHistory'
            """);
        Add("index", """
            SELECT tablename || ' ' || indexname || ' ' || replace(indexdef, schemaname || '.', 'SCHEMA.')
            FROM pg_indexes WHERE schemaname = @s AND tablename <> '__EFMigrationsHistory'
            """);
        Add("function", """
            SELECT p.proname || ' ' || pg_get_function_arguments(p.oid) || ' ' || p.provolatile::text || p.proparallel::text || ' ' || md5(p.prosrc)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @s AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
            """);
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>The objects EF cannot model and that the baseline adds itself with the search script (<c>SearchSchema</c>): they are left out of the first comparison.</summary>
    public static bool IsSearchObject(string line)
        => line.StartsWith("column catalog_search.document ", StringComparison.Ordinal)
        || line.StartsWith("index catalog_search ix_catalog_search_document ", StringComparison.Ordinal)
        || line.StartsWith("index catalog_search ix_catalog_search_text_trgm ", StringComparison.Ordinal)
        || line.StartsWith("function catalog_tokens_tsvector ", StringComparison.Ordinal);

    /// <summary>What differs: lines the expected schema has that the actual lacks or has differently, and lines the actual has that the expected does not. Empty means equal.</summary>
    public static List<string> Compare(IEnumerable<string> actual, IEnumerable<string> expected, bool ignoreSearchObjects)
    {
        var a = actual.Where(l => !ignoreSearchObjects || !IsSearchObject(l)).ToHashSet(StringComparer.Ordinal);
        var e = expected.Where(l => !ignoreSearchObjects || !IsSearchObject(l)).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        problems.AddRange(e.Where(l => !a.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).Select(l => "expected, not found as such in the database: " + l));
        problems.AddRange(a.Where(l => !e.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).Select(l => "found in the database, not expected: " + l));
        return problems;
    }

    /// <summary>
    /// The newest migration (the list is oldest first) whose result the actual schema equals, or -1 with the differences against the NEWEST one (the most useful list for the operator). The expected
    /// description of a migration is produced lazily and only until one matches.
    /// </summary>
    public static (int Index, List<string> Problems) Choose(IReadOnlyList<string> migrationIds, Func<string, IReadOnlyCollection<string>> expectedFor, IReadOnlyCollection<string> actual)
    {
        List<string>? against = null;
        for (var i = migrationIds.Count - 1; i >= 0; i--)
        {
            var problems = Compare(actual, expectedFor(migrationIds[i]), ignoreSearchObjects: true);
            if (problems.Count == 0) return (i, []);
            against ??= problems;
        }
        return (-1, against ?? ["there are no migrations"]);
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static void Exec(DbConnection conn, DbTransaction tx, string sql, IDictionary<string, object>? parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (parameters is not null) foreach (var (k, v) in parameters) { var p = cmd.CreateParameter(); p.ParameterName = k; p.Value = v; cmd.Parameters.Add(p); }
        cmd.ExecuteNonQuery();
    }
}
