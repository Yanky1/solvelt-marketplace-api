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

public sealed record ProjectionContext(MarketplaceDb Db, PortalEvent Event, ProjectionStore Store, TimeProvider Time)
{
    /// <summary>
    /// <see cref="ProjectionStore.TryAdvanceAsync"/> for this event: its id (the tie-breaker) and its ordering time. Call this before changing the aggregate;
    /// when it returns false return <see cref="HandlerOutcome.Skipped"/>.
    /// </summary>
    public Task<bool> AdvanceAsync(string aggregateType, string aggregateKey, string fieldGroup, string? terminalState = null, CancellationToken ct = default)
        => Store.TryAdvanceAsync(aggregateType, aggregateKey, fieldGroup, Event.Id, Event.CreatedAt, terminalState, ct);

    /// <summary>
    /// The ordering of C17.1 for events whose scope the portal numbers: when the event carries <c>sequenceScope</c> equal to <paramref name="expectedScope"/> and a <c>sequence</c>, it is applied only if that
    /// sequence is greater than the last one applied in this group (an older or equal one, such as a dead event re-queued after its successors, is skipped). Without them, or for another scope, ARRIVAL order
    /// decides (see <see cref="ProjectionStore.TryAdvanceByArrivalAsync"/>). Call it before changing the aggregate; when it returns false return <see cref="HandlerOutcome.Skipped"/>.
    /// </summary>
    public Task<bool> AdvanceBySequenceAsync(string aggregateType, string aggregateKey, string fieldGroup, string expectedScope, CancellationToken ct = default)
        => Event is { Sequence: { } sequence, SequenceScope: { } scope } && string.Equals(scope, expectedScope, StringComparison.Ordinal)
            ? Store.TryAdvanceBySequenceAsync(aggregateType, aggregateKey, fieldGroup, sequence, Event.Id, Event.CreatedAt, ct)
            : Store.TryAdvanceByArrivalAsync(aggregateType, aggregateKey, fieldGroup, Event.Id, Event.CreatedAt, ct);
}

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

/// <summary>The terminal state names handlers pass to <see cref="ProjectionContext.AdvanceAsync"/> for orders.</summary>
public static class TerminalStates
{
    public const string Cancelled = "cancelled", Refunded = "refunded", Delivered = "delivered", Completed = "completed";
}

/// <summary>
/// Which terminal-to-terminal steps are legal, per aggregate type (decision O-10, following the portal's state machine). Terminal states are
/// absorbing: the same state again is fine (partial refunds repeat <c>refunded</c>), any other step must be listed. An aggregate type with no rules is
/// absorbing. Orders: delivered to completed or refunded, completed to refunded (goodwill); cancelled and refunded never change.
/// </summary>
public sealed class TerminalStateRules
{
    private readonly Dictionary<string, HashSet<(string From, string To)>> _allowed = new(StringComparer.Ordinal);

    public TerminalStateRules()
    {
        Allow("order", TerminalStates.Delivered, TerminalStates.Completed);
        Allow("order", TerminalStates.Delivered, TerminalStates.Refunded);
        Allow("order", TerminalStates.Completed, TerminalStates.Refunded);
    }

    public TerminalStateRules Allow(string aggregateType, string from, string to)
    {
        if (!_allowed.TryGetValue(aggregateType, out var set)) _allowed[aggregateType] = set = [];
        set.Add((from, to));
        return this;
    }

    public bool Allows(string aggregateType, string from, string to)
        => from == to || (_allowed.TryGetValue(aggregateType, out var set) && set.Contains((from, to)));
}

/// <summary>Ordering and terminal-state rules shared by every handler (CONTRACT.md §1.2 point 3).</summary>
public sealed class ProjectionStore(MarketplaceDb db, TerminalStateRules rules)
{
    /// <summary>The field group that holds an aggregate's terminal state: one row per aggregate, so the state governs every field group.</summary>
    public const string TerminalGroup = "$terminal";

    /// <summary>Times are compared at PostgreSQL's microsecond precision, so a value never compares differently before and after it is stored.</summary>
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Ticks - u.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>Older by time, or at the same stored time with a smaller event id: a total order, so the result does not depend on arrival order.</summary>
    private static bool IsOlder(DateTimeOffset at, string eventId, ProjectionState s)
        => at < s.LastAppliedAt || (at == s.LastAppliedAt && string.CompareOrdinal(eventId, s.LastEventId ?? "") < 0);

    /// <summary>
    /// Records that the event (<paramref name="eventId"/>, ordering time <paramref name="createdAt"/>) is being applied to (aggregate, field group), or says
    /// it must not be. False when: the aggregate is in a terminal state and this event is not a terminal one (terminal states are never reopened, whichever
    /// field group the event belongs to); the event is terminal but the step from the current terminal state is not allowed by <see cref="TerminalStateRules"/>
    /// (delivered then cancelled; cancelled then anything else); or it is older than what the group, or the terminal state, already saw. Same-time events are
    /// ordered by event id. Two events racing to create the same state row conflict on the key or the version: the loser's transaction rolls back (5xx) and
    /// the portal retries.
    /// </summary>
    public async Task<bool> TryAdvanceAsync(string aggregateType, string aggregateKey, string fieldGroup, string eventId, DateTimeOffset createdAt,
        string? terminalState = null, CancellationToken ct = default)
    {
        if (fieldGroup == TerminalGroup) throw new ArgumentException($"'{TerminalGroup}' is reserved.", nameof(fieldGroup));
        createdAt = ToStoredPrecision(createdAt);

        var terminal = await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, TerminalGroup], ct);
        if (terminal?.TerminalState is { } current)
        {
            if (terminalState is null) return false;
            if (!rules.Allows(aggregateType, current, terminalState)) return false;
            if (IsOlder(createdAt, eventId, terminal)) return false;
        }
        var group = await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, fieldGroup], ct);
        if (group is not null && IsOlder(createdAt, eventId, group)) return false;

        Touch(group, aggregateType, aggregateKey, fieldGroup, createdAt, eventId, null);
        if (terminalState is not null) Touch(terminal, aggregateType, aggregateKey, TerminalGroup, createdAt, eventId, terminalState);
        return true;
    }

    /// <summary>
    /// C17.1: the event is applied only if its portal <paramref name="sequence"/> is greater than the last one applied in this (aggregate, group). Equal or lower is a duplicate or a late older event
    /// (a dead one re-queued after its successors): its successors, which carry the later state, are already applied, so it is skipped. The time and the id play no part.
    /// </summary>
    public async Task<bool> TryAdvanceBySequenceAsync(string aggregateType, string aggregateKey, string fieldGroup, long sequence, string eventId, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (fieldGroup == TerminalGroup) throw new ArgumentException($"'{TerminalGroup}' is reserved.", nameof(fieldGroup));
        var group = await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, fieldGroup], ct);
        if (group?.LastSequence is { } last && sequence <= last) return false;
        Touch(group, aggregateType, aggregateKey, fieldGroup, ToStoredPrecision(createdAt), eventId, null, sequence);
        return true;
    }

    /// <summary>
    /// The order for events that carry no sequence: ARRIVAL order. An event is applied unless its time is STRICTLY older than what the group already applied (a re-queued old event); the same time is
    /// applied in the order the events arrived (a random event id is no order: two edits in the same instant must not be ranked by it). The portal delivers one scope strictly in order, so this is its order.
    /// </summary>
    public async Task<bool> TryAdvanceByArrivalAsync(string aggregateType, string aggregateKey, string fieldGroup, string eventId, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (fieldGroup == TerminalGroup) throw new ArgumentException($"'{TerminalGroup}' is reserved.", nameof(fieldGroup));
        createdAt = ToStoredPrecision(createdAt);
        var group = await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, fieldGroup], ct);
        if (group is not null && createdAt < group.LastAppliedAt) return false;
        Touch(group, aggregateType, aggregateKey, fieldGroup, createdAt, eventId, null);
        return true;
    }

    private void Touch(ProjectionState? row, string aggregateType, string aggregateKey, string fieldGroup, DateTimeOffset at, string eventId, string? terminalState, long? sequence = null)
    {
        if (row is null)
        {
            db.ProjectionStates.Add(new ProjectionState { AggregateType = aggregateType, AggregateKey = aggregateKey, FieldGroup = fieldGroup, LastAppliedAt = at, LastEventId = eventId, TerminalState = terminalState, LastSequence = sequence });
            return;
        }
        row.LastAppliedAt = at;
        row.LastEventId = eventId;
        if (sequence is not null) row.LastSequence = sequence;
        if (terminalState is not null) row.TerminalState = terminalState;
        row.Version++;
    }

    public async Task<string?> TerminalStateAsync(string aggregateType, string aggregateKey, CancellationToken ct = default)
        => (await db.ProjectionStates.FindAsync([aggregateType, aggregateKey, TerminalGroup], ct))?.TerminalState;

    public async Task<bool> IsTerminalAsync(string aggregateType, string aggregateKey, CancellationToken ct = default)
        => await TerminalStateAsync(aggregateType, aggregateKey, ct) is not null;
}
