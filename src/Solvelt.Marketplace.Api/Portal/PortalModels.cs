using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solvelt.Marketplace.Api.Portal;

// ---- shared ----
public sealed record PortalError(string? Code, string? Message, IReadOnlyList<InvalidOrderError>? Errors = null);
public sealed record InvalidOrderError(int Index, string Code, string Message);

public sealed record PortalResult<T>(int Status, T? Value, PortalError? Error, int Attempts)
{
    public bool IsSuccess => Status is >= 200 and < 300;
    /// <summary>True when the final answer was one the contract says to re-send later (retries were exhausted).</summary>
    public bool Retryable { get; init; }
}

// ---- POST /orders (ORDERS.md §3.2, LOCAL-DELIVERY.md §4) ----
public sealed record PortalBuyer(string DisplayName, string RelayEmail, string? Phone = null);
public sealed record PortalAddress(string Name, string Line1, string? Line2, string City, string? Region, string PostalCode, string Country, double? Lat = null, double? Lng = null);
/// <summary>A line names its product by ids, never by SKU (CONTRACT.md §3).</summary>
public sealed record OrderLineRequest(string MarketplaceLineId, Guid SellerId, Guid BrandId, Guid ProductId, Guid? VariantId,
    int Quantity, long UnitPriceCents, long DiscountCents = 0, long TaxCents = 0);
/// <summary>
/// One shipping charge per (seller, brand) group. <see cref="Speed"/>, <see cref="Method"/> and <see cref="TaxCents"/> are contract request C8 (accepted by the portal, not merged yet):
/// they are sent only when <c>Portal:PerGroupShipping</c> is on, and omitted (null) otherwise.
/// </summary>
public sealed record ShippingChargeRequest(Guid SellerId, Guid BrandId, long AmountCents, string? Speed = null, string? Method = null, long? TaxCents = null);
public sealed record LocalDeliveryRequest(Guid SellerId, Guid BrandId, string Method, Guid WindowId, string Date);
public sealed record CreateOrderRequest(string MarketplaceOrderId, DateTimeOffset PlacedAt, string Currency, PortalBuyer Buyer, PortalAddress ShippingAddress,
    string ShippingMethod, string ShippingSpeed, string? GiftMessage, IReadOnlyList<OrderLineRequest> Lines, IReadOnlyList<ShippingChargeRequest> Shipping,
    IReadOnlyList<LocalDeliveryRequest>? Deliveries = null);
public sealed record SellerOrderRef(Guid SellerOrderId, string Number, Guid SellerId, Guid BrandId, string Status, bool Oversold, long TotalCents, long FeeCents, long NetCents);
public sealed record CreateOrderResponse(bool Created, IReadOnlyList<SellerOrderRef> Orders);

// ---- buyer events (ORDERS.md §3.3). Response shapes are per-order result lists the portal docs do not pin as a schema: returned raw. ----
public sealed record CancelRequestBody(string RequestId, string Reason, string? Note = null, string? SellerOrderNumber = null);
public sealed record ReturnLineRequest(string MarketplaceLineId, int Quantity);
public sealed record ReturnRequestBody(string ReturnId, string Reason, string? Comment, string? SellerOrderNumber, IReadOnlyList<ReturnLineRequest> Lines);
/// <summary>Deliberately has no <c>deliveredAt</c>: the portal stamps its own server time (never a client value).</summary>
public sealed record DeliveryConfirmationBody(string? SellerOrderNumber = null, string? TrackingNumber = null);

/// <summary>A binary answer (an attachment). <see cref="Body"/> is null unless the status is 2xx; <see cref="Retryable"/> says the portal answered something the contract says to try again later.</summary>
public sealed record PortalBytes(int Status, byte[]? Body, string? ContentType, bool Retryable)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

// ---- messaging (ORDERS.md §3.3, §6) and content reports (CATALOG.md §6) ----
/// <summary><c>kind</c> is <c>order</c> or <c>product</c>; the buyer is shown to the seller by display name only.</summary>
public sealed record BuyerMessageBody(string MessageId, string ConversationId, string Kind, string? MarketplaceOrderId, string? SellerOrderNumber, Guid? ProductId,
    string BuyerName, string Text, DateTimeOffset SentAt);
/// <summary><c>reporterRef</c> is an opaque reference for the reporter (never a name or e-mail).</summary>
public sealed record ContentReportBody(string ReportId, string Kind, string TargetId, string Reason, string? Note, string ReporterRef, string? SellerOrderNumber = null, Guid? SellerId = null);

/// <summary>The answer to <c>POST /returns/{id}/label-link</c>: a signed link valid about 24 h, bound to that one label.</summary>
public sealed record ReturnLabelLink(string? LabelUrl, DateTimeOffset? LabelUrlExpiresAt, string? TrackingNumber, string? Carrier);

// ---- GET /shipping/settings (SHIPPING.md §4) ----
public sealed record ShipsFrom(string? Country, string? Region);
public sealed record ShippingSpeedSetting(string Speed, bool Enabled, string Method, long? FlatCents, long? BaseCents, long? PerKgCents);
public sealed record ShippingZone(string Name, string Kind, IReadOnlyList<string> Countries, IReadOnlyList<ShippingSpeedSetting> Speeds);
public sealed record ShippingProfile(Guid Id, string Name, bool IsDefault, string Currency, long? FreeShippingThresholdCents, IReadOnlyList<ShippingZone> Zones);
public sealed record ShippingSettings(Guid SellerId, Guid BrandId, int HandlingDays, ShipsFrom? ShipsFrom, IReadOnlyList<ShippingProfile> Profiles);

// ---- POST /local-delivery/eligibility (LOCAL-DELIVERY.md §4) ----
public sealed record EligibilityAddress(string PostalCode, string Country, double? Lat = null, double? Lng = null);
public sealed record LocalDeliveryEligibilityRequest(Guid SellerId, Guid BrandId, long SubtotalCents, EligibilityAddress Address);
public sealed record DeliveryZoneRef(Guid Id, string Name);
public sealed record DeliveryWindow(Guid WindowId, string Date, DateTimeOffset Start, DateTimeOffset End, int Remaining);
public sealed record LocalDeliveryEligibility(bool Eligible, string? Reason, string? Method, long? FeeCents, DeliveryZoneRef? Zone, string? TimeZone, IReadOnlyList<DeliveryWindow>? Windows);

// ---- GET /services/{id}/availability (SERVICES.md §6) ----
public sealed record ServiceAvailabilityQuery(Guid ServiceId, DateTimeOffset From, DateTimeOffset To, string Location, string? PostalCode = null,
    Guid? ProviderId = null, double? Lat = null, double? Lng = null, int? Minutes = null, IReadOnlyList<string>? AddOns = null);
public sealed record AvailabilityProvider(Guid ProviderId, string TimeZone);
public sealed record AvailabilitySlot(DateTimeOffset StartAt, DateTimeOffset EndAt, IReadOnlyList<Guid> ProviderIds);
public sealed record ServiceAvailability(int DurationMinutes, long PriceCents, IReadOnlyList<AvailabilityProvider> Providers, IReadOnlyList<AvailabilitySlot> Slots);

// ---- bookings (SERVICES.md §6) ----
public sealed record IntakeAnswerBody(string Id, string Answer);
/// <summary>The booking body (<c>POST /bookings/holds</c> and <c>POST /bookings</c>). <c>Start</c> is the slot the portal itself listed; the portal re-validates it, the price and the duration.</summary>
public sealed record BookingRequestBody(string MarketplaceBookingId, Guid ServiceId, Guid? ProviderId, DateTimeOffset Start, int? DurationMinutes, string Location,
    IReadOnlyList<string> AddOnIds, PortalBuyer Buyer, PortalAddress? Address, IReadOnlyList<IntakeAnswerBody>? IntakeAnswers = null);
public sealed record BookingConfirmBody(PortalBuyer Buyer, PortalAddress? Address, IReadOnlyList<IntakeAnswerBody>? IntakeAnswers = null);
/// <summary>The portal's booking summary. Every amount is the portal's (long cents); <c>OnlineInstructions</c> (the meeting link) is only present for a confirmed online booking.</summary>
public sealed record PortalBookingSummary(Guid BookingId, string? Number, string MarketplaceBookingId, string Status, Guid? ServiceId, Guid? ProviderId, string? Location,
    DateTimeOffset? StartAt, DateTimeOffset? EndAt, int DurationMinutes, DateTimeOffset? HoldExpiresAt, DateTimeOffset? ApprovalDeadline, long PriceCents, long AddOnsCents,
    long TravelFeeCents, long TotalCents, long CancellationFeeCents, long AdditionalChargeCents, DateTimeOffset? ProposedStartAt, int RescheduleCount, string? OnlineInstructions,
    PortalBookingPolicy? Policy = null);
/// <summary>The policy snapshot the portal froze at booking time (SERVICES.md §4). Optional in the summary until the portal sends it (contract request); null means "unknown", never a guess.</summary>
public sealed record PortalBookingPolicy(int? CancelFreeHours, int? CancelFeePercent, int? NoShowFeePercent, int? MaxReschedules, int? ApprovalHours);
public sealed record BookingResponse(bool Created, PortalBookingSummary Booking);

// ---- GET /services/{id}/options (contract request M4-C1; the portal has not shipped it yet, so every field is optional and the reader degrades) ----
public sealed record ServiceAddOn(string? Id, string? Name, string? Description, long PriceCents, int ExtraMinutes);
public sealed record IntakeQuestion(string? Id, string? Label, string? Kind, bool Required, IReadOnlyList<string>? Choices, int? MaxLength);
public sealed record ServiceProviderInfo(Guid ProviderId, string? DisplayName, Guid? PhotoImageId);
public sealed record ServiceTravel(long? FeeCents, IReadOnlyList<string>? ServiceAreaPostalCodes, double? ServiceRadiusKm);
public sealed record ServiceOptionsDocument(Guid? ServiceId, string? Version, IReadOnlyList<ServiceAddOn>? AddOns, IReadOnlyList<IntakeQuestion>? IntakeQuestions,
    IReadOnlyList<ServiceProviderInfo>? Providers, string? Assignment, PortalBookingPolicy? Policy, ServiceTravel? Travel);
public sealed record BookingCancelBody(string RequestId, string? Reason);
public sealed record BookingRescheduleBody(string RequestId, DateTimeOffset NewStart);
public sealed record BookingProposalBody(bool Accept);

// ---- POST /payment-events (PAYMENTS.md §6) ----
public static class PaymentEventTypes
{
    public const string Captured = "payment.captured", RefundProcessed = "refund.processed";
    public const string DisputeCreated = "dispute.created", DisputeUpdated = "dispute.updated", DisputeClosed = "dispute.closed";
}
public static class PaymentKinds
{
    public const string Order = "order", Booking = "booking", CancellationFee = "booking_cancellation_fee",
        NoShowFee = "booking_no_show_fee", ExtraCharge = "booking_extra_charge";
}
public sealed record PaymentCapturedData(string Kind, string? MarketplaceOrderId, string? MarketplaceBookingId, string? SellerOrderNumber,
    string PaymentRef, string Currency, long? AmountCents = null, DateTimeOffset? CapturedAt = null);
public sealed record RefundProcessedData(string RefundId, string? MarketplaceOrderId, string? SellerOrderNumber, string? MarketplaceBookingId,
    long AmountCents, long? ShippingCents = null, string? Currency = null, string? Reason = null);
public sealed record DisputeData(string DisputeId, string? MarketplaceOrderId, string? SellerOrderNumber, string? MarketplaceBookingId,
    long AmountCents, string Status, long? FeeCents = null, string? Currency = null, string? Reason = null, DateTimeOffset? EvidenceDueBy = null, DateTimeOffset? CreatedAt = null);
/// <summary><see cref="EventId"/> is ours, stored in our outbox before sending (the portal dedupes on it).</summary>
public sealed record PaymentEventRequest(string EventId, string Type, object Data);
public sealed record PaymentEventResponse(string Status, JsonElement? Result);

public static class PortalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
