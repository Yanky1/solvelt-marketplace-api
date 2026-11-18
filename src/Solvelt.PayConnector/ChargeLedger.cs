using Microsoft.Data.Sqlite;

namespace Solvelt.PayConnector;

public enum BeginKind { Started, Replay, AttemptAlreadyPaid, TooManyRequests }

/// <summary>One row of the ledger. NEVER card data: ids, an amount and the outcome. A <c>save</c> row (card tokenisation, V-16) also keeps the processor's TOKEN, brand, last four digits and expiry until the marketplace API has them: a token is a credential for OUR processor account, not a card number.</summary>
public sealed record LedgerRow(string TransactionId, string AttemptId, string Invoice, long Cents, string State, string? ChargeId, string? ErrorCode, bool Reported,
    string Kind = "sale", string? Token = null, string? Brand = null, string? Last4 = null, string? Exp = null);

public sealed record BeginResult(BeginKind Kind, LedgerRow? Row);

/// <summary>
/// The connector's memory, in a SQLite file of its own: what makes it idempotent. A request for a <c>transaction_id</c> it has already seen never reaches Sola again (it replays the stored answer), an attempt
/// that has been paid takes no further request, an attempt takes at most <c>MaxRequestsPerAttempt</c>, and a result that could not yet be reported to the marketplace API is kept until it can.
/// States: <c>processing</c>, <c>approved</c>, <c>declined</c>, <c>error</c>, <c>unknown</c> (the card may have been charged: for a person).
/// </summary>
public class ChargeLedger : IDisposable
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
              created_at     INTEGER NOT NULL,
              kind           TEXT NOT NULL DEFAULT 'sale',
              token          TEXT NULL,
              brand          TEXT NULL,
              last4          TEXT NULL,
              exp            TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_charges_attempt ON charges(attempt_id);
            """);
        // a ledger file made before card saving (V-16) lacks the new columns
        foreach (var (col, ddl) in new[] { ("kind", "ALTER TABLE charges ADD COLUMN kind TEXT NOT NULL DEFAULT 'sale'"), ("token", "ALTER TABLE charges ADD COLUMN token TEXT NULL"), ("brand", "ALTER TABLE charges ADD COLUMN brand TEXT NULL"), ("last4", "ALTER TABLE charges ADD COLUMN last4 TEXT NULL"), ("exp", "ALTER TABLE charges ADD COLUMN exp TEXT NULL") })
            if (Scalar(null, $"SELECT COUNT(*) FROM pragma_table_info('charges') WHERE name = '{col}'") == 0) Exec(ddl);
    }

    private void Exec(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    public BeginResult Begin(string transactionId, string attemptId, string invoice, long cents, int maxRequestsPerAttempt, DateTimeOffset now, string kind = "sale")
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var existing = Read(tx, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported, kind, token, brand, last4, exp FROM charges WHERE transaction_id = $t", ("$t", transactionId)).FirstOrDefault();
            if (existing is not null) { tx.Commit(); return new BeginResult(BeginKind.Replay, existing); }
            if (Scalar(tx, "SELECT COUNT(*) FROM charges WHERE attempt_id = $a AND state IN ('approved','processing','unknown')", ("$a", attemptId)) > 0) { tx.Commit(); return new BeginResult(BeginKind.AttemptAlreadyPaid, null); }
            if (Scalar(tx, "SELECT COUNT(*) FROM charges WHERE attempt_id = $a", ("$a", attemptId)) >= maxRequestsPerAttempt) { tx.Commit(); return new BeginResult(BeginKind.TooManyRequests, null); }
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO charges(transaction_id, attempt_id, invoice, cents, state, created_at, kind) VALUES($t,$a,$i,$c,'processing',$n,$k)";
            ins.Parameters.AddWithValue("$t", transactionId); ins.Parameters.AddWithValue("$a", attemptId); ins.Parameters.AddWithValue("$i", invoice); ins.Parameters.AddWithValue("$c", cents); ins.Parameters.AddWithValue("$n", now.ToUnixTimeSeconds()); ins.Parameters.AddWithValue("$k", kind);
            ins.ExecuteNonQuery();
            tx.Commit();
            return new BeginResult(BeginKind.Started, new LedgerRow(transactionId, attemptId, invoice, cents, "processing", null, null, false, kind));
        }
    }

    /// <summary><paramref name="cents"/>: what the gateway actually authorised, when that is known (it replaces the amount asked for, so a report says what was really charged).</summary>
    public virtual void Finish(string transactionId, string state, string? chargeId, string? errorCode, long? cents = null)
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

    /// <summary>A card was saved at the processor: the row keeps the token and the display facts until the marketplace API has them (then <see cref="ClearToken"/>).</summary>
    public virtual void FinishSave(string transactionId, string state, string? token, string? brand, string? last4, string? exp, string? errorCode)
    {
        lock (_gate) Exec2("UPDATE charges SET state = $s, token = $k, brand = $b, last4 = $l, exp = $e, error_code = $c WHERE transaction_id = $t",
            ("$s", state), ("$k", (object?)token ?? DBNull.Value), ("$b", (object?)brand ?? DBNull.Value), ("$l", (object?)last4 ?? DBNull.Value), ("$e", (object?)exp ?? DBNull.Value), ("$c", (object?)errorCode ?? DBNull.Value), ("$t", transactionId));
    }

    /// <summary>The API has the token: it is erased from the ledger (the row stays as proof that the save happened).</summary>
    public void ClearToken(string transactionId) { lock (_gate) Exec2("UPDATE charges SET token = NULL WHERE transaction_id = $t", ("$t", transactionId)); }

    /// <summary>A token the API has not acknowledged after <paramref name="cutoff"/> is WIPED (a token must not pile up in this file): the row becomes <c>abandoned</c> (<c>token_not_delivered</c>), which is reported to the API as an unclear save, and the rows are returned for the log.</summary>
    public IReadOnlyList<LedgerRow> WipeStaleTokens(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var rows = Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported, kind, token, brand, last4, exp FROM charges WHERE kind = 'save' AND token IS NOT NULL AND created_at <= $c", ("$c", cutoff.ToUnixTimeSeconds()));
            foreach (var r in rows) Exec2("UPDATE charges SET token = NULL, state = 'abandoned', error_code = 'token_not_delivered', reported = 0 WHERE transaction_id = $t", ("$t", r.TransactionId));
            return rows;
        }
    }

    /// <summary>Rows still "processing" that were created before <paramref name="cutoff"/> become "unknown" (the request that owned them died after the sale may have started). Returns them, for the log.</summary>
    public IReadOnlyList<LedgerRow> SweepStuck(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var rows = Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported, kind, token, brand, last4, exp FROM charges WHERE state = 'processing' AND created_at <= $c", ("$c", cutoff.ToUnixTimeSeconds()));
            foreach (var r in rows) Exec2("UPDATE charges SET state = 'unknown', error_code = 'stuck_processing' WHERE transaction_id = $t AND state = 'processing'", ("$t", r.TransactionId));
            return rows;
        }
    }

    private void Exec2(string sql, params (string, object)[] ps) { using var c = _db.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v); c.ExecuteNonQuery(); }

    public void MarkReported(string transactionId)
    {
        lock (_gate) { using var c = _db.CreateCommand(); c.CommandText = "UPDATE charges SET reported = 1 WHERE transaction_id = $t"; c.Parameters.AddWithValue("$t", transactionId); c.ExecuteNonQuery(); }
    }

    /// <summary>Finished results the marketplace API has not been told about yet (it was down, or answered an error).</summary>
    public IReadOnlyList<LedgerRow> Unreported()
    {
        lock (_gate) return Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported, kind, token, brand, last4, exp FROM charges WHERE reported = 0 AND state <> 'processing' ORDER BY created_at LIMIT 50");
    }

    public LedgerRow? Find(string transactionId)
    {
        lock (_gate) return Read(null, "SELECT transaction_id, attempt_id, invoice, cents, state, charge_id, error_code, reported, kind, token, brand, last4, exp FROM charges WHERE transaction_id = $t", ("$t", transactionId)).FirstOrDefault();
    }

    private List<LedgerRow> Read(SqliteTransaction? tx, string sql, params (string, object)[] ps)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        using var r = c.ExecuteReader();
        var rows = new List<LedgerRow>();
        while (r.Read()) rows.Add(new LedgerRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0, r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return rows;
    }

    private long Scalar(SqliteTransaction? tx, string sql, params (string, object)[] ps)
    {
        using var c = _db.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(c.ExecuteScalar());
    }

    public void Dispose() => _db.Dispose();
}
