using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class BookingPaymentStatus
{
    /// <summary>The intent exists; no money has arrived.</summary>
    public const string Pending = "pending";
    /// <summary>Stripe took exactly the amount we priced.</summary>
    public const string Captured = "captured";
    /// <summary>The money arrived but the booking could not be honoured (hold gone, wrong amount): it is refunded in full and never reported to the portal as a sale.</summary>
    public const string Void = "void";
    public const string Failed = "failed";
}

/// <summary>
/// One charge of a booking (decision M4-08). Kinds follow the portal's <c>payment.captured</c> kinds: <c>booking</c>, <c>booking_cancellation_fee</c>, <c>booking_no_show_fee</c>,
/// <c>booking_extra_charge</c>. <see cref="Key"/> (unique) is what makes "one active payment per booking attempt": the booking's own payment is <c>pay:{BK}:booking</c>, a fee
/// is <c>pay:{BK}:fee:cancel</c> and so on, so a second request, a double click or a parallel one is the same row and the same Stripe intent (the key is also Stripe's idempotency key).
/// The amount is the portal's, never the client's.
/// </summary>
public sealed class BookingPayment
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public Guid BookingId { get; set; }
    public string Kind { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public long AmountCents { get; set; }
    public string? IntentId { get; set; }
    public string Status { get; set; } = BookingPaymentStatus.Pending;
    public long CapturedCents { get; set; }
    /// <summary>Sum of refunds RESERVED so far (before Stripe is called), never above <see cref="CapturedCents"/>.</summary>
    public long RefundedCents { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    /// <summary>The portal has been told (<c>payment.captured</c> queued).</summary>
    public bool ReportedToPortal { get; set; }
}

/// <summary>A refund of a booking payment: reserved against the cap, then sent to Stripe, then (when it reverses a sale the portal knows) reported as <c>refund.processed</c> with <c>marketplaceBookingId</c>.</summary>
public sealed class BookingRefund
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public Guid PaymentId { get; set; }
    public Guid BookingId { get; set; }
    public long AmountCents { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = RefundStatus.Reserved;
    public string? StripeRefundId { get; set; }
    public bool ReportToPortal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class BookingPaymentConfiguration : IEntityTypeConfiguration<BookingPayment>, IEntityTypeConfiguration<BookingRefund>
{
    public void Configure(EntityTypeBuilder<BookingPayment> b)
    {
        b.ToTable("booking_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Key).HasMaxLength(120).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.IntentId).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.FailureCode).HasMaxLength(60);
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.IntentId).IsUnique();
        b.HasIndex(x => x.BookingId);
    }

    public void Configure(EntityTypeBuilder<BookingRefund> b)
    {
        b.ToTable("booking_refunds");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Key).HasMaxLength(160).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(60).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.StripeRefundId).HasMaxLength(100);
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.PaymentId);
    }
}
