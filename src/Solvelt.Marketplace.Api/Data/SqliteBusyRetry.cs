using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// SQLite is the development and test database. Under contention it can refuse to open a connection or to begin a transaction with SQLITE_BUSY (5) or SQLITE_LOCKED (6)
/// without waiting (a connection opening while another closes or checkpoints the write-ahead log), and an ordinary request then answered 500 (decision V-44).
/// Opening and beginning are retried here a few times with a short pause (under a second in all); what still fails is mapped to 503 too_busy by the exception middleware.
/// Registered for SQLite only, never for PostgreSQL.
/// </summary>
public static class SqliteBusyRetry
{
    /// <summary>Attempts in all, and the pause before each retry; the pauses add up to 105 ms, so the whole retry stays far under a second.</summary>
    public const int Attempts = 4;
    private static readonly int[] PauseMs = [15, 30, 60];

    /// <summary>Busy or locked, directly or wrapped; a unique violation, a syntax error or any other failure is not.</summary>
    public static bool IsBusy(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    public static T Run<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < Attempts - 1) { Thread.Sleep(PauseMs[attempt]); }
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < Attempts - 1) { await Task.Delay(PauseMs[attempt], ct); }
        }
    }

    /// <summary>Opens the connection itself (with the retries) and tells EF not to.</summary>
    public sealed class Opening : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            if (connection.State == ConnectionState.Open) return result;
            Run(() => { connection.Open(); return 0; });
            return InterceptionResult.Suppress();
        }

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (connection.State == ConnectionState.Open) return result;
            await RunAsync(async () => { await connection.OpenAsync(cancellationToken); return 0; }, cancellationToken);
            return InterceptionResult.Suppress();
        }
    }

    /// <summary>Begins the transaction itself (with the retries) and hands EF the result.</summary>
    public sealed class Beginning : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
            => InterceptionResult<DbTransaction>.SuppressWithResult(Run(() => connection.BeginTransaction(eventData.IsolationLevel)));

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
            => InterceptionResult<DbTransaction>.SuppressWithResult(await RunAsync(async () => await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken), cancellationToken));
    }

    /// <summary>The one place the database provider is chosen: SQLite gets the retries, PostgreSQL does not.</summary>
    public static void Configure(DbContextOptionsBuilder o, string provider, string? connectionString, string sqliteDefault)
    {
        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(connectionString);
        else o.UseSqlite(connectionString ?? sqliteDefault).AddInterceptors(new Opening(), new Beginning());
    }
}
