using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// Sends a PAID order to the portal (<c>POST /orders</c>, idempotent on <c>marketplaceOrderId</c> = our order number) from the outbox, so a timeout or a portal outage is retried
/// with the same id and yields one order. On success it stores the seller orders and queues one <c>payment.captured</c> per seller order. A <c>422 invalid_order</c> (nothing was
/// created at the portal, for example a delivery window that filled up) fails the order and refunds the buyer in full.
/// </summary>
public sealed class OrderIngestHandler(MarketplaceDb db, ISignedPortalClient portal, OutboxWriter outbox, RefundService refunds, Portal.Webhooks.PortalEventProcessor portalEvents, TimeProvider time) : IOutboxHandler
{
    public const string Type = "order.ingest";
    public sealed record Payload(Guid OrderId);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var order = await db.Orders.Include(o => o.Groups).Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == p.OrderId, ct);
        if (order is null) return OutboxResult.Dead("order_missing");
        if (order.Status != OrderStatus.Paid) return OutboxResult.Delivered();   // already placed, or failed and refunded: nothing left to do

        PortalResult<CreateOrderResponse> result;
        try { result = await portal.CreateOrderAsync(BuildRequest(order), ct); }
        catch (PortalUnavailableException) { return OutboxResult.Retry("portal_unavailable"); }
        catch (PortalNotConfiguredException) { return OutboxResult.Retry("portal_not_configured"); }

        if (result.IsSuccess && result.Value is { } created) return await StoreAsync(order, created, ct);
        if (result.Status == 422 && result.Error?.Code == "invalid_order") return await FailAsync(order, result.Error, ct);
        return PortalOutcome.From(result);
    }

    /// <summary>The ingest body, from stored ids and server amounts only. A line names its product by ids, never by SKU (CONTRACT.md §3).</summary>
    public static CreateOrderRequest BuildRequest(MarketplaceOrder o)
    {
        var lines = new List<OrderLineRequest>();
        foreach (var g in o.Groups.OrderBy(g => g.SellerId).ThenBy(g => g.BrandId))
        {
            var gl = o.Lines.Where(l => l.GroupId == g.Id).OrderBy(l => l.MarketplaceLineId, StringComparer.Ordinal).ToList();
            var shipTax = AllocateShippingTax(g.ShippingTaxCents, gl);
            for (var i = 0; i < gl.Count; i++)
            {
                var l = gl[i];
                lines.Add(new OrderLineRequest(l.MarketplaceLineId, l.SellerId, l.BrandId, l.ListingId, l.VariantId, l.Quantity, l.UnitPriceCents, 0, l.TaxCents + shipTax[i]));
            }
        }
        var carrier = o.Groups.Where(g => g.DeliveryKind == "carrier").ToList();
        // The portal takes one method and speed per order (a gap, see M3-09): the FASTEST chosen speed, so no seller plans for slower delivery than promised.
        var speed = carrier.Select(g => g.ShippingSpeed!).OrderBy(SpeedRank).FirstOrDefault() ?? "standard";
        var method = carrier.Count == 0 ? "local" : char.ToUpperInvariant(speed[0]) + speed[1..];
        var deliveries = o.Groups.Where(g => g.DeliveryKind == "local").Select(g => new LocalDeliveryRequest(g.SellerId, g.BrandId, "local", g.LocalWindowId!.Value, g.LocalDate!)).ToList();
        return new CreateOrderRequest(o.Number, o.PaidAt ?? o.CreatedAt, o.Currency, new PortalBuyer(o.BuyerName, o.RelayEmail, o.Phone),
            new PortalAddress(o.ShipName, o.ShipLine1, o.ShipLine2, o.ShipCity, o.ShipRegion, o.ShipPostalCode, o.ShipCountry, o.ShipLat, o.ShipLng),
            method, speed, o.GiftMessage, lines,
            o.Groups.OrderBy(g => g.SellerId).ThenBy(g => g.BrandId).Select(g => new ShippingChargeRequest(g.SellerId, g.BrandId, g.ShippingCents)).ToList(), deliveries.Count == 0 ? null : deliveries);
    }

    private static int SpeedRank(string s) => s.ToLowerInvariant() switch { "overnight" => 0, "expedited" => 1, "standard" => 2, _ => 3 };

    /// <summary>
    /// The portal's total is items + shipping + the tax on the LINES, with no field for tax on shipping. Tax charged on shipping is therefore added to the group's taxable lines
    /// (in proportion to their tax, the first line takes the remainder), so the portal's total for the seller order equals what the buyer paid for it, exactly.
    /// </summary>
    public static long[] AllocateShippingTax(long shippingTaxCents, IReadOnlyList<OrderLine> lines)
    {
        var result = new long[lines.Count];
        if (shippingTaxCents <= 0 || lines.Count == 0) return result;
        var weights = lines.Select(l => l.TaxCents > 0 ? l.TaxCents : 0L).ToArray();
        if (weights.Sum() == 0) weights = lines.Select(l => l.LineTotalCents).ToArray();
        var total = weights.Sum();
        long given = 0;
        for (var i = 0; i < lines.Count; i++) { result[i] = total == 0 ? 0 : shippingTaxCents * weights[i] / total; given += result[i]; }
        result[0] += shippingTaxCents - given;
        return result;
    }

    private async Task<OutboxResult> StoreAsync(MarketplaceOrder order, CreateOrderResponse created, CancellationToken ct)
    {
        // Match on OUR ids and the portal's together: seller and brand must be equal to the group's, and every group must be answered exactly once.
        var pairs = created.Orders.Select(r => (Ref: r, Group: order.Groups.Where(g => g.SellerId == r.SellerId && g.BrandId == r.BrandId).ToList())).ToList();
        if (pairs.Count != order.Groups.Count || pairs.Any(x => x.Group.Count != 1) || pairs.Select(x => x.Group[0].Id).Distinct().Count() != order.Groups.Count)
            return OutboxResult.Dead("seller_order_mismatch");   // never guess which seller order is whose: a person looks

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        var claimed = await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Paid)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Placed).SetProperty(o => o.PlacedAt, now).SetProperty(o => o.UpdatedAt, now), ct);
        if (claimed == 0) { await tx.RollbackAsync(ct); return OutboxResult.Delivered(); }   // a parallel delivery stored it first
        foreach (var (r, g) in pairs)
        {
            await db.OrderGroups.Where(x => x.Id == g[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SellerOrderId, (Guid?)r.SellerOrderId).SetProperty(x => x.SellerOrderNumber, r.Number).SetProperty(x => x.Oversold, r.Oversold), ct);
            // payment.captured goes only after the portal returned the seller order (the portal would answer 404 order_not_found otherwise).
            outbox.EnqueuePaymentEvent($"captured:{order.Number}:{r.Number}", PaymentEventTypes.Captured,
                new PaymentCapturedData(PaymentKinds.Order, order.Number, null, r.Number, order.PaymentIntentId!, order.Currency, g[0].TotalCents, order.PaidAt));
        }
        await db.SaveChangesAsync(ct);
        await EnqueueBuyerMailAsync(order, "Your Solvelt order " + order.Number,
            $"Thank you for your order {order.Number}.\n\nTotal charged: {Money(order.TotalCents, order.Currency)}. Each seller prepares their own part; you will get tracking details as soon as they ship.\n", $"placed:{order.Number}", ct);
        await tx.CommitAsync(ct);
        // seller orders now exist: re-apply portal events (shipped, refund.issued, ...) that arrived before we knew them
        await portalEvents.ReplayParkedAsync("order", order.Number, ct);
        return OutboxResult.Delivered();
    }

    private async Task<OutboxResult> FailAsync(MarketplaceOrder order, PortalError error, CancellationToken ct)
    {
        var code = error.Errors?.FirstOrDefault()?.Code is { Length: > 0 } c ? new string(c.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_').Take(50).ToArray()) : "invalid_order";
        var windowGone = code.StartsWith("delivery_", StringComparison.Ordinal);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        var claimed = await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Paid)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Failed).SetProperty(o => o.FailureCode, code).SetProperty(o => o.UpdatedAt, now), ct);
        if (claimed == 0) { await tx.RollbackAsync(ct); return OutboxResult.Delivered(); }
        // Nothing was created at the portal, so the whole payment goes back; the reservation, the Stripe refund and the mail are queued with the status change.
        await refunds.ReserveAsync(order.Id, null, order.TotalCents, 0, $"fail:{order.Number}", "order_failed", reportToPortal: false, portalRefundId: null, ct);
        await EnqueueBuyerMailAsync(order, "We couldn't place your order " + order.Number,
            (windowGone ? "The delivery time you chose is no longer available, so the seller couldn't accept your order. " : "One of the items could no longer be ordered, so the sellers couldn't accept your order. ")
            + $"You have not been charged: {Money(order.TotalCents, order.Currency)} is being refunded to your card in full (it can take a few days to show). Please place the order again and choose again.\n", $"failed:{order.Number}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return OutboxResult.Delivered();
    }

    private async Task EnqueueBuyerMailAsync(MarketplaceOrder order, string subject, string body, string dedupe, CancellationToken ct)
    {
        var email = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == order.AccountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
        if (email is null) return;   // an account deleted meanwhile has no address to write to
        outbox.EnqueueEmail(new EmailMessage(email, subject, body), "mail:" + dedupe);
        await db.SaveChangesAsync(ct);
    }

    public static string Money(long cents, string currency) => $"{(currency == "USD" ? "$" : currency + " ")}{cents / 100}.{cents % 100:D2}";
}
