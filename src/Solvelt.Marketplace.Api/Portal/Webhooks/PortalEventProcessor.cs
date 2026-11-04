using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

public enum ProcessOutcome { Applied, Parked, Stored, Skipped, Duplicate }

/// <summary>
/// Applies a verified portal event. The event-id insert and every read-model change commit in ONE transaction; any failure rolls both
/// back, so the portal's retry sees a clean slate. The id insert is the first statement of the transaction, so concurrent deliveries
/// of the same event serialise on the unique key (the loser is a duplicate and does nothing).
/// </summary>
public sealed class PortalEventProcessor(MarketplaceDb db, ProjectionRegistry registry, ProjectionStore store, TimeProvider time, ILogger<PortalEventProcessor> log)
{
    public async Task<ProcessOutcome> ProcessAsync(PortalEvent evt, string rawBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = new PortalInboundEvent
        {
            Id = evt.Id, Type = evt.Type, PortalCreatedAt = evt.CreatedAt.ToUniversalTime(),
            ReceivedAt = time.GetUtcNow().ToUniversalTime(), Status = InboundStatus.Stored, PayloadJson = rawBody,
        };
        db.PortalInboundEvents.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.PortalInboundEvents.AsNoTracking().AnyAsync(e => e.Id == evt.Id, ct)) return ProcessOutcome.Duplicate;
            throw;
        }

        var outcome = await ApplyAsync(row, evt, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Portal event of type {Type} {Outcome}", evt.Type, outcome);
        return outcome;
    }

    private async Task<ProcessOutcome> ApplyAsync(PortalInboundEvent row, PortalEvent evt, CancellationToken ct)
    {
        var handler = registry.Find(evt.Type);
        if (handler is null)
        {
            row.Status = InboundStatus.Stored;
            row.Version++;
            return ProcessOutcome.Stored;
        }
        var result = await handler.HandleAsync(new ProjectionContext(db, evt, store, time), ct);
        row.Status = result.Status;
        row.AggregateType = result.AggregateType;
        row.AggregateKey = result.AggregateKey;
        row.Note = result.Note;
        row.AppliedAt = result.Status == InboundStatus.Applied ? time.GetUtcNow().ToUniversalTime() : null;
        row.Version++;
        return result.Status switch
        {
            InboundStatus.Applied => ProcessOutcome.Applied,
            InboundStatus.Parked => ProcessOutcome.Parked,
            InboundStatus.Skipped => ProcessOutcome.Skipped,
            _ => throw new InvalidOperationException($"Handler returned unknown status '{result.Status}'."),
        };
    }

    /// <summary>
    /// Re-applies the parked events of one aggregate, oldest portal time first, each in its own transaction. Call it when the aggregate
    /// appears (e.g. after our order-ingest call stored the seller order ids). Safe to run concurrently: a row is claimed by its version.
    /// </summary>
    public async Task<int> ReplayParkedAsync(string aggregateType, string aggregateKey, CancellationToken ct = default)
    {
        var ids = await db.PortalInboundEvents.AsNoTracking()
            .Where(e => e.Status == InboundStatus.Parked && e.AggregateType == aggregateType && e.AggregateKey == aggregateKey)
            .OrderBy(e => e.PortalCreatedAt).ThenBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        var applied = 0;
        foreach (var id in ids)
            if (await ReplayOneAsync(id, ct)) applied++;
        return applied;
    }

    /// <summary>Re-tries up to <paramref name="limit"/> parked events across all aggregates (for a periodic sweep). Returns how many left the parked state.</summary>
    public async Task<int> ReplayAllParkedAsync(int limit = 200, CancellationToken ct = default)
    {
        var keys = await db.PortalInboundEvents.AsNoTracking().Where(e => e.Status == InboundStatus.Parked)
            .OrderBy(e => e.PortalCreatedAt).Take(limit).Select(e => new { e.AggregateType, e.AggregateKey }).ToListAsync(ct);
        var n = 0;
        foreach (var k in keys.Distinct())
            n += await ReplayParkedAsync(k.AggregateType!, k.AggregateKey!, ct);
        return n;
    }

    private async Task<bool> ReplayOneAsync(string id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await db.PortalInboundEvents.FirstOrDefaultAsync(e => e.Id == id && e.Status == InboundStatus.Parked, ct);
            if (row?.PayloadJson is null) { await tx.RollbackAsync(CancellationToken.None); return false; }
            if (!PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes(row.PayloadJson), out var evt, out _)) { await tx.RollbackAsync(CancellationToken.None); return false; }
            var outcome = await ApplyAsync(row, evt!, ct);
            await db.SaveChangesAsync(ct);   // the version check makes a concurrent replayer fail here and roll back its side effects
            await tx.CommitAsync(ct);
            return outcome != ProcessOutcome.Parked;
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
