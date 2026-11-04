using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>Quote request statuses as the portal names them (SERVICES.md §5), plus the two only we have: <see cref="Requesting"/> (row exists, the portal has not answered) and <see cref="Failed"/>.</summary>
public static class QuoteStatus
{
    public const string Requesting = "Requesting", Requested = "Requested", Quoted = "Quoted", Accepted = "Accepted", Declined = "Declined", Withdrawn = "Withdrawn", Expired = "Expired", Failed = "Failed";
    public static readonly string[] Terminal = [Accepted, Declined, Withdrawn, Expired, Failed];
    public static bool IsTerminal(string s) => Terminal.Contains(s);
    public static bool IsOpen(string s) => s is Requested or Quoted;
}

/// <summary>
/// A buyer's quote request for a "quote required" service (decision M4-13). Inserted before the portal is called (like a hold), matched by OUR <c>QT-…</c> id and the portal's quote id.
/// The provider's answer (<c>quote.sent</c>) fills the price, duration, the bookable slots and the expiry; accepting one slot creates a booking. Amounts are the portal's.
/// </summary>
public sealed class BuyerQuote
{
    public Guid Id { get; set; }
    public string MarketplaceQuoteId { get; set; } = "";
    public Guid? PortalQuoteId { get; set; }
    public Guid AccountId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ListingId { get; set; }
    public string Status { get; set; } = QuoteStatus.Requesting;
    public string? FailureCode { get; set; }
    public string Location { get; set; } = "";
    // What the buyer asked for (personal data: see the PII table).
    public string Details { get; set; } = "";
    public string? PreferredTimesJson { get; set; }
    public string? AddrName { get; set; }
    public string? AddrLine1 { get; set; }
    public string? AddrLine2 { get; set; }
    public string? AddrCity { get; set; }
    public string? AddrRegion { get; set; }
    public string? AddrPostalCode { get; set; }
    public string? AddrCountry { get; set; }
    public double? AddrLat { get; set; }
    public double? AddrLng { get; set; }
    public string? Phone { get; set; }
    // The provider's answer (portal numbers).
    public string Currency { get; set; } = "USD";
    public long PriceCents { get; set; }
    public int DurationMinutes { get; set; }
    public string? SlotsJson { get; set; }
    public Guid? ProviderId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Note { get; set; }
    public string? DeclineReason { get; set; }
    /// <summary>The booking created by accepting (our id).</summary>
    public Guid? BookingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RedactedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class BuyerQuoteConfiguration : IEntityTypeConfiguration<BuyerQuote>
{
    public void Configure(EntityTypeBuilder<BuyerQuote> b)
    {
        b.ToTable("quotes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MarketplaceQuoteId).HasMaxLength(64).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.FailureCode).HasMaxLength(60);
        b.Property(x => x.Location).HasMaxLength(10).IsRequired();
        b.Property(x => x.Details).HasMaxLength(4000).IsRequired();
        b.Property(x => x.PreferredTimesJson).HasMaxLength(1000);
        b.Property(x => x.AddrName).HasMaxLength(200);
        b.Property(x => x.AddrLine1).HasMaxLength(200);
        b.Property(x => x.AddrLine2).HasMaxLength(200);
        b.Property(x => x.AddrCity).HasMaxLength(100);
        b.Property(x => x.AddrRegion).HasMaxLength(100);
        b.Property(x => x.AddrPostalCode).HasMaxLength(20);
        b.Property(x => x.AddrCountry).HasMaxLength(2);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.SlotsJson).HasMaxLength(4000);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.DeclineReason).HasMaxLength(500);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.MarketplaceQuoteId).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.Status });
        b.HasIndex(x => x.PortalQuoteId);
    }
}
