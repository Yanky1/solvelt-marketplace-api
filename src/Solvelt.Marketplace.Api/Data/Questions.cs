using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class QuestionStatus
{
    public const string Sending = "sending", Sent = "sent", Rejected = "rejected";
}

/// <summary>
/// A buyer's question about a listing (M6, C1: <c>POST /products/{id}/questions</c>). We keep our copy so the buyer sees what they asked and whether it reached the seller; the seller's published answer comes back
/// through the catalog feed (the product's Q&amp;A), never through this table. <see cref="Id"/> is the idempotency key we send (<c>questionId</c>).
/// </summary>
public sealed class ProductQuestion
{
    public string Id { get; set; } = "";
    public Guid AccountId { get; set; }
    public Guid ListingId { get; set; }
    public string Text { get; set; } = "";
    /// <summary>First name and last initial (the portal never publishes the asker's name).</summary>
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = QuestionStatus.Sending;
    /// <summary>A short code when the portal refused it (<c>listing_unavailable</c>, <c>invalid_question</c>).</summary>
    public string? RejectedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class QuestionsConfiguration : IEntityTypeConfiguration<ProductQuestion>
{
    public void Configure(EntityTypeBuilder<ProductQuestion> b)
    {
        b.ToTable("product_questions");
        b.HasKey(q => q.Id);
        b.Property(q => q.Id).HasMaxLength(100);
        b.Property(q => q.Text).HasMaxLength(4000).IsRequired();
        b.Property(q => q.DisplayName).HasMaxLength(240).IsRequired();
        b.Property(q => q.Status).HasMaxLength(12).IsRequired();
        b.Property(q => q.RejectedReason).HasMaxLength(40);
        b.HasIndex(q => new { q.AccountId, q.CreatedAt });
        b.HasIndex(q => new { q.AccountId, q.ListingId, q.CreatedAt });
    }
}
