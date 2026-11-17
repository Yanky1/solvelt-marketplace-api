using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class VoiceOutcome
{
    public const string InProgress = "in_progress", Abandoned = "abandoned", Transferred = "transferred", OrderPlaced = "order_placed";
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
        b.HasIndex(c => c.StartedAt);
    }
}
