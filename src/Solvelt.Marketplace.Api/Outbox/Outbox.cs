using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Outbox;

public enum OutboxOutcome { Delivered, Retry, Dead }

/// <summary>What a handler says happened. <see cref="Error"/> is a short CODE (stored); <see cref="RetryAfter"/> may lengthen the wait.</summary>
public readonly record struct OutboxResult(OutboxOutcome Outcome, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static OutboxResult Delivered() => new(OutboxOutcome.Delivered);
    public static OutboxResult Retry(string error, TimeSpan? retryAfter = null) => new(OutboxOutcome.Retry, error, retryAfter);
    public static OutboxResult Dead(string error) => new(OutboxOutcome.Dead, error);
}

/// <summary>One handler per message type. Handlers run outside any database transaction (they call the network) and must be idempotent: delivery is at least once.</summary>
public interface IOutboxHandler
{
    IReadOnlyCollection<string> Types { get; }
    Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct);
}

public sealed class OutboxHandlerRegistry
{
    private readonly Dictionary<string, IOutboxHandler> _byType = new(StringComparer.Ordinal);

    public OutboxHandlerRegistry(IEnumerable<IOutboxHandler> handlers)
    {
        foreach (var h in handlers)
            foreach (var t in h.Types)
                if (!_byType.TryAdd(t, h)) throw new InvalidOperationException($"Outbox type '{t}' has more than one handler.");
    }

    public IOutboxHandler? Find(string type) => _byType.GetValueOrDefault(type);
}

/// <summary>Writes messages. Nothing is saved here: the caller's own <c>SaveChanges</c> commits the message together with the change that causes it.</summary>
public sealed class OutboxWriter(MarketplaceDb db, TimeProvider time)
{
    public OutboxMessage Enqueue(string type, object payload, string? dedupeKey = null, TimeSpan? delay = null)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var m = new OutboxMessage
        {
            Id = Guid.NewGuid(), Type = type, DedupeKey = dedupeKey, Payload = JsonSerializer.Serialize(payload, PortalJson.Options),
            Status = OutboxStatus.Pending, NextAttemptAt = now + (delay ?? TimeSpan.Zero), CreatedAt = now,
        };
        db.OutboxMessages.Add(m);
        return m;
    }

    /// <summary>
    /// Producer pattern for a thing that must be sent once (a payment event, an order mail): queues the message in the caller's own transaction
    /// <b>unless the dedupe key is taken</b>, and says which happened. A duplicate is an "already done", never an exception, so it can not roll back the
    /// caller's business change. The insert is <c>ON CONFLICT DO NOTHING</c> (PostgreSQL) / <c>INSERT OR IGNORE</c> (SQLite): it needs no earlier read
    /// and has no race. Call it inside the same transaction as the business change and commit them together.
    /// </summary>
    public async Task<bool> TryEnqueueAsync(string type, object payload, string dedupeKey, TimeSpan? delay = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dedupeKey);
        var now = time.GetUtcNow().ToUniversalTime();
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table("outbox_messages");
        string Col(string property) => "\"" + entity.FindProperty(property)!.GetColumnName(table) + "\"";
        object Time(DateTimeOffset t) => db.Database.IsSqlite() ? new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter().ConvertToProvider(t.ToUniversalTime())! : t.ToUniversalTime();
        var cols = new[] { "Id", "Type", "DedupeKey", "Payload", "Status", "Attempts", "NextAttemptAt", "CreatedAt", "Version" };
        var values = new object[]
        {
            Guid.NewGuid(), type, dedupeKey, JsonSerializer.Serialize(payload, PortalJson.Options), OutboxStatus.Pending, 0,
            Time(now + (delay ?? TimeSpan.Zero)), Time(now), 0,
        };
        var list = string.Join(", ", cols.Select(Col));
        var marks = string.Join(", ", values.Select((_, i) => "{" + i + "}"));
        var sql = db.Database.IsSqlite()
            ? $"INSERT OR IGNORE INTO \"outbox_messages\" ({list}) VALUES ({marks})"
            : $"INSERT INTO \"outbox_messages\" ({list}) VALUES ({marks}) ON CONFLICT ({Col("DedupeKey")}) DO NOTHING";
        return await db.Database.ExecuteSqlRawAsync(sql, values, ct) == 1;
    }

    /// <summary>True when a message with this dedupe key is already queued (or was delivered): the thing is not sent twice.</summary>
    public Task<bool> ExistsAsync(string dedupeKey, CancellationToken ct = default) => db.OutboxMessages.AnyAsync(m => m.DedupeKey == dedupeKey, ct);
}

/// <summary>
/// Delivers due messages at least once. A message is CLAIMED with an atomic <c>UPDATE ... WHERE version = v</c> (so several dispatchers never send the
/// same message), handled outside any transaction, then finished with another compare-and-set. A dispatcher that dies leaves a lease that simply expires.
/// Failures back off exponentially (<see cref="OutboxOptions.BackoffSeconds"/> x 2^(n-1), capped) and become dead after <see cref="OutboxOptions.MaxAttempts"/>.
/// </summary>
public sealed class OutboxDispatcher(MarketplaceDb db, OutboxHandlerRegistry registry, TimeProvider time, IOptions<OutboxOptions> options, OpsCounters counters, ILogger<OutboxDispatcher> log)
{
    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        var due = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now && (m.LockedUntil == null || m.LockedUntil < now))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Select(m => new { m.Id, m.Version }).Take(Math.Max(1, o.BatchSize)).ToListAsync(ct);
        var handled = 0;
        foreach (var candidate in due)
            if (await TryHandleAsync(candidate.Id, candidate.Version, ct)) handled++;
        return handled;
    }

    private async Task<bool> TryHandleAsync(Guid id, int version, CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        var lease = now.AddSeconds(Math.Max(5, o.LeaseSeconds));
        var claimedVersion = version + 1;
        var claimed = await db.OutboxMessages.Where(m => m.Id == id && m.Status == OutboxStatus.Pending && m.Version == version && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, (DateTimeOffset?)lease).SetProperty(m => m.Version, claimedVersion), ct);
        if (claimed == 0) return false;   // another dispatcher got it

        var message = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id, ct);
        OutboxResult result;
        try
        {
            var handler = registry.Find(message.Type);
            result = handler is null ? OutboxResult.Retry("no_handler") : await handler.HandleAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result = OutboxResult.Retry(ex.GetType().Name);   // the type only: a message may carry PII or portal payload
            log.LogWarning("Outbox message of type {Type} failed with {ExceptionType} (attempt {Attempt})", message.Type, ex.GetType().FullName, message.Attempts + 1);
        }
        await FinishAsync(message, claimedVersion, result);
        return true;
    }

    private async Task FinishAsync(OutboxMessage message, int claimedVersion, OutboxResult result)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        var attempts = message.Attempts + 1;
        var error = result.Error is { Length: > 200 } e ? e[..200] : result.Error;
        var id = message.Id;
        int rows;
        if (result.Outcome == OutboxOutcome.Delivered)
        {
            rows = await db.OutboxMessages.Where(m => m.Id == id && m.Version == claimedVersion).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Delivered).SetProperty(m => m.DeliveredAt, (DateTimeOffset?)now).SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null).SetProperty(m => m.LastError, (string?)null).SetProperty(m => m.Version, claimedVersion + 1), CancellationToken.None);
        }
        else if (result.Outcome == OutboxOutcome.Dead || attempts >= Math.Max(1, o.MaxAttempts))
        {
            rows = await db.OutboxMessages.Where(m => m.Id == id && m.Version == claimedVersion).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Dead).SetProperty(m => m.DeadAt, (DateTimeOffset?)now).SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null).SetProperty(m => m.LastError, error).SetProperty(m => m.Version, claimedVersion + 1), CancellationToken.None);
            if (rows == 1) { counters.MessageDied(); log.LogError("Outbox message of type {Type} is dead after {Attempts} attempt(s): {Error}", message.Type, attempts, error); }
        }
        else
        {
            var next = now + Backoff(attempts, result.RetryAfter);
            rows = await db.OutboxMessages.Where(m => m.Id == id && m.Version == claimedVersion).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempts).SetProperty(m => m.NextAttemptAt, next)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null).SetProperty(m => m.LastError, error).SetProperty(m => m.Version, claimedVersion + 1), CancellationToken.None);
        }
        // 0 rows: our lease expired and another dispatcher claimed (and finished) the message meanwhile. Its result stands; ours is dropped.
        if (rows == 0) log.LogWarning("Outbox message of type {Type} finished after its lease had expired; the newer claim's result stands", message.Type);
    }

    /// <summary>BackoffSeconds x 2^(attempt-1), capped; a handler's own RetryAfter can only lengthen it.</summary>
    public TimeSpan Backoff(int attempt, TimeSpan? retryAfter = null)
    {
        var o = options.Value;
        var seconds = Math.Min(Math.Max(0, o.BackoffSeconds) * Math.Pow(2, Math.Max(0, attempt - 1)), Math.Max(1, o.MaxBackoffSeconds));
        var computed = TimeSpan.FromSeconds(seconds);
        return retryAfter is { } ra && ra > computed ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, Math.Max(1, o.MaxBackoffSeconds))) : computed;
    }
}

public sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<OutboxOptions> options, ILogger<OutboxWorker> log, TimeProvider time) : BackgroundService
{
    private readonly Solvelt.Marketplace.Api.Auth.AuthPurgeSchedule _authPurge = new(TimeSpan.FromMinutes(15));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Outbox worker failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One poll: deliver what is due, purge the outbox, and purge the auth tables when their turn has come (about every 15 minutes, L8).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var d = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        while (await d.DispatchDueAsync(ct) >= Math.Max(1, options.Value.BatchSize)) { }   // keep draining while full batches come back
        await scope.ServiceProvider.GetRequiredService<OutboxMaintenance>().PurgeAsync(ct);
        if (_authPurge.ShouldRun(time.GetUtcNow())) await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Auth.AuthMaintenance>().PurgeAsync(ct);
    }
}

/// <summary>Retention (PII table): delivered payloads go after a day, dead ones after 30 days, rows after 90.</summary>
public sealed class OutboxMaintenance(MarketplaceDb db, IOptions<OutboxOptions> options, TimeProvider time)
{
    public sealed record Result(int PayloadsPurged, int RowsDeleted);

    public async Task<Result> PurgeAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        var deliveredCutoff = now.AddDays(-Math.Max(0, o.DeliveredPayloadRetentionDays));
        var deadCutoff = now.AddDays(-Math.Max(1, o.DeadPayloadRetentionDays));
        var rowCutoff = now.AddDays(-Math.Max(o.DeadPayloadRetentionDays, o.RowRetentionDays));
        var purged = await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Delivered && m.Payload != null && m.DeliveredAt < deliveredCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Payload, (string?)null).SetProperty(m => m.PayloadPurgedAt, (DateTimeOffset?)now), ct);
        purged += await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Dead && m.Payload != null && m.DeadAt < deadCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Payload, (string?)null).SetProperty(m => m.PayloadPurgedAt, (DateTimeOffset?)now), ct);
        var deleted = await db.OutboxMessages.Where(m => (m.Status == OutboxStatus.Delivered && m.DeliveredAt < rowCutoff) || (m.Status == OutboxStatus.Dead && m.DeadAt < rowCutoff))
            .ExecuteDeleteAsync(ct);
        return new Result(purged, deleted);
    }
}
