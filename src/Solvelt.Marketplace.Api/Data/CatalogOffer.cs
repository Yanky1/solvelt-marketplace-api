using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A quantity offer projected from the seller portal's offer feed (contract C18, decision Q-02): price tiers by minimum quantity and free gifts, for one seller and ONE brand. Tiers, gifts and the scope ids are
/// kept as JSON text (they are read whole, never queried); the columns that are queried are the ids, the version bookkeeping and <see cref="Visible"/>. Money is <c>long</c> cents.
/// </summary>
public sealed class CatalogOffer
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "USD";
    /// <summary><c>products</c>, <c>category</c> or <c>all</c>.</summary>
    public string Scope { get; set; } = "all";
    public string ProductIdsJson { get; set; } = "[]";
    public string CategoryIdsJson { get; set; } = "[]";
    public string TiersJson { get; set; } = "[]";
    public string GiftsJson { get; set; } = "[]";
    public DateTimeOffset? ActiveFrom { get; set; }
    public DateTimeOffset? ActiveTo { get; set; }
    public bool Demo { get; set; }
    /// <summary>The version we hold, the highest version we know exists (an event or a feed entry) and the version an unlist/end was seen at: the same bookkeeping as a listing, so a stale page never re-lists what an event hid.</summary>
    public long Version { get; set; }
    public long KnownVersion { get; set; }
    public long? UnlistedVersion { get; set; }
    public bool Visible { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int FetchAttempts { get; set; }
    public DateTimeOffset? NextFetchAt { get; set; }
    public long Row { get; set; }

    public void RecomputeVisibility() => Visible = Version > 0 && !Demo && (UnlistedVersion is null || UnlistedVersion < Version);
}

internal sealed class CatalogOfferConfiguration : IEntityTypeConfiguration<CatalogOffer>
{
    public void Configure(EntityTypeBuilder<CatalogOffer> b)
    {
        b.ToTable("catalog_offers");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).ValueGeneratedNever();
        b.Property(o => o.Name).HasMaxLength(200).IsRequired();
        b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        b.Property(o => o.Scope).HasMaxLength(12).IsRequired();
        b.Property(o => o.ProductIdsJson).IsRequired();
        b.Property(o => o.CategoryIdsJson).IsRequired();
        b.Property(o => o.TiersJson).IsRequired();
        b.Property(o => o.GiftsJson).IsRequired();
        b.Property(o => o.Row).IsConcurrencyToken();
        b.HasIndex(o => new { o.SellerId, o.BrandId, o.Visible });
        b.HasIndex(o => o.KnownVersion);
    }
}
