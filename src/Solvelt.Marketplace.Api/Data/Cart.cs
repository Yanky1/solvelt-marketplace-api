using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A server-side cart: one per signed-in buyer (<see cref="AccountId"/>) or one per guest (<see cref="GuestTokenHash"/>, the SHA-256 of an opaque token the
/// web server keeps in its own cookie). The cart holds ids and quantities only; every price, name and availability is read from the catalog each time.
/// </summary>
public sealed class Cart : Solvelt.Marketplace.Api.Demo.IDemoMarked
{
    /// <summary>Created by "Fill with demo data" (M9): removed by "Remove demo data", never sent to the portal, a payment provider or a mailbox.</summary>
    public bool IsDemo { get; set; }
    /// <summary>Which press of the button created it.</summary>
    public Guid? DemoBatchId { get; set; }
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public string? GuestTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Last change; drives retention (guest carts 30 days, buyer carts 90 days idle).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CartLine
{
    public Guid Id { get; set; }
    public Guid CartId { get; set; }
    public Guid ListingId { get; set; }
    public Guid VariantId { get; set; }
    public int Quantity { get; set; }
    /// <summary>The unit price the buyer last saw; only used to say "price changed". Never trusted for money.</summary>
    public long SeenUnitPriceCents { get; set; }
    /// <summary>Unix milliseconds (our clock) the line was added: keeps the order stable.</summary>
    public long AddedAtMs { get; set; }
    /// <summary>Created by "Fill with demo data" (M9, security review M2): "Remove demo data" deletes only marked lines, so a real line the buyer added to the same cart stays. Cleared never; a line is demo or it is not.</summary>
    public bool IsDemo { get; set; }
}

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>, IEntityTypeConfiguration<CartLine>
{
    public void Configure(EntityTypeBuilder<Cart> b)
    {
        b.ToTable("carts");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.GuestTokenHash).HasMaxLength(64);
        b.HasIndex(c => c.AccountId).IsUnique();          // one cart per buyer (NULLs allowed for guests)
        b.HasIndex(c => c.GuestTokenHash).IsUnique();
        b.HasIndex(c => c.UpdatedAt);
    }

    public void Configure(EntityTypeBuilder<CartLine> b)
    {
        b.ToTable("cart_lines");
        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedNever();
        b.Property(l => l.IsDemo).HasDefaultValue(false);              // the cart service's raw upserts do not name the column: the database default makes it false (M9-05)
        b.HasIndex(l => new { l.CartId, l.VariantId }).IsUnique();   // the same variant is one line; parallel adds merge
        b.HasOne<Cart>().WithMany().HasForeignKey(l => l.CartId).OnDelete(DeleteBehavior.Cascade);
    }
}
