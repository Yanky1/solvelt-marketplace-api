using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A card an account saved BY PHONE through the pay connector (decision V-16). It holds the processor's TOKEN (a reference to the card at Sola, usable only with our own Sola key), the brand, the last four digits and the
/// expiry: never the card number and never a security code, which this API has never seen. Erased with the account (deletion and redaction).
/// </summary>
public sealed class SavedCard
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Token { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Last4 { get; set; } = "";
    /// <summary>Two digits each, as Sola returns the expiry (MMYY).</summary>
    public string ExpMonth { get; set; } = "";
    public string ExpYear { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class SavedCardConfiguration : IEntityTypeConfiguration<SavedCard>
{
    public void Configure(EntityTypeBuilder<SavedCard> b)
    {
        b.ToTable("saved_cards");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Token).HasMaxLength(100).IsRequired();
        b.Property(c => c.Brand).HasMaxLength(20).IsRequired();
        b.Property(c => c.Last4).HasMaxLength(4).IsRequired();
        b.Property(c => c.ExpMonth).HasMaxLength(2).IsRequired();
        b.Property(c => c.ExpYear).HasMaxLength(2).IsRequired();
        b.HasIndex(c => new { c.AccountId, c.Token }).IsUnique();
    }
}
