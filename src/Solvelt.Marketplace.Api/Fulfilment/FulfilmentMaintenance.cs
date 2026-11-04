using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// Retention for what the account area stores (PII table, M5-05): return label links go the moment they expire; ninety days after EVERY seller order of an order is closed
/// (delivered, cancelled or refunded) the delivery address, phone, gift message, buyer name, relay address and the buyer's free-text notes are replaced, like the portal does
/// for its own copy (ORDERS.md §7). Money, lines and ids stay (accounting).
/// </summary>
public sealed class FulfilmentMaintenance(MarketplaceDb db, TimeProvider time, Microsoft.Extensions.Options.IOptions<Infrastructure.NotificationOptions> notifications)
{
    public const int RedactAfterDays = 90;
    public const string Redacted = "Redacted";
    public const int PositionRetentionHours = 24;
    public sealed record Result(int LabelsCleared, int OrdersRedacted, int PositionsCleared = 0, int ProofsCleared = 0, int NotificationsDeleted = 0, int ThreadsRedacted = 0);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var labels = await db.OrderReturns.Where(r => r.LabelUrl != null && r.LabelUrlExpiresAt != null && r.LabelUrlExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LabelUrl, (string?)null).SetProperty(r => r.LabelTrackingUrl, (string?)null), ct);

        // live positions: at most 24 h; proof links: at their expiry (the images themselves are never stored)
        var posCut = now.AddHours(-PositionRetentionHours);
        var positions = await db.OrderShipments.Where(x => x.PositionAt != null && x.PositionAt <= posCut)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PositionLat, (double?)null).SetProperty(x => x.PositionLng, (double?)null).SetProperty(x => x.PositionAt, (DateTimeOffset?)null), ct);
        var proofs = await db.OrderShipments.Where(x => x.ProofJson != null && x.ProofExpiresAt != null && x.ProofExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProofJson, (string?)null), ct);

        var notifCut = now.AddDays(-Math.Max(1, notifications.Value.RetentionDays));
        var deleted = await db.Notifications.Where(n => n.CreatedAt < notifCut).ExecuteDeleteAsync(ct);

        var cutoff = now.AddDays(-RedactAfterDays);
        // CLOSED orders are chosen in SQL: every seller order is delivered/cancelled/refunded since before the cut-off, and no return is still open (its address is still needed). Oldest first,
        // in batches, so a backlog drains instead of stalling behind open orders.
        string[] closedStates = [FulfilmentStatus.Delivered, FulfilmentStatus.Cancelled, FulfilmentStatus.Refunded];
        string[] openReturns = [ReturnStatus.Pending, ReturnStatus.Requested, ReturnStatus.Approved];
        var redacted = 0;
        List<Guid> batch;
        do
        {
            batch = await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Placed && o.ShipName != Redacted
                    && db.OrderGroups.Any(g => g.OrderId == o.Id)
                    && db.OrderGroups.Where(g => g.OrderId == o.Id).All(g => db.SellerOrderStates.Any(s => s.GroupId == g.Id && closedStates.Contains(s.Status) && s.UpdatedAt <= cutoff))
                    && !db.OrderReturns.Any(r => r.OrderId == o.Id && openReturns.Contains(r.Status)))
                .OrderBy(o => o.CreatedAt).Select(o => o.Id).Take(200).ToListAsync(ct);
            foreach (var id in batch)
            {
                await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.ShipName, Redacted).SetProperty(o => o.ShipLine1, Redacted).SetProperty(o => o.ShipLine2, (string?)null).SetProperty(o => o.ShipCity, Redacted)
                    .SetProperty(o => o.ShipRegion, (string?)null).SetProperty(o => o.ShipPostalCode, "").SetProperty(o => o.ShipLat, (double?)null).SetProperty(o => o.ShipLng, (double?)null)
                    .SetProperty(o => o.Phone, (string?)null).SetProperty(o => o.GiftMessage, (string?)null).SetProperty(o => o.BuyerName, Redacted).SetProperty(o => o.RelayEmail, Redacted).SetProperty(o => o.GuestEmail, (string?)null), ct);
                await db.OrderCancelRequests.Where(r => r.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null).SetProperty(r => r.DecisionNote, (string?)null), ct);
                await db.OrderReturns.Where(r => r.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Comment, (string?)null).SetProperty(r => r.DecisionNote, (string?)null)
                    .SetProperty(r => r.LabelUrl, (string?)null).SetProperty(r => r.LabelTrackingUrl, (string?)null), ct);
                redacted++;
            }
        } while (batch.Count == 200 && !ct.IsCancellationRequested);
        // Messages (PII table, M5-13): an order thread 90 days after its seller order closed, a product question 90 days after its last message (the portal's own policy). Texts are emptied,
        // the files and their names deleted, the thread closed. Oldest first, in batches.
        var threads = 0;
        List<Guid> due;
        do
        {
            due = await db.MessageThreads.Where(t => t.RedactedAt == null
                    && ((t.Kind == ThreadKinds.Order && db.SellerOrderStates.Any(s => s.GroupId == t.GroupId && closedStates.Contains(s.Status) && s.UpdatedAt <= cutoff))
                        || (t.Kind == ThreadKinds.Product && t.LastMessageAt <= cutoff)))
                .OrderBy(t => t.LastMessageAt).Select(t => t.Id).Take(200).ToListAsync(ct);
            foreach (var tid in due)
            {
                var msgIds = db.ThreadMessages.Where(m => m.ThreadId == tid).Select(m => m.Id);
                await db.MessageAttachmentData.Where(d => db.MessageAttachments.Any(a => a.Id == d.AttachmentId && msgIds.Contains(a.MessageId))).ExecuteDeleteAsync(ct);
                await db.MessageAttachments.Where(a => msgIds.Contains(a.MessageId)).ExecuteDeleteAsync(ct);
                await db.ThreadMessages.Where(m => m.ThreadId == tid).ExecuteUpdateAsync(s => s.SetProperty(m => m.Text, "").SetProperty(m => m.RedactedAt, (DateTimeOffset?)now).SetProperty(m => m.ModerationNote, (string?)null), ct);
                await db.MessageThreads.Where(t => t.Id == tid).ExecuteUpdateAsync(s => s.SetProperty(t => t.RedactedAt, (DateTimeOffset?)now), ct);
                threads++;
            }
        } while (due.Count == 200 && !ct.IsCancellationRequested);

        return new Result(labels, redacted, positions, proofs, deleted, threads);
    }
}
