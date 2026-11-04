using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class RefundEventTests : OrderFlowBase
{
    private int _portalEvent;

    private async Task<MarketplaceOrder> Paid(bool ingest = true)
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        if (ingest) await Drain();
        return await Order(number);
    }

    private static OrderGroup JudaicaGroup(MarketplaceOrder o) => o.Groups.Single(g => g.ItemsCents == 4200);
    private static OrderGroup Bakery(MarketplaceOrder o) => o.Groups.Single(g => g.ItemsCents == 1300);

    private async Task<string> Portal(string type, MarketplaceOrder o, OrderGroup g, object? extra = null, string? id = null, Guid? sellerOrderId = null, string? mid = null)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = sellerOrderId ?? g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = mid ?? o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var body = Body(id ?? $"pe-{++_portalEvent:D3}", type, _k.F.Clock.GetUtcNow(), data);
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;
    }

    private async Task<List<JsonElement>> Sent(string type) => Portal2("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() == type).ToList();
    private IReadOnlyList<ReceivedCall> Portal2(string path) => _k.F.Portal.VerifiedCalls.Where(c => c.PathAndQuery == "/api/internal/v1/" + path).ToList();

    [Fact]
    public async Task A_portal_refund_returns_items_shipping_and_the_tax_on_them_in_proportion_and_the_last_one_returns_exactly_the_rest()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        Assert.Equal(417, g.TaxCents);                                  // 373 on the line + 44 on shipping
        Assert.Equal(5117, g.TotalCents);
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { refundId = "rf-1", amountCents = 1000, shippingCents = 0, reason = "damaged" }));
        await Drain();
        var first = Assert.Single(Stripe.Refunds);
        Assert.Equal(1000 + 89, first.AmountCents);                     // 417 x 1000 / 4700 = 88.7 -> 89
        var e = Assert.Single(await Sent("refund.processed"));
        var d = e.GetProperty("data");
        Assert.Equal((first.Id, g.SellerOrderNumber, 1000L, 0L), (d.GetProperty("refundId").GetString(), d.GetProperty("sellerOrderNumber").GetString(), d.GetProperty("amountCents").GetInt64(), d.GetProperty("shippingCents").GetInt64()));   // the portal hears items and shipping only
        Assert.Equal("refund-processed:portal:rf-1", e.GetProperty("eventId").GetString());

        Assert.Equal("applied", await Portal("refund.issued", o, g, new { refundId = "rf-2", amountCents = 3200, shippingCents = 500, reason = "other" }));
        await Drain();
        Assert.Equal(2, Stripe.Refunds.Count);
        Assert.Equal(g.TotalCents - 1089, Stripe.Refunds.Last().AmountCents);   // the rest of the seller order, tax remainder included
        var after = await Order(o.Number);
        var ag = after.Groups.Single(x => x.Id == g.Id);
        Assert.Equal((4700L, 417L, 5117L), (ag.RefundedNetCents, ag.RefundedTaxCents, ag.RefundedCents));
        Assert.Equal(5117, after.RefundedCents);
        // not a cent more, and the other seller order is untouched
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { refundId = "rf-3", amountCents = 1, shippingCents = 0 }));
        Assert.Equal(2, Stripe.Refunds.Count);
        Assert.Equal(0, after.Groups.Single(x => x.Id == Bakery(o).Id).RefundedCents);
    }

    [Fact]
    public async Task A_replayed_or_repeated_refund_is_one_refund()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { refundId = "rf-1", amountCents = 700, shippingCents = 0 }, id: "pe-same"));
        Assert.Equal("duplicate", await Portal("refund.issued", o, g, new { refundId = "rf-1", amountCents = 700, shippingCents = 0 }, id: "pe-same"));    // the same event again
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { refundId = "rf-1", amountCents = 700, shippingCents = 0 }, id: "pe-other-id"));   // the same refund under another event id
        await Drain(); await Drain();
        Assert.Single(Stripe.Refunds);
        Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync()));
    }

    [Fact]
    public async Task An_event_is_matched_by_our_order_id_and_the_portals_seller_order_id_together()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        var other = Bakery(o);
        // right marketplace id, the OTHER seller order's id: the id pair must be equal to the stored pair of some group, and this one is: it refunds THAT group (the one the portal named)
        Assert.Equal("applied", await Portal("refund.issued", o, other, new { refundId = "rf-a", amountCents = 100, shippingCents = 0 }));
        await Drain();
        Assert.Equal(100L, (await Order(o.Number)).Groups.Single(x => x.Id == other.Id).RefundedNetCents);
        Assert.Equal(0, (await Order(o.Number)).Groups.Single(x => x.Id == g.Id).RefundedCents);
        // a portal id that belongs to none of our groups: nothing is refunded
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { refundId = "rf-b", amountCents = 100, shippingCents = 0 }, sellerOrderId: Guid.NewGuid()));
        // a marketplace id that is not ours at all
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { refundId = "rf-c", amountCents = 100, shippingCents = 0 }, mid: "MP-20990101-ZZZZZZZZ"));
        // a missing reference and bad amounts
        Assert.Equal("skipped", await PostRaw("refund.issued", new { refundId = "rf-d", amountCents = 100 }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { refundId = "rf-e", amountCents = -5, shippingCents = 0 }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { refundId = "rf-f", amountCents = 0, shippingCents = 0 }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { amountCents = 100, shippingCents = 0 }));
        await Drain();
        Assert.Single(Stripe.Refunds);
    }

    private async Task<string> PostRaw(string type, object data)
    {
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"pe-raw-{++_portalEvent}", type, _k.F.Clock.GetUtcNow(), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task A_refund_that_arrives_before_the_seller_orders_are_stored_waits_and_is_applied_when_they_are()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        var gate = false;
        _onOrder = c => gate ? Created(c, 201, true) : throw new HttpRequestException("down");
        await Pay(number, intent, total);
        await Drain();                                                  // the ingest has not stored the seller orders: the portal may already know them, we do not
        var o = await Order(number);
        Assert.Null(o.Groups.Single().SellerOrderId);
        var soid = Guid.NewGuid();
        // the portal's event names the seller order; we cannot match it yet, so it is parked, not lost and not applied
        Assert.Equal("parked", await Portal("refund.issued", o, o.Groups.Single(), new { refundId = "rf-early", amountCents = 500, shippingCents = 0 }, sellerOrderId: soid));
        Assert.Empty(Stripe.Refunds);
        // the ingest finally answers with that very seller order id
        gate = true;
        _sellerOrders[$"{number}|{o.Groups.Single().SellerId}|{o.Groups.Single().BrandId}"] = (soid, "SO-2026-000042");
        _k.F.Clock.Advance(TimeSpan.FromSeconds(31));
        await Drain();
        Assert.Equal("placed", (await Order(number)).Status);
        await Drain();
        var refund = Assert.Single(Stripe.Refunds);                     // the parked event was applied right after the ingest stored the seller order
        Assert.True(refund.AmountCents >= 500);
        Assert.Single(await Sent("refund.processed"));
    }

    [Fact]
    public async Task A_cancelled_seller_order_is_refunded_in_full_with_its_shipping_and_tax_and_a_partial_remainder_without_shipping()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        var bakery = Bakery(o);
        Assert.Equal("applied", await Portal("order.cancelled", o, g, new { refundDueCents = 4700, partial = false, reason = "seller" }));        // items + shipping, as the ledger counts it
        Assert.Equal("applied", await Portal("order.cancelled", o, bakery, new { refundDueCents = 650, partial = true }));
        await Drain();
        Assert.Equal(2, Stripe.Refunds.Count);
        Assert.Contains(Stripe.Refunds, r => r.AmountCents == g.TotalCents);                      // 5117: everything the buyer paid for that seller order, tax included
        var after = await Order(o.Number);
        Assert.Equal(g.TotalCents, after.Groups.Single(x => x.Id == g.Id).RefundedCents);
        var b = after.Groups.Single(x => x.Id == bakery.Id);
        Assert.Equal((650L, 0L), (b.RefundedNetCents, b.RefundedTaxCents));                       // no tax on exempt food, and shipping stays with the shipped part
        var reported = await Sent("refund.processed");
        Assert.Equal(2, reported.Count);
        var big = reported.Single(e => e.GetProperty("data").GetProperty("sellerOrderNumber").GetString() == g.SellerOrderNumber).GetProperty("data");
        Assert.Equal((4200L, 500L), (big.GetProperty("amountCents").GetInt64(), big.GetProperty("shippingCents").GetInt64()));
        // nothing due is nothing refunded; a cancellation that claims more than is left is cut down, never exceeded
        Assert.Equal("skipped", await Portal("order.cancelled", o, bakery, new { refundDueCents = 0 }));
        Assert.Equal("applied", await Portal("order.cancelled", o, bakery, new { refundDueCents = 99999, partial = false }, id: "pe-big"));
        await Drain();
        Assert.Equal(bakery.ItemsCents + bakery.ShippingCents, (await Order(o.Number)).Groups.Single(x => x.Id == bakery.Id).RefundedNetCents);
        Assert.Equal(bakery.TotalCents, (await Order(o.Number)).Groups.Single(x => x.Id == bakery.Id).RefundedCents);
        Assert.True((await Order(o.Number)).RefundedCents <= o.PaidCents);
    }

    [Fact]
    public async Task Parallel_refund_events_for_one_seller_order_reserve_exactly_what_fits_and_return_the_tax_once()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        // 8 distinct refunds of 2500 net on a seller order that holds 4700: one fits, the others do not
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Portal("refund.issued", o, g, new { refundId = $"rf-p{i}", amountCents = 2500, shippingCents = 0 }, id: $"pe-p{i}")));
        Assert.Equal(1, results.Count(r => r == "applied"));
        await Drain();
        Assert.Single(Stripe.Refunds);
        var ag = (await Order(o.Number)).Groups.Single(x => x.Id == g.Id);
        Assert.Equal(2500, ag.RefundedNetCents);
        Assert.Equal(2500 + 222, ag.RefundedCents);                     // 417 x 2500 / 4700 = 221.8 -> 222
        // 2200 more is exactly the rest: the remaining tax (417 - 222 = 195) comes with it, once
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { refundId = "rf-last", amountCents = 2200, shippingCents = 0 }));
        Assert.Equal(g.TotalCents, (await Order(o.Number)).Groups.Single(x => x.Id == g.Id).RefundedCents);
    }

    [Fact]
    public async Task A_refund_made_in_the_stripe_dashboard_is_recorded_against_the_cap_ours_are_not_mistaken_for_it()
    {
        var o = await Paid();
        var g = JudaicaGroup(o);
        await Portal("refund.issued", o, g, new { refundId = "rf-1", amountCents = 1000, shippingCents = 0 });
        await Drain();
        var ourTotal = (await Order(o.Number)).RefundedCents;
        var charge = (string id, long refunded) => Event("charge.refunded", "ch_1", null, 0, id: id, extra: new { payment_intent = o.PaymentIntentId, amount_refunded = refunded });
        // Stripe reports exactly our own refund: nothing new
        Assert.Contains("ignored", await (await PostStripe(charge("evt_c1", ourTotal))).Content.ReadAsStringAsync());
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
        // a person refunded another 500 in the dashboard
        Assert.Contains("applied", await (await PostStripe(charge("evt_c2", ourTotal + 500))).Content.ReadAsStringAsync());
        Assert.Equal(ourTotal + 500, (await Order(o.Number)).RefundedCents);
        Assert.Equal(500, (await _k.Db(db => db.OrderRefunds.SingleAsync(r => r.Reason == "external"))).AmountCents);
        await PostStripe(charge("evt_c3", ourTotal + 500));              // the same total again, under another event id
        Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
        Assert.Contains("unmatched", await (await PostStripe(Event("charge.refunded", "ch_x", null, 0, extra: new { payment_intent = "pi_not_ours", amount_refunded = 5 }))).Content.ReadAsStringAsync());
    }

    // ---- disputes ----

    private string Dispute(string type, MarketplaceOrder o, string status, long amount = 5000, string? id = null, string dp = "dp_1")
        => Event(type, dp, null, amount, id: id, extra: new { payment_intent = o.PaymentIntentId, status, reason = "fraudulent", evidence_details = new { due_by = 1_800_000_000L } });

    [Fact]
    public async Task A_dispute_is_reported_to_the_portal_per_seller_order_with_the_amount_split_exactly()
    {
        var o = await Paid();
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: o.TotalCents, id: "evt_d1"))).Content.ReadAsStringAsync());
        await Drain();
        var created = await Sent("dispute.created");
        Assert.Equal(2, created.Count);                                  // one per seller order of the payment
        Assert.Equal(2, created.Select(e => e.GetProperty("eventId").GetString()).Distinct().Count());
        var datas = created.Select(e => e.GetProperty("data")).ToList();
        Assert.All(datas, d => { Assert.Equal("dp_1", d.GetProperty("disputeId").GetString()); Assert.Equal("needs_response", d.GetProperty("status").GetString()); Assert.Equal(o.Number, d.GetProperty("marketplaceOrderId").GetString()); });
        // the seller orders hold items + shipping (tax is the marketplace's): 4700 + 1800 = 6500 of the 6917 paid
        Assert.Equal(6500, datas.Sum(d => d.GetProperty("amountCents").GetInt64()));
        Assert.Equal(4700, datas.Single(d => d.GetProperty("sellerOrderNumber").GetString() == JudaicaGroup(o).SellerOrderNumber).GetProperty("amountCents").GetInt64());
        Assert.Single(datas, d => d.TryGetProperty("feeCents", out var f) && f.ValueKind == JsonValueKind.Number);   // the portal's default dispute fee applies once, not per seller order... the others say 0
        // the same Stripe event again changes nothing
        await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: o.TotalCents, id: "evt_d1"));
        await Drain();
        Assert.Equal(2, (await Sent("dispute.created")).Count);
        // evidence update, then closed won / lost map to updated / closed
        await PostStripe(Dispute("charge.dispute.updated", o, "under_review", amount: o.TotalCents, id: "evt_d2"));
        await PostStripe(Dispute("charge.dispute.closed", o, "won", amount: o.TotalCents, id: "evt_d3"));
        await Drain();
        Assert.Equal(2, (await Sent("dispute.updated")).Count);
        var closed = await Sent("dispute.closed");
        Assert.Equal(2, closed.Count);
        Assert.All(closed, e => Assert.Equal("won", e.GetProperty("data").GetProperty("status").GetString()));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L), created[0].GetProperty("data").GetProperty("evidenceDueBy").GetDateTimeOffset());
    }

    [Fact]
    public async Task Inquiries_unknown_statuses_and_failed_or_unmatched_orders_send_nothing_and_an_unstored_order_is_retried()
    {
        var o = await Paid();
        Assert.Contains("ignored", await (await PostStripe(Dispute("charge.dispute.created", o, "warning_needs_response", id: "evt_i1"))).Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await PostStripe(Dispute("charge.dispute.closed", o, "charge_refunded", id: "evt_i2"))).Content.ReadAsStringAsync());
        Assert.Contains("unmatched", await (await PostStripe(Event("charge.dispute.created", "dp_x", null, 5, extra: new { payment_intent = "pi_not_ours", status = "needs_response" }))).Content.ReadAsStringAsync());
        await Drain();
        Assert.Empty(await Sent("dispute.created"));
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.closed", o, "warning_closed", id: "evt_i3"))).Content.ReadAsStringAsync());   // a closed inquiry is told
        // an order that was paid but whose seller orders are not stored yet: 5xx so Stripe tries again later
        await _k.Db(db => db.OrderGroups.Where(g => g.OrderId == o.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.SellerOrderNumber, (string?)null)));
        Assert.True((int)(await PostStripe(Dispute("charge.dispute.created", o, "needs_response", id: "evt_i4"))).StatusCode >= 500);
        Assert.Equal(0, await _k.Db(db => db.StripeEvents.CountAsync(e => e.Id == "evt_i4")));
    }

    [Fact]
    public void The_largest_remainder_split_adds_up_exactly()
    {
        foreach (var (total, weights) in new[] { (100L, new long[] { 1, 1, 1 }), (6500L, new long[] { 4700, 1800 }), (1L, new long[] { 5, 5 }), (0L, new long[] { 1 }), (7L, new long[] { 0, 0 }), (999_999_999L, new long[] { 3, 7, 11, 13 }) })
        {
            var parts = StripeEventProcessor.Allocate(total, weights);
            Assert.Equal(weights.Sum() == 0 ? 0 : total, parts.Sum());
            Assert.All(parts, p => Assert.True(p >= 0));
        }
        Assert.Equal([34L, 33L, 33L], StripeEventProcessor.Allocate(100, [1, 1, 1]));
    }
}
