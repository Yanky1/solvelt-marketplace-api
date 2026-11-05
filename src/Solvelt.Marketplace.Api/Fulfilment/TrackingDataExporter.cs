using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// M5's "tracking" section of "download my data" (review MED-3): for each of the buyer's seller orders the fulfilment state (accepted, shipped, delivered, cancelled, failed attempts) and the
/// parcels with carrier, tracking number and the carrier's timeline. NOT in it: the driver's position (a live value kept 24 h at most, cleared on delivery), the driver's name, the portal's ids,
/// and the proof-of-delivery links (short-lived signed portal URLs that grant access to images). Other buyers' orders are never read: the query goes through the account's own orders.
/// </summary>
public sealed class TrackingDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "tracking";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var groups = await (from g in db.OrderGroups.AsNoTracking() join o in db.Orders.AsNoTracking() on g.OrderId equals o.Id where o.AccountId == accountId
                            orderby o.CreatedAt, o.Number, g.SellerOrderNumber select new { g.Id, o.Number, g.SellerOrderNumber, g.SellerName, demo = o.IsDemo }).ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var states = (await db.SellerOrderStates.AsNoTracking().Where(s => ids.Contains(s.GroupId)).ToListAsync(ct)).ToDictionary(s => s.GroupId);
        var shipments = (await db.OrderShipments.AsNoTracking().Where(s => ids.Contains(s.GroupId)).OrderBy(s => s.ShippedAt).ThenBy(s => s.Id).ToListAsync(ct)).ToLookup(s => s.GroupId);
        return new
        {
            sellerOrders = groups.Select(g =>
            {
                states.TryGetValue(g.Id, out var s);
                return new
                {
                    order = g.Number, sellerOrder = g.SellerOrderNumber, seller = g.SellerName, demo = g.demo,
                    state = s is null ? null : new
                    {
                        status = s.Status, s.AcceptedAt, s.ShippedAt, s.DeliveredAt, s.DeliveredBy, s.CancelledAt, s.CancelledBy, cancelReason = s.CancelReason, s.DeliveryMethodChangedTo,
                        failedDeliveryAttempts = s.DeliveryAttemptsFailed, lastFailedAttemptAt = s.LastAttemptFailedAt, lastFailedAttemptReason = s.LastAttemptFailedReason, confirmedReceivedAt = s.BuyerConfirmedAt,
                    },
                    shipments = shipments[g.Id].Select(p => new
                    {
                        p.Method, p.Carrier, p.Service, p.TrackingNumber, p.TrackingUrl, p.ShippedAt, p.DeliveredAt, voided = p.VoidedAt != null, status = p.TrackingStatus, statusDetail = p.TrackingStatusDetail,
                        estimatedDelivery = p.Eta, deliveryWindowStart = p.EtaWindowStart, deliveryWindowEnd = p.EtaWindowEnd, placement = p.Placement, timeline = Timeline(p.TrackingEventsJson),
                    }),
                };
            }),
        };
    }

    private static object? Timeline(string? json) { try { return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json); } catch (JsonException) { return null; } }
}
