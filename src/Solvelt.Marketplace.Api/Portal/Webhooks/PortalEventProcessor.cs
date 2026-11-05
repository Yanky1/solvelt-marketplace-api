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
        var receivedAt = time.GetUtcNow().ToUniversalTime();
        var raw = evt.RawCreatedAt.ToUniversalTime();
        var ordering = PortalEvent.EffectiveTime(raw, receivedAt);
        if (ordering < raw)
            log.LogWarning("Portal event of type {Type} is dated {AheadSeconds} s in the future; projections order it by receipt time + 5 minutes instead", evt.Type, (int)(raw - receivedAt).TotalSeconds);
        var row = new PortalInboundEvent
        {
            Id = evt.Id, Type = evt.Type, PortalCreatedAt = raw, OrderingAt = ordering,
            ReceivedAt = receivedAt, Status = InboundStatus.Stored, PayloadJson = rawBody,
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
        await tx.CommitAsync(CancellationToken.None);   // a client disconnect must not leave the commit ambiguous
        log.LogInformation("Portal event of type {Type} {Outcome}", evt.Type, outcome);
        return outcome;
    }

    public const string PredecessorDeadMark = "predecessor_dead: an earlier event of this seller order never arrived; reconcile it";

    /// <summary>The note with the predecessor-dead mark FIRST (the rest follows, cut to the column's 500 characters).</summary>
    public static string PredecessorDeadNote(string? note)
    {
        var rest = note is { Length: > 0 } && !note.StartsWith(PredecessorDeadMark, StringComparison.Ordinal) ? "; " + note : "";
        var all = PredecessorDeadMark + rest;
        return all.Length <= 500 ? all : all[..500];
    }

    private void MarkPredecessorDead(PortalInboundEvent row, PortalEvent evt)
    {
        if (!evt.PredecessorDead || evt.Type.StartsWith("booking.", StringComparison.Ordinal)) return;
        row.Note = PredecessorDeadNote(row.Note);
        log.LogError("Portal event of type {Type} says an earlier event of its seller order went dead; the order view may have a gap and needs a person to reconcile", evt.Type);
    }

    private async Task<ProcessOutcome> ApplyAsync(PortalInboundEvent row, PortalEvent evt, CancellationToken ct)
    {
        var handler = registry.Find(evt.Type);
        if (handler is null)
        {
            row.Status = InboundStatus.Stored;
            MarkPredecessorDead(row, evt);   // an unknown type still carries the portal's warning about the gap (review L-a)
            row.Version++;
            return ProcessOutcome.Stored;
        }
        var result = await handler.HandleAsync(new ProjectionContext(db, evt with { CreatedAt = row.OrderingAt }, store, time), ct);
        row.Status = result.Status;
        row.AggregateType = result.AggregateType;
        row.AggregateKey = result.AggregateKey;
        row.Note = result.Note;
        // C14: an earlier event of this seller order went Dead at the portal, so our view of it may have a gap. Booking events reconcile by themselves (BookingEventHandler); for orders the portal offers no read
        // we can use here, so the event is applied as usual and the stored row says "reconcile" where staff look (and the log carries an error): never silent. The mark is set HERE, in the one place that
        // writes the note, so a replay of a parked event (which re-runs this) keeps it, and it goes FIRST so the 500-character limit can never cut it (review M2).
        MarkPredecessorDead(row, evt);
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
    /// Re-applies the parked events of one aggregate, oldest ordering time first, each in its own transaction. Call it when the aggregate appears
    /// (e.g. after our order-ingest call stored the seller order ids). Safe to run concurrently: a row is claimed first (see <see cref="ReplayOneAsync"/>),
    /// and a replay that loses a race or hits a database conflict leaves its event parked for the next pass instead of aborting the rest of the list.
    /// </summary>
    public async Task<int> ReplayParkedAsync(string aggregateType, string aggregateKey, CancellationToken ct = default)
    {
        var ids = await db.PortalInboundEvents.AsNoTracking()
            .Where(e => e.Status == InboundStatus.Parked && e.AggregateType == aggregateType && e.AggregateKey == aggregateKey)
            .OrderBy(e => e.OrderingAt).ThenBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        var applied = 0;
        foreach (var id in ids)
            if (await ReplayOneAsync(id, ct)) applied++;
        // whatever is still parked has now been tried: the sweep serves other aggregates before it comes back to this one
        var now = time.GetUtcNow().ToUniversalTime();
        await db.PortalInboundEvents.Where(e => e.Status == InboundStatus.Parked && e.AggregateType == aggregateType && e.AggregateKey == aggregateKey)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastReplayAt, (DateTimeOffset?)now), ct);
        return applied;
    }

    /// <summary>
    /// A periodic sweep over parked events: up to <paramref name="limit"/> aggregates per call, the ones never tried or tried longest ago first, so
    /// events that never resolve cannot starve the ones that can. Returns how many events left the parked state.
    /// </summary>
    public async Task<int> ReplayAllParkedAsync(int limit = 200, CancellationToken ct = default)
    {
        var keys = await db.PortalInboundEvents.AsNoTracking().Where(e => e.Status == InboundStatus.Parked)
            .GroupBy(e => new { e.AggregateType, e.AggregateKey })
            .Select(g => new { g.Key.AggregateType, g.Key.AggregateKey, LastReplay = g.Max(e => e.LastReplayAt), Oldest = g.Min(e => e.ReceivedAt) })
            .OrderBy(x => x.LastReplay != null).ThenBy(x => x.LastReplay).ThenBy(x => x.Oldest)
            .Take(Math.Max(1, limit)).ToListAsync(ct);
        var n = 0;
        foreach (var k in keys)
            n += await ReplayParkedAsync(k.AggregateType!, k.AggregateKey!, ct);
        return n;
    }

    /// <summary>
    /// Claim first, then apply, in one transaction: the claim is an atomic <c>UPDATE ... WHERE status = parked AND version = v</c>, so of several
    /// concurrent replayers exactly one proceeds and the others (0 rows) skip. A conflict inside the handler's own writes (two replayers creating the
    /// same projection state row on PostgreSQL) rolls the claim back and counts as a lost claim: the event stays parked, nothing is half-applied.
    /// </summary>
    private async Task<bool> ReplayOneAsync(string id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var version = await db.PortalInboundEvents.AsNoTracking().Where(e => e.Id == id && e.Status == InboundStatus.Parked).Select(e => (int?)e.Version).FirstOrDefaultAsync(ct);
        if (version is not { } v) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var claimed = await db.PortalInboundEvents.Where(e => e.Id == id && e.Status == InboundStatus.Parked && e.Version == v)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, v + 1), ct);
            if (claimed == 0) { await tx.RollbackAsync(CancellationToken.None); return false; }
            var row = await db.PortalInboundEvents.FirstAsync(e => e.Id == id, ct);
            if (row.PayloadJson is null || !PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes(row.PayloadJson), out var evt, out _))
            { await tx.RollbackAsync(CancellationToken.None); return false; }
            var outcome = await ApplyAsync(row, evt!, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(CancellationToken.None);
            return outcome != ProcessOutcome.Parked;
        }
        // Any failure of ONE event (a database conflict, or a handler that throws) rolls its transaction back, leaves it parked and lets the rest of the
        // list go on, so one poisoned event cannot stop the others or the LastReplayAt stamp that keeps the sweep fair. Only a cancelled caller stops the replay.
        // Only the exception type is logged: its message may carry data from the event.
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            log.LogWarning("A parked event could not be re-applied now ({ExceptionType}); it stays parked for the next pass", ex.GetType().Name);
            return false;
        }
    }
}
