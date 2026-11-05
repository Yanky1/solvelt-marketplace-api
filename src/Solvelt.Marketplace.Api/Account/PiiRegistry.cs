using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>
/// Which module erases which buyer-linked tables when an account is deleted (review of the account slice, A3). Every entity with an <c>AccountId</c> must appear here, so a
/// new table that holds a buyer's data cannot be added without a decision about its erasure (the test scans the EF model). A module either has an <see cref="IAccountEraser"/>
/// registered, or is listed as pending, and the pending list is asserted exactly: the day a module registers its eraser, the test makes its owner move it out of the list.
/// Pending modules are a LAUNCH GATE: until they are empty the deletion of an account leaves that module's personal data behind.
/// </summary>
public static class PiiRegistry
{
    public sealed record Module(string Name, string Owner, Type[] Entities, Type? Eraser);

    public static readonly IReadOnlyList<Module> Modules =
    [
        new("account", "M1", [typeof(BuyerAddress), typeof(BuyerSession), typeof(EmailToken)], null),   // erased by AccountService.RedactAsync itself
        new("messaging", "M5", [typeof(Notification), typeof(NotificationPreference), typeof(MessageThread)], typeof(Messaging.MessagingAccountEraser)),
        new("orders-and-carts", "M3", [typeof(MarketplaceOrder), typeof(Cart)], typeof(Checkout.OrdersAccountEraser)),   // decision M3-35
        new("order-cancellations-and-returns", "M5", [typeof(OrderCancelRequest), typeof(OrderReturn)], null),
        new("bookings", "M4", [typeof(Booking), typeof(BuyerQuote)], typeof(Services.BookingsAccountEraser)),
    ];

    /// <summary>Modules that hold buyer data and have no eraser yet (launch gate). Account itself is not pending: its own job erases it.</summary>
    public static IEnumerable<Module> Pending => Modules.Where(m => m.Name != "account" && m.Eraser is null);
}
