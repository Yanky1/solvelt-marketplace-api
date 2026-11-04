using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// Retention for what the account area stores (PII table, M5-05): return label links go the moment they expire; ninety days after EVERY seller order of an order is closed
/// (delivered, cancelled or refunded) the delivery address, phone, gift message, buyer name, relay address and the buyer's free-text notes are replaced, like the portal does
/// for its own copy (ORDERS.md §7). Money, lines and ids stay (accounting).
/// </summary>
public sealed class FulfilmentMaintenance(MarketplaceDb db, TimeProvider time)
{
    public const int RedactAfterDays = 90;
    public const string Redacted = "Redacted";
    public sealed record Result(int LabelsCleared, int OrdersRedacted);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var labels = await db.OrderReturns.Where(r => r.LabelUrl != null && r.LabelUrlExpiresAt != null && r.LabelUrlExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LabelUrl, (string?)null).SetProperty(r => r.LabelTrackingUrl, (string?)null), ct);

        var cutoff = now.AddDays(-RedactAfterDays);
        var candidates = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Placed && o.ShipName != Redacted).Select(o => o.Id).Take(500).ToListAsync(ct);
        var redacted = 0;
        foreach (var id in candidates)
        {
            var states = await db.SellerOrderStates.AsNoTracking().Where(s => s.OrderId == id).ToListAsync(ct);
            var groups = await db.OrderGroups.AsNoTracking().CountAsync(g => g.OrderId == id, ct);
            var closed = states.Count == groups && states.All(s => s.Status is FulfilmentStatus.Delivered or FulfilmentStatus.Cancelled or FulfilmentStatus.Refunded);
            if (!closed || states.Max(s => s.UpdatedAt) > cutoff) continue;
            await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.ShipName, Redacted).SetProperty(o => o.ShipLine1, Redacted).SetProperty(o => o.ShipLine2, (string?)null).SetProperty(o => o.ShipCity, Redacted)
                .SetProperty(o => o.ShipRegion, (string?)null).SetProperty(o => o.ShipPostalCode, "").SetProperty(o => o.ShipLat, (double?)null).SetProperty(o => o.ShipLng, (double?)null)
                .SetProperty(o => o.Phone, (string?)null).SetProperty(o => o.GiftMessage, (string?)null).SetProperty(o => o.BuyerName, Redacted).SetProperty(o => o.RelayEmail, Redacted), ct);
            await db.OrderCancelRequests.Where(r => r.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null).SetProperty(r => r.DecisionNote, (string?)null), ct);
            await db.OrderReturns.Where(r => r.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Comment, (string?)null).SetProperty(r => r.DecisionNote, (string?)null)
                .SetProperty(r => r.LabelUrl, (string?)null).SetProperty(r => r.LabelTrackingUrl, (string?)null), ct);
            redacted++;
        }
        return new Result(labels, redacted);
    }
}
