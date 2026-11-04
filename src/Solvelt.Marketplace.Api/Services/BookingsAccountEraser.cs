using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// M4's part of deleting an account (M1's <see cref="IAccountEraser"/>, decision M4-18): the buyer's bookings and quote requests lose their personal data AT ONCE, whatever their state
/// (the same fields the retention job wipes: name, address, coordinates, phone, intake answers, meeting link, notes and preferred times). The number, the money fields and the status stay.
/// Safe to run again: a redacted row is skipped.
/// </summary>
public sealed class BookingsAccountEraser(MarketplaceDb db, TimeProvider time) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Quotes.Where(q => q.AccountId == accountId && q.RedactedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Details, "").SetProperty(q => q.PreferredTimesJson, (string?)null).SetProperty(q => q.Note, (string?)null).SetProperty(q => q.DeclineReason, (string?)null)
                .SetProperty(q => q.AddrName, (string?)null).SetProperty(q => q.AddrLine1, (string?)null).SetProperty(q => q.AddrLine2, (string?)null).SetProperty(q => q.AddrCity, (string?)null).SetProperty(q => q.AddrRegion, (string?)null)
                .SetProperty(q => q.AddrPostalCode, (string?)null).SetProperty(q => q.AddrCountry, (string?)null).SetProperty(q => q.AddrLat, (double?)null).SetProperty(q => q.AddrLng, (double?)null).SetProperty(q => q.Phone, (string?)null)
                .SetProperty(q => q.RedactedAt, (DateTimeOffset?)now), ct);
        await db.Bookings.Where(b => b.AccountId == accountId && b.RedactedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.AddrName, (string?)null).SetProperty(b => b.AddrLine1, (string?)null).SetProperty(b => b.AddrLine2, (string?)null)
                .SetProperty(b => b.AddrCity, (string?)null).SetProperty(b => b.AddrRegion, (string?)null).SetProperty(b => b.AddrPostalCode, (string?)null)
                .SetProperty(b => b.AddrCountry, (string?)null).SetProperty(b => b.AddrLat, (double?)null).SetProperty(b => b.AddrLng, (double?)null)
                .SetProperty(b => b.Phone, (string?)null).SetProperty(b => b.IntakeJson, (string?)null).SetProperty(b => b.OnlineInstructions, (string?)null)
                .SetProperty(b => b.RedactedAt, (DateTimeOffset?)now), ct);
    }
}
