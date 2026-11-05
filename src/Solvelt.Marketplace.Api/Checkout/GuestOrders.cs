using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>Where mail about an order goes: its account's address, or for a guest order the address typed at checkout.</summary>
public static class OrderMail
{
    public static async Task<string?> AddressAsync(MarketplaceDb db, MarketplaceOrder order, CancellationToken ct)
    {
        if (order.AccountId is { } id)
            return await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == id && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);   // an account deleted meanwhile has no address to write to
        return order.GuestEmail;
    }
}

/// <summary>
/// Attaches guest orders to an account once its address is PROVEN (M1's <see cref="IGuestOrderClaimer"/>; decision M3-24): ONE conditional update, idempotent (a claimed order has an account, so a repeat
/// matches nothing), matching only orders still without an account and whose checkout address equals the proven one. Claiming freezes the guest link: it then shows only number, status and date.
/// </summary>
public sealed class GuestOrderClaimer(MarketplaceDb db, TimeProvider time) : IGuestOrderClaimer
{
    public Task<int> ClaimAsync(Guid accountId, string normalisedEmail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(normalisedEmail)) return Task.FromResult(0);
        var now = time.GetUtcNow().ToUniversalTime();
        // An account whose address was only "demo verified" (no mailbox proof) never receives a guest order, on any path (sign-in, passkey, reset, link): the guard is part of the one UPDATE.
        return db.Orders.Where(o => o.AccountId == null && o.GuestEmail == normalisedEmail && !db.BuyerAccounts.Any(a => a.Id == accountId && a.DemoVerifiedAt != null))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.AccountId, (Guid?)accountId).SetProperty(o => o.ClaimedAt, (DateTimeOffset?)now).SetProperty(o => o.UpdatedAt, now), ct);
    }
}
