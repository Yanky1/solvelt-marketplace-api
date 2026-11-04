using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// Tells a bad record from a bad moment. A <b>deterministic</b> failure (a constraint, a value that does not fit, a logic error on this
/// record) will fail the same way every time, so the record is skipped and the cursor moves on. Anything else (timeout, dropped connection,
/// deadlock, cancelled query, a concurrent update) is <b>transient</b>: it must not be treated as a bad record. The unit of work is rolled
/// back, the cursor stays where it was, and the run fails so the next one retries (with the worker's backoff).
/// </summary>
public static class SyncErrors
{
    public static bool IsDeterministic(Exception ex)
    {
        if (ex is DbUpdateConcurrencyException) return false;      // someone else changed the row: try again, do not skip
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException or OperationCanceledException or IOException or System.Net.Sockets.SocketException: return false;
                case PostgresException p: return p.SqlState.StartsWith("22", StringComparison.Ordinal) || p.SqlState.StartsWith("23", StringComparison.Ordinal);   // data exception, integrity violation
                case NpgsqlException: return false;                // connection level
                case SqliteException s: return s.SqliteErrorCode == 19;    // SQLITE_CONSTRAINT; busy, locked, I/O are transient
            }
        }
        return ex is DbUpdateException or InvalidOperationException;       // EF or our own logic refusing this record (e.g. a duplicate key in one snapshot)
    }
}
