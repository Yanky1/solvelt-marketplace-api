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
        await db.Orders.Where(o => o.AccountId == accountId).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.BuyerName, Redacted).SetProperty(o => o.Phone, (string?)null).SetProperty(o => o.GiftMessage, (string?)null)
            .SetProperty(o => o.ShipName, Redacted).SetProperty(o => o.ShipLine1, Redacted).SetProperty(o => o.ShipLine2, (string?)null).SetProperty(o => o.ShipCity, Redacted)
            .SetProperty(o => o.ShipRegion, (string?)null).SetProperty(o => o.ShipPostalCode, "").SetProperty(o => o.ShipLat, (double?)null).SetProperty(o => o.ShipLng, (double?)null)
            .SetProperty(o => o.RelayEmail, tombstone).SetProperty(o => o.GuestEmail, (string?)null), ct);
        var cartIds = db.Carts.Where(c => c.AccountId == accountId).Select(c => c.Id);
        await db.CartLines.Where(l => cartIds.Contains(l.CartId)).ExecuteDeleteAsync(ct);
        await db.Carts.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}
