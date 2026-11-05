using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A named wishlist of one signed-in buyer (M6). Private by default; <see cref="ShareKeyHash"/> (SHA-256 of a random 256-bit key that only the share link carries) makes it readable
/// by anyone holding the link. The key is random and used for nothing else, so it is no signing key and cannot be derived from another secret.
/// </summary>
public sealed class Wishlist
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Trimmed, NFC, lower case: the unique key of the name within the account.</summary>
    public string NameKey { get; set; } = "";
    public string? ShareKeyHash { get; set; }
    public DateTimeOffset? SharedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One listing on a wishlist. <see cref="BaselinePriceCents"/> and <see cref="BaselineAvailable"/> are what the buyer last knew; a catalog update that goes below the price or makes an
/// unavailable listing available again is a notification (price drop, back in stock) and moves the baseline, so each change is announced once.
/// </summary>
public sealed class WishlistItem
{
    public Guid Id { get; set; }
    public Guid WishlistId { get; set; }
    public Guid ListingId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public long BaselinePriceCents { get; set; }
    public string BaselineCurrency { get; set; } = "";
    public bool BaselineAvailable { get; set; }
    /// <summary>When the buyer was last told of a price drop / a restock of this listing: at most one mail per kind per cooldown (M6-03).</summary>
    public DateTimeOffset? LastPriceNotifiedAt { get; set; }
    public DateTimeOffset? LastStockNotifiedAt { get; set; }
}

/// <summary>A listing a signed-in buyer looked at: at most <see cref="ListLimits.MaxRecentlyViewed"/> per account, newest first. The time is ours, never the client's.</summary>
public sealed class RecentlyViewedItem
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ListingId { get; set; }
    public DateTimeOffset ViewedAt { get; set; }
}

public static class ListLimits
{
    public const int MaxLists = 20, MaxItemsPerList = 200, MaxNameLength = 60, MaxRecentlyViewed = 20, MaxMembershipQuery = 50;
}

internal sealed class ListsConfiguration : IEntityTypeConfiguration<Wishlist>, IEntityTypeConfiguration<WishlistItem>, IEntityTypeConfiguration<RecentlyViewedItem>
{
    public void Configure(EntityTypeBuilder<Wishlist> b)
    {
        b.ToTable("wishlists");
        b.HasKey(w => w.Id);
        b.Property(w => w.Id).ValueGeneratedNever();
        b.Property(w => w.Name).HasMaxLength(ListLimits.MaxNameLength * 4).IsRequired();   // the limit counts characters (text elements); the column leaves room for multi-unit ones
        b.Property(w => w.NameKey).HasMaxLength(ListLimits.MaxNameLength * 4).IsRequired();
        b.Property(w => w.ShareKeyHash).HasMaxLength(64);
        b.HasIndex(w => new { w.AccountId, w.NameKey }).IsUnique();
        b.HasIndex(w => w.ShareKeyHash).IsUnique();
    }

    public void Configure(EntityTypeBuilder<WishlistItem> b)
    {
        b.ToTable("wishlist_items");
        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedNever();
        b.Property(i => i.BaselineCurrency).HasMaxLength(3).IsRequired();
        b.HasIndex(i => new { i.WishlistId, i.ListingId }).IsUnique();
        b.HasIndex(i => i.ListingId);
        b.HasOne<Wishlist>().WithMany().HasForeignKey(i => i.WishlistId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<RecentlyViewedItem> b)
    {
        b.ToTable("recently_viewed");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.HasIndex(r => new { r.AccountId, r.ListingId }).IsUnique();
        b.HasIndex(r => new { r.AccountId, r.ViewedAt });
    }
}
