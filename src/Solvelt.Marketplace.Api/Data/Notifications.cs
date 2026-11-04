using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>The groups a buyer can switch email for. In-app notifications are always kept; only email follows the preference.</summary>
public static class NotificationCategories
{
    public const string OrderUpdates = "order_updates", Shipped = "shipped", OutForDelivery = "out_for_delivery", Delivered = "delivered",
        Messages = "messages", Bookings = "bookings", All = "all";
    /// <summary>Mail about money and about a delivery that did not happen (refund issued, order cancelled, failed delivery attempt): TRANSACTIONAL (decision O-21 / M5-20). It cannot be switched
    /// off, "all" does not touch it, and it is not in <see cref="Switchable"/>; the in-app entry exists as always.</summary>
    public const string Transactional = "transactional";
    public static readonly string[] Switchable = [OrderUpdates, Shipped, OutForDelivery, Delivered, Messages, Bookings];
    public static readonly string[] AlwaysOn = ["refunds", "cancellations", "failed_delivery_attempts"];
}

public static class NotificationKinds
{
    public const string OrderAccepted = "order_accepted", OrderShipped = "order_shipped", OutForDelivery = "out_for_delivery", OrderDelivered = "order_delivered",
        OrderCancelled = "order_cancelled", CancellationRejected = "cancellation_rejected", ReturnApproved = "return_approved", ReturnRejected = "return_rejected",
        ReturnReceived = "return_received", ReturnLabelReady = "return_label_ready", DeliveryAttemptFailed = "delivery_attempt_failed", RefundIssued = "refund_issued",
        MessageReceived = "message_received",
        BookingConfirmed = "booking_confirmed", BookingDeclined = "booking_declined", BookingTimeProposed = "booking_time_proposed", BookingRescheduled = "booking_rescheduled",
        BookingCancelled = "booking_cancelled", BookingCompleted = "booking_completed", BookingReminder = "booking_reminder", BookingExtraChargeRequested = "booking_extra_charge_requested", BookingNoShow = "booking_no_show";

    public static string CategoryOf(string kind) => kind switch
    {
        OrderShipped => NotificationCategories.Shipped,
        OutForDelivery => NotificationCategories.OutForDelivery,
        OrderDelivered => NotificationCategories.Delivered,
        RefundIssued or OrderCancelled or DeliveryAttemptFailed => NotificationCategories.Transactional,
        MessageReceived => NotificationCategories.Messages,
        var k when k.StartsWith("booking_") => NotificationCategories.Bookings,
        _ => NotificationCategories.OrderUpdates,
    };
}

/// <summary>
/// One entry of the buyer's notification centre. Holds ids and a few non-personal parameters (seller name, carrier, amount), never an address or free text. <see cref="DedupeKey"/>
/// (portal event id + kind) is unique, so an event produces each notification once.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Kind { get; set; } = "";
    public string? OrderNumber { get; set; }
    public string? SellerOrderNumber { get; set; }
    public string? ParamsJson { get; set; }
    public string DedupeKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Email preferences. No row means the defaults (everything on). A category that is off sends no email; the in-app entry is still kept.</summary>
public sealed class NotificationPreference
{
    public Guid AccountId { get; set; }
    public bool EmailOrderUpdates { get; set; } = true;
    public bool EmailShipped { get; set; } = true;
    public bool EmailOutForDelivery { get; set; } = true;
    public bool EmailDelivered { get; set; } = true;
    public bool EmailMessages { get; set; } = true;
    public bool EmailBookings { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    public bool Wants(string category) => category switch
    {
        NotificationCategories.OrderUpdates => EmailOrderUpdates, NotificationCategories.Shipped => EmailShipped, NotificationCategories.OutForDelivery => EmailOutForDelivery,
        NotificationCategories.Delivered => EmailDelivered, NotificationCategories.Messages => EmailMessages,
        NotificationCategories.Bookings => EmailBookings, _ => false,
    };

    public void Set(string category, bool on)
    {
        switch (category)
        {
            case NotificationCategories.OrderUpdates: EmailOrderUpdates = on; break;
            case NotificationCategories.Shipped: EmailShipped = on; break;
            case NotificationCategories.OutForDelivery: EmailOutForDelivery = on; break;
            case NotificationCategories.Delivered: EmailDelivered = on; break;
            case NotificationCategories.Messages: EmailMessages = on; break;
            case NotificationCategories.Bookings: EmailBookings = on; break;
        }
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>, IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications");
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).ValueGeneratedNever();
        b.Property(n => n.Kind).HasMaxLength(40).IsRequired();
        b.Property(n => n.OrderNumber).HasMaxLength(40);
        b.Property(n => n.SellerOrderNumber).HasMaxLength(60);
        b.Property(n => n.ParamsJson).HasMaxLength(1000);
        b.Property(n => n.DedupeKey).HasMaxLength(160).IsRequired();
        b.HasIndex(n => n.DedupeKey).IsUnique();
        b.HasIndex(n => new { n.AccountId, n.CreatedAt });
    }

    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("notification_preferences");
        b.HasKey(p => p.AccountId);
        b.Property(p => p.AccountId).ValueGeneratedNever();
    }
}
