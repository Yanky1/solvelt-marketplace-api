using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A portal listing document we could not read (no category, no usable price, a missing required field), set aside so ONE bad document never stops the catalog sync: the listing is hidden if we held it, the
/// reason is a short code (never the document), and the row goes away when the listing is next applied readable (a newer version) or removed. The count is shown on <c>/health</c>. Row per listing.
/// </summary>
public sealed class CatalogQuarantine
{
    public Guid ListingId { get; set; }
    /// <summary><c>required_field</c>, <c>price</c>, <c>quote_price</c>, <c>seller</c>, <c>brand</c>, <c>category</c>: what could not be read.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The portal version of the unreadable document when it was readable far enough to say.</summary>
    public long? Version { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int Seen { get; set; }
}

internal sealed class CatalogQuarantineConfiguration : IEntityTypeConfiguration<CatalogQuarantine>
{
    public void Configure(EntityTypeBuilder<CatalogQuarantine> b)
    {
        b.ToTable("catalog_quarantine");
        b.HasKey(q => q.ListingId);
        b.Property(q => q.ListingId).ValueGeneratedNever();
        b.Property(q => q.Reason).HasMaxLength(40).IsRequired();
    }
}
