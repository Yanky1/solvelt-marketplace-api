using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>What the buyer sees as the state of one seller order (a <see cref="OrderGroup"/>): our projection of the portal's status machine (ORDERS.md §2).</summary>
public static class FulfilmentStatus
{
    public const string New = "new", Accepted = "accepted", OnHold = "on_hold", PartiallyShipped = "partially_shipped", Shipped = "shipped",
        Delivered = "delivered", Cancelled = "cancelled", Refunded = "refunded";

    /// <summary>The portal's status names (as they appear in <c>order.resumed</c>) to ours; anything unknown maps to null (the event is then ignored for status).</summary>
    public static string? FromPortal(string? s) => s?.Trim().ToLowerInvariant().Replace("_", "").Replace(" ", "") switch
    {
        "new" => New, "accepted" => Accepted, "onhold" => OnHold, "partiallyshipped" => PartiallyShipped, "shipped" => Shipped,
        "delivered" or "completed" => Delivered, "cancelled" => Cancelled, "refunded" => Refunded, _ => null,
    };
}

/// <summary>
/// The projected state of one seller order, created by the first event that names it. One row per <see cref="OrderGroup"/>.
/// The buyer's own delivery confirmation is recorded with OUR server clock (<see cref="BuyerConfirmedAt"/>); a client value is never read.
/// </summary>
public sealed class SellerOrderState
{
    public Guid GroupId { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = FulfilmentStatus.New;
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    /// <summary><c>seller</c> or <c>buyer</c> (from <c>order.delivered.by</c>).</summary>
    public string? DeliveredBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>The seller switched a carrier order to local delivery (<c>order.delivery_method_changed</c>): "local" or null.</summary>
    public string? DeliveryMethodChangedTo { get; set; }
    public int DeliveryAttemptsFailed { get; set; }
    public DateTimeOffset? LastAttemptFailedAt { get; set; }
    /// <summary><c>no_one_home | address_issue | refused | unsafe</c> only (the portal never sends the free-text note).</summary>
    public string? LastAttemptFailedReason { get; set; }
    /// <summary>OUR time when the buyer pressed "I received it" and the claim succeeded. Set once by a conditional UPDATE.</summary>
    public DateTimeOffset? BuyerConfirmedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One parcel (or local delivery) of a seller order. Keyed to the portal's shipment id; additive and idempotent (an event may be applied twice).</summary>
public sealed class OrderShipment
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string PortalShipmentId { get; set; } = "";
    /// <summary><c>carrier</c> or <c>local</c>.</summary>
    public string Method { get; set; } = "carrier";
    public string? Carrier { get; set; }
    public string? Service { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    /// <summary>A refunded label reopened the shipment (<c>shipment.voided</c>): its units no longer count as shipped.</summary>
    public DateTimeOffset? VoidedAt { get; set; }
    public string? TrackingStatus { get; set; }
    public string? TrackingStatusDetail { get; set; }
    public DateTimeOffset? Eta { get; set; }
    /// <summary>Local delivery: the promised window and the first name of the driver (first name only, as the portal sends it).</summary>
    public DateTimeOffset? EtaWindowStart { get; set; }
    public DateTimeOffset? EtaWindowEnd { get; set; }
    public string? DriverFirstName { get; set; }
    /// <summary>Carrier timeline: JSON array of <c>{status, detail, location, at}</c>, newest last, at most 30 entries.</summary>
    public string? TrackingEventsJson { get; set; }
    /// <summary>
    /// Coarse driver position (about 100 m), present only while the buyer's stop is next. Kept at most 24 h (<see cref="PositionAt"/>), cleared by the next event that carries none, by delivery,
    /// and by the maintenance job. Never logged.
    /// </summary>
    public double? PositionLat { get; set; }
    public double? PositionLng { get; set; }
    public DateTimeOffset? PositionAt { get; set; }
    /// <summary>Proof of delivery: portal-relative short-lived signed links (JSON array of path+query), kept only until <see cref="ProofExpiresAt"/>. The images are fetched and served by us.</summary>
    public string? ProofJson { get; set; }
    public DateTimeOffset? ProofExpiresAt { get; set; }
    public string? Placement { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OrderShipmentLine
{
    public Guid ShipmentId { get; set; }
    public string MarketplaceLineId { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>
/// Per order line, what the portal says was shipped, cancelled, returned and refunded, plus the units we have reserved for return requests that the portal has not decided yet.
/// A return may ask for at most <c>Shipped - Returned - Refunded - ReturnPending</c> units (ORDERS.md §3.3); the reservation is one conditional UPDATE, so parallel requests cannot oversell it.
/// </summary>
public sealed class OrderLineFulfilment
{
    public Guid LineId { get; set; }
    public int ShippedQty { get; set; }
    public int CancelledQty { get; set; }
    public int ReturnedQty { get; set; }
    public int RefundedQty { get; set; }
    public int ReturnPendingQty { get; set; }
}

public static class CancelRequestStatus
{
    /// <summary>Stored, not yet delivered to the portal (the outbox sends it).</summary>
    public const string Pending = "pending";
    /// <summary>The portal stored it; the seller decides (<c>order.cancelled</c> approves, <c>cancellation.decided</c> rejects).</summary>
    public const string Requested = "requested";
    public const string Rejected = "rejected", Approved = "approved", AlreadyCancelled = "already_cancelled", Failed = "failed";
}

public sealed class OrderCancelRequest
{
    public Guid Id { get; set; }
    /// <summary>Our idempotency id towards the portal (<c>requestId</c>), stored before the call.</summary>
    public string RequestId { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid GroupId { get; set; }
    public Guid AccountId { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public string Status { get; set; } = CancelRequestStatus.Pending;
    /// <summary>The group id while the request is open, otherwise NULL; unique, so a seller order has at most one open cancel request however many parallel clicks arrive.</summary>
    public string? OpenKey { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ReturnStatus
{
    public const string Pending = "pending", Requested = "requested", Approved = "approved", Rejected = "rejected", Received = "received", Refunded = "refunded", Failed = "failed";
}

public sealed class OrderReturn
{
    public Guid Id { get; set; }
    /// <summary>Our id towards the portal (<c>returnId</c> / <c>marketplaceReturnId</c>), stored before the call.</summary>
    public string ReturnId { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid GroupId { get; set; }
    public Guid AccountId { get; set; }
    public string Reason { get; set; } = "";
    public string? Comment { get; set; }
    public string Status { get; set; } = ReturnStatus.Pending;
    /// <summary>The client's idempotency key (header), unique per account: the same click twice is one return.</summary>
    public string? ClientKey { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>The fresh short-lived label link (never stored past <see cref="LabelUrlExpiresAt"/>).</summary>
    public string? LabelUrl { get; set; }
    public DateTimeOffset? LabelUrlExpiresAt { get; set; }
    public string? LabelCarrier { get; set; }
    public string? LabelTrackingNumber { get; set; }
    public string? LabelTrackingUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrderReturnLine> Lines { get; set; } = [];
}

public sealed class OrderReturnLine
{
    public Guid ReturnId { get; set; }
    public Guid LineId { get; set; }
    public string MarketplaceLineId { get; set; } = "";
    public int Quantity { get; set; }
    /// <summary>The reservation (<see cref="OrderLineFulfilment.ReturnPendingQty"/>) for this line is still held.</summary>
    public bool Reserved { get; set; }
}

internal sealed class FulfilmentConfiguration : IEntityTypeConfiguration<SellerOrderState>, IEntityTypeConfiguration<OrderShipment>, IEntityTypeConfiguration<OrderShipmentLine>,
    IEntityTypeConfiguration<OrderLineFulfilment>, IEntityTypeConfiguration<OrderCancelRequest>, IEntityTypeConfiguration<OrderReturn>, IEntityTypeConfiguration<OrderReturnLine>
{
    public void Configure(EntityTypeBuilder<SellerOrderState> b)
    {
        b.ToTable("seller_order_states");
        b.HasKey(s => s.GroupId);
        b.Property(s => s.GroupId).ValueGeneratedNever();
        b.Property(s => s.Status).HasMaxLength(30).IsRequired();
        b.Property(s => s.DeliveredBy).HasMaxLength(20);
        b.Property(s => s.CancelledBy).HasMaxLength(30);
        b.Property(s => s.CancelReason).HasMaxLength(100);
        b.Property(s => s.DeliveryMethodChangedTo).HasMaxLength(20);
        b.Property(s => s.LastAttemptFailedReason).HasMaxLength(30);
        b.HasIndex(s => s.OrderId);
    }

    public void Configure(EntityTypeBuilder<OrderShipment> b)
    {
        b.ToTable("order_shipments");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.PortalShipmentId).HasMaxLength(100).IsRequired();
        b.Property(s => s.Method).HasMaxLength(10).IsRequired();
        b.Property(s => s.Carrier).HasMaxLength(100);
        b.Property(s => s.Service).HasMaxLength(100);
        b.Property(s => s.TrackingNumber).HasMaxLength(100);
        b.Property(s => s.TrackingUrl).HasMaxLength(500);
        b.Property(s => s.TrackingStatus).HasMaxLength(40);
        b.Property(s => s.TrackingStatusDetail).HasMaxLength(200);
        b.Property(s => s.DriverFirstName).HasMaxLength(60);
        b.Property(s => s.TrackingEventsJson).HasMaxLength(8000);
        b.Property(s => s.ProofJson).HasMaxLength(2000);
        b.Property(s => s.Placement).HasMaxLength(40);
        b.HasIndex(s => new { s.GroupId, s.PortalShipmentId }).IsUnique();
    }

    public void Configure(EntityTypeBuilder<OrderShipmentLine> b)
    {
        b.ToTable("order_shipment_lines");
        b.HasKey(l => new { l.ShipmentId, l.MarketplaceLineId });
        b.Property(l => l.MarketplaceLineId).HasMaxLength(60);
    }

    public void Configure(EntityTypeBuilder<OrderLineFulfilment> b)
    {
        b.ToTable("order_line_fulfilment");
        b.HasKey(f => f.LineId);
        b.Property(f => f.LineId).ValueGeneratedNever();
    }

    public void Configure(EntityTypeBuilder<OrderCancelRequest> b)
    {
        b.ToTable("order_cancel_requests");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.RequestId).HasMaxLength(60).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(40).IsRequired();
        b.Property(r => r.Note).HasMaxLength(500);
        b.Property(r => r.Status).HasMaxLength(30).IsRequired();
        b.Property(r => r.OpenKey).HasMaxLength(40);
        b.Property(r => r.DecisionNote).HasMaxLength(500);
        b.HasIndex(r => r.RequestId).IsUnique();
        b.HasIndex(r => r.OpenKey).IsUnique();
        b.HasIndex(r => new { r.OrderId, r.GroupId });
    }

    public void Configure(EntityTypeBuilder<OrderReturn> b)
    {
        b.ToTable("order_returns");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.ReturnId).HasMaxLength(60).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(40).IsRequired();
        b.Property(r => r.Comment).HasMaxLength(1000);
        b.Property(r => r.Status).HasMaxLength(30).IsRequired();
        b.Property(r => r.ClientKey).HasMaxLength(64);
        b.Property(r => r.DecisionNote).HasMaxLength(500);
        b.Property(r => r.LabelUrl).HasMaxLength(1000);
        b.Property(r => r.LabelCarrier).HasMaxLength(100);
        b.Property(r => r.LabelTrackingNumber).HasMaxLength(100);
        b.Property(r => r.LabelTrackingUrl).HasMaxLength(500);
        b.HasIndex(r => r.ReturnId).IsUnique();
        b.HasIndex(r => new { r.AccountId, r.ClientKey }).IsUnique();
        b.HasIndex(r => new { r.OrderId, r.GroupId });
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReturnId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<OrderReturnLine> b)
    {
        b.ToTable("order_return_lines");
        b.HasKey(l => new { l.ReturnId, l.LineId });
        b.Property(l => l.MarketplaceLineId).HasMaxLength(60).IsRequired();
    }
}
