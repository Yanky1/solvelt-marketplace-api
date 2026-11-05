using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>
/// Which module erases which buyer-linked tables when an account is deleted (review of the account slice, A3, and of 1725a68, item 6). The test scans the EF model: every
/// entity with an <c>AccountId</c>, with a property that looks like a buyer's data (email, phone, street, postal code, name of a recipient or buyer, file name, coordinates), or
/// that is a child (EF foreign key) of a registered entity must either be claimed by a module below or sit in <see cref="NoBuyerData"/> with the reason, so a new table that
/// holds a buyer's data cannot be added without a decision about its erasure. What the scan cannot see is data inside free-form columns such as JSON payloads. A module either has an <see cref="IAccountEraser"/>
/// registered, or is listed as pending, and the pending list is asserted exactly: the day a module registers its eraser, the test makes its owner move it out of the list.
/// Pending modules are a LAUNCH GATE: until they are empty the deletion of an account leaves that module's personal data behind.
/// </summary>
public static class PiiRegistry
{
    public sealed record Module(string Name, string Owner, Type[] Entities, Type? Eraser);

    public static readonly IReadOnlyList<Module> Modules =
    [
        new("demo-data", "M2", [], typeof(Demo.DemoAccountEraser)),   // marked rows of the modules below (M9); first, so a demo row is removed outright, not redacted
        new("account", "M1", [typeof(BuyerAccount), typeof(BuyerAddress), typeof(BuyerSession), typeof(EmailToken), typeof(BuyerPasskey), typeof(WebAuthnChallenge)], null),   // erased by AccountService.RedactAsync itself
        new("messaging", "M5", [typeof(Notification), typeof(NotificationPreference), typeof(MessageThread), typeof(ThreadMessage), typeof(MessageAttachment), typeof(MessageAttachmentData)], typeof(Messaging.MessagingAccountEraser)),
        new("orders-and-carts", "M3", [typeof(MarketplaceOrder), typeof(Cart)], typeof(Checkout.OrdersAccountEraser)),   // decision M3-35
        new("order-cancellations-and-returns", "M5", [typeof(OrderCancelRequest), typeof(OrderReturn)], typeof(Fulfilment.AfterSalesAccountEraser)),
        new("portal-inbound-events", "M1", [typeof(PortalInboundEvent)], typeof(Portal.Webhooks.PortalEventsAccountEraser)),   // decision M1-82: the raw bodies of events about the account's orders, bookings, quotes and conversations
        new("bookings", "M4", [typeof(Booking), typeof(BuyerQuote)], typeof(Services.BookingsAccountEraser)),
    ];

    /// <summary>Entities the scan would otherwise flag that hold no personal data of a buyer, each with the reason. A new entry needs a reviewer who agrees with the reason.</summary>
    public static readonly IReadOnlyList<(Type Entity, string Reason)> NoBuyerData =
    [
        (typeof(CartLine), "items and quantities of a cart; the cart (claimed) holds the owner"),
        (typeof(OrderGroup), "per-seller totals, shipping money and seller names of an order; the order (claimed) holds the buyer and the address"),
        (typeof(OrderLine), "items, quantities and prices of an order; the order (claimed) holds the buyer and the address"),
        (typeof(OrderReturnLine), "which order lines come back and how many; the return (claimed) holds the buyer"),
        (typeof(StaffAccount), "a member of staff: their own email and name, not a buyer's data (M7-01); no buyer id; see the PII table"),
        (typeof(OrderShipment), "carrier, tracking and the delivery driver's own position: data about the driver, not the buyer"),
    ];

    /// <summary>Modules that hold buyer data and have no eraser yet (launch gate). Account itself is not pending: its own job erases it.</summary>
    public static IEnumerable<Module> Pending => Modules.Where(m => m.Name != "account" && m.Eraser is null);
}
