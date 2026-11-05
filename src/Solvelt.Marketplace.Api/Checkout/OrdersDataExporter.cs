using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// M3's part of "download my data" (decision M3-44): the buyer's orders as stored: status, dates, totals, the delivery address and phone, the gift message, the lines, the seller order numbers and
/// the refunds. NOT in it: our idempotency and payment ids (Stripe intent and refund ids, refund keys, the pending-order key), seller and brand ids, the portal's ids, and the relay address (it
/// belongs to the order, not to the buyer). Orders of other buyers are never read: the query is by the account id. Redacted orders export their redacted values, as stored.
/// </summary>
public sealed class OrdersDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "orders";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery()
            .Where(o => o.AccountId == accountId).OrderBy(o => o.CreatedAt).ThenBy(o => o.Number).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var refunds = (await db.OrderRefunds.AsNoTracking().Where(r => ids.Contains(r.OrderId) && r.Status == RefundStatus.Succeeded).OrderBy(r => r.CreatedAt).ThenBy(r => r.Key).ToListAsync(ct)).ToLookup(r => r.OrderId);
        return new
        {
            orders = orders.Select(o => new
            {
                o.Number, demo = o.IsDemo, o.Status, o.Currency, o.CreatedAt, o.PaidAt, o.PlacedAt,
                o.ItemsCents, o.ShippingCents, o.TaxCents, o.TotalCents, o.PaidCents, o.RefundedCents,
                buyerName = o.BuyerName, o.Phone, o.GiftMessage, o.GuestEmail,
                shippingAddress = new { name = o.ShipName, line1 = o.ShipLine1, line2 = o.ShipLine2, city = o.ShipCity, region = o.ShipRegion, postalCode = o.ShipPostalCode, country = o.ShipCountry, latitude = o.ShipLat, longitude = o.ShipLng },
                sellerOrders = o.Groups.OrderBy(g => g.SellerOrderNumber).ThenBy(g => g.SellerName).Select(g => new
                {
                    g.SellerName, g.BrandName, number = g.SellerOrderNumber, delivery = g.DeliveryKind, speed = g.ShippingSpeed, localDate = g.LocalDate,
                    g.ItemsCents, g.ShippingCents, g.TaxCents, g.TotalCents, g.RefundedCents,
                }),
                lines = o.Lines.OrderBy(l => l.MarketplaceLineId).Select(l => new { l.Title, variant = l.VariantName, l.Quantity, l.UnitPriceCents, l.LineTotalCents, l.TaxCents, l.RefundedQuantity }),
                refunds = refunds[o.Id].Select(r => new { r.AmountCents, r.ShippingCents, r.TaxCents, r.Reason, date = r.CreatedAt }),
            }),
        };
    }
}
