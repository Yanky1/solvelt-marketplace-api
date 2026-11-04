using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class OutboxStatus
{
    public const string Pending = "pending", Delivered = "delivered", Dead = "dead";
}

/// <summary>
/// Our own transactional outbox: calls to the portal and emails are written here in the SAME transaction as the change that causes them, and a
/// dispatcher delivers them at least once with leases, exponential backoff and a dead letter. <see cref="DedupeKey"/> is our idempotency id for the
/// thing being sent (a payment event id, an order number), unique, so the same thing is never queued twice.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    /// <summary>Selects the handler, e.g. <c>email.send</c>, <c>portal.payment-event</c>.</summary>
    public string Type { get; set; } = "";
    public string? DedupeKey { get; set; }
    /// <summary>JSON the handler needs. May hold PII (an email address, a shipping address) or a secret link: it is dropped once delivered (see the PII table).</summary>
    public string? Payload { get; set; }
    public string Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>The lease: a dispatcher that claimed the message owns it until then (a crashed dispatcher's claim simply expires).</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>An error CODE or exception type, never a message (messages can carry PII or portal payload).</summary>
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? DeadAt { get; set; }
    public DateTimeOffset? PayloadPurgedAt { get; set; }
    /// <summary>Concurrency token and claim ticket: every claim and every final update compares and bumps it.</summary>
    public int Version { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.Type).HasMaxLength(100).IsRequired();
        b.Property(m => m.DedupeKey).HasMaxLength(200);
        b.Property(m => m.Status).HasMaxLength(20).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(200);
        b.Property(m => m.Version).IsConcurrencyToken();
        b.HasIndex(m => m.DedupeKey).IsUnique();                       // several NULLs are allowed; a key is used once
        b.HasIndex(m => new { m.Status, m.NextAttemptAt });
        b.HasIndex(m => new { m.Status, m.DeliveredAt });
    }
}
