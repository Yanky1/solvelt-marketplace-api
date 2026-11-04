using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class InboundStatus
{
    /// <summary>A handler applied it to the read model.</summary>
    public const string Applied = "applied";
    /// <summary>Its aggregate is not known yet; re-applied when the aggregate appears.</summary>
    public const string Parked = "parked";
    /// <summary>No handler for this event type (forward compatible): stored and acknowledged.</summary>
    public const string Stored = "stored";
    /// <summary>A handler saw it and deliberately did nothing (older than applied state, or a terminal state must not reopen).</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// One row per portal event id: the idempotency and replay guard of <c>POST /api/portal/webhooks</c>, and the generic store of
/// every event we received (unknown types included). Inserted in the same transaction as the projection change.
/// </summary>
public sealed class PortalInboundEvent
{
    /// <summary>The portal's event id (from the signed body), unique.</summary>
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>The portal's <c>createdAt</c> (UTC): the ordering key of the projections.</summary>
    public DateTimeOffset PortalCreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Status { get; set; } = InboundStatus.Stored;
    public string? AggregateType { get; set; }
    public string? AggregateKey { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    /// <summary>The raw signed body. Nulled by retention (see the PII table in DECISIONS); parked events keep it until applied.</summary>
    public string? PayloadJson { get; set; }
    public DateTimeOffset? PayloadPurgedAt { get; set; }
    /// <summary>Concurrency token: two replayers of the same parked event cannot both apply it.</summary>
    public int Version { get; set; }
}

internal sealed class PortalInboundEventConfiguration : IEntityTypeConfiguration<PortalInboundEvent>
{
    public void Configure(EntityTypeBuilder<PortalInboundEvent> b)
    {
        b.ToTable("portal_inbound_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasMaxLength(100);
        b.Property(e => e.Type).HasMaxLength(200).IsRequired();
        b.Property(e => e.Status).HasMaxLength(20).IsRequired();
        b.Property(e => e.AggregateType).HasMaxLength(50);
        b.Property(e => e.AggregateKey).HasMaxLength(200);
        b.Property(e => e.Note).HasMaxLength(500);
        b.Property(e => e.Version).IsConcurrencyToken();
        b.HasIndex(e => new { e.Status, e.AggregateType, e.AggregateKey, e.PortalCreatedAt });
        b.HasIndex(e => e.ReceivedAt);
    }
}
