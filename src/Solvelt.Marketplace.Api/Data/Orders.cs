using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class OrderStatus
{
    /// <summary>Created at checkout; the buyer has not paid yet.</summary>
    public const string PendingPayment = "pending_payment";
    /// <summary>The buyer changed something and a newer pending order replaced this one (its intent is cancelled; a payment that still lands is honoured).</summary>
    public const string Superseded = "superseded";
    public const string Paid = "paid", Placed = "placed", Failed = "failed", Expired = "expired";
}

/// <summary>
/// Our order (<c>MP-...</c>): what the buyer is charged once, split into one group per (seller, brand) exactly like the portal's seller orders.
/// Every amount is computed by the server (QuoteService) and stored in <c>long</c> cents; the intent amount is <see cref="TotalCents"/>, which equals the sum of the groups.
/// </summary>
public sealed class MarketplaceOrder
{
    public Guid Id { get; set; }
    /// <summary><c>MP-yyyyMMdd-XXXXXXXX</c>: our idempotency id towards the portal (<c>marketplaceOrderId</c>) and Stripe.</summary>
    public string Number { get; set; } = "";
    public Guid AccountId { get; set; }
    public Guid? CartId { get; set; }
    public string Status { get; set; } = OrderStatus.PendingPayment;
    /// <summary>
    /// SHA-256 of everything the buyer agreed to (cart lines with server prices, deliveries, address, totals) while the order is pending, otherwise NULL; unique.
    /// A parallel double-submit produces the same value, so the database allows exactly one pending order for it (and so one PaymentIntent).
    /// </summary>
    public string? ActiveKey { get; set; }
    public string Currency { get; set; } = "USD";
    public long ItemsCents { get; set; }
    public long ShippingCents { get; set; }
    public long TaxCents { get; set; }
    public long TotalCents { get; set; }
    public string? PaymentIntentId { get; set; }
    // Buyer as the portal and sellers see them (CONTRACT.md §3, §8).
    public string BuyerName { get; set; } = "";
    public string RelayEmail { get; set; } = "";
    public string? Phone { get; set; }
    // Delivery address snapshot (personal data: see the PII table).
    public string ShipName { get; set; } = "";
    public string ShipLine1 { get; set; } = "";
    public string? ShipLine2 { get; set; }
    public string ShipCity { get; set; } = "";
    public string? ShipRegion { get; set; }
    public string ShipPostalCode { get; set; } = "";
    public string ShipCountry { get; set; } = "";
    public double? ShipLat { get; set; }
    public double? ShipLng { get; set; }
    public string? GiftMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? FailureCode { get; set; }

    public List<OrderGroup> Groups { get; set; } = [];
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderGroup
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public string SellerName { get; set; } = "";
    public string BrandName { get; set; } = "";
    /// <summary><c>carrier</c> or <c>local</c>.</summary>
    public string DeliveryKind { get; set; } = "carrier";
    public string? ShippingSpeed { get; set; }
    public Guid? LocalWindowId { get; set; }
    public string? LocalDate { get; set; }
    public long ItemsCents { get; set; }
    public long ShippingCents { get; set; }
    public long ShippingTaxCents { get; set; }
    public long TaxCents { get; set; }
    public long TotalCents { get; set; }
    /// <summary>The portal's seller order, stored when the order is ingested.</summary>
    public Guid? SellerOrderId { get; set; }
    public string? SellerOrderNumber { get; set; }
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid GroupId { get; set; }
    /// <summary>Our line id towards the portal (<c>marketplaceLineId</c>): <c>&lt;order number&gt;-&lt;n&gt;</c>.</summary>
    public string MarketplaceLineId { get; set; } = "";
    public Guid ListingId { get; set; }
    public Guid VariantId { get; set; }
    public Guid SellerId { get; set; }
    public Guid BrandId { get; set; }
    public string Title { get; set; } = "";
    public string VariantName { get; set; } = "";
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
    public long LineTotalCents { get; set; }
    public long TaxCents { get; set; }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<MarketplaceOrder>, IEntityTypeConfiguration<OrderGroup>, IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<MarketplaceOrder> b)
    {
        b.ToTable("marketplace_orders");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).ValueGeneratedNever();
        b.Property(o => o.Number).HasMaxLength(40).IsRequired();
        b.Property(o => o.Status).HasMaxLength(30).IsRequired();
        b.Property(o => o.ActiveKey).HasMaxLength(64);
        b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        b.Property(o => o.PaymentIntentId).HasMaxLength(100);
        b.Property(o => o.BuyerName).HasMaxLength(60);
        b.Property(o => o.RelayEmail).HasMaxLength(120);
        b.Property(o => o.Phone).HasMaxLength(40);
        b.Property(o => o.ShipName).HasMaxLength(120);
        b.Property(o => o.ShipLine1).HasMaxLength(200);
        b.Property(o => o.ShipLine2).HasMaxLength(200);
        b.Property(o => o.ShipCity).HasMaxLength(100);
        b.Property(o => o.ShipRegion).HasMaxLength(60);
        b.Property(o => o.ShipPostalCode).HasMaxLength(20);
        b.Property(o => o.ShipCountry).HasMaxLength(2);
        b.Property(o => o.GiftMessage).HasMaxLength(500);
        b.Property(o => o.FailureCode).HasMaxLength(60);
        b.HasIndex(o => o.Number).IsUnique();
        b.HasIndex(o => o.ActiveKey).IsUnique();             // at most one pending order per identical checkout (NULLs allowed)
        b.HasIndex(o => o.PaymentIntentId).IsUnique();
        b.HasIndex(o => new { o.AccountId, o.CreatedAt });
        b.HasIndex(o => new { o.Status, o.CreatedAt });
        b.HasMany(o => o.Groups).WithOne().HasForeignKey(g => g.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<OrderGroup> b)
    {
        b.ToTable("marketplace_order_groups");
        b.HasKey(g => g.Id);
        b.Property(g => g.Id).ValueGeneratedNever();
        b.Property(g => g.SellerName).HasMaxLength(200);
        b.Property(g => g.BrandName).HasMaxLength(200);
        b.Property(g => g.DeliveryKind).HasMaxLength(10).IsRequired();
        b.Property(g => g.ShippingSpeed).HasMaxLength(30);
        b.Property(g => g.LocalDate).HasMaxLength(10);
        b.Property(g => g.SellerOrderNumber).HasMaxLength(60);
        b.HasIndex(g => new { g.OrderId, g.SellerId, g.BrandId }).IsUnique();
        b.HasIndex(g => g.SellerOrderId);
    }

    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("marketplace_order_lines");
        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedNever();
        b.Property(l => l.MarketplaceLineId).HasMaxLength(60).IsRequired();
        b.Property(l => l.Title).HasMaxLength(300);
        b.Property(l => l.VariantName).HasMaxLength(200);
        b.HasIndex(l => l.MarketplaceLineId).IsUnique();
    }
}
