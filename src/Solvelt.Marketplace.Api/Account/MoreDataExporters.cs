using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>The buyer's carts and what is in them (review MED-3). Demo rows are marked <c>demo: true</c> (L7), never dropped: the file shows what we hold.</summary>
public sealed class CartsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "carts";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var carts = await db.Carts.AsNoTracking().Where(c => c.AccountId == accountId).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var ids = carts.Select(c => c.Id).ToList();
        var lines = (await db.CartLines.AsNoTracking().Where(l => ids.Contains(l.CartId)).OrderBy(l => l.AddedAtMs).ToListAsync(ct)).ToLookup(l => l.CartId);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => db.CartLines.Any(x => x.ListingId == l.Id && ids.Contains(x.CartId))).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        return new
        {
            carts = carts.Select(c => new
            {
                createdAt = c.CreatedAt, updatedAt = c.UpdatedAt, demo = c.IsDemo,
                lines = lines[c.Id].Select(l => new { item = titles.GetValueOrDefault(l.ListingId), l.Quantity, lastSeenUnitPriceCents = l.SeenUnitPriceCents, addedAt = DateTimeOffset.FromUnixTimeMilliseconds(l.AddedAtMs) }),
            }),
        };
    }
}

/// <summary>Passkeys: the name the buyer gave, when it was added and last used. NOT the credential id, the public key or the counter: they are secrets of the sign-in, not data about the person.</summary>
public sealed class PasskeysDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "passkeys";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
        => new
        {
            passkeys = await db.BuyerPasskeys.AsNoTracking().Where(p => p.AccountId == accountId).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .Select(p => new { p.Name, p.CreatedAt, p.LastUsedAt, syncedAcrossDevices = p.BackedUp }).ToListAsync(ct),
        };
}
