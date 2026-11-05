using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The money side of the portal order read (decision M3-41): refunds and the refunded total are compared and ALERTED, never changed.</summary>
public sealed class OrderMoneyReconcileTests : OrderFlowBase
{
    private static PortalSellerOrderState Portal(long refunded, params PortalOrderRefund[] refunds)
        => new(Guid.NewGuid(), "SO-1", null, null, "accepted", null, "USD", 14262, 0, 500, 0, refunded, [], [.. refunds], [], [], null);
    private static PortalOrderRefund PR(string id, long amount) => new(id, amount, 0, 0, 0, null, null);
    private static OrderRefund Row(string key, long amount, string status = RefundStatus.Succeeded) => new() { Id = Guid.NewGuid(), Key = key, AmountCents = amount, Status = status, Reason = "portal_refund" };
    private static IReadOnlyList<MoneyMismatch> Compare(PortalSellerOrderState p, params OrderRefund[] rows) => OrderMoneyReconcileObserver.Compare("SO-1", p, rows);

    [Fact]
    public void A_seller_order_whose_refunds_agree_has_no_mismatch()
    {
        Assert.Empty(Compare(Portal(0)));                                                                       // nothing refunded on either side
        Assert.Empty(Compare(Portal(4587, PR("rf-1", 4587)), Row("portal:rf-1", 4587)));
        Assert.Empty(Compare(Portal(4587 + 500, PR("rf-1", 4587), PR("rf-2", 500)), Row("portal:rf-1", 4587), Row("portal:rf-2", 500, RefundStatus.Reserved)));   // a reserved refund counts as ours
    }

    [Fact]
    public void A_refund_the_portal_lists_and_we_hold_nowhere_is_reported_with_the_total()
    {
        var m = Compare(Portal(4587, PR("rf-lost", 4587)));
        Assert.Contains(m, x => x.Kind == MoneyMismatchKind.PortalRefundUnknownHere && x.RefundId == "rf-lost" && x.Portal == 4587 && x.Ours == 0);
        Assert.Contains(m, x => x.Kind == MoneyMismatchKind.RefundedTotalDiffers && x.Portal == 4587 && x.Ours == 0);
    }

    [Fact]
    public void A_refund_we_rejected_or_failed_is_not_money_we_hold_but_the_portal_counts_it()
    {
        foreach (var status in new[] { RefundStatus.Rejected, RefundStatus.Failed })
        {
            var m = Compare(Portal(4587, PR("rf-1", 4587)), Row("portal:rf-1", 4587, status));
            Assert.Contains(m, x => x.Kind == MoneyMismatchKind.PortalRefundNotLiveHere && x.RefundId == "rf-1");
            Assert.Contains(m, x => x.Kind == MoneyMismatchKind.RefundedTotalDiffers && x.Portal == 4587 && x.Ours == 0);
        }
    }

    [Fact]
    public void An_amount_that_differs_and_a_live_portal_refund_the_portal_does_not_list_are_reported()
    {
        var cut = Compare(Portal(8262, PR("rf-1", 9174)), Row("portal:rf-1", 8262));                              // a refund we cut when Stripe had refunded part of it
        Assert.Equal([MoneyMismatchKind.RefundAmountDiffers], cut.Select(x => x.Kind));                           // the totals agree (8262 both), only the amount of that one refund differs
        Assert.Contains(cut, x => x.Kind == MoneyMismatchKind.RefundAmountDiffers && x.Portal == 9174 && x.Ours == 8262);
        var extra = Compare(Portal(0), Row("portal:rf-ghost", 700));
        Assert.Contains(extra, x => x.Kind == MoneyMismatchKind.OurRefundNotAtPortal && x.RefundId == "rf-ghost" && x.Ours == 700);
    }

    [Fact]
    public void Only_the_sum_of_live_refunds_counts_for_the_total_and_a_portal_without_a_refund_list_is_still_compared()
    {
        var m = Compare(Portal(1000), Row("cancelled:evt-1", 600), Row("portal:rf-1", 400, RefundStatus.Rejected));
        Assert.Equal([MoneyMismatchKind.RefundedTotalDiffers], m.Select(x => x.Kind));                           // 1000 at the portal, 600 live here (cancel refunds have no portal refund id)
        Assert.Equal(("SO-1", 1000L, 600L), (m[0].SellerOrderNumber, m[0].Portal, m[0].Ours));
    }

    [Fact]
    public async Task A_lost_refund_event_is_alerted_through_the_portal_read_and_nothing_is_changed()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter", 3);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        var g = o.Groups.Single();
        _onOther = c => c.Method == "GET" && c.PathAndQuery.StartsWith("/api/internal/v1/orders/MP-")
            ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                marketplaceOrderId = o.Number,
                sellerOrders = new[] { new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, status = "accepted", updatedAt = _k.F.Clock.GetUtcNow(), currency = "USD", totalCents = o.TotalCents, refundedCents = 4587,
                    lines = Array.Empty<object>(), refunds = new[] { new { refundId = "rf-lost", amountCents = 4587, taxCents = 0, shippingCents = 0, shippingTaxCents = 0 } }, shipments = Array.Empty<object>(), returns = Array.Empty<object>() } },
            }))
            : null;
        var before = await Order(number);

        // an event of this order arrives flagged predecessorDead: M5's reconcile reads the portal, then every observer sees the state
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = number }, ["acceptedAt"] = DateTimeOffset.UtcNow };
        var body = "{\"predecessorDead\":true," + Body("dead-money-1", "order.accepted", _k.F.Clock.GetUtcNow().AddSeconds(-30), data).TrimStart()[1..];
        var (status, _) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        await Drain();

        var alerts = _k.F.Logs.Lines.Where(l => l.Contains("Order money reconcile")).ToList();
        Assert.Contains(alerts, l => l.Contains("PortalRefundUnknownHere") && l.Contains(number) && l.Contains("rf-lost"));
        Assert.Contains(alerts, l => l.Contains("RefundedTotalDiffers"));
        Assert.DoesNotContain(alerts, l => l.Contains("@"));                                                    // no buyer data in the alert
        // money never moves: no refund was reserved or sent, no counter changed
        var after = await Order(number);
        Assert.Equal((before.RefundedCents, before.Groups.Single().RefundedCents), (after.RefundedCents, after.Groups.Single().RefundedCents));
        Assert.Empty(Stripe.Refunds);
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync()));
    }
}
