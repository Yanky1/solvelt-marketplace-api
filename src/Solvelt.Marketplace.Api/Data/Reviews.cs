using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class ReviewStatus
{
    public const string Published = "published", Hidden = "hidden", Removed = "removed";
}

public static class ReviewLimits
{
    public const int MaxTextLength = 2000, MinTextLength = 1, MaxNameLength = 60, EditDays = 30, PageSize = 10, MaxPhotos = 5;
}

/// <summary>
/// A buyer's review of a listing (M6, decisions M6-04..). It exists only for ONE delivered order line or ONE completed booking of that buyer (a unique index per buyer and line / booking), is editable for
/// <see cref="ReviewLimits.EditDays"/> days (our clock) and carries only the first name and last initial. <see cref="Status"/> follows the portal's moderation (hidden is reversible, removed is not shown at all).
/// <see cref="HelpfulCount"/> is a counter kept by atomic increments (one vote per buyer, a unique index).
/// </summary>
public sealed class Review
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ListingId { get; set; }
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public Guid? OrderLineId { get; set; }
    public Guid? BookingId { get; set; }
    public int Rating { get; set; }
    public string Text { get; set; } = "";
    /// <summary>"Jane B.": shown next to the review; the only part of the buyer's name that leaves the account.</summary>
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = ReviewStatus.Published;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? StatusAt { get; set; }
    /// <summary>What staff chose to tell the buyer (from <c>content.report_decided</c>), shown to the author only.</summary>
    public string? ModerationNote { get; set; }
    public int HelpfulCount { get; set; }
    /// <summary>The seller's single reply (relayed by the portal, <c>review.reply_posted</c>): text, its stable id and its time.</summary>
    public string? ReplyText { get; set; }
    public string? ReplyId { get; set; }
    public DateTimeOffset? ReplyAt { get; set; }
}

public sealed class ReviewVote
{
    public Guid ReviewId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A buyer's report of a review (or of its reply, kind <c>review_reply</c>); our report id is what the portal's decision event must carry back.</summary>
public sealed class ReviewReport
{
    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid AccountId { get; set; }
    public string Kind { get; set; } = "review";
    public string ReportId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Decision { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Rating counts per scope (<c>listing</c>, <c>seller</c>, <c>brand</c>) kept by atomic increments in the same transaction as the review change, so parallel writers cannot lose an update.</summary>
public sealed class RatingAggregate
{
    public string Scope { get; set; } = "";
    public Guid ScopeId { get; set; }
    public int Count { get; set; }
    public long Sum { get; set; }
    public int Stars1 { get; set; }
    public int Stars2 { get; set; }
    public int Stars3 { get; set; }
    public int Stars4 { get; set; }
    public int Stars5 { get; set; }
}

internal sealed class ReviewsConfiguration : IEntityTypeConfiguration<Review>, IEntityTypeConfiguration<ReviewVote>, IEntityTypeConfiguration<ReviewReport>, IEntityTypeConfiguration<RatingAggregate>
{
    public void Configure(EntityTypeBuilder<Review> b)
    {
        b.ToTable("reviews");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Text).HasMaxLength(ReviewLimits.MaxTextLength * 4).IsRequired();
        b.Property(r => r.DisplayName).HasMaxLength(ReviewLimits.MaxNameLength * 4).IsRequired();
        b.Property(r => r.Status).HasMaxLength(12).IsRequired();
        b.Property(r => r.ModerationNote).HasMaxLength(400);
        b.Property(r => r.ReplyText).HasMaxLength(8000);
        b.Property(r => r.ReplyId).HasMaxLength(100);
        b.HasIndex(r => new { r.AccountId, r.OrderLineId }).IsUnique();   // one review per buyer and order line (NULLs are allowed more than once)
        b.HasIndex(r => new { r.AccountId, r.BookingId }).IsUnique();     // ... and per booking
        b.HasIndex(r => new { r.ListingId, r.Status, r.CreatedAt });
        b.HasIndex(r => r.AccountId);
    }

    public void Configure(EntityTypeBuilder<ReviewVote> b)
    {
        b.ToTable("review_votes");
        b.HasKey(v => new { v.ReviewId, v.AccountId });
        b.HasIndex(v => v.AccountId);
    }

    public void Configure(EntityTypeBuilder<ReviewReport> b)
    {
        b.ToTable("review_reports");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Kind).HasMaxLength(20).IsRequired();
        b.Property(r => r.ReportId).HasMaxLength(100).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(200).IsRequired();
        b.Property(r => r.Note).HasMaxLength(1000);
        b.Property(r => r.Decision).HasMaxLength(20);
        b.HasIndex(r => r.ReportId).IsUnique();
        b.HasIndex(r => new { r.ReviewId, r.AccountId, r.Kind }).IsUnique();   // one report per buyer, review and kind
    }

    public void Configure(EntityTypeBuilder<RatingAggregate> b)
    {
        b.ToTable("rating_aggregates");
        b.HasKey(a => new { a.Scope, a.ScopeId });
        b.Property(a => a.Scope).HasMaxLength(12);
    }
}
