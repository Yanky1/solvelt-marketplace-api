using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// Booking statuses as the portal names them (SERVICES.md §4), plus the two states only we have: <see cref="HoldPending"/> (our row exists, the portal has not
/// answered the hold yet) and <see cref="Failed"/> (the portal refused the hold: the slot went to someone else, the area is not served ...).
/// Absorbing (terminal) states: Completed, Declined, Expired, CancelledByBuyer, CancelledBySeller, NoShow, Failed (decision O-10).
/// </summary>
public static class BookingStatus
{
    public const string HoldPending = "HoldPending", Hold = "Hold", Requested = "Requested", Confirmed = "Confirmed", Rescheduled = "Rescheduled", InProgress = "InProgress",
        Completed = "Completed", Declined = "Declined", Expired = "Expired", CancelledByBuyer = "CancelledByBuyer", CancelledBySeller = "CancelledBySeller", NoShow = "NoShow", Failed = "Failed";

    public static readonly string[] Terminal = [Completed, Declined, Expired, CancelledByBuyer, CancelledBySeller, NoShow, Failed];
    public static readonly string[] ActiveHold = [HoldPending, Hold];
    public static bool IsTerminal(string s) => Terminal.Contains(s);
    /// <summary>A booking the buyer can see as theirs (a failed or pending hold attempt is not a booking).</summary>
    public static bool IsVisible(string s) => s is not (HoldPending or Failed);
    /// <summary>Statuses the portal sends, parsed leniently; null for anything we do not know.</summary>
    public static string? FromPortal(string? s) => s is not null && Array.IndexOf(All, s) >= 0 ? s : null;
    private static readonly string[] All = [Hold, Requested, Confirmed, Rescheduled, InProgress, Completed, Declined, Expired, CancelledByBuyer, CancelledBySeller, NoShow];
}

/// <summary>
/// A buyer's booking (<c>BK-…</c>, our idempotency id towards the portal). The row is inserted BEFORE the portal is called (decision M4-02), so a crash or retry
/// finds it and asks the portal about the same id instead of creating another. Every amount is a <c>long</c> in cents and is the portal's, never the client's.
/// </summary>
public sealed class Booking
{
    public Guid Id { get; set; }
    /// <summary><c>BK-…</c> (at most 64 characters from the portal's id alphabet).</summary>
    public string MarketplaceBookingId { get; set; } = "";
    /// <summary>The portal's booking id; null until the hold answer is stored. Events are matched on BOTH ids (<c>EventRefs.Matches</c>).</summary>
    public Guid? PortalBookingId { get; set; }
    public string? Number { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>The caller's <c>Idempotency-Key</c> for the hold; unique per account, so a double click or a retry returns the same booking.</summary>
    public string IdempotencyKey { get; set; } = "";
    /// <summary>Hash of what the key was used for: the same key with a different request is refused, not answered with the first booking.</summary>
    public string RequestHash { get; set; } = "";
    public Guid ListingId { get; set; }
    public Guid? ProviderId { get; set; }
    public string Status { get; set; } = BookingStatus.HoldPending;
    /// <summary>Why a hold attempt failed (portal code), for the buyer's message. Null otherwise.</summary>
    public string? FailureCode { get; set; }
    public string Location { get; set; } = "";
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public int DurationMinutes { get; set; }
    public string Currency { get; set; } = "USD";
    public long PriceCents { get; set; }
    public long AddOnsCents { get; set; }
    public long TravelFeeCents { get; set; }
    public long TotalCents { get; set; }
    public string AddOnIds { get; set; } = "";
    public DateTimeOffset? HoldExpiresAt { get; set; }
    public DateTimeOffset? ApprovalDeadline { get; set; }
    public DateTimeOffset? ProposedStartAt { get; set; }
    public long CancellationFeeCents { get; set; }
    public long RefundDueCents { get; set; }
    public long NoShowFeeCents { get; set; }
    public long AdditionalChargeCents { get; set; }
    public int RescheduleCount { get; set; }
    // The policy snapshot as the portal sent it (null = the portal has not told us): used only to PREVIEW a fee; the fee itself is always the portal's.
    public int? PolicyCancelFreeHours { get; set; }
    public int? PolicyCancelFeePercent { get; set; }
    public int? PolicyNoShowFeePercent { get; set; }
    public int? PolicyMaxReschedules { get; set; }
    /// <summary>A seller moved the booking (<c>booking.rescheduled</c> with <c>requiresBuyerConsent</c>): the buyer must accept the new time or cancel without a fee.</summary>
    public bool RequiresBuyerConsent { get; set; }
    /// <summary>The time the buyer asked for while a request-mode service decides (<c>booking.reschedule_requested</c>), cleared by <c>reschedule_decided</c>.</summary>
    public DateTimeOffset? RescheduleRequestedStartAt { get; set; }
    /// <summary>"requested" (the seller asks the buyer for more money, nothing is charged yet), "declined", "paid"; null when there is no request. Charged only after the buyer's consent (decision M4-06).</summary>
    public string? ExtraChargeState { get; set; }
    /// <summary>The meeting link: stored only while the booking is Confirmed, Rescheduled or InProgress, and cleared when it leaves them. Never logged, never in a list.</summary>
    public string? OnlineInstructions { get; set; }
    // Where the service happens (personal data, for at-customer services): see the PII table.
    public string? AddrName { get; set; }
    public string? AddrLine1 { get; set; }
    public string? AddrLine2 { get; set; }
    public string? AddrCity { get; set; }
    public string? AddrRegion { get; set; }
    public string? AddrPostalCode { get; set; }
    public string? AddrCountry { get; set; }
    public double? AddrLat { get; set; }
    public double? AddrLng { get; set; }
    public string? Phone { get; set; }
    /// <summary>Intake answers as JSON (personal data).</summary>
    public string? IntakeJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Set when the personal data above was redacted by the retention job.</summary>
    public DateTimeOffset? RedactedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.ToTable("bookings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MarketplaceBookingId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.FailureCode).HasMaxLength(60);
        b.Property(x => x.Location).HasMaxLength(10).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.AddOnIds).HasMaxLength(1000).IsRequired();
        b.Property(x => x.OnlineInstructions).HasMaxLength(2000);
        b.Property(x => x.ExtraChargeState).HasMaxLength(20);
        b.Property(x => x.AddrName).HasMaxLength(200);
        b.Property(x => x.AddrLine1).HasMaxLength(200);
        b.Property(x => x.AddrLine2).HasMaxLength(200);
        b.Property(x => x.AddrCity).HasMaxLength(100);
        b.Property(x => x.AddrRegion).HasMaxLength(100);
        b.Property(x => x.AddrPostalCode).HasMaxLength(20);
        b.Property(x => x.AddrCountry).HasMaxLength(2);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.IntakeJson).HasMaxLength(8000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.MarketplaceBookingId).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.AccountId, x.Status });
        b.HasIndex(x => new { x.Status, x.HoldExpiresAt });
        b.HasIndex(x => x.PortalBookingId);
    }
}
