using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class VoiceOutcome
{
    public const string InProgress = "in_progress", Abandoned = "abandoned", Transferred = "transferred", OrderPlaced = "order_placed",
        /// <summary>The server ended the AI part of the call after too many off-topic requests (V2, V-11).</summary>
        EndedOffTopic = "ended_off_topic";
}

/// <summary>
/// One phone call to the voice agent: metrics only (decision V-01). No audio, no transcript, no address, no card data, no full phone number;
/// the caller is kept as a masked form (last four digits). <see cref="CallSid"/> is SignalWire's call id (needed to match its callbacks).
/// </summary>
public sealed class VoiceCall
{
    public Guid Id { get; set; }
    public string CallSid { get; set; } = "";
    public string CallerMasked { get; set; } = "";
    /// <summary>A keyed hash (HMAC-SHA256, <c>Voice:CallerHashKey</c>) of the caller's number: it lets us count phone orders per number per day without keeping the number. Null when the number was unknown.</summary>
    public string? CallerKey { get; set; }
    /// <summary>The same kind of keyed hash for the number that PROVED itself by SMS code in this call (it can differ from caller ID). Guest order lookups use this one, never caller ID.</summary>
    public string? VerifiedKey { get; set; }
    /// <summary>The model id used for this call, chosen at its start.</summary>
    public string Model { get; set; } = "";
    /// <summary><c>full</c> or <c>mini</c>.</summary>
    public string Tier { get; set; } = "full";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public long AudioInTokens { get; set; }
    public long AudioOutTokens { get; set; }
    public long TextInTokens { get; set; }
    public long TextOutTokens { get; set; }
    /// <summary>Estimated cost in micro-dollars (1 USD = 1,000,000), from the configured prices.</summary>
    public long EstimatedCostMicros { get; set; }
    /// <summary>Latency samples: caller stops speaking to the first agent audio.</summary>
    public int LatencySamples { get; set; }
    public long LatencyTotalMs { get; set; }
    public string Outcome { get; set; } = VoiceOutcome.InProgress;
    public DateTimeOffset? TransferRequestedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class VoiceCallConfiguration : IEntityTypeConfiguration<VoiceCall>
{
    public void Configure(EntityTypeBuilder<VoiceCall> b)
    {
        b.ToTable("voice_calls");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.CallSid).HasMaxLength(64).IsRequired();
        b.Property(c => c.CallerMasked).HasMaxLength(16).IsRequired();
        b.Property(c => c.Model).HasMaxLength(100).IsRequired();
        b.Property(c => c.Tier).HasMaxLength(10).IsRequired();
        b.Property(c => c.Outcome).HasMaxLength(20).IsRequired();
        b.Property(c => c.Version).IsConcurrencyToken();
        b.HasIndex(c => c.CallSid).IsUnique();        // SignalWire retries its webhook: one row per call
        b.Property(c => c.CallerKey).HasMaxLength(64);
        b.HasIndex(c => c.StartedAt);
        b.Property(c => c.VerifiedKey).HasMaxLength(64);
        b.HasIndex(c => new { c.CallerKey, c.StartedAt });
        b.HasIndex(c => c.VerifiedKey);
    }
}

/// <summary>
/// One attempt to pay an order by phone (decision V-07): the amount and invoice the SERVER asked for, and what the provider later said. It outlives the call (a text-link payment can finish after
/// the call ended) and holds no card data, no number and no name: only an order number, an amount and the provider's transaction reference.
/// </summary>
public sealed class VoicePaymentAttempt
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public string OrderNumber { get; set; } = "";
    public string Method { get; set; } = "";
    public long AmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary><c>pending</c>, <c>paid</c>, <c>declined</c>, <c>failed</c>, <c>mismatch</c> (the provider took another amount or invoice: needs a person), <c>duplicate</c> (a second approved sale for an order already paid: needs a refund or void by a person).</summary>
    public string Status { get; set; } = "pending";
    /// <summary>The provider's transaction id (Sola <c>xRefNum</c>): unique, so a repeated notification is the same payment.</summary>
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
}

internal sealed class VoicePaymentAttemptConfiguration : IEntityTypeConfiguration<VoicePaymentAttempt>
{
    public void Configure(EntityTypeBuilder<VoicePaymentAttempt> b)
    {
        b.ToTable("voice_payment_attempts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.OrderNumber).HasMaxLength(40).IsRequired();
        b.Property(a => a.Method).HasMaxLength(20).IsRequired();
        b.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        b.Property(a => a.Status).HasMaxLength(20).IsRequired();
        b.Property(a => a.ProviderRef).HasMaxLength(100);
        b.HasIndex(a => a.ProviderRef).IsUnique();
        b.HasIndex(a => a.OrderNumber);
        b.HasIndex(a => a.CreatedAt);
    }
}
