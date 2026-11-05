using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The "orders" section of the buyer's data export (decision M3-44).</summary>
public sealed class OrdersDataExporterTests : OrderFlowBase
{
    private async Task<string> PlaceOrder(string email, string street)
    {
        await Login(email);
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = new { name = "Jane Brown", line1 = street, city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", phone = "+1 718 555 0100" } });
        await Pay(number, intent, total);
        await Drain();
        return number;
    }

    [Fact]
    public async Task The_export_has_the_buyers_orders_as_stored_with_refunds_and_nothing_internal_or_of_anyone_else()
    {
        var otherNumber = await PlaceOrder("export-other@example.com", "99 Secret-of-Other St");
        var number = await PlaceOrder("export-me@example.com", "1 Main St");
        var order = await Order(number);
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db =>
        {
            db.OrderRefunds.Add(new OrderRefund { Id = Guid.NewGuid(), Key = "portal:KEY-INTERNAL", OrderId = order.Id, GroupId = order.Groups.Single().Id, AmountCents = 700, Reason = "portal_refund", Status = RefundStatus.Succeeded, StripeRefundId = "re_INTERNAL", CreatedAt = now, UpdatedAt = now });
            db.OrderRefunds.Add(new OrderRefund { Id = Guid.NewGuid(), Key = "portal:KEY-REJECTED", OrderId = order.Id, AmountCents = 300, Reason = "portal_refund", Status = RefundStatus.Rejected, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            return 0;
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/data-export");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session);
        var res = await _k.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        var section = JsonDocument.Parse(text).RootElement.GetProperty("sections").GetProperty("orders");
        var ordersText = section.GetRawText();                                                   // the leak checks look at this section only (the account section has its own, M1)
        var orders = section.GetProperty("orders");
        var o = Assert.Single(orders.EnumerateArray());
        Assert.Equal(number, o.GetProperty("number").GetString());
        Assert.Equal(order.TotalCents, o.GetProperty("totalCents").GetInt64());
        Assert.Equal("1 Main St", o.GetProperty("shippingAddress").GetProperty("line1").GetString());
        Assert.Equal(order.Lines.Count, o.GetProperty("lines").GetArrayLength());
        Assert.Equal(order.Groups.Single().SellerOrderNumber, o.GetProperty("sellerOrders")[0].GetProperty("number").GetString());
        var refund = Assert.Single(o.GetProperty("refunds").EnumerateArray());                   // only money that went back
        Assert.Equal(700, refund.GetProperty("amountCents").GetInt64());
        var internals = new[] { otherNumber, "Secret-of-Other", "export-other@example.com", "KEY-INTERNAL", "KEY-REJECTED", "re_INTERNAL", order.PaymentIntentId!, order.RelayEmail,
            order.Groups.Single().SellerId.ToString(), order.Groups.Single().BrandId.ToString(), order.Groups.Single().SellerOrderId!.Value.ToString() };
        foreach (var leak in internals) Assert.DoesNotContain(leak, ordersText, StringComparison.OrdinalIgnoreCase);
    }
}
