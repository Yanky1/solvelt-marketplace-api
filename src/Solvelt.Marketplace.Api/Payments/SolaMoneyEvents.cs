using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Payments;

/// <summary>
/// The money events of a Sola-paid phone order, as the portal's contract C19 (<c>docs/CONTRACT.md</c> of the seller portal, section C19 and the checklist C19.6) specifies them (decision V-49).
/// A Stripe event is left exactly as it always was (<c>provider</c> absent means Stripe, C19.1 rule 1), so a website order never changes; a Sola event names <c>"sola"</c>, carries Sola's own
/// references (at most 100 characters) and, for a dispute, ALWAYS a <c>feeCents</c> (otherwise the portal applies its default of 1500). Nothing here talks to Sola.
/// </summary>
public static class SolaMoneyEvents
{
    /// <summary>The <c>provider</c> field of an event of an order paid by <paramref name="paymentProvider"/>: <c>"sola"</c> for Sola, nothing (null) for Stripe.</summary>
    public static string? ProviderField(string? paymentProvider) => paymentProvider == PaymentProviders.Sola ? PaymentProviders.Sola : null;

    /// <summary>A Sola reference the portal will accept: not empty, at most 100 characters, plain characters only (the contract's limit; <c>422 invalid_payment_ref</c> otherwise).</summary>
    public static bool IsValidSolaRef(string? reference) => !string.IsNullOrWhiteSpace(reference) && reference.Length <= PaymentProviders.MaxSolaRefLength && reference.All(c => !char.IsControl(c)) && reference == reference.Trim();

    /// <summary>
    /// <c>payment.captured</c> for one seller order of a paid order: for Stripe the payment intent id as before; for Sola <c>provider: "sola"</c> and <c>paymentRef</c> = Sola's <c>xRefNum</c>.
    /// Null when the order is Sola's and has no valid reference: it is never sent (the caller dead-letters it and alerts), because the portal would refuse it.
    /// </summary>
    public static PaymentCapturedData? Capture(MarketplaceOrder order, long amountCents, string sellerOrderNumber)
    {
        if (order.PaymentProvider == PaymentProviders.Sola)
            return IsValidSolaRef(order.PaymentRef)
                ? new PaymentCapturedData(PaymentKinds.Order, order.Number, null, sellerOrderNumber, order.PaymentRef!, order.Currency, amountCents, order.PaidAt, PaymentProviders.Sola)
                : null;
        return new PaymentCapturedData(PaymentKinds.Order, order.Number, null, sellerOrderNumber, order.PaymentIntentId!, order.Currency, amountCents, order.PaidAt);
    }

    /// <summary>The event id of a Sola capture: one per seller order, from the order number and the seller order (our ids; the portal dedupes on it). Never longer than the portal's 100.</summary>
    public static string CaptureEventId(string orderNumber, string sellerOrderNumber) => $"captured:{orderNumber}:{sellerOrderNumber}";

    /// <summary><c>refund.processed</c> of a Sola refund: <paramref name="solaRefundRef"/> is the <c>xRefNum</c> of the refund transaction (<c>cc:refund</c> or <c>cc:voidrefund</c>), sent only after Sola confirmed it.</summary>
    public static RefundProcessedData? Refund(string solaRefundRef, string marketplaceOrderId, string sellerOrderNumber, long itemsCents, long shippingCents, string currency, string? reason)
        => IsValidSolaRef(solaRefundRef) ? new RefundProcessedData(solaRefundRef, marketplaceOrderId, sellerOrderNumber, null, itemsCents, shippingCents, currency, reason, PaymentProviders.Sola) : null;

    /// <summary>
    /// <c>dispute.created</c>, <c>.updated</c> or <c>.closed</c> of a Sola chargeback, one per seller order (this seller order's share). <paramref name="feeCents"/> is REQUIRED and always sent: left out, the portal
    /// would charge its default of 1500. Null when the chargeback reference is not a valid Sola reference or the fee is negative.
    /// </summary>
    public static DisputeData? Dispute(string solaChargebackRef, string marketplaceOrderId, string sellerOrderNumber, long amountCents, long feeCents, string currency, string status, string? reason = null, DateTimeOffset? evidenceDueBy = null)
        => IsValidSolaRef(solaChargebackRef) && feeCents >= 0 && amountCents >= 0
            ? new DisputeData(solaChargebackRef, marketplaceOrderId, sellerOrderNumber, null, amountCents, status, feeCents, currency, reason, evidenceDueBy, null, PaymentProviders.Sola)
            : null;

    /// <summary>The statuses the portal knows for a dispute (C19.6); anything else is a bug in the sender.</summary>
    public static readonly IReadOnlySet<string> DisputeStatuses = new HashSet<string> { "needs_response", "under_review", "won", "lost", "warning_closed" };

    /// <summary>The type of a dispute event for a status: <c>dispute.created</c> for the first report, <c>dispute.closed</c> for a final one, otherwise <c>dispute.updated</c>.</summary>
    public static string DisputeType(string status, bool first) => first ? PaymentEventTypes.DisputeCreated : status is "won" or "lost" or "warning_closed" ? PaymentEventTypes.DisputeClosed : PaymentEventTypes.DisputeUpdated;

    /// <summary>An id that cannot collide (C19.1 rule 8): <c>sola-cb-{chargebackRef}-{status}-{sellerOrderNumber}</c>, cut to the portal's 100 characters with a hash when long.</summary>
    public static string DisputeEventId(string chargebackRef, string status, string sellerOrderNumber)
    {
        var id = $"sola-cb-{chargebackRef}-{status}-{sellerOrderNumber}";
        return id.Length <= 100 ? id : id[..80] + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
    }
}
