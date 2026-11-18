using Microsoft.Data.Sqlite;

namespace Solvelt.PayConnector;

public enum BeginKind { Started, Replay, AttemptAlreadyPaid, TooManyRequests }

/// <summary>One row of the ledger. NEVER card data: ids, an amount and the outcome.</summary>
public sealed record LedgerRow(string TransactionId, string AttemptId, string Invoice, long Cents, string State, string? ChargeId, string? ErrorCode, bool Reported);

public sealed record BeginResult(BeginKind Kind, LedgerRow? Row);

/// <summary>
/// The connector's memory, in a SQLite file of its own: what makes it idempotent. A request for a <c>transaction_id</c> it has already seen never reaches Sola again (it replays the stored answer), an attempt
/// that has been paid takes no further request, an attempt takes at most <c>MaxRequestsPerAttempt</c>, and a result that could not yet be reported to the marketplace API is kept until it can.
/// States: <c>processing</c>, <c>approved</c>, <c>declined</c>, <c>error</c>, <c>unknown</c> (the card may have been charged: for a person).
/// </summary>
public sealed class ChargeLedger : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public ChargeLedger(string path)
    {
        SQLitePCL.Batteries_V2.Init();   // Microsoft.Data.Sqlite.Core does not load the native SQLite by itself
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS charges (
              transaction_id TEXT NOT NULL PRIMARY KEY,
              attempt_id     TEXT NOT NULL,
              invoice        TEXT NOT NULL,
              cents          INTEGER NOT NULL,
              state          TEXT NOT NULL,
              charge_id      TEXT NULL,
              error_code     TEXT NULL,
              reported       INTEGER NOT NULL DEFAULT 0,
              created_at     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_charges_attempt ON charges(attempt_id);
            """);
    }

    private void Exec(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    public BeginResult Begin(string transactionId, string attemptId, string invoice, long cents, int maxRequestsPerAttempt, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var existing = Read(tx, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported FROM charges WHERE transaction_id = $t", ("$t", transactionId)).FirstOrDefault();
            if (existing is not null) { tx.Commit(); return new BeginResult(BeginKind.Replay, existing); }
            if (Scalar(tx, "SELECT COUNT(*) FROM charges WHERE attempt_id = $a AND state IN ('approved','processing','unknown')", ("$a", attemptId)) > 0) { tx.Commit(); return new BeginResult(BeginKind.AttemptAlreadyPaid, null); }
            if (Scalar(tx, "SELECT COUNT(*) FROM charges WHERE attempt_id = $a", ("$a", attemptId)) >= maxRequestsPerAttempt) { tx.Commit(); return new BeginResult(BeginKind.TooManyRequests, null); }
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO charges(transaction_id, attempt_id, invoice, cents, state, created_at) VALUES($t,$a,$i,$c,'processing',$n)";
            ins.Parameters.AddWithValue("$t", transactionId); ins.Parameters.AddWithValue("$a", attemptId); ins.Parameters.AddWithValue("$i", invoice); ins.Parameters.AddWithValue("$c", cents); ins.Parameters.AddWithValue("$n", now.ToUnixTimeSeconds());
            ins.ExecuteNonQuery();
            tx.Commit();
            return new BeginResult(BeginKind.Started, new LedgerRow(transactionId, attemptId, invoice, cents, "processing", null, null, false));
        }
    }

    /// <summary><paramref name="cents"/>: what the gateway actually authorised, when that is known (it replaces the amount asked for, so a report says what was really charged).</summary>
    public void Finish(string transactionId, string state, string? chargeId, string? errorCode, long? cents = null)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE charges SET state = $s, charge_id = $c, error_code = $e, cents = COALESCE($n, cents) WHERE transaction_id = $t";
            c.Parameters.AddWithValue("$n", (object?)cents ?? DBNull.Value);
            c.Parameters.AddWithValue("$s", state); c.Parameters.AddWithValue("$c", (object?)chargeId ?? DBNull.Value); c.Parameters.AddWithValue("$e", (object?)errorCode ?? DBNull.Value); c.Parameters.AddWithValue("$t", transactionId);
            c.ExecuteNonQuery();
        }
    }

    public void MarkReported(string transactionId)
    {
        lock (_gate) { using var c = _db.CreateCommand(); c.CommandText = "UPDATE charges SET reported = 1 WHERE transaction_id = $t"; c.Parameters.AddWithValue("$t", transactionId); c.ExecuteNonQuery(); }
    }

    /// <summary>Finished results the marketplace API has not been told about yet (it was down, or answered an error).</summary>
    public IReadOnlyList<LedgerRow> Unreported()
    {
        lock (_gate) return Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported FROM charges WHERE reported = 0 AND state <> 'processing' ORDER BY created_at LIMIT 50");
    }

    public LedgerRow? Find(string transactionId)
    {
        lock (_gate) return Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported FROM charges WHERE transaction_id = $t", ("$t", transactionId)).FirstOrDefault();
    }

    private List<LedgerRow> Read(SqliteTransaction? tx, string sql, params (string, object)[] ps)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        using var r = c.ExecuteReader();
        var rows = new List<LedgerRow>();
        while (r.Read()) rows.Add(new LedgerRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0));
        return rows;
    }

    private long Scalar(SqliteTransaction tx, string sql, params (string, object)[] ps)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(c.ExecuteScalar());
    }

    public void Dispose() => _db.Dispose();
}
