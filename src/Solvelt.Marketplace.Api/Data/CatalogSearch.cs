using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// The search document of one listing (M2-01). Every text column holds <c>Slug.Words</c> output (lower-case ASCII words), so the index
/// and the query terms are normalised by the same code. Visibility is NOT stored here: it is joined from the listing at query time, so an
/// unlist event hides a listing from search at once without touching the index. On PostgreSQL a generated, weighted <c>document</c>
/// tsvector (title A, brand B, search terms C, description D) with a GIN index is added by <c>SearchSchema</c>.
/// </summary>
public sealed class CatalogSearchRow
{
    public Guid ListingId { get; set; }
    public Guid BrandId { get; set; }
    public string Title { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Terms { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Title, brand and terms joined: the target of the typo-tolerant (trigram) match.</summary>
    public string Text { get; set; } = "";
}

internal sealed class CatalogSearchConfiguration : IEntityTypeConfiguration<CatalogSearchRow>
{
    public void Configure(EntityTypeBuilder<CatalogSearchRow> b)
    {
        b.ToTable("catalog_search");
        b.HasKey(x => x.ListingId);
        b.Property(x => x.ListingId).HasColumnName("listing_id").ValueGeneratedNever();
        b.Property(x => x.BrandId).HasColumnName("brand_id");
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(400).IsRequired();
        b.Property(x => x.Brand).HasColumnName("brand").HasMaxLength(300).IsRequired();
        b.Property(x => x.Terms).HasColumnName("terms").HasMaxLength(2000).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(4000).IsRequired();
        b.Property(x => x.Text).HasColumnName("text").HasMaxLength(2800).IsRequired();
        b.HasIndex(x => x.BrandId);
        b.HasOne<CatalogListing>().WithOne().HasForeignKey<CatalogSearchRow>(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
    }
}
