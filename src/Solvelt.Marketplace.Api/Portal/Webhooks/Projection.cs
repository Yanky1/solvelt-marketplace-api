using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

/// <summary>What a handler did with an event. Everything a handler writes through <see cref="ProjectionContext.Db"/> commits (or rolls back) with the event row.</summary>
public sealed record HandlerOutcome(string Status, string? AggregateType = null, string? AggregateKey = null, string? Note = null)
{
    public static HandlerOutcome Applied() => new(InboundStatus.Applied);
    /// <summary>The aggregate is unknown yet (e.g. <c>order.shipped</c> before our ingest call returned): re-applied when it appears.</summary>
    public static HandlerOutcome Parked(string aggregateType, string aggregateKey, string? note = null) => new(InboundStatus.Parked, aggregateType, aggregateKey, note);
    /// <summary>Deliberately not applied: older than the applied state, or a terminal state must not reopen.</summary>
    public static HandlerOutcome Skipped(string reason) => new(InboundStatus.Skipped, Note: reason);
}

public sealed record ProjectionContext(MarketplaceDb Db, PortalEvent Event, ProjectionStore Store, TimeProvider Time);

/// <summary>One handler per event type. Handlers must be idempotent and must not call out over the network (ack fast: heavy work goes to the outbox).</summary>
public interface IProjectionHandler
{
    IReadOnlyCollection<string> EventTypes { get; }
    Task<HandlerOutcome> HandleAsync(ProjectionContext context, CancellationToken ct);
}

public sealed class ProjectionRegistry
{
    private readonly Dictionary<string, IProjectionHandler> _byType = new(StringComparer.Ordinal);

    public ProjectionRegistry(IEnumerable<IProjectionHandler> handlers)
    {
        foreach (var h in handlers)
            foreach (var type in h.EventTypes)
                if (!_byType.TryAdd(type, h))
                    throw new InvalidOperationException($"Event type '{type}' has more than one projection handler ({_byType[type].GetType().Name}, {h.GetType().Name}).");
    }

    public IProjectionHandler? Find(string eventType) => _byType.GetValueOrDefault(eventType);
    public IReadOnlyCollection<string> RegisteredTypes => _byType.Keys;
}

/// <summary>Ordering and terminal-state rules shared by every handler (CONTRACT.md §1.2 point 3).</summary>
public sealed class ProjectionStore(MarketplaceDb db)
{
    /// <summary>
    /// Records that the event with portal time <paramref name="createdAt"/> is being applied to (aggregate, field group), or says it must not be.
    /// False when the group already saw a strictly newer event (an older event never overwrites newer state), or when the aggregate is in
    /// a terminal state and this event is not itself a terminal one (terminal states are never reopened). Equal times apply.
    /// A terminal event after a terminal state is allowed when it is not older (delivered then refunded).
    /// Two events racing to create the same state row conflict on the key or the version: the loser's transaction rolls back (5xx) and the portal retries.
    /// </summary>
    public async Task<bool> TryAdvanceAsync(string aggregateType, string aggregateKey, string fieldGroup, DateTimeOffset createdAt, string? terminalState = null, CancellationToken ct = default)
    {
        createdAt = createdAt.ToUniversalTime();
        var state = await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, fieldGroup], ct);
        if (state is null)
        {
            // A terminal state set under another field group of the same aggregate also blocks non-terminal events here.
            if (terminalState is null && await IsTerminalAsync(aggregateType, aggregateKey, ct)) return false;
            db.ProjectionStates.Add(new ProjectionState { AggregateType = aggregateType, AggregateKey = aggregateKey, FieldGroup = fieldGroup, LastAppliedAt = createdAt, TerminalState = terminalState });
            return true;
        }
        if (state.TerminalState is not null && terminalState is null) return false;
        if (createdAt < state.LastAppliedAt) return false;
        state.LastAppliedAt = createdAt;
        if (terminalState is not null) state.TerminalState = terminalState;
        state.Version++;
        return true;
    }

    public async Task<bool> IsTerminalAsync(string aggregateType, string aggregateKey, CancellationToken ct = default)
        => await db.ProjectionStates.AnyAsync(s => s.AggregateType == aggregateType && s.AggregateKey == aggregateKey && s.TerminalState != null, ct)
           || db.ProjectionStates.Local.Any(s => s.AggregateType == aggregateType && s.AggregateKey == aggregateKey && s.TerminalState != null);
}
