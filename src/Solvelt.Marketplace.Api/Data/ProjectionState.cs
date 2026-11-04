using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// Per aggregate and per field group: the portal <c>createdAt</c> of the last event applied (an older event never overwrites newer
/// state) and the terminal state, if any (cancelled, refunded, delivered ... never reopened by a non-terminal event).
/// </summary>
public sealed class ProjectionState
{
    public string AggregateType { get; set; } = "";
    public string AggregateKey { get; set; } = "";
    public string FieldGroup { get; set; } = "";
    public DateTimeOffset LastAppliedAt { get; set; }
    public string? TerminalState { get; set; }
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
        b.Property(s => s.Version).IsConcurrencyToken();
    }
}
