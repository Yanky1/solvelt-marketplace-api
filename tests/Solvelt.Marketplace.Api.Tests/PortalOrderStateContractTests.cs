using System.Text.Json;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The portal's own example of GET /orders/{marketplaceOrderId} (portal docs/ORDERS.md, C16), with the "..." filled in: our models read every field of it.</summary>
public sealed class PortalOrderStateContractTests
{
    private const string Example = """
    { "marketplaceOrderId": "MP-1001",
      "sellerOrders": [{
        "sellerOrderId": "5b1d3c0e-0000-4000-8000-000000000001", "sellerOrderNumber": "SO-2026-000123",
        "sellerId": "5b1d3c0e-0000-4000-8000-000000000002", "brandId": "5b1d3c0e-0000-4000-8000-000000000003",
        "status": "Delivered", "updatedAt": "2026-11-05T10:00:00Z",
        "currency": "USD", "totalCents": 6640, "taxCents": 600, "shippingCents": 400, "shippingTaxCents": 40, "refundedCents": 4140,
        "lines": [{ "marketplaceLineId": "LA", "sku": "SKU-1", "quantity": 3, "shippedQty": 3, "cancelledQty": 0, "refundedQty": 2, "returnedQty": 2 }],
        "refunds": [{ "refundId": "RF-1", "amountCents": 4140, "taxCents": 440, "shippingCents": 100, "shippingTaxCents": 10, "reason": "returned_item", "createdAt": "2026-11-05T09:00:00Z" }],
        "shipments": [{ "shipmentId": "SH-1", "method": "carrier", "carrier": "UPS", "trackingNumber": "1Z999AA10123456784", "status": null, "shippedAt": "2026-11-02T10:00:00Z", "deliveredAt": "2026-11-04T10:00:00Z" }],
        "returns": [{ "returnId": "R-A", "status": "Refunded", "createdAt": "2026-11-05T08:00:00Z", "updatedAt": "2026-11-05T09:00:00Z" }],
        "lastEventSequence": 7 } ] }
    """;

    [Fact]
    public void The_portals_example_order_state_is_read_in_full()
    {
        var s = JsonSerializer.Deserialize<PortalOrderState>(Example, PortalJson.Options)!;
        Assert.Equal("MP-1001", s.MarketplaceOrderId);
        var so = Assert.Single(s.SellerOrders!);
        Assert.Equal("SO-2026-000123", so.SellerOrderNumber);
        Assert.Equal(Guid.Parse("5b1d3c0e-0000-4000-8000-000000000001"), so.SellerOrderId);
        Assert.Equal("Delivered", so.Status);
        Assert.Equal(("USD", 6640L, 600L, 400L, 40L, 4140L), (so.Currency, so.TotalCents, so.TaxCents, so.ShippingCents, so.ShippingTaxCents, so.RefundedCents));
        Assert.Equal(7, so.LastEventSequence);
        var l = Assert.Single(so.Lines!);
        Assert.Equal(("LA", 3, 3, 0, 2, 2), (l.MarketplaceLineId, l.Quantity, l.ShippedQty, l.CancelledQty, l.RefundedQty, l.ReturnedQty));
        var r = Assert.Single(so.Refunds!);
        Assert.Equal((4140L, 440L, 100L, 10L, "returned_item"), (r.AmountCents, r.TaxCents, r.ShippingCents, r.ShippingTaxCents, r.Reason));
        var sh = Assert.Single(so.Shipments!);
        Assert.Null(sh.Status);                                         // a shipment's status can be null
        Assert.Equal("1Z999AA10123456784", sh.TrackingNumber);
        Assert.NotNull(sh.DeliveredAt);
        var rt = Assert.Single(so.Returns!);
        Assert.Equal(("R-A", "Refunded"), (rt.ReturnId, rt.Status));
    }

    [Fact]
    public void A_seller_order_with_no_events_has_a_null_sequence_and_empty_lists()
    {
        const string json = """{ "marketplaceOrderId": "MP-1", "sellerOrders": [{ "sellerOrderId": "5b1d3c0e-0000-4000-8000-000000000001", "status": "Created", "currency": "USD", "totalCents": 1, "taxCents": 0, "shippingCents": 0, "shippingTaxCents": 0, "refundedCents": 0, "lines": [], "refunds": [], "shipments": [], "returns": [], "lastEventSequence": null }] }""";
        var so = Assert.Single(JsonSerializer.Deserialize<PortalOrderState>(json, PortalJson.Options)!.SellerOrders!);
        Assert.Null(so.LastEventSequence);
        Assert.Empty(so.Lines!);
    }
}
