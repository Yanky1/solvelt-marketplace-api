using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class OrderStatus
{
    /// <summary>Created at checkout; the buyer has not paid yet.</summary>
    public const string PendingPayment = "pending_payment";
    /// <summary>The buyer changed something and a newer pending order replaced this one (its intent is cancelled; a payment that still lands is honoured).</summary>
    public const string Superseded = "superseded";
    /// <summary>A delayed payment method is in flight at Stripe (<c>payment_intent.processing</c>): the money may still arrive, so neither a newer checkout nor the expiry may close this order.</summary>
    public const string PaymentProcessing = "payment_processing";
    public const string Paid = "paid", Placed = "placed", Failed = "failed", Expired = "expired";
}

/// <summary>
/// Our order (<c>MP-...</c>): what the buyer is charged once, split into one group per (seller, brand) exactly like the portal's seller orders.
/// Every amount is computed by the server (QuoteService) and stored in <c>long</c> cents; the intent amount is <see cref="TotalCents"/>, which equals the sum of the groups.
/// </summary>
public sealed class MarketplaceOrder : Solvelt.Marketplace.Api.Demo.IDemoMarked
{
    /// <summary>Created by "Fill with demo data" (M9): removed by "Remove demo data", never sent to the portal, a payment provider or a mailbox.</summary>
    public bool IsDemo { get; set; }
    /// <summary>Which press of the button created it.</summary>
    public Guid? DemoBatchId { get; set; }
    public Guid Id { get; set; }
    /// <summary><c>MP-yyyyMMdd-XXXXXXXX</c>: our idempotency id towards the portal (<c>marketplaceOrderId</c>) and Stripe.</summary>
    public string Number { get; set; } = "";
    /// <summary>The buyer's account; NULL for a guest order until its address is proven and it is claimed (decision M3-24).</summary>
    public Guid? AccountId { get; set; }
    /// <summary>The address a guest typed at checkout (trimmed, lower-cased): where the order link is mailed, and what a verified account claims the order by. NULL for an account order. Personal data (PII table).</summary>
    public string? GuestEmail { get; set; }
    /// <summary>When a guest order was attached to an account. From then on the guest link shows only the order's number, status and date (an attacker who typed someone else's address keeps a link that reveals nothing of the owner).</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
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
    /// <summary>When the portal accepted the order (seller orders stored).</summary>
    public DateTimeOffset? PlacedAt { get; set; }
    public string? FailureCode { get; set; }
    /// <summary>What Stripe actually took (set when the payment succeeds; equals <see cref="TotalCents"/> unless the amounts disagreed, which fails the order).</summary>
    public long PaidCents { get; set; }
    /// <summary>Cents reserved for refunds so far. Raised only by a conditional UPDATE that keeps it at or below <see cref="PaidCents"/>: refunds can never exceed what was paid.</summary>
    public long RefundedCents { get; set; }

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
    /// <summary>The portal could not cover every unit from stock; the seller will sort it out (shown to the buyer).</summary>
    public bool Oversold { get; set; }
    /// <summary>Per seller order, the same cap as on the order (items + shipping + tax).</summary>
    public long RefundedCents { get; set; }
    /// <summary>Shipping returned so far (shipping excl. tax; never above <see cref="ShippingCents"/>).</summary>
    public long RefundedShippingCents { get; set; }
    /// <summary>Tax on shipping returned so far (only used while the portal takes shipping tax as its own field, C8; otherwise it is inside the line tax).</summary>
    public long RefundedShippingTaxCents { get; set; }
    /// <summary>
    /// Tax inside what was returned so far: the portal's amounts include line tax, its ledger excludes it, so this is what we back out of every <c>refund.processed</c>
    /// (floor proration per line, the last refund of a line takes the remainder; decision M3-16).
    /// </summary>
    public long RefundedTaxCents { get; set; }
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
    /// <summary>Units refunded or cancelled so far (never above <see cref="Quantity"/>).</summary>
    public int RefundedQuantity { get; set; }
    /// <summary>Tax backed out of the refunds of this line so far (of the tax we sent at ingest).</summary>
    public long RefundedTaxCents { get; set; }
}

public static class RefundStatus
{
    /// <summary>The amount is reserved against the cap and a Stripe refund is queued.</summary>
    public const string Reserved = "reserved", Succeeded = "succeeded", Rejected = "rejected", Failed = "failed", Deferred = "deferred";
}

/// <summary>
/// One refund we owe the buyer. <see cref="Key"/> is our idempotency id (<c>fail:&lt;order&gt;</c>, <c>portal:&lt;portal refund id&gt;</c>, ...), unique: a replayed webhook or retried
/// outbox message cannot create a second refund. The same key is Stripe's <c>Idempotency-Key</c>.
/// </summary>
public sealed class OrderRefund
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid? GroupId { get; set; }
    public long AmountCents { get; set; }
    public long ShippingCents { get; set; }
    /// <summary>The tax inside <see cref="AmountCents"/> (the portal's amounts include line tax; <c>refund.processed</c> reports the amount without it).</summary>
    public long TaxCents { get; set; }
    /// <summary>The part of <see cref="TaxCents"/> that is tax on shipping (C8 mode), so a refusal gives it back to the right counter.</summary>
    public long ShippingTaxCents { get; set; }
    /// <summary>JSON <c>[{ "lineId", "quantity", "taxCents" }]</c>: which line counters this refund moved, so a refusal by Stripe gives them back exactly.</summary>
    public string? PartsJson { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Which send of this refund is current (it goes up when the amount is cut and re-sent under a new idempotency key): an older queued message does nothing (decision M3-34).</summary>
    public int Attempt { get; set; }
    public string Status { get; set; } = RefundStatus.Reserved;
    public string? StripeRefundId { get; set; }
    /// <summary>Send <c>refund.processed</c> to the portal once Stripe confirms (refunds the portal asked for).</summary>
    public bool ReportToPortal { get; set; }
    public string? PortalRefundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Idempotency and audit of Stripe webhooks: the event id is the primary key. No payload is stored (it can hold card details).</summary>
public sealed class StripeEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public long StripeCreated { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>applied, ignored (a type we do not handle), unmatched (not ours).</summary>
    public string Outcome { get; set; } = "";
    public string? ObjectId { get; set; }
}

/// <summary>The last Stripe event time sent for a dispute and whether it is closed: events older than it, or after it closed, are dropped (disputes arrive in any order).</summary>
public sealed class DisputeState
{
    public string DisputeId { get; set; } = "";
    public long LastCreated { get; set; }
    /// <summary>The Stripe event id of the last event applied: the tie-breaker between two events of the same second.</summary>
    public string LastEventId { get; set; } = "";
    public bool Closed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DisputeStateConfiguration : IEntityTypeConfiguration<DisputeState>
{
    public void Configure(EntityTypeBuilder<DisputeState> b)
    {
        b.ToTable("dispute_states");
        b.HasKey(d => d.DisputeId);
        b.Property(d => d.DisputeId).HasMaxLength(100);
    }
}

internal sealed class OrderExtrasConfiguration : IEntityTypeConfiguration<OrderRefund>, IEntityTypeConfiguration<StripeEvent>
{
    public void Configure(EntityTypeBuilder<OrderRefund> b)
    {
        b.ToTable("order_refunds");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Key).HasMaxLength(100).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(60);
        b.Property(r => r.Status).HasMaxLength(20).IsRequired();
        b.Property(r => r.StripeRefundId).HasMaxLength(100);
        b.Property(r => r.PortalRefundId).HasMaxLength(100);
        b.HasIndex(r => r.Key).IsUnique();
        b.HasIndex(r => r.OrderId);
        b.HasIndex(r => r.PortalRefundId);
    }

    public void Configure(EntityTypeBuilder<StripeEvent> b)
    {
        b.ToTable("stripe_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasMaxLength(100);
        b.Property(e => e.Type).HasMaxLength(100).IsRequired();
        b.Property(e => e.Outcome).HasMaxLength(20).IsRequired();
        b.Property(e => e.ObjectId).HasMaxLength(100);
        b.HasIndex(e => e.ReceivedAt);
    }
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
        b.Property(o => o.GuestEmail).HasMaxLength(254);
        b.HasIndex(o => o.GuestEmail);
        b.Property(o => o.FailureCode).HasMaxLength(60);
        b.HasIndex(o => o.Number).IsUnique();
        b.HasIndex(o => o.ActiveKey).IsUnique();             // at most one pending order per identical checkout (NULLs allowed)
        b.HasIndex(o => o.CartId).IsUnique().HasFilter("\"Status\" = 'pending_payment'");   // and at most ONE pending order per cart, whatever differs between checkouts
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
