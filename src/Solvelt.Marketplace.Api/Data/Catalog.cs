using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>Display data of a seller (never contact or account data). <see cref="Active"/> false hides every listing of the seller.</summary>
public sealed class CatalogSeller
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class CatalogBrand
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? LogoImageId { get; set; }
}

public sealed class CatalogCategory
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}

public static class ListingTypes
{
    public const string Product = "product", Service = "service";
    public static bool IsValid(string? t) => t is Product or Service;
}

/// <summary>
/// A product or service as last known from the portal. Versioned by the portal's <c>version</c> (the sync cursor): a snapshot is applied only
/// when it is newer than <see cref="Version"/>, so replays, retries and stale poll pages are harmless and order does not matter.
/// Visible (CONTRACT.md §2) = last applied status Active, seller active, and not unlisted: an unlist event of version u hides the listing for
/// every snapshot with version &lt;= u, immediately, even if a stale poll page delivers it again.
/// </summary>
public sealed class CatalogListing
{
    public Guid Id { get; set; }
    /// <summary>Stable, unique, generated once (SEO): kept when the title changes.</summary>
    public string Slug { get; set; } = "";
    public string Type { get; set; } = ListingTypes.Product;
    public string Title { get; set; } = "";
    /// <summary>ASCII-folded, lower-case title used for sorting; compared with a binary collation so SQLite and PostgreSQL order identically.</summary>
    public string TitleSort { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid CategoryId { get; set; }
    /// <summary>The portal status of the last applied snapshot ("Active", "Archived", ...).</summary>
    public string Status { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public long PriceFromCents { get; set; }
    public long PriceToCents { get; set; }
    public long? CompareAtFromCents { get; set; }
    /// <summary>Any variant can be bought or booked now.</summary>
    public bool Available { get; set; }
    /// <summary>Version of the last applied snapshot; 0 for a stub created from an event before the snapshot was fetched.</summary>
    public long Version { get; set; }
    /// <summary>Highest version seen in any event or snapshot; greater than <see cref="Version"/> means a fetch is pending.</summary>
    public long KnownVersion { get; set; }
    /// <summary>Version of the latest unlist event applied, if any.</summary>
    public long? UnlistedVersion { get; set; }
    /// <summary>Derived: Status Active, not a stub, not unlisted. The seller's flag is joined at query time.</summary>
    public bool Visible { get; set; }
    public bool IsStub { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    /// <summary>Concurrency token: an event and a sync run cannot silently overwrite each other.</summary>
    public int Row { get; set; }

    public List<CatalogVariant> Variants { get; set; } = [];
    public List<CatalogImage> Images { get; set; } = [];
    public List<CatalogAttribute> Attributes { get; set; } = [];
    public List<CatalogFaq> Faqs { get; set; } = [];

    public bool IsUnlisted => UnlistedVersion is { } u && u >= Version;

    public void RecomputeVisibility() => Visible = !IsStub && Status == "Active" && !IsUnlisted;
}

public sealed class CatalogVariant
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string Name { get; set; } = "";
    public long PriceCents { get; set; }
    public long? CompareAtCents { get; set; }
    public bool Available { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>An image is the portal image id plus its dimensions; bytes are fetched from the portal and served from our own CDN path.</summary>
public sealed class CatalogImage
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Alt { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class CatalogAttribute
{
    public long Id { get; set; }
    public Guid ListingId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class CatalogFaq
{
    public long Id { get; set; }
    public Guid ListingId { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>Sync cursor and a lease so two instances never run the job at once.</summary>
public sealed class CatalogSyncState
{
    public string Key { get; set; } = "";
    public string? Cursor { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class CatalogConfiguration :
    IEntityTypeConfiguration<CatalogSeller>, IEntityTypeConfiguration<CatalogBrand>, IEntityTypeConfiguration<CatalogCategory>,
    IEntityTypeConfiguration<CatalogListing>, IEntityTypeConfiguration<CatalogVariant>, IEntityTypeConfiguration<CatalogImage>,
    IEntityTypeConfiguration<CatalogAttribute>, IEntityTypeConfiguration<CatalogFaq>, IEntityTypeConfiguration<CatalogSyncState>
{
    public void Configure(EntityTypeBuilder<CatalogSeller> b)
    {
        b.ToTable("catalog_sellers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // ids come from the portal: an entity with a key set is new, not "existing"
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
    }

    public void Configure(EntityTypeBuilder<CatalogBrand> b)
    {
        b.ToTable("catalog_brands");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // ids come from the portal: an entity with a key set is new, not "existing"
        b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => x.SellerId);
    }

    public void Configure(EntityTypeBuilder<CatalogCategory> b)
    {
        b.ToTable("catalog_categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // ids come from the portal: an entity with a key set is new, not "existing"
        b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => x.ParentId);
    }

    public void Configure(EntityTypeBuilder<CatalogListing> b)
    {
        b.ToTable("catalog_listings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // ids come from the portal: an entity with a key set is new, not "existing"
        b.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        b.Property(x => x.Type).HasMaxLength(20).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.TitleSort).HasMaxLength(300).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(600);
        b.Property(x => x.Status).HasMaxLength(40).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Row).IsConcurrencyToken();
        b.Ignore(x => x.IsUnlisted);
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => new { x.Visible, x.CategoryId });
        b.HasIndex(x => new { x.Visible, x.BrandId });
        b.HasIndex(x => new { x.Visible, x.SellerId });
        b.HasIndex(x => x.KnownVersion);
        b.HasMany(x => x.Variants).WithOne().HasForeignKey(v => v.ListingId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Images).WithOne().HasForeignKey(v => v.ListingId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Attributes).WithOne().HasForeignKey(v => v.ListingId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Faqs).WithOne().HasForeignKey(v => v.ListingId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<CatalogVariant> b)
    {
        b.ToTable("catalog_variants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // ids come from the portal: an entity with a key set is new, not "existing"
        b.Property(x => x.Name).HasMaxLength(300).IsRequired();
    }

    public void Configure(EntityTypeBuilder<CatalogImage> b)
    {
        b.ToTable("catalog_images");
        b.HasKey(x => new { x.ListingId, x.Id });
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Alt).HasMaxLength(300);
    }

    public void Configure(EntityTypeBuilder<CatalogAttribute> b)
    {
        b.ToTable("catalog_attributes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(100).IsRequired();
        b.Property(x => x.Value).HasMaxLength(500).IsRequired();
        b.HasIndex(x => new { x.Key, x.Value });
    }

    public void Configure(EntityTypeBuilder<CatalogFaq> b)
    {
        b.ToTable("catalog_faqs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Question).HasMaxLength(500).IsRequired();
    }

    public void Configure(EntityTypeBuilder<CatalogSyncState> b)
    {
        b.ToTable("catalog_sync_state");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(50);
        b.Property(x => x.Cursor).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(500);
    }
}
