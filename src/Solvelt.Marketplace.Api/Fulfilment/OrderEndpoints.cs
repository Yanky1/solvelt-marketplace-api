using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Fulfilment;

public sealed record CancelBody(string? SellerOrderNumber, string? Reason, string? Note);
public sealed record ReturnBody(string? SellerOrderNumber, string? Reason, string? Comment, List<ReturnLineBody>? Lines);
public sealed record ReturnLineBody(string? LineId, int Quantity);
/// <summary>Deliberately has no time field: a client <c>deliveredAt</c> is never deserialised, let alone used.</summary>
public sealed record ConfirmBody(string? SellerOrderNumber);

/// <summary>The buyer's account area: their orders as seen through the portal's events, and what they can do with them (M5). Every route is the signed-in buyer's own; anything else is a 404.</summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapBuyerOrders(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/orders").RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("", List);
        g.MapGet("/{number}", Detail);
        g.MapPost("/{number}/cancel-requests", Cancel);
        g.MapPost("/{number}/return-requests", Return);
        g.MapPost("/{number}/delivery-confirmations", Confirm);
        g.MapPost("/{number}/returns/{returnId}/label-link", Label);
        return app;
    }

    /// <summary>A position older than this is not drawn (the driver may have stopped sharing); it is still deleted only at 24 h or earlier.</summary>
    public const int PositionFreshMinutes = 30;

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    internal static IResult Respond(ActionResult r, HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        if (r.Errors is not null) return r.Code is null ? Problems.Validation(r.Errors) : Results.Json(new ValidationBody(r.Code, r.Message ?? "", r.Errors), statusCode: r.Status);
        if (r.Body is not null) return Results.Json(r.Body, statusCode: r.Status);
        return Problems.Json(r.Status, r.Code ?? "error", r.Message ?? "");
    }

    private static async Task<IResult> List(HttpContext ctx, MarketplaceDb db, int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var q = db.Orders.AsNoTracking().Where(o => o.AccountId == buyer.AccountId && (o.Status == OrderStatus.Placed || o.Status == OrderStatus.Paid));
        // keyset cursor (time, id): orders created in the same instant are neither skipped nor repeated
        if (before is { } b) q = beforeId is { } bid ? q.Where(o => o.CreatedAt < b || (o.CreatedAt == b && o.Id.CompareTo(bid) < 0)) : q.Where(o => o.CreatedAt < b);
        var orders = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take + 1).Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().ToListAsync(ct);
        var page = orders.Take(take).ToList();
        var groupIds = page.SelectMany(o => o.Groups.Select(g => g.Id)).ToList();
        var states = await db.SellerOrderStates.AsNoTracking().Where(s => groupIds.Contains(s.GroupId)).ToDictionaryAsync(s => s.GroupId, s => s.Status, ct);
        return Private(Results.Json(new
        {
            orders = page.Select(o => new
            {
                number = o.Number, createdAt = o.CreatedAt, currency = o.Currency, totalCents = o.TotalCents, refundedCents = o.RefundedCents,
                groups = o.Groups.OrderBy(g => g.SellerName).Select(g => new
                {
                    sellerOrderNumber = g.SellerOrderNumber, sellerName = g.SellerName, brandName = g.BrandName,
                    status = g.SellerOrderId is null ? "processing" : states.GetValueOrDefault(g.Id, FulfilmentStatus.New),
                    itemCount = o.Lines.Where(l => l.GroupId == g.Id).Sum(l => l.Quantity), firstItem = o.Lines.Where(l => l.GroupId == g.Id).Select(l => l.Title).FirstOrDefault(),
                }),
            }),
            next = orders.Count > take ? new { before = page[^1].CreatedAt, beforeId = page[^1].Id } : null,
        }), ctx);
    }

    private static async Task<IResult> Detail(string number, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var o = await db.Orders.AsNoTracking().Include(x => x.Groups).Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(x => x.Number == number && x.AccountId == buyer.AccountId, ct);
        if (o is null || o.Status is not (OrderStatus.Placed or OrderStatus.Paid)) return Private(Problems.NotFound(), ctx);
        var gids = o.Groups.Select(g => g.Id).ToList();
        var lineIds = o.Lines.Select(l => l.Id).ToList();
        var states = await db.SellerOrderStates.AsNoTracking().Where(s => gids.Contains(s.GroupId)).ToDictionaryAsync(s => s.GroupId, ct);
        var ship = await db.OrderShipments.AsNoTracking().Where(s => gids.Contains(s.GroupId)).ToListAsync(ct);
        var shipIds = ship.Select(s => s.Id).ToList();
        var shipLines = await db.OrderShipmentLines.AsNoTracking().Where(l => shipIds.Contains(l.ShipmentId)).ToListAsync(ct);
        var fulfil = await db.OrderLineFulfilments.AsNoTracking().Where(f => lineIds.Contains(f.LineId)).ToDictionaryAsync(f => f.LineId, ct);
        var cancels = await db.OrderCancelRequests.AsNoTracking().Where(r => r.OrderId == o.Id).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var returns = await db.OrderReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.OrderId == o.Id).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var refunds = await db.OrderRefunds.AsNoTracking().Where(r => r.OrderId == o.Id && r.Status != RefundStatus.Rejected).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var now = time.GetUtcNow();

        return Private(Results.Json(new
        {
            number = o.Number, status = o.Status, createdAt = o.CreatedAt, paidAt = o.PaidAt, currency = o.Currency,
            itemsCents = o.ItemsCents, shippingCents = o.ShippingCents, taxCents = o.TaxCents, totalCents = o.TotalCents, refundedCents = o.RefundedCents,
            shipTo = new { name = o.ShipName, line1 = o.ShipLine1, line2 = o.ShipLine2, city = o.ShipCity, region = o.ShipRegion, postalCode = o.ShipPostalCode, country = o.ShipCountry },
            giftMessage = o.GiftMessage,
            groups = o.Groups.OrderBy(g => g.SellerName).Select(g =>
            {
                states.TryGetValue(g.Id, out var st);
                var status = g.SellerOrderId is null ? "processing" : st?.Status ?? FulfilmentStatus.New;
                var glines = o.Lines.Where(l => l.GroupId == g.Id).ToList();
                var open = cancels.FirstOrDefault(r => r.GroupId == g.Id && r.OpenKey != null);
                var lastCancel = open ?? cancels.FirstOrDefault(r => r.GroupId == g.Id);
                var canReturn = g.SellerOrderId is not null && status is FulfilmentStatus.Shipped or FulfilmentStatus.PartiallyShipped or FulfilmentStatus.Delivered
                    && glines.Any(l => fulfil.TryGetValue(l.Id, out var f) && f.ShippedQty - f.ReturnedQty - f.RefundedQty - f.ReturnPendingQty > 0);
                return new
                {
                    sellerOrderNumber = g.SellerOrderNumber, sellerName = g.SellerName, brandName = g.BrandName, status,
                    deliveryKind = st?.DeliveryMethodChangedTo ?? g.DeliveryKind, shippingSpeed = g.ShippingSpeed, localDate = g.LocalDate,
                    itemsCents = g.ItemsCents, shippingCents = g.ShippingCents, taxCents = g.TaxCents, totalCents = g.TotalCents, refundedCents = g.RefundedCents, oversold = g.Oversold,
                    acceptedAt = st?.AcceptedAt, shippedAt = st?.ShippedAt, deliveredAt = st?.DeliveredAt, deliveredBy = st?.DeliveredBy, cancelledAt = st?.CancelledAt,
                    deliveryAttemptsFailed = st?.DeliveryAttemptsFailed ?? 0, lastAttemptFailedReason = st?.LastAttemptFailedReason, buyerConfirmedAt = st?.BuyerConfirmedAt,
                    canCancel = g.SellerOrderId is not null && open is null && status is FulfilmentStatus.New or FulfilmentStatus.Accepted or FulfilmentStatus.OnHold or FulfilmentStatus.PartiallyShipped,
                    canConfirmDelivery = status == FulfilmentStatus.Shipped && st?.BuyerConfirmedAt is null,
                    canReturn,
                    cancelRequest = lastCancel is null ? null : new { status = lastCancel.Status, reason = lastCancel.Reason, decisionNote = lastCancel.DecisionNote, createdAt = lastCancel.CreatedAt },
                    lines = glines.Select(l =>
                    {
                        fulfil.TryGetValue(l.Id, out var f);
                        return new
                        {
                            lineId = l.MarketplaceLineId, title = l.Title, variantName = l.VariantName, quantity = l.Quantity, unitPriceCents = l.UnitPriceCents, lineTotalCents = l.LineTotalCents, taxCents = l.TaxCents,
                            shippedQty = f?.ShippedQty ?? 0, cancelledQty = f?.CancelledQty ?? 0, returnedQty = f?.ReturnedQty ?? 0, refundedQty = f?.RefundedQty ?? 0,
                            returnableQty = f is null ? 0 : Math.Max(0, f.ShippedQty - f.ReturnedQty - f.RefundedQty - f.ReturnPendingQty),
                        };
                    }),
                    shipments = ship.Where(s => s.GroupId == g.Id && s.ShippedAt != null).OrderBy(s => s.ShippedAt)   // a placeholder made by an early tracking event shows once its shipped event arrives
                        .Select(s => new
                    {
                        id = s.Id, method = s.Method, carrier = s.Carrier, service = s.Service, trackingNumber = s.TrackingNumber, trackingUrl = s.TrackingUrl, shippedAt = s.ShippedAt, deliveredAt = s.DeliveredAt,
                        voided = s.VoidedAt != null, trackingStatus = s.TrackingStatus, trackingStatusDetail = s.TrackingStatusDetail, eta = s.Eta,
                        etaWindowStart = s.EtaWindowStart, etaWindowEnd = s.EtaWindowEnd, driverFirstName = s.DriverFirstName, placement = s.Placement,
                        timeline = s.TrackingEventsJson is null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(s.TrackingEventsJson),
                        // live position: only while the stop is next, at most 24 h old (the job clears it too), and only a recent one is drawn
                        position = s.TrackingStatus == "out_for_delivery" && s.DeliveredAt == null && s.PositionLat != null && s.PositionAt > now.AddMinutes(-PositionFreshMinutes) ? new { lat = s.PositionLat, lng = s.PositionLng, at = s.PositionAt } : null,
                        proofCount = s.ProofExpiresAt > now && s.ProofJson != null ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.ProofJson)!.Count : 0,
                        lines = shipLines.Where(l => l.ShipmentId == s.Id).Select(l => new { lineId = l.MarketplaceLineId, quantity = l.Quantity }),
                    }),
                    returns = returns.Where(r => r.GroupId == g.Id).Select(r => new
                    {
                        returnId = r.ReturnId, status = r.Status, reason = r.Reason, decisionNote = r.DecisionNote, createdAt = r.CreatedAt,
                        lines = r.Lines.Select(l => new { lineId = l.MarketplaceLineId, quantity = l.Quantity }),
                        // a link is shown only while it is alive; an approved return can ask for a fresh one
                        label = r.LabelUrl is not null && r.LabelUrlExpiresAt > now ? new { url = r.LabelUrl, expiresAt = r.LabelUrlExpiresAt, carrier = r.LabelCarrier, trackingNumber = r.LabelTrackingNumber, trackingUrl = r.LabelTrackingUrl } : null,
                        canRefreshLabel = r.Status == ReturnStatus.Approved,
                    }),
                    refunds = refunds.Where(r => r.GroupId == g.Id).Select(r => new { amountCents = r.AmountCents, shippingCents = r.ShippingCents, status = r.Status, reason = r.Reason, createdAt = r.CreatedAt }),
                };
            }),
        }), ctx);
    }

    private static async Task<IResult> Cancel(string number, CancelBody? body, HttpContext ctx, BuyerOrderActions a, CancellationToken ct)
        => Respond(await a.CancelAsync(ctx.Buyer(), number, body?.SellerOrderNumber, body?.Reason, body?.Note, ct), ctx);

    private static async Task<IResult> Return(string number, ReturnBody? body, HttpContext ctx, BuyerOrderActions a, CancellationToken ct)
        => Respond(await a.RequestReturnAsync(ctx.Buyer(), number, body?.SellerOrderNumber, body?.Reason, body?.Comment,
            body?.Lines?.Select(l => new ReturnLineInput(l.LineId, l.Quantity)).ToList(), ctx.Request.Headers["Idempotency-Key"].FirstOrDefault(), ct), ctx);

    private static async Task<IResult> Confirm(string number, ConfirmBody? body, HttpContext ctx, BuyerOrderActions a, CancellationToken ct)
        => Respond(await a.ConfirmDeliveryAsync(ctx.Buyer(), number, body?.SellerOrderNumber, ct), ctx);

    private static async Task<IResult> Label(string number, string returnId, HttpContext ctx, BuyerOrderActions a, CancellationToken ct)
        => Respond(await a.RefreshLabelAsync(ctx.Buyer(), number, returnId, ct), ctx);
}
