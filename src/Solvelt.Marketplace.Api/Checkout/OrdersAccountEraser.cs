using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// M3's part of deleting an account (M1's <see cref="IAccountEraser"/>, decision M3-35; orchestrator review). The buyer's orders keep their number, status, totals, tax and lines for accounting,
/// but lose everything that identifies a person: name, phone, the delivery address with coordinates, the gift message and the relay address (replaced by the tombstone
/// <c>deleted-&lt;id&gt;@relay.invalid</c>, which can never receive mail). The buyer's carts are deleted. Guest orders (no account) whose guest address equals the deleted account's address lose that
/// address. It runs before the account's own address is erased, so the guest match still has the address to compare. Idempotent: a second run changes nothing.
/// </summary>
public sealed class OrdersAccountEraser(MarketplaceDb db) : IAccountEraser, IAccountDeletionHook
{
    public const string Redacted = "Redacted";
    public static string Tombstone(Guid accountId) => Auth.RelayIdentity.Tombstone(accountId);   // M1's, so both agree

    /// <summary>Runs inside the delete transaction, while the address is still known (M1 frees it at once): a guest order placed with it loses it. Guest orders are matched by the lower-cased address they stored.</summary>
    public async Task OnDeletingAsync(Guid accountId, string email, CancellationToken ct)
    {
        var lower = email.Trim().ToLowerInvariant();
        await db.Orders.Where(o => o.AccountId == null && o.GuestEmail == lower).ExecuteUpdateAsync(s => s.SetProperty(o => o.GuestEmail, (string?)null), ct);
    }

    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var tombstone = Tombstone(accountId);
        // Only CLOSED orders are redacted now (review M1). An open order (not yet paid, paid but not yet sent to the portal, or with a seller order still to be delivered) still needs its real address, phone
        // and name: the portal ingest sends them to the seller, and the seller must be able to ship; the relay address must keep working for seller messages and refund mail. Those orders are redacted by the
        // retention job 90 days after the last seller order closes (M5-05), which also reaches orders of a deleted account. The decision is M3-40.
        string[] closedStates = [FulfilmentStatus.Delivered, FulfilmentStatus.Cancelled, FulfilmentStatus.Refunded];
        string[] openReturns = [ReturnStatus.Pending, ReturnStatus.Requested, ReturnStatus.Approved];
        var closed = db.Orders.Where(o => o.AccountId == accountId
            && (o.Status == OrderStatus.Failed || o.Status == OrderStatus.Expired || o.Status == OrderStatus.Superseded
                || (o.Status == OrderStatus.Placed
                    && db.OrderGroups.Any(g => g.OrderId == o.Id)
                    && db.OrderGroups.Where(g => g.OrderId == o.Id).All(g => db.SellerOrderStates.Any(st => st.GroupId == g.Id && closedStates.Contains(st.Status)))
                    && !db.OrderReturns.Any(r => r.OrderId == o.Id && openReturns.Contains(r.Status)))));
        await closed.ExecuteUpdateAsync(s => s
            .SetProperty(o => o.BuyerName, Redacted).SetProperty(o => o.Phone, (string?)null).SetProperty(o => o.GiftMessage, (string?)null)
            .SetProperty(o => o.ShipName, Redacted).SetProperty(o => o.ShipLine1, Redacted).SetProperty(o => o.ShipLine2, (string?)null).SetProperty(o => o.ShipCity, Redacted)
            .SetProperty(o => o.ShipRegion, (string?)null).SetProperty(o => o.ShipPostalCode, "").SetProperty(o => o.ShipLat, (double?)null).SetProperty(o => o.ShipLng, (double?)null)
            .SetProperty(o => o.RelayEmail, tombstone).SetProperty(o => o.GuestEmail, (string?)null), ct);
        // A guest address on any order of the account (a claimed guest order) is dropped at once, open or closed: nothing sends to it after the account is gone.
        await db.Orders.Where(o => o.AccountId == accountId && o.GuestEmail != null).ExecuteUpdateAsync(s => s.SetProperty(o => o.GuestEmail, (string?)null), ct);
        var cartIds = db.Carts.Where(c => c.AccountId == accountId).Select(c => c.Id);
        await db.CartLines.Where(l => cartIds.Contains(l.CartId)).ExecuteDeleteAsync(ct);
        await db.Carts.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}
