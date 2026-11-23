using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Decision V-44: SQLite (development and test) refusing to open or begin with BUSY/LOCKED is retried briefly, and what still fails is a 503 too_busy, never a 500. PostgreSQL gets none of it.</summary>
public sealed class SqliteBusyRetryTests
{
    private static SqliteException Busy(int code = 5) => new("database is locked", code);

    [Fact]
    public void A_busy_answer_is_retried_and_the_later_attempt_wins()
    {
        var calls = 0;
        var r = SqliteBusyRetry.Run(() => { if (++calls < 3) throw Busy(); return "opened"; });
        Assert.Equal(("opened", 3), (r, calls));
        calls = 0;
        Assert.Equal("locked-then-fine", SqliteBusyRetry.Run(() => { if (++calls < 2) throw Busy(6); return "locked-then-fine"; }));
    }

    [Fact]
    public async Task The_async_retry_is_the_same()
    {
        var calls = 0;
        var r = await SqliteBusyRetry.RunAsync(() => { if (++calls < 4) throw Busy(); return Task.FromResult("ok"); }, CancellationToken.None);
        Assert.Equal(("ok", 4), (r, calls));
    }

    [Fact]
    public void A_busy_answer_that_never_clears_escapes_after_the_attempts_and_in_well_under_a_second()
    {
        var calls = 0;
        var sw = Stopwatch.StartNew();
        Assert.Throws<SqliteException>(() => SqliteBusyRetry.Run<int>(() => { calls++; throw Busy(); }));
        Assert.Equal(SqliteBusyRetry.Attempts, calls);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 1000);
    }

    [Theory]
    [InlineData(19)]   // a constraint (unique, not null, foreign key) is a real answer, not a lock
    [InlineData(1)]    // a SQL error
    public void Anything_but_busy_or_locked_is_not_retried(int code)
    {
        var calls = 0;
        Assert.Throws<SqliteException>(() => SqliteBusyRetry.Run<int>(() => { calls++; throw new SqliteException("no", code); }));
        Assert.Equal(1, calls);
        Assert.False(SqliteBusyRetry.IsBusy(new InvalidOperationException("x")));
        Assert.True(SqliteBusyRetry.IsBusy(new InvalidOperationException("wrapped", Busy())));
    }

    [Fact]
    public async Task The_opening_interceptor_opens_the_connection_itself_through_the_retries()
    {
        var c = new FlakyConnection(failures: 2);
        var result = await new SqliteBusyRetry.Opening().ConnectionOpeningAsync(c, null!, default);
        Assert.True(result.IsSuppressed);   // EF must not open it again
        Assert.Equal((3, ConnectionState.Open), (c.OpenCalls, c.State));
        var sync = new FlakyConnection(failures: 1);
        Assert.True(new SqliteBusyRetry.Opening().ConnectionOpening(sync, null!, default).IsSuppressed);
        Assert.Equal((2, ConnectionState.Open), (sync.OpenCalls, sync.State));
    }

    [Fact]
    public async Task An_open_that_keeps_failing_escapes_the_interceptor_as_the_busy_error()
    {
        var c = new FlakyConnection(failures: 99);
        await Assert.ThrowsAsync<SqliteException>(async () => await new SqliteBusyRetry.Opening().ConnectionOpeningAsync(c, null!, default));
        Assert.Equal(SqliteBusyRetry.Attempts, c.OpenCalls);
    }

    private static async Task<(int Status, string? RetryAfter, string Code)> Through(Exception thrown)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await new ProblemExceptionMiddleware(_ => throw thrown, NullLogger<ProblemExceptionMiddleware>.Instance).InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var code = body.Length == 0 ? "" : JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
        return (ctx.Response.StatusCode, ctx.Response.Headers.RetryAfter.ToString() is { Length: > 0 } s ? s : null, code);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task A_busy_error_that_escapes_is_a_503_too_busy_with_retry_after_never_a_500(int code)
    {
        Assert.Equal((503, "1", "too_busy"), await Through(Busy(code)));
        Assert.Equal((503, "1", "too_busy"), await Through(new InvalidOperationException("wrapped by EF", Busy(code))));
    }

    [Fact]
    public async Task Only_busy_and_locked_become_503_and_the_existing_too_busy_still_does()
    {
        Assert.Equal(500, (await Through(new SqliteException("constraint", 19))).Status);
        Assert.Equal(500, (await Through(new InvalidOperationException("boom"))).Status);
        Assert.Equal((503, "1", "too_busy"), await Through(new TooBusyException("busy", Busy())));
        Assert.Equal((503, "1", "too_busy"), await Through(new TooBusyException("busy")));
    }

    private static IEnumerable<IInterceptor> InterceptorsOf(string provider, string cs)
    {
        var o = new DbContextOptionsBuilder<MarketplaceDb>();
        SqliteBusyRetry.Configure(o, provider, cs, "Data Source=unused.db");
        return o.Options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? [];
    }

    [Fact]
    public void The_retry_is_registered_for_sqlite_and_never_for_postgres()
    {
        var sqlite = InterceptorsOf("Sqlite", "Data Source=x.db").ToList();
        Assert.Contains(sqlite, i => i is SqliteBusyRetry.Opening);
        Assert.Contains(sqlite, i => i is SqliteBusyRetry.Beginning);
        Assert.DoesNotContain(InterceptorsOf("Postgres", "Host=localhost;Database=x;Username=u;Password=p"), i => i is SqliteBusyRetry.Opening or SqliteBusyRetry.Beginning);
        Assert.Empty(InterceptorsOf("postgres", "Host=localhost;Database=x;Username=u;Password=p"));
    }

    [Fact]
    public async Task A_real_sqlite_context_still_opens_queries_and_commits_through_the_interceptors()
    {
        var path = Path.Combine(Path.GetTempPath(), "v44-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var o = new DbContextOptionsBuilder<MarketplaceDb>();
            SqliteBusyRetry.Configure(o, "Sqlite", $"Data Source={path}", "unused");
            await using var db = new MarketplaceDb(o.Options);
            await db.Database.EnsureCreatedAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("INSERT INTO \"rating_aggregates\" (\"Scope\", \"ScopeId\", \"Count\", \"Sum\", \"Stars1\", \"Stars2\", \"Stars3\", \"Stars4\", \"Stars5\") VALUES ('x', 'y', 0, 0, 0, 0, 0, 0, 0)"));
            await tx.CommitAsync();
            Assert.Equal(1, await db.RatingAggregates.CountAsync());
        }
        finally { foreach (var f in new[] { path, path + "-shm", path + "-wal" }) { try { File.Delete(f); } catch (IOException) { } } }
    }

    /// <summary>A connection whose first <paramref name="failures"/> opens answer SQLITE_BUSY.</summary>
    private sealed class FlakyConnection(int failures) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public int OpenCalls { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() { if (++OpenCalls <= failures) throw Busy(); _state = ConnectionState.Open; }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
