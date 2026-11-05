using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// A guest's read-only view of where their order is (decision M5-30; shapes agreed with M3, M3-24 / M3-32). The credential is the order-access link of M1's <see cref="GuestLinks"/> in the
/// <c>X-Guest-Order</c> header: it names ONE order and is mailed to the checkout address only. BEFORE a claim (no account, <c>ClaimedAt</c> empty) the guest sees the progress of the order,
/// never an address, a phone, proof of delivery, a driver position or any action; AFTER a claim the link shows only <c>{ number, status, createdAt, claimed: true }</c> (nothing of the
/// owner's can be read through it). A link for another order or a bad link is a 401 / 404.
/// </summary>
public static class GuestOrderEndpoints
{
    public static IEndpointRouteBuilder MapGuestOrders(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/guest-orders/{number}", Get).RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    private static async Task<IResult> Get(string number, HttpContext ctx, MarketplaceDb db, GuestLinks links, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        if (links.Parse(ctx.Request.Headers[CheckoutEndpoints.GuestOrderHeader].ToString()) is not { } orderId) return Problems.Json(401, "unauthorized", "Open the link from your order email to continue.");
        var o = await db.Orders.AsNoTracking().Include(x => x.Groups).Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == orderId && x.Number == number, ct);
        if (o is null || o.Status is not (OrderStatus.Placed or OrderStatus.Paid)) return Problems.NotFound();
        if (o.ClaimedAt is not null || o.AccountId is not null) return Results.Json(new { number = o.Number, status = o.Status, createdAt = o.CreatedAt, claimed = true });
        var gids = o.Groups.Select(g => g.Id).ToList();
        var lineIds = o.Lines.Select(l => l.Id).ToList();
        var states = await db.SellerOrderStates.AsNoTracking().Where(s => gids.Contains(s.GroupId)).ToDictionaryAsync(s => s.GroupId, ct);
        var ship = await db.OrderShipments.AsNoTracking().Where(s => gids.Contains(s.GroupId) && s.ShippedAt != null && s.VoidedAt == null).OrderBy(s => s.ShippedAt).ToListAsync(ct);
        var fulfil = await db.OrderLineFulfilments.AsNoTracking().Where(f => lineIds.Contains(f.LineId)).ToDictionaryAsync(f => f.LineId, ct);
        return Results.Json(new
        {
            number = o.Number, status = o.Status, createdAt = o.CreatedAt, paidAt = o.PaidAt, currency = o.Currency, totalCents = o.TotalCents, refundedCents = o.RefundedCents, claimed = false,
            groups = o.Groups.OrderBy(g => g.SellerName).Select(g =>
            {
                states.TryGetValue(g.Id, out var st);
                return new
                {
                    sellerOrderNumber = g.SellerOrderNumber, sellerName = g.SellerName, brandName = g.BrandName, deliveryKind = st?.DeliveryMethodChangedTo ?? g.DeliveryKind,
                    status = g.SellerOrderId is null ? "processing" : st?.Status ?? FulfilmentStatus.New,
                    acceptedAt = st?.AcceptedAt, shippedAt = st?.ShippedAt, deliveredAt = st?.DeliveredAt, cancelledAt = st?.CancelledAt,
                    lines = o.Lines.Where(l => l.GroupId == g.Id).Select(l => new { title = l.Title, variantName = l.VariantName, quantity = l.Quantity, shippedQty = fulfil.TryGetValue(l.Id, out var f) ? f.ShippedQty : 0 }),
                    shipments = ship.Where(s => s.GroupId == g.Id).Select(s => new
                    {
                        method = s.Method, carrier = s.Carrier, trackingNumber = s.TrackingNumber, trackingUrl = s.TrackingUrl, shippedAt = s.ShippedAt, deliveredAt = s.DeliveredAt,
                        trackingStatus = s.TrackingStatus, trackingStatusDetail = s.TrackingStatusDetail, eta = s.Eta,
                    }),
                };
            }),
        });
    }
}
