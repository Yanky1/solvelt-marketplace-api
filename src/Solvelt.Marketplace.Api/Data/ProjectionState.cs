using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// Per aggregate and per field group: the portal <c>createdAt</c> of the last event applied (an older event never overwrites newer
/// state). The terminal state of an aggregate lives on ONE row per aggregate (field group <c>$terminal</c>), so it governs every field group.
/// </summary>
public sealed class ProjectionState
{
    public string AggregateType { get; set; } = "";
    public string AggregateKey { get; set; } = "";
    public string FieldGroup { get; set; } = "";
    public DateTimeOffset LastAppliedAt { get; set; }
    /// <summary>Id of the event that set <see cref="LastAppliedAt"/>: the deterministic tie-breaker when two events carry the same stored time.</summary>
    public string? LastEventId { get; set; }
    public string? TerminalState { get; set; }
    /// <summary>The highest portal <c>sequence</c> applied in this group (C17.1: events of one scope such as <c>review:{id}</c> carry an increasing integer); null until an event with a sequence was applied.</summary>
    public long? LastSequence { get; set; }
    public int Version { get; set; }
}

internal sealed class ProjectionStateConfiguration : IEntityTypeConfiguration<ProjectionState>
{
    public void Configure(EntityTypeBuilder<ProjectionState> b)
    {
        b.ToTable("projection_states");
        b.HasKey(s => new { s.AggregateType, s.AggregateKey, s.FieldGroup });
        b.Property(s => s.AggregateType).HasMaxLength(50);
        b.Property(s => s.AggregateKey).HasMaxLength(200);
        b.Property(s => s.FieldGroup).HasMaxLength(100);
        b.Property(s => s.TerminalState).HasMaxLength(50);
        b.Property(s => s.LastEventId).HasMaxLength(100);
        b.Property(s => s.Version).IsConcurrencyToken();
    }
}
