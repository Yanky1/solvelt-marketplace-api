using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class RefundEventTests : OrderFlowBase
{
    // These tests describe the INTERIM mapping (decision M3-09: one order-level speed, shipping tax folded into line tax), which stays as the emergency path now that Portal:PerGroupShipping is ON by default
    // (M3-37). They run with the switch off, exactly as written; the switch-on contract is in PerGroupShippingTests.
    public RefundEventTests() : base(new Dictionary<string, string?> { ["Portal:PerGroupShipping"] = "false" }) { }

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

    // ---- C9: the portal's amounts INCLUDE line tax; the buyer gets exactly them; refund.processed excludes tax by the same floor proration ----

    /// <summary>3 pewter cups (4200 each) from one seller: items 12600, shipping 500, line tax 1118, tax on shipping 44 (folded into the line: 1162 sent), total 14262. The portal's line net is 12600 + 1162 = 13762.</summary>
    private async Task<(MarketplaceOrder Order, OrderGroup Group, string LineId)> Three()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter", 3);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        var g = o.Groups.Single();
        Assert.Equal((12600L, 500L, 1162L, 14262L), (g.ItemsCents, g.ShippingCents, g.TaxCents, g.TotalCents));
        return (o, g, o.Lines.Single().MarketplaceLineId);
    }

    private Task<string> RefundLines(MarketplaceOrder o, OrderGroup g, string refundId, string lineId, int qty, long lineAmount, long shipping = 0, string? id = null)
        => Portal("refund.issued", o, g, new { RefundId = refundId, AmountCents = lineAmount + shipping, ShippingCents = shipping, Lines = new[] { new { marketplaceLineId = lineId, quantity = qty, amountCents = lineAmount } } }, id: id);

    [Fact]
    public async Task One_of_three_units_refunds_exactly_the_portal_amount_and_refund_processed_is_that_minus_floor_prorated_tax()
    {
        var (o, g, line) = await Three();
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));          // floor(13762 / 3)
        await Drain();
        var refund = Assert.Single(Stripe.Refunds);
        Assert.Equal(4587, refund.AmountCents);                                            // exactly what the portal said: no tax added on top
        var d = Assert.Single(await Sent("refund.processed")).GetProperty("data");
        Assert.Equal((4587L - 387, 0L), (d.GetProperty("amountCents").GetInt64(), d.GetProperty("shippingCents").GetInt64()));   // 387 = floor(1162 / 3): items 4200 excluding tax
        var ag = (await Order(o.Number)).Groups.Single();
        Assert.Equal((4587L, 387L, 0L), (ag.RefundedCents, ag.RefundedTaxCents, ag.RefundedShippingCents));
        Assert.Equal((1, 387L), ((await Order(o.Number)).Lines.Single().RefundedQuantity, (await Order(o.Number)).Lines.Single().RefundedTaxCents));
    }

    [Fact]
    public async Task Three_successive_partial_refunds_sum_to_the_paid_line_exactly_and_the_last_takes_the_remainder()
    {
        var (o, g, line) = await Three();
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        Assert.Equal("applied", await RefundLines(o, g, "rf-2", line, 1, 4587));
        Assert.Equal("applied", await RefundLines(o, g, "rf-3", line, 1, 4588));          // 13762 - 2 x 4587: the portal's last unit takes the remainder
        await Drain();
        Assert.Equal(new long[] { 4587, 4587, 4588 }, Stripe.Refunds.Select(r => r.AmountCents).OrderBy(x => x).ToArray());
        Assert.Equal(13762, Stripe.Refunds.Sum(r => r.AmountCents));                       // the whole line, exactly
        var items = (await Sent("refund.processed")).Select(e => e.GetProperty("data").GetProperty("amountCents").GetInt64()).OrderBy(x => x).ToList();
        Assert.Equal([4200L, 4200L, 4200L], items);                                        // 4587-387, 4587-387, 4588-388: items 12600 in total, tax 1162 backed out exactly
        var after = await Order(o.Number);
        Assert.Equal((13762L, 1162L, 3), (after.Groups.Single().RefundedCents, after.Groups.Single().RefundedTaxCents, after.Lines.Single().RefundedQuantity));
        // a fourth unit does not exist; the shipping is still there to refund (shipping only, no tax in it)
        Assert.Equal("skipped", await RefundLines(o, g, "rf-4", line, 1, 100));
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { RefundId = "rf-ship", AmountCents = 500, ShippingCents = 500 }));
        await Drain();
        var ship = (await Sent("refund.processed")).Select(e => e.GetProperty("data")).Single(d => d.GetProperty("shippingCents").GetInt64() == 500);
        Assert.Equal(0, ship.GetProperty("amountCents").GetInt64());
        Assert.Equal(14262, (await Order(o.Number)).RefundedCents);                        // all that was paid, not a cent more
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "rf-more", AmountCents = 1, ShippingCents = 0 }));
    }

    [Fact]
    public async Task A_full_cancel_returns_everything_not_yet_returned_and_a_partial_remainder_cancel_the_units_net_without_shipping()
    {
        var (o, g, line) = await Three();
        await RefundLines(o, g, "rf-1", line, 1, 4587);                                    // one unit already refunded
        // full cancel: TotalCents - already refunded = 14262 - 4587, shipping and tax included
        Assert.Equal("applied", await Portal("order.cancelled", o, g, new { RefundDueCents = 9675, Partial = false }));
        await Drain();
        Assert.Equal(2, Stripe.Refunds.Count);
        Assert.Contains(Stripe.Refunds, r => r.AmountCents == 9675);
        var d = (await Sent("refund.processed")).Select(e => e.GetProperty("data")).Single(x => x.GetProperty("amountCents").GetInt64() != 4200 && x.GetProperty("shippingCents").GetInt64() == 500);
        Assert.Equal(8400, d.GetProperty("amountCents").GetInt64());                        // 9675 - 500 shipping - 775 remaining tax (1162 - 387): the 2 other units
        var after = await Order(o.Number);
        Assert.Equal((14262L, 1162L, 500L, 3), (after.Groups.Single().RefundedCents, after.Groups.Single().RefundedTaxCents, after.Groups.Single().RefundedShippingCents, after.Lines.Single().RefundedQuantity));

        // partial remainder cancel (a second order): the cancelled unit's line net INCLUDING tax, no shipping
        var (o2, g2, line2) = await SecondThree();
        Assert.Equal("applied", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 4587, Partial = true, Lines = new[] { new { marketplaceLineId = line2, quantity = 1, amountCents = 4587 } } }));
        await Drain();
        var p = (await Sent("refund.processed")).Select(e => e.GetProperty("data")).Single(x => x.GetProperty("sellerOrderNumber").GetString() == g2.SellerOrderNumber);
        Assert.Equal(3, Stripe.Refunds.Count);
        Assert.Equal(2, Stripe.Refunds.Count(r => r.AmountCents == 4587));                  // the first order's unit and this cancelled one: the buyer got exactly the portal amount
        Assert.Equal((4200L, 0L), (p.GetProperty("amountCents").GetInt64(), p.GetProperty("shippingCents").GetInt64()));
        Assert.Equal((0L, 387L), ((await Order(o2.Number)).Groups.Single().RefundedShippingCents, (await Order(o2.Number)).Groups.Single().RefundedTaxCents));
        // a cancellation that claims more than is left is cut down to it, never above
        Assert.Equal("skipped", await Portal("order.cancelled", o, g, new { RefundDueCents = 99999, Partial = false }, id: "pe-again"));   // fully returned already: nothing left to cancel
        Assert.Equal(14262, (await Order(o.Number)).Groups.Single().RefundedCents);
        Assert.Equal("skipped", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 99999, Partial = true }, id: "pe-big"));    // a partial cancel must name its units (review R6): not acted on, nothing moves
        Assert.Equal(4587, (await Order(o2.Number)).Groups.Single().RefundedCents);                                                          // unchanged: only the one cancelled unit was returned
    }

    private async Task<(MarketplaceOrder Order, OrderGroup Group, string LineId)> SecondThree()
    {
        await AddToCart("kiddush-cup-pewter", 3);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        return (o, o.Groups.Single(), o.Lines.Single().MarketplaceLineId);
    }

    [Fact]
    public async Task Refunds_that_do_not_add_up_or_name_a_line_we_do_not_have_are_not_acted_on()
    {
        var (o, g, line) = await Three();
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "bad-sum", AmountCents = 5000, ShippingCents = 0, Lines = new[] { new { marketplaceLineId = line, quantity = 1, amountCents = 4587 } } }));   // lines + shipping != amount
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "bad-line", AmountCents = 100, ShippingCents = 0, Lines = new[] { new { marketplaceLineId = "L-not-ours", quantity = 1, amountCents = 100 } } }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "too-many", AmountCents = 100, ShippingCents = 0, Lines = new[] { new { marketplaceLineId = line, quantity = 4, amountCents = 100 } } }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "bad-qty", AmountCents = 100, ShippingCents = 0, Lines = new[] { new { marketplaceLineId = line, quantity = 0, amountCents = 100 } } }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "shipping-too-big", AmountCents = 600, ShippingCents = 600 }));
        await Drain();
        Assert.Empty(Stripe.Refunds);
        Assert.Equal(0, (await Order(o.Number)).RefundedCents);
    }

    [Fact]
    public async Task A_refund_stripe_refuses_because_the_money_is_gone_keeps_its_reservation_and_one_that_never_moved_money_gives_the_counters_back()
    {
        var (o, g, line) = await Three();
        Stripe.Capture(o.PaymentIntentId!);
        var intent = Stripe.Intents.Single();
        await Stripe.RefundAsync(intent.Id, intent.CapturedCents, "dashboard", new Dictionary<string, string>(), default);   // someone refunded everything in the dashboard
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        await Drain();
        var after = await Order(o.Number);
        // the money is already gone (everything was refunded in the dashboard): this refund never reached the buyer, so its unit and amounts are given back (the portal is not told it was refunded);
        // the dashboard's money, confirmed by a fresh read at Stripe, takes the order's cap, so the same money still cannot be refunded twice (decision M3-28, review R2)
        Assert.Equal((0L, 0L, 0), (after.Groups.Single().RefundedCents, after.Groups.Single().RefundedTaxCents, after.Lines.Single().RefundedQuantity));
        Assert.Equal(after.PaidCents, after.RefundedCents);
        Assert.Equal("skipped", await RefundLines(o, g, "rf-again", line, 1, 4587));          // another try is refused by the order cap (skipped, an alarm), not sent to Stripe again
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "stripe.refund")));
        Assert.Equal("rejected", (await _k.Db(db => db.OrderRefunds.SingleAsync(r => r.Key == "portal:rf-1"))).Status);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "stripe.refund" && m.Status == "dead" && m.LastError == "stripe_charge_exceeds_amount")));

        // a refusal that moved no money (the intent is unknown to Stripe): everything is given back, the unit can be refunded again
        var (o2, g2, line2) = await SecondThree();
        await _k.Db(db => db.Orders.Where(x => x.Id == o2.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentIntentId, "pi_unknown_to_stripe")));
        Assert.Equal("applied", await RefundLines(o2, g2, "rf-2", line2, 1, 4587));
        await Drain();
        var back = (await Order(o2.Number)).Groups.Single();
        Assert.Equal((0L, 0L, 0L), (back.RefundedCents, back.RefundedTaxCents, back.RefundedShippingCents));
        Assert.Equal(0, (await Order(o2.Number)).Lines.Single().RefundedQuantity);
    }

    [Fact]
    public async Task A_full_cancel_that_arrives_before_an_earlier_refund_waits_for_it_and_both_are_applied_in_the_right_order()
    {
        var (o, g, line) = await Three();
        // the portal returned 4587 (rf-1) and then cancelled (due 9675 = 14262 - 4587), but the cancel arrives FIRST
        Assert.Equal("parked", await Portal("order.cancelled", o, g, new { RefundDueCents = 9675, Partial = false }, id: "pe-cancel"));
        await Drain();
        Assert.Empty(Stripe.Refunds);                                                        // nothing moved: the cancel did not run ahead of rf-1
        Assert.Equal(0, (await Order(o.Number)).Groups.Single().RefundedCents);
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        using (var scope = _k.F.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor>().ReplayAllParkedAsync());
        await Drain();
        Assert.Equal([4587L, 9675L], Stripe.Refunds.Select(r => r.AmountCents).Order().ToList());   // the buyer got exactly what the portal says, in total all of it
        var after = await Order(o.Number);
        Assert.Equal((14262L, 3), (after.Groups.Single().RefundedCents, after.Lines.Single().RefundedQuantity));
        var processed = (await Sent("refund.processed")).Select(e => e.GetProperty("data").GetProperty("amountCents").GetInt64()).Order().ToList();
        Assert.Equal([4200L, 8400L], processed);                                              // the portal expected 8400 for the cancel (it reported 8013 when the cancel ran first)
        // the portal's own figure, when it sends one, is used instead of inferring it
        var (o2, g2, _) = await SecondThree();
        Assert.Equal("parked", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 9675, Partial = false, PreviouslyRefundedCents = 4587 }, id: "pe-c2"));
        Assert.Equal("applied", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 14262, Partial = false, PreviouslyRefundedCents = 0 }, id: "pe-c3"));   // nothing earlier: runs at once
    }

    [Fact]
    public async Task Review_R6_inconsistent_partial_cancels_absurd_amounts_and_failed_refunds_are_handled_without_moving_wrong_money()
    {
        var (o, g, line) = await Three();
        // a partial cancel whose line amounts do not add up to what is due
        Assert.Equal("skipped", await Portal("order.cancelled", o, g, new { RefundDueCents = 4000, Partial = true, Lines = new[] { new { marketplaceLineId = line, quantity = 1, amountCents = 4587 } } }, id: "pe-bad"));
        // absurd figures are refused before any arithmetic: no exception, nothing reserved
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "huge", AmountCents = long.MaxValue, ShippingCents = 0 }));
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "huge2", AmountCents = 100, ShippingCents = 0, Lines = new[] { new { marketplaceLineId = line, quantity = 1, amountCents = long.MaxValue }, new { marketplaceLineId = line, quantity = 1, amountCents = long.MaxValue } } }));
        Assert.Equal("skipped", await Portal("order.cancelled", o, g, new { RefundDueCents = long.MaxValue, Partial = false }, id: "pe-huge"));
        Assert.Equal(0, (await Order(o.Number)).Groups.Single().RefundedCents);
        // a refund that Stripe accepted and later FAILED gives its counters back, so the unit can be refunded again
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        await Drain();
        var refund = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "portal:rf-1"));
        Assert.Equal("succeeded", refund.Status);
        var failed = Event("charge.refund.updated", refund.StripeRefundId!, null, 4587, extra: new { status = "failed" });
        Assert.Contains("applied", await (await PostStripe(failed)).Content.ReadAsStringAsync());
        var after = await Order(o.Number);
        Assert.Equal((0L, 0L, 0), (after.Groups.Single().RefundedCents, after.Groups.Single().RefundedTaxCents, after.Lines.Single().RefundedQuantity));
        Assert.Equal("failed", (await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "portal:rf-1"))).Status);
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("FAILED at Stripe"));
        // an unknown (dashboard) refund that failed is only logged; a succeeded update changes nothing
        Assert.Contains("unmatched", await (await PostStripe(Event("charge.refund.updated", "re_dashboard", null, 100, extra: new { status = "failed" }))).Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await PostStripe(Event("charge.refund.updated", refund.StripeRefundId!, null, 4587, extra: new { status = "succeeded" }))).Content.ReadAsStringAsync());
        // two dashboard refunds that bring Stripe's cumulative figure to the same number (one failed and was refunded again) have different keys: the second is recorded, not a unique-key failure
        Stripe.Capture(o.PaymentIntentId!);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("charge.refunded", "ch_a", null, 0, extra: new { payment_intent = o.PaymentIntentId, amount_refunded = 300L }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("charge.refunded", "ch_b", null, 0, extra: new { payment_intent = o.PaymentIntentId, amount_refunded = 100L }))).StatusCode);   // a refund failed: the cumulative figure went down (nothing to add)
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("charge.refunded", "ch_c", null, 0, extra: new { payment_intent = o.PaymentIntentId, amount_refunded = 500L }))).StatusCode);
        Assert.Equal(2, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
    }

    [Fact]
    public async Task A_portal_refund_that_no_longer_fits_after_a_dashboard_refund_is_cut_to_what_is_really_left_and_resent()
    {
        var (o, g, line) = await Three();                                                  // paid 14262
        await Stripe.RefundAsync(o.PaymentIntentId!, 6000, "dashboard", new Dictionary<string, string>(), default);   // not processed by our webhook yet (review R2)
        Assert.Equal("applied", await RefundLines(o, g, "rf-big", line, 2, 9174));       // the portal asks 9174: 6000 + 9174 > 14262
        await Drain();
        var refund = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "portal:rf-big"));
        Assert.Equal(("succeeded", 8262L), (refund.Status, refund.AmountCents));         // what is really left, sent under a new idempotency attempt
        Assert.Equal(14262, Stripe.Intents.Single().RefundedCents);                       // the buyer has all of it back: 6000 + 8262
        var after = await Order(o.Number);
        Assert.Equal(after.PaidCents, after.RefundedCents);                               // the counters follow the confirmed money
        Assert.Equal(8262, after.Groups.Single().RefundedCents);
        Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external" && r.AmountCents == 6000)));
        Assert.Single(await Sent("refund.processed"));                                    // the portal is told once, after the money went
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("was cut from 9174 to 8262"));
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
        Assert.Equal(100L, (await Order(o.Number)).Groups.Single(x => x.Id == other.Id).RefundedCents);
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
        Assert.Equal(500, refund.AmountCents);
        Assert.Single(await Sent("refund.processed"));
    }

    [Fact]
    public async Task Parallel_refunds_still_reserve_exactly_what_fits_by_amount_and_by_units()
    {
        var (o, g, line) = await Three();
        // by units: 8 refunds of 2 of the 3 units: one fits
        var byUnits = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RefundLines(o, g, $"rf-u{i}", line, 2, 9174, id: $"pe-u{i}")));
        Assert.Equal(1, byUnits.Count(r => r == "applied"));
        // by amount (no lines): 8 refunds of 4000 on what is left (14262 - 9174 = 5088): one fits
        var byAmount = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Portal("refund.issued", o, g, new { RefundId = $"rf-a{i}", AmountCents = 4000, ShippingCents = 0 }, id: $"pe-a{i}")));
        Assert.Equal(1, byAmount.Count(r => r == "applied"));
        await Drain();
        Assert.Equal(2, Stripe.Refunds.Count);
        var after = (await Order(o.Number)).Groups.Single();
        Assert.Equal(9174 + 4000, after.RefundedCents);
        Assert.True(after.RefundedCents <= after.TotalCents);
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
    public async Task Per_seller_order_disputes_are_on_by_default_so_a_multi_seller_dispute_goes_out_at_once_one_event_per_seller_order()
    {
        Assert.True(new Solvelt.Marketplace.Api.Infrastructure.PortalOptions().PerSellerOrderDisputes);          // the shipped default (decision M3-33)
        var o = await Paid();
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: o.TotalCents, id: "evt_on"))).Content.ReadAsStringAsync());
        await Drain();
        var created = await Sent("dispute.created");
        Assert.Equal(2, created.Count);                                                      // no hold, no flag to turn on
        Assert.Equal(2, created.Select(e => e.GetProperty("data").GetProperty("sellerOrderNumber").GetString()).Distinct().Count());
        Assert.Equal(6500, created.Sum(e => e.GetProperty("data").GetProperty("amountCents").GetInt64()));     // the full dispute: each seller order's sale (tax stays with the portal's staff flag)
        Assert.All(created, e => Assert.Equal("USD", e.GetProperty("data").GetProperty("currency").GetString()));
        // a won dispute is sent for EVERY seller order (the portal credits a seller only through its own event)
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostStripe(Dispute("charge.dispute.closed", o, "won", amount: o.TotalCents, id: "evt_on2"));
        await Drain();
        var closed = await Sent("dispute.closed");
        Assert.Equal(2, closed.Count);
        Assert.All(closed, e => Assert.Equal("won", e.GetProperty("data").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_multi_seller_dispute_is_held_until_the_portal_keys_disputes_per_seller_order_and_then_split_exactly()
    {
        var o = await Paid();
        _k.F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvelt.Marketplace.Api.Infrastructure.PortalOptions>>().Value.PerSellerOrderDisputes = false;   // the emergency switch / a portal without C10
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: o.TotalCents, id: "evt_d1"))).Content.ReadAsStringAsync());
        await Drain();
        Assert.Empty(await Sent("dispute.created"));                                       // C10 not merged: several events with one dispute id would book only the first, so they wait
        Assert.Equal(2, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "portal.payment-event" && m.Status == "pending" && m.DedupeKey!.StartsWith("payment:dispute:"))));
        using (var s0 = _k.F.Services.CreateScope()) Assert.Equal(0, await s0.ServiceProvider.GetRequiredService<OrderMaintenance>().ReleaseHeldDisputesAsync());   // flag off: nothing is released
        _k.F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvelt.Marketplace.Api.Infrastructure.PortalOptions>>().Value.PerSellerOrderDisputes = true;
        using (var s1 = _k.F.Services.CreateScope()) Assert.Equal(2, await s1.ServiceProvider.GetRequiredService<OrderMaintenance>().ReleaseHeldDisputesAsync());
        await Drain();
        var created = await Sent("dispute.created");
        Assert.Equal(2, created.Count);                                                    // one per seller order of the payment
        Assert.Equal(2, created.Select(e => e.GetProperty("eventId").GetString()).Distinct().Count());
        var datas = created.Select(e => e.GetProperty("data")).ToList();
        Assert.All(datas, d => { Assert.Equal("dp_1", d.GetProperty("disputeId").GetString()); Assert.Equal("needs_response", d.GetProperty("status").GetString()); Assert.Equal(o.Number, d.GetProperty("marketplaceOrderId").GetString()); Assert.True(d.TryGetProperty("createdAt", out _)); });
        // the seller orders hold items + shipping (tax is the marketplace's): 4700 + 1800 = 6500 of the 6917 paid
        Assert.Equal(6500, datas.Sum(d => d.GetProperty("amountCents").GetInt64()));
        Assert.Equal(4700, datas.Single(d => d.GetProperty("sellerOrderNumber").GetString() == JudaicaGroup(o).SellerOrderNumber).GetProperty("amountCents").GetInt64());
        Assert.Single(datas, d => d.TryGetProperty("feeCents", out var f) && f.ValueKind == JsonValueKind.Number);   // the portal's default dispute fee applies once; the others say 0
        // the same Stripe event again changes nothing
        await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: o.TotalCents, id: "evt_d1"));
        await Drain();
        Assert.Equal(2, (await Sent("dispute.created")).Count);
        // evidence update, then closed won / lost map to updated / closed
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostStripe(Dispute("charge.dispute.updated", o, "under_review", amount: o.TotalCents, id: "evt_d2"));
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostStripe(Dispute("charge.dispute.closed", o, "won", amount: o.TotalCents, id: "evt_d3"));
        await Drain();
        Assert.Equal(2, (await Sent("dispute.updated")).Count);
        var closed = await Sent("dispute.closed");
        Assert.Equal(2, closed.Count);
        Assert.All(closed, e => Assert.Equal("won", e.GetProperty("data").GetProperty("status").GetString()));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L), created[0].GetProperty("data").GetProperty("evidenceDueBy").GetDateTimeOffset());
    }

    // ---- C10 shares (decision M3-26) ----

    [Fact]
    public void Dispute_shares_follow_the_charged_totals_are_capped_at_what_each_seller_order_sold_and_the_cut_goes_to_those_with_room()
    {
        // 2-seller split: proportional to the charged totals, floor, the remainder to the largest group
        Assert.Equal([2169L, 831L], StripeEventProcessor.AllocateDispute(3000, [4999, 1918], [4700, 1800]));
        // a full dispute: each share hits its cap (the tax that cannot be placed stays with the portal's staff flag)
        Assert.Equal([4700L, 1800L], StripeEventProcessor.AllocateDispute(6917, [4999, 1918], [4700, 1800]));
        // the cap binds on one: what it cuts goes to the group with room, so the shares add up to min(amount, sum of sales)
        Assert.Equal([1000L, 5000L], StripeEventProcessor.AllocateDispute(6000, [5000, 5000], [1000, 9000]));
        Assert.Equal([50L, 50L], StripeEventProcessor.AllocateDispute(200, [100, 100], [50, 50]));
        // degenerate input is all zero, never negative
        Assert.Equal([0L, 0L], StripeEventProcessor.AllocateDispute(0, [100, 100], [50, 50]));
        Assert.Equal([0L, 0L], StripeEventProcessor.AllocateDispute(-5, [100, 100], [50, 50]));
        Assert.Equal([0L, 0L], StripeEventProcessor.AllocateDispute(100, [0, 0], [50, 50]));
        Assert.Equal([0L, 100L], StripeEventProcessor.AllocateDispute(100, [100, 100], [0, 500]));
        // the invariants over many shapes
        var rnd = new Random(7);
        for (var n = 0; n < 2000; n++)
        {
            var k = rnd.Next(1, 5);
            var totals = Enumerable.Range(0, k).Select(_ => (long)rnd.Next(1, 20000)).ToList();
            var sales = totals.Select(t => Math.Max(0L, t - rnd.Next(0, 3000))).ToList();
            var amount = rnd.Next(1, 40000);
            var shares = StripeEventProcessor.AllocateDispute(amount, totals, sales);
            Assert.All(shares.Zip(sales), z => Assert.InRange(z.First, 0, z.Second));
            Assert.Equal(Math.Min(amount, sales.Sum()), shares.Sum());
        }
    }

    [Fact]
    public async Task A_partial_dispute_on_two_seller_orders_is_split_by_charged_totals_with_the_fee_split_the_same_way_and_a_rejected_share_does_not_stop_the_other()
    {
        var o = await Paid();
        _k.F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvelt.Marketplace.Api.Infrastructure.PortalOptions>>().Value.PerSellerOrderDisputes = true;
        // the portal refuses one seller order's share (exceeds_sale) and takes the other
        var refused = JudaicaGroup(o).SellerOrderNumber;
        _onPaymentEvent = c => c.BodyText.Contains(refused!) ? FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"exceeds_sale\"}") : null;
        var body = Dispute("charge.dispute.created", o, "needs_response", amount: 3000, id: "evt_p1");
        body = body.Replace("\"status\":\"needs_response\"", "\"status\":\"needs_response\",\"balance_transactions\":[{\"fee\":1500}]");
        await PostStripe(body);
        await Drain();
        var datas = (await Sent("dispute.created")).Select(e => e.GetProperty("data")).ToList();
        Assert.Equal(2, datas.Count);
        Assert.Equal(3000, datas.Sum(d => d.GetProperty("amountCents").GetInt64()));                       // proportional to the charged totals, adds up exactly
        Assert.Equal(1500, datas.Sum(d => d.GetProperty("feeCents").GetInt64()));                          // the fee split the same way
        await PostStripe(Dispute("charge.dispute.updated", o, "under_review", amount: o.TotalCents, id: "evt_p2", dp: "dp_1"));   // a full dispute: the tax cannot be placed, so an info line says so (no personal data)
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("unallocated-dispute flag") && l.Contains(o.Number) && !l.Contains("@"));
        var sold = o.Groups.ToDictionary(g => g.SellerOrderNumber!, g => g.ItemsCents + g.ShippingCents);
        Assert.All(datas, d => Assert.InRange(d.GetProperty("amountCents").GetInt64(), 0, sold[d.GetProperty("sellerOrderNumber").GetString()!]));
        // the refused share is dead (a person sees it); the other was delivered on its own
        var states = await _k.Db(db => db.OutboxMessages.Where(m => m.DedupeKey!.StartsWith("payment:dispute:")).Select(m => new { m.DedupeKey, m.Status }).ToListAsync());
        Assert.Equal(1, states.Count(x => x.Status == "dead" && x.DedupeKey!.EndsWith(refused!)));
        Assert.Equal(1, states.Count(x => x.Status == "delivered"));
    }

    [Fact]
    public async Task A_created_that_arrives_after_a_later_event_is_still_sent_once_and_nothing_reopens_a_closed_dispute_and_same_second_events_have_a_stable_order()
    {
        var (o, g, _) = await Three();
        var createdBody = Dispute("charge.dispute.created", o, "needs_response", amount: 5000, id: "evt_c");
        _k.F.Clock.Advance(TimeSpan.FromSeconds(30));
        var fundsBody = Dispute("charge.dispute.funds_withdrawn", o, "needs_response", amount: 5000, id: "evt_f");
        // the later event arrives first: it is sent (the portal upserts) with the amount, and the created that follows is NOT dropped (review R4)
        Assert.Contains("applied", await (await PostStripe(fundsBody)).Content.ReadAsStringAsync());
        Assert.Contains("applied", await (await PostStripe(createdBody)).Content.ReadAsStringAsync());
        await Drain();
        Assert.Single(await Sent("dispute.created"));
        var updated = Assert.Single(await Sent("dispute.updated")).GetProperty("data");
        Assert.True(updated.GetProperty("amountCents").GetInt64() > 0);                    // the first event for the seller order carries the amount
        // closed, then a stale created: nothing reopens a closed dispute
        _k.F.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.closed", o, "won", amount: 5000, id: "evt_w"))).Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: 5000, id: "evt_c2"))).Content.ReadAsStringAsync());
        // two updates of the SAME second: the larger event id wins whatever the arrival order
        var (o2, _, _) = await SecondThree();
        _k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        var a = Dispute("charge.dispute.updated", o2, "under_review", amount: 5000, id: "evt_a", dp: "dp_tie");
        var b = Dispute("charge.dispute.updated", o2, "needs_response", amount: 5000, id: "evt_b", dp: "dp_tie");
        Assert.Contains("applied", await (await PostStripe(b)).Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await PostStripe(a)).Content.ReadAsStringAsync());   // same second, smaller id: older
        Assert.Equal("evt_b", await _k.Db(db => db.DisputeStates.Where(d => d.DisputeId == "dp_tie").Select(d => d.LastEventId).SingleAsync()));
    }

    [Fact]
    public async Task Parallel_events_of_one_dispute_never_lose_the_state_and_every_dispute_event_carries_the_orders_currency()
    {
        var (o, _, _) = await Three();
        var bodies = new List<string>();
        for (var i = 0; i < 8; i++) { _k.F.Clock.Advance(TimeSpan.FromSeconds(1)); bodies.Add(Dispute("charge.dispute.updated", o, "under_review", amount: 5000, id: $"evt_p{i}", dp: "dp_par")); }
        var results = await Task.WhenAll(bodies.AsEnumerable().Reverse().Select(b => PostStripe(b)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var state = await _k.Db(db => db.DisputeStates.AsNoTracking().SingleAsync(d => d.DisputeId == "dp_par"));
        Assert.Equal("evt_p7", state.LastEventId);                                            // the newest one won, however they interleaved
        // a Stripe dispute in another currency is still sent in the ORDER's currency (the portal refuses a mismatch with 422 currency_mismatch)
        var eur = Event("charge.dispute.created", "dp_eur", null, 5000, currency: "eur", id: "evt_eur", extra: new { payment_intent = o.PaymentIntentId, status = "needs_response", reason = "fraudulent" });
        await PostStripe(eur);
        await Drain();
        var sent = (await Sent("dispute.created")).Select(e => e.GetProperty("data")).Where(d => d.GetProperty("disputeId").GetString() == "dp_eur").ToList();
        Assert.Single(sent);
        Assert.Equal("USD", sent[0].GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Dispute_states_are_purged_90_days_after_a_closed_dispute_and_180_after_an_open_one()
    {
        await Login();
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db =>
        {
            db.DisputeStates.AddRange(
                new DisputeState { DisputeId = "dp_closed_old", Closed = true, LastEventId = "e", UpdatedAt = now.AddDays(-91) },
                new DisputeState { DisputeId = "dp_closed_new", Closed = true, LastEventId = "e", UpdatedAt = now.AddDays(-89) },
                new DisputeState { DisputeId = "dp_open_mid", Closed = false, LastEventId = "e", UpdatedAt = now.AddDays(-120) },
                new DisputeState { DisputeId = "dp_open_old", Closed = false, LastEventId = "e", UpdatedAt = now.AddDays(-181) });
            await db.SaveChangesAsync();
            return 0;
        });
        using var scope = _k.F.Services.CreateScope();
        Assert.Equal(2, await DisputeStateMaintenance.PurgeAsync(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), scope.ServiceProvider.GetRequiredService<TimeProvider>()));
        Assert.Equal(["dp_closed_new", "dp_open_mid"], (await _k.Db(db => db.DisputeStates.Select(d => d.DisputeId).ToListAsync())).Order().ToList());
    }

    [Fact]
    public async Task A_single_seller_dispute_goes_out_at_once_and_nothing_follows_a_closed_one_and_older_events_are_dropped()
    {
        var (o, g, _) = await Three();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: 14000, id: "evt_s1"));
        await Drain();
        var created = Assert.Single(await Sent("dispute.created"));                       // one seller order: no C10 needed, no hold
        Assert.Equal(g.SellerOrderNumber, created.GetProperty("data").GetProperty("sellerOrderNumber").GetString());
        Assert.Equal(13100, created.GetProperty("data").GetProperty("amountCents").GetInt64());   // 14000 charged back, but the seller order sold 12600 + 500 = 13100 (tax excluded): the share is capped there (M3-26; was 12859 by proportion)
        // an update that is OLDER than what we already sent (Stripe delivers out of order) is dropped
        var older = Dispute("charge.dispute.updated", o, "under_review", amount: 14000, id: "evt_s0");
        var closedBody = Dispute("charge.dispute.closed", o, "lost", amount: 14000, id: "evt_s2");
        _k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        var newer = Dispute("charge.dispute.updated", o, "under_review", amount: 14000, id: "evt_s1b");
        Assert.Contains("applied", await (await PostStripe(newer)).Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await PostStripe(older)).Content.ReadAsStringAsync());       // created earlier than the update just sent
        Assert.Contains("ignored", await (await PostStripe(closedBody)).Content.ReadAsStringAsync());   // created before the update just sent: stale, dropped
        await Drain();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("applied", await (await PostStripe(Dispute("charge.dispute.closed", o, "lost", amount: 14000, id: "evt_s3"))).Content.ReadAsStringAsync());
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("ignored", await (await PostStripe(Dispute("charge.dispute.updated", o, "under_review", amount: 14000, id: "evt_s4"))).Content.ReadAsStringAsync());   // nothing after closed
        await Drain();
        Assert.Single(await Sent("dispute.closed"));
        Assert.Single(await Sent("dispute.updated"));
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

    // ---- survivors of the review: each protection and each cap alone, and the tax remainder with a non-exact split ----

    private async Task<(Guid OrderId, Guid GroupId)> HandmadeOrder(long paid, long groupTotal, long groupTax, int qty = 3, long shipping = 0, long shippingTax = 0)
    {
        var order = new MarketplaceOrder
        {
            Id = Guid.NewGuid(), Number = "MP-20261104-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), AccountId = Guid.NewGuid(), Status = "placed", Currency = "USD",
            PaidAt = _k.F.Clock.GetUtcNow(), PaidCents = paid, TotalCents = paid, ItemsCents = groupTotal - groupTax, TaxCents = groupTax, CreatedAt = _k.F.Clock.GetUtcNow(), UpdatedAt = _k.F.Clock.GetUtcNow(),
            BuyerName = "x", RelayEmail = "x", ShipName = "x", ShipLine1 = "x", ShipCity = "x", ShipPostalCode = "x", ShipCountry = "US", PaymentIntentId = "pi_hand_" + Guid.NewGuid().ToString("N")[..8],
        };
        var group = new OrderGroup { Id = Guid.NewGuid(), OrderId = order.Id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), DeliveryKind = "carrier", ItemsCents = groupTotal - groupTax - shipping, ShippingCents = shipping, ShippingTaxCents = shippingTax, TaxCents = groupTax, TotalCents = groupTotal, SellerOrderNumber = "SO-HAND" };
        order.Groups.Add(group);
        order.Lines.Add(new OrderLine { Id = Guid.NewGuid(), GroupId = group.Id, MarketplaceLineId = order.Number + "-1", SellerId = group.SellerId, BrandId = group.BrandId, Quantity = qty, UnitPriceCents = (groupTotal - groupTax - shipping) / qty, LineTotalCents = groupTotal - groupTax - shipping, TaxCents = groupTax - shippingTax, ListingId = Guid.NewGuid(), VariantId = Guid.NewGuid() });
        await _k.Db(async db => { db.Orders.Add(order); await db.SaveChangesAsync(); return 0; });
        return (order.Id, group.Id);
    }

    [Fact]
    public async Task A_tax_of_100_refunded_as_three_thirds_returns_33_33_and_34_and_never_more_than_100()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 100);   // 900 net + 100 tax, 3 units
        var lineId = (await _k.Db(db => db.OrderLines.AsNoTracking().SingleAsync(l => l.GroupId == groupId))).MarketplaceLineId;
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        var taxes = new List<long>();
        foreach (var (key, amount) in new[] { ("a", 333L), ("b", 333L), ("c", 334L) })
        {
            Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, amount, 0, [new PortalRefundLine(lineId, 1)], null, false, false, key, "t", key), false, default));
            taxes.Add((await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == key))).TaxCents);
        }
        Assert.Equal([33L, 33L, 34L], taxes);                                                        // floor, floor, the remainder
        Assert.Equal(100, taxes.Sum());
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 1, 0, [], null, false, false, "d", "t", null), false, default));
    }

    [Fact]
    public async Task The_seller_order_cap_and_the_order_cap_each_hold_alone()
    {
        await Login();
        // seller-order cap alone: the order was paid far more than this seller order's total (another seller order holds the rest)
        var (o1, g1) = await HandmadeOrder(paid: 5000, groupTotal: 1000, groupTax: 0);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReservePortalAsync(new PortalRefund(o1, g1, 1001, 0, [], null, false, false, "g-over", "t", "g"), false, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o1, g1, 1000, 0, [], null, false, false, "g-fits", "t", "g2"), false, default));
        // order cap alone: the seller order claims to hold more than the order was paid (a corrupted row): the order's own cap still stops it
        var (o2, g2) = await HandmadeOrder(paid: 500, groupTotal: 1000, groupTax: 0);
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 501, 0, [], null, false, false, "o-over", "t", "o"), false, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 500, 0, [], null, false, false, "o-fits", "t", "o2"), false, default));
        Assert.Equal((500L, 500L), ((await Order((await _k.Db(db => db.Orders.AsNoTracking().SingleAsync(x => x.Id == o2))).Number)).RefundedCents, 500L));
    }

    [Fact]
    public async Task The_raw_refund_reservation_has_its_own_total_cap()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 0);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReserveAsync(orderId, groupId, 1001, 0, "raw-over", "t", false, null, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(orderId, groupId, 600, 0, "raw-1", "t", false, null, default));
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReserveAsync(orderId, null, 401, 0, "raw-2", "t", false, null, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(orderId, null, 400, 0, "raw-3", "t", false, null, default));
    }

    // ---- C8 switch on: shipping tax is the portal's own field, so it comes back with the shipping, proportionally (decision M3-15) ----

    [Fact]
    public async Task With_per_group_shipping_on_the_tax_on_shipping_is_refunded_proportionally_with_shipping_and_the_last_takes_the_remainder()
    {
        await Login();
        // seller order: 3 units, items 3000, line tax 300, shipping 500, tax on shipping 45 => total 3845; the line carries ONLY its own tax (C8 on: nothing folded in)
        var (orderId, groupId) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        var line = await _k.Db(db => db.OrderLines.AsNoTracking().SingleAsync(l => l.GroupId == groupId));
        Assert.Equal(300, line.TaxCents);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        // refund 200 of the 500 shipping (no lines): shipping tax floor(45 x 200 / 500) = 18 comes back with it
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 218, 200, [], null, false, false, "s1", "t", "s1"), perGroupShipping: true, default));
        var r1 = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "s1"));
        Assert.Equal((218L, 200L, 18L, 18L), (r1.AmountCents, r1.ShippingCents, r1.TaxCents, r1.ShippingTaxCents));
        // the other 300 of the shipping is the last: it returns the remaining 27, exactly 45 in all
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 327, 300, [], null, false, false, "s2", "t", "s2"), perGroupShipping: true, default));
        var r2 = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "s2"));
        Assert.Equal((27L, 27L), (r2.TaxCents, r2.ShippingTaxCents));
        var g = await _k.Db(db => db.OrderGroups.AsNoTracking().SingleAsync(x => x.Id == groupId));
        Assert.Equal((500L, 45L), (g.RefundedShippingCents, g.RefundedShippingTaxCents));
        // a full cancel of what is left then returns the line tax only (300), the shipping tax is already back
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 3300, 0, [], null, true, true, "s3", "t", null), perGroupShipping: true, default));
        var r3 = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(x => x.Key == "s3"));
        Assert.Equal((3300L, 300L, 0L), (r3.AmountCents, r3.TaxCents, r3.ShippingTaxCents));
        Assert.Equal(3845, (await _k.Db(db => db.OrderGroups.AsNoTracking().SingleAsync(x => x.Id == groupId))).RefundedCents);
        // with the switch OFF (shipping tax folded into the line) the same shipping refund backs out no shipping tax of its own
        var (o2, g2) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 200, 200, [], null, false, false, "off", "t", "off"), perGroupShipping: false, default));
        Assert.Equal(0, (await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == "off"))).ShippingTaxCents);
    }

    // ---- review C2 / C3 / survivors M19, M21-M23 (C8 refund semantics, brief N8) ----

    private async Task<OrderRefund> Refund(string key) => await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Key == key));
    private async Task<OrderGroup> GroupOf(Guid id) => await _k.Db(db => db.OrderGroups.AsNoTracking().SingleAsync(x => x.Id == id));

    [Fact]
    public async Task The_shipping_tax_is_never_counted_when_it_does_not_fit_inside_the_amount_and_never_more_than_is_left()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        // C2: 200 of shipping and an amount of 200: an older portal that does not send the shipping tax; the inferred 18 does not fit, so it is NOT refunded and NOT counted
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 200, 200, [], null, false, false, "c2", "t", "c2"), true, default));
        var c2 = await Refund("c2");
        Assert.Equal((0L, 0L), (c2.TaxCents, c2.ShippingTaxCents));
        var g = await GroupOf(groupId);
        Assert.Equal((0L, 0L), (g.RefundedTaxCents, g.RefundedShippingTaxCents));                     // before the fix: tax 0 but 18 counted, and never returned later
        // the portal's explicit figure is used as sent; more than what is left is cut to what is left (45 - 0)
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 218, 200, [], null, false, false, "x1", "t", "x1", ShippingTaxCents: 18), true, default));
        Assert.Equal((18L, 18L), ((await Refund("x1")).TaxCents, (await Refund("x1")).ShippingTaxCents));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 127, 100, [], null, false, false, "x2", "t", "x2", ShippingTaxCents: 99), true, default));   // 99 claimed, 27 left
        Assert.Equal((27L, 27L), ((await Refund("x2")).TaxCents, (await Refund("x2")).ShippingTaxCents));
        Assert.Equal(45, (await GroupOf(groupId)).RefundedShippingTaxCents);
    }

    [Fact]
    public async Task A_refund_of_an_amount_without_lines_takes_the_shipping_tax_out_before_prorating_the_line_tax()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        // C3: 1318 = items 1000 + line tax 100 + shipping 200 + shipping tax 18 (the lines hold 3000 + 300): the line tax is 100 and the total tax 118 (before: 133, 15 cents too much)
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 1318, 200, [], null, false, false, "c3", "t", "c3", ShippingTaxCents: 18), true, default));
        var c3 = await Refund("c3");
        Assert.Equal((118L, 18L), (c3.TaxCents, c3.ShippingTaxCents));
        Assert.Equal(1318 - 200 - 118, c3.AmountCents - c3.ShippingCents - c3.TaxCents);               // refund.processed's items part: 1000
        // the same refund inferred (an older portal that sends no shipping tax) comes out the same
        var (o2, g2) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 1318, 200, [], null, false, false, "c3b", "t", "c3b"), true, default));
        Assert.Equal((118L, 18L), ((await Refund("c3b")).TaxCents, (await Refund("c3b")).ShippingTaxCents));
    }

    [Fact]
    public async Task When_the_shipping_tax_is_inferred_the_last_shipping_refund_takes_the_remainder_of_an_uneven_split()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        // 45 on a 333 / 167 split of the shipping: floor(45 x 333 / 500) = 29, then the last takes 16 (a plain proration would give floor(45 x 167 / 500) = 15 and lose a cent)
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 362, 333, [], null, false, false, "u1", "t", "u1"), true, default));
        Assert.Equal(29, (await Refund("u1")).ShippingTaxCents);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 183, 167, [], null, false, false, "u2", "t", "u2"), true, default));
        Assert.Equal(16, (await Refund("u2")).ShippingTaxCents);
        Assert.Equal(45, (await GroupOf(groupId)).RefundedShippingTaxCents);
    }

    [Fact]
    public async Task A_stripe_refusal_that_moved_no_money_gives_the_shipping_tax_counter_back_too()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 3845, groupTotal: 3845, groupTax: 345, qty: 3, shipping: 500, shippingTax: 45);
        using (var scope = _k.F.Services.CreateScope())
            Assert.Equal(ReserveOutcome.Reserved, await scope.ServiceProvider.GetRequiredService<RefundService>().ReservePortalAsync(new PortalRefund(orderId, groupId, 218, 200, [], null, false, false, "m22", "t", "m22", ShippingTaxCents: 18), true, default));
        var before = await GroupOf(groupId);
        Assert.Equal((218L, 200L, 18L, 18L), (before.RefundedCents, before.RefundedShippingCents, before.RefundedTaxCents, before.RefundedShippingTaxCents));
        Stripe.RejectRefundWith = "api_key_expired";                       // nothing moved at Stripe
        await Drain();
        var after = await GroupOf(groupId);
        Assert.Equal((0L, 0L, 0L, 0L), (after.RefundedCents, after.RefundedShippingCents, after.RefundedTaxCents, after.RefundedShippingTaxCents));   // every counter is back, the shipping tax one included
        Assert.Equal("rejected", (await Refund("m22")).Status);
    }

    [Fact]
    public async Task A_portal_refund_event_with_the_c8_switch_on_uses_its_explicit_shipping_tax_end_to_end()
    {
        var (o, g, _) = await Three();                                     // the order was priced and ingested with the switch OFF; the refund arrives with it ON
        _k.F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvelt.Marketplace.Api.Infrastructure.PortalOptions>>().Value.PerGroupShipping = true;
        await _k.Db(db => db.OrderGroups.Where(x => x.Id == g.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShippingTaxCents, 44)));
        // amount = shipping 200 + shipping tax 18 (no lines); the portal states both
        Assert.Equal("applied", await Portal("refund.issued", o, g, new { RefundId = "n8-1", AmountCents = 218, ShippingCents = 200, ShippingTaxCents = 18, TaxCents = 18 }));
        await Drain();
        Assert.Equal(218, Assert.Single(Stripe.Refunds).AmountCents);       // the buyer gets exactly the portal's amount
        var d = Assert.Single(await Sent("refund.processed")).GetProperty("data");
        Assert.Equal((0L, 200L), (d.GetProperty("amountCents").GetInt64(), d.GetProperty("shippingCents").GetInt64()));   // tax-exclusive, as before
        var ag = await GroupOf(g.Id);
        Assert.Equal((200L, 18L, 18L), (ag.RefundedShippingCents, ag.RefundedShippingTaxCents, ag.RefundedTaxCents));
        // an amount that does not add up with the stated shipping tax is not acted on
        Assert.Equal("skipped", await Portal("refund.issued", o, g, new { RefundId = "n8-bad", AmountCents = 400, ShippingCents = 100, ShippingTaxCents = 9, Lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 1, amountCents = 100 } } }));
    }

    // ---- review R8: survivors ----

    [Fact]
    public async Task Parallel_single_unit_refunds_of_one_seller_order_never_refund_more_units_than_it_has_and_never_more_money()
    {
        var (o, g, line) = await Three();                                                    // 3 units
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RefundLines(o, g, $"par-{i}", line, 1, 4587, id: $"pe-par-{i}")));
        Assert.Equal(3, results.Count(r => r == "applied"));                                 // only three units exist (the rest: skipped, an alarm), whatever the interleaving
        var after = await Order(o.Number);
        Assert.Equal(3, after.Lines.Single().RefundedQuantity);
        Assert.True(after.Groups.Single().RefundedCents <= after.Groups.Single().TotalCents);
        Assert.Equal(1162, after.Groups.Single().RefundedTaxCents);                          // the tax of three units, exactly once
        Assert.Equal(3, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Key.StartsWith("portal:par-"))));
    }

    [Fact]
    public async Task A_refund_of_an_amount_without_lines_prorates_the_line_tax_and_the_last_one_takes_the_remainder_and_no_tax_means_no_tax()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 100);       // 900 net + 100 tax
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 400, 0, [], null, false, false, "g1", "t", "g1"), false, default));
        Assert.Equal(40, (await Refund("g1")).TaxCents);                                                  // floor(100 x 400 / 1000)
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 600, 0, [], null, false, false, "g2", "t", "g2"), false, default));
        Assert.Equal(60, (await Refund("g2")).TaxCents);                                                  // the rest: exactly 100 in all
        Assert.Equal(100, (await GroupOf(groupId)).RefundedTaxCents);
        var (o2, g2) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 0);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 333, 0, [], null, false, false, "z1", "t", "z1"), false, default));
        Assert.Equal(0, (await Refund("z1")).TaxCents);
    }

    [Fact]
    public async Task An_explicit_portal_tax_is_used_but_never_above_what_the_amount_holds_or_what_tax_is_left()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 100);
        using var scope = _k.F.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RefundService>();
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 400, 0, [], 25, false, false, "e1", "t", "e1"), false, default));
        Assert.Equal(25, (await Refund("e1")).TaxCents);                                                  // the portal's figure, not our 40
        // clamp 1: more than the amount can hold (shipping 90 of 100 leaves 10)
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(orderId, groupId, 100, 0, [], 80, false, false, "e2", "t", "e2"), false, default));
        Assert.Equal(75, (await Refund("e2")).TaxCents);                                                  // 80 asked, 75 left: never above what is left (clamp 2)
        var (o2, g2) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 500, shipping: 400);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReservePortalAsync(new PortalRefund(o2, g2, 450, 400, [], 300, false, false, "e3", "t", "e3"), false, default));
        Assert.Equal(50, (await Refund("e3")).TaxCents);                                                  // 300 asked, but only 450 - 400 shipping = 50 of the amount is not shipping (clamp 1 alone)
    }

    [Fact]
    public async Task A_refusal_with_the_code_charge_already_refunded_is_judged_by_a_fresh_read_not_by_the_code()
    {
        await Login();
        var (orderId, groupId) = await HandmadeOrder(paid: 1000, groupTotal: 1000, groupTax: 100);
        using (var scope = _k.F.Services.CreateScope())
            Assert.Equal(ReserveOutcome.Reserved, await scope.ServiceProvider.GetRequiredService<RefundService>().ReservePortalAsync(new PortalRefund(orderId, groupId, 400, 0, [], null, false, false, "ca1", "t", "ca1"), false, default));
        Stripe.RejectRefundWith = "charge_already_refunded";                  // the code says "all gone", Stripe's own books say nothing was refunded
        await Drain();
        var g = await GroupOf(groupId);
        Assert.Equal((0L, 0L), (g.RefundedCents, g.RefundedTaxCents));         // nothing moved, so everything is given back (the old code kept the reservation forever)
        Assert.Equal("rejected", (await Refund("ca1")).Status);
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
    }

    [Fact]
    public async Task Only_held_multi_seller_dispute_events_are_released_and_nothing_else_that_waits_in_the_outbox()
    {
        await Login();
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db =>
        {
            var w = new Solvelt.Marketplace.Api.Outbox.OutboxWriter(db, _k.F.Clock);
            w.EnqueuePaymentEvent("dispute:held", "dispute.created", new { x = 1 }, TimeSpan.FromDays(3650));      // held: far away, a dispute
            w.EnqueuePaymentEvent("dispute:soon", "dispute.created", new { x = 2 }, TimeSpan.FromMinutes(5));      // a dispute, but due soon (a retry): not touched
            w.EnqueuePaymentEvent("refund-processed:far", "refund.processed", new { x = 3 }, TimeSpan.FromDays(3650));   // far away but not a dispute: not touched
            await db.SaveChangesAsync();
            return 0;
        });
        _k.F.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Solvelt.Marketplace.Api.Infrastructure.PortalOptions>>().Value.PerSellerOrderDisputes = true;
        using (var scope = _k.F.Services.CreateScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrderMaintenance>().ReleaseHeldDisputesAsync());
        var rows = await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey!.StartsWith("payment:")).Select(m => new { m.DedupeKey, m.NextAttemptAt }).ToListAsync());
        Assert.True(rows.Single(r => r.DedupeKey == "payment:dispute:held").NextAttemptAt <= now.AddMinutes(1));
        Assert.True(rows.Single(r => r.DedupeKey == "payment:dispute:soon").NextAttemptAt > now.AddMinutes(4));
        Assert.True(rows.Single(r => r.DedupeKey == "payment:refund-processed:far").NextAttemptAt > now.AddDays(3000));
    }

    // ---- independent review of 820b369..49a7006 (Q1-Q5, decision M3-34) ----

    private async Task<OutboxResult> RunRefundHandler(string key, int attempt)
    {
        using var scope = _k.F.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().OfType<StripeRefundHandler>().Single();
        return await handler.HandleAsync(new OutboxMessage { Type = RefundService.StripeRefundType, Payload = JsonSerializer.Serialize(new RefundService.RefundPayload(key, attempt)) }, default);
    }

    [Fact]
    public async Task Q1_a_refusal_while_one_of_our_refunds_is_in_flight_finishes_that_refund_and_counts_nothing_twice()
    {
        var (o, g, line) = await Three();                                                    // paid 14262
        Stripe.TimeoutAfterExecuteNext = true;                                               // refund A reaches Stripe and is executed, but the answer is lost
        Assert.Equal("applied", await RefundLines(o, g, "rf-A", line, 1, 4587));
        await Drain();
        Assert.Equal("reserved", (await Refund("portal:rf-A")).Status);                      // our side still thinks it is waiting for a retry
        Assert.Equal(4587, Stripe.Intents.Single().RefundedCents);                           // ... but the money has left
        Stripe.RejectRefundWith = "charge_disputed";                                         // refund B is refused for an unrelated reason
        Assert.Equal("applied", await RefundLines(o, g, "rf-B", line, 1, 4587));
        await Drain();
        Assert.Equal("succeeded", (await Refund("portal:rf-A")).Status);                     // A is finished by its refund_key (it used to be released, or counted again as a dashboard refund)
        Assert.Equal("rejected", (await Refund("portal:rf-B")).Status);
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));   // nothing was booked twice
        var after = await Order(o.Number);
        Assert.Equal((4587L, 4587L, 1), (after.RefundedCents, after.Groups.Single().RefundedCents, after.Lines.Single().RefundedQuantity));   // exactly A's unit and money; B's are back
        Assert.Single(await Sent("refund.processed"));                                       // the portal is told about A once
        _k.F.Clock.Advance(TimeSpan.FromMinutes(5));
        await Drain();                                                                       // A's own retry finds it done
        Assert.Single(await Sent("refund.processed"));
        Assert.Equal(4587, Stripe.Intents.Single().RefundedCents);
    }

    [Fact]
    public async Task Q2_an_older_message_of_a_cut_refund_does_nothing_and_an_idempotency_error_is_looked_at_again_never_a_rejection()
    {
        var (o, g, line) = await Three();
        await Stripe.RefundAsync(o.PaymentIntentId!, 6000, "dashboard", new Dictionary<string, string>(), default);        // a dashboard refund our webhook has not seen
        Assert.Equal("applied", await RefundLines(o, g, "rf-big", line, 2, 9174));
        Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-big", 0)).Outcome);       // attempt 0 is refused and CUT to 8262; attempt 1 is queued
        var cut = await Refund("portal:rf-big");
        Assert.Equal((8262L, 1, "reserved"), (cut.AmountCents, cut.Attempt, cut.Status));
        var sentBefore = Stripe.Refunds.Count;
        // the outbox redelivers attempt 0 (at-least-once): it must not send the NEW amount under the OLD key, nor reject anything
        Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-big", 0)).Outcome);
        Assert.Equal(sentBefore, Stripe.Refunds.Count);
        Assert.Equal("reserved", (await Refund("portal:rf-big")).Status);
        await Drain();                                                                       // attempt 1 goes out
        Assert.Equal(("succeeded", 8262L), ((await Refund("portal:rf-big")).Status, (await Refund("portal:rf-big")).AmountCents));
        Assert.Equal(14262, Stripe.Intents.Single().RefundedCents);
        // an idempotency error says "same key, other call": retried later, the reservation stays
        var (o2, g2, line2) = await SecondThree();
        Stripe.RejectRefundWith = "idempotency_key_in_use";
        Assert.Equal("applied", await RefundLines(o2, g2, "rf-idem", line2, 1, 4587));
        var r = await RunRefundHandler("portal:rf-idem", 0);
        Assert.Equal(OutboxOutcome.Retry, r.Outcome);
        Assert.Equal("reserved", (await Refund("portal:rf-idem")).Status);
        await Drain();
        Assert.Equal("succeeded", (await Refund("portal:rf-idem")).Status);                 // and it goes through on the next try
    }

    [Fact]
    public async Task Q3_a_cut_refund_books_a_plain_amount_with_its_tax_prorated_and_gives_the_units_back_so_a_later_release_is_exact()
    {
        var (o, g, line) = await Three();                                                    // 3 units, tax 1162; 2 units = 9174 with tax 774
        await Stripe.RefundAsync(o.PaymentIntentId!, 6000, "dashboard", new Dictionary<string, string>(), default);
        Assert.Equal("applied", await RefundLines(o, g, "rf-big", line, 2, 9174, shipping: 0));
        Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-big", 0)).Outcome);      // cut to 8262
        var row = await Refund("portal:rf-big");
        var group = await GroupOf(g.Id);
        var l = (await Order(o.Number)).Lines.Single();
        var newTax = 774L * 8262 / 9174;                                                     // floor, the same proration as a goodwill amount
        Assert.Equal((8262L, newTax, 0L, 0L, null), (row.AmountCents, row.TaxCents, row.ShippingCents, row.ShippingTaxCents, row.PartsJson));
        Assert.Equal((8262L, newTax, 0L, 0L), (group.RefundedCents, group.RefundedTaxCents, group.RefundedShippingCents, group.RefundedShippingTaxCents));   // the group's books equal the row: nothing left over from the old reservation
        Assert.Equal((0, 0L), (l.RefundedQuantity, l.RefundedTaxCents));                     // the units are given back
        await Drain();
        var processed = Assert.Single(await Sent("refund.processed")).GetProperty("data");
        Assert.Equal(8262 - newTax, processed.GetProperty("amountCents").GetInt64());        // the portal's net amount is the cut amount without ITS tax
    }

    [Fact]
    public async Task Q4_the_external_row_of_a_refusal_is_keyed_by_the_refund_and_its_send_not_by_the_cumulative_figure()
    {
        var (o, g, line) = await Three();
        await Stripe.RefundAsync(o.PaymentIntentId!, 14262, "dashboard", new Dictionary<string, string>(), default);       // everything refunded outside
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        await Drain();
        var external = await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Reason == "external"));
        Assert.Equal("external:portal:rf-1:0", external.Key);
        Assert.DoesNotContain("14262", external.Key);
    }

    [Fact]
    public async Task Q5_the_mail_of_a_failed_order_whose_money_was_already_returned_says_so_only_when_nothing_could_go_back()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        _onOrder = _ => FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"invalid_order\",\"errors\":[{\"index\":0,\"code\":\"product_inactive\",\"message\":\"x\"}]}");
        await Pay(number, intent, total);
        await Stripe.RefundAsync(intent, total, "dashboard", new Dictionary<string, string>(), default);                  // all of it was refunded in the dashboard before our refund ran
        await Drain();
        var mail = Assert.Single(_k.Sink.Sent, m => m.Subject.Contains("couldn't place"));
        Assert.Contains("already been returned", mail.TextBody);
    }

    [Fact]
    public async Task Q5b_a_failed_orders_refund_refused_for_an_unrelated_reason_while_money_can_still_go_back_does_not_claim_it_was_already_returned()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        _onOrder = _ => FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"invalid_order\",\"errors\":[{\"index\":0,\"code\":\"product_inactive\",\"message\":\"x\"}]}");
        Stripe.RejectRefundWith = "charge_disputed";                                         // our refund is refused, but nothing was refunded anywhere: all of it can still go back
        await Pay(number, intent, total);
        await Drain();
        Assert.DoesNotContain(_k.Sink.Sent, m => m.Subject.Contains("couldn't place"));      // no "already been returned" (it was not), and no "is being refunded" (it is not)
        Assert.Equal("rejected", (await _k.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Reason == "order_failed"))).Status);
        Assert.Equal(0, (await Order(number)).RefundedCents);                                // given back: a person can reserve it again
    }

    // ---- independent review of 341c655..f890812 (M1, M2, L3, L4; decision M3-36) ----

    [Fact]
    public async Task M1_a_slow_worker_overtaken_by_a_parallel_one_does_not_cut_or_release_the_refund_a_second_time()
    {
        var (o, g, line) = await Three();
        await Stripe.RefundAsync(o.PaymentIntentId!, 6000, "dashboard", new Dictionary<string, string>(), default);
        Assert.Equal("applied", await RefundLines(o, g, "rf-race", line, 2, 9174));
        // worker 1 is refused by Stripe; before it takes the order lock, worker 2 (the redelivery after a lease expiry) does the whole refusal and cut
        Stripe.OnRefusal = async () => Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-race", 0)).Outcome);
        Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-race", 0)).Outcome);
        var cut = await Refund("portal:rf-race");
        Assert.Equal((8262L, 1, "reserved"), (cut.AmountCents, cut.Attempt, cut.Status));
        var after = await Order(o.Number);
        Assert.Equal(6000L + 8262, after.RefundedCents);                                     // the dashboard money and the cut refund, each once
        Assert.Equal(8262L, after.Groups.Single().RefundedCents);
        Assert.Equal(0, after.Lines.Single().RefundedQuantity);                              // the cut refund is a plain amount: its units were given back once
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == "refund:portal:rf-race:1")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task M2_a_refund_Stripe_lists_as_failed_or_canceled_is_not_money_and_is_given_back(string listedAs)
    {
        var (o, g, line) = await Three();
        Stripe.TimeoutAfterExecuteNext = true;
        Assert.Equal("applied", await RefundLines(o, g, "rf-A", line, 1, 4587));
        await Drain();
        Assert.Equal("reserved", (await Refund("portal:rf-A")).Status);
        Stripe.SetListedStatus("portal:rf-A", listedAs);                                     // Stripe's list shows it as failed or canceled
        Stripe.RejectRefundWith = "charge_disputed";                                         // and the retry is refused
        Assert.Equal(OutboxOutcome.Dead, (await RunRefundHandler("portal:rf-A", 0)).Outcome);   // dead-lettered for a person, with the counters given back
        Assert.Equal("rejected", (await Refund("portal:rf-A")).Status);                      // it was NOT finished as if the money had left
        var after = await Order(o.Number);
        Assert.Equal((0L, 0L, 0), (after.RefundedCents, after.Groups.Single().RefundedCents, after.Lines.Single().RefundedQuantity));
        Assert.Empty(await Sent("refund.processed"));
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
    }

    [Fact]
    public async Task M2_a_refund_Stripe_lists_as_pending_is_waited_for_not_finished_and_not_released()
    {
        var (o, g, line) = await Three();
        Stripe.TimeoutAfterExecuteNext = true;
        Assert.Equal("applied", await RefundLines(o, g, "rf-A", line, 1, 4587));
        await Drain();
        Stripe.SetListedStatus("portal:rf-A", "pending");
        Stripe.RejectRefundWith = "charge_disputed";
        var r = await RunRefundHandler("portal:rf-A", 0);
        Assert.Equal(OutboxOutcome.Retry, r.Outcome);
        Assert.Equal("reserved", (await Refund("portal:rf-A")).Status);
        var after = await Order(o.Number);
        Assert.Equal((4587L, 1), (after.RefundedCents, after.Lines.Single().RefundedQuantity));   // still reserved
        Assert.Empty(await Sent("refund.processed"));
    }

    [Fact]
    public async Task M2_a_refund_that_failed_later_is_found_by_its_refund_key_when_its_id_was_never_stored()
    {
        var (o, g, line) = await Three();
        Stripe.TimeoutAfterExecuteNext = true;                                               // Stripe made the refund, we never saw its id
        Assert.Equal("applied", await RefundLines(o, g, "rf-A", line, 1, 4587));
        await Drain();
        var row = await Refund("portal:rf-A");
        Assert.Equal(("reserved", null), (row.Status, row.StripeRefundId));
        var listed = (await Stripe.ListRefundsAsync(o.PaymentIntentId!, default)).Single();
        var evt = "{\"id\":\"evt_ru_m2\",\"type\":\"charge.refund.updated\",\"data\":{\"object\":{\"id\":\"" + listed.Id + "\",\"object\":\"refund\",\"status\":\"failed\",\"amount\":4587,\"metadata\":{\"refund_key\":\"portal:rf-A\"}}}}";
        Assert.True((await PostStripe(evt)).IsSuccessStatusCode);
        Assert.Equal("failed", (await Refund("portal:rf-A")).Status);
        var after = await Order(o.Number);
        Assert.Equal((0L, 0), (after.RefundedCents, after.Lines.Single().RefundedQuantity));      // its counters are back
    }

    [Fact]
    public async Task L4_only_idempotency_key_in_use_is_waited_for_any_other_idempotency_error_dies_visibly_and_releases_nothing()
    {
        var (o, g, line) = await Three();
        Assert.Equal("applied", await RefundLines(o, g, "rf-idem", line, 1, 4587));
        Stripe.RejectRefundWith = "idempotency_error";
        var r = await RunRefundHandler("portal:rf-idem", 0);
        Assert.Equal(OutboxOutcome.Dead, r.Outcome);
        Assert.Equal("reserved", (await Refund("portal:rf-idem")).Status);                  // nothing was given back: money may have moved
        Assert.Equal(4587L, (await Order(o.Number)).RefundedCents);
    }

    // ---- portal C14: order.cancelled names the refunds it nets out; predecessorDead ----

    [Fact]
    public async Task C14_a_cancel_waits_for_every_refund_it_names_and_then_runs_with_both_applied_exactly_once()
    {
        var (o, g, line) = await Three();
        // the cancel names rf-1 (4587 returned before it) but arrives first: parked, nothing moves
        Assert.Equal("parked", await Portal("order.cancelled", o, g, new { RefundDueCents = 9675, Partial = false, RefundIdsAccounted = new[] { "rf-1" }, PreviouslyRefundedCents = 4587 }, id: "pe-c14"));
        await Drain();
        Assert.Empty(Stripe.Refunds);
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        using (var scope = _k.F.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor>().ReplayAllParkedAsync());
        await Drain();
        Assert.Equal([4587L, 9675L], Stripe.Refunds.Select(r => r.AmountCents).Order().ToList());
        Assert.Equal(14262L, (await Order(o.Number)).Groups.Single().RefundedCents);
    }

    [Fact]
    public async Task C14_a_cancel_whose_named_refunds_are_all_applied_runs_at_once_whatever_the_figures_say_and_empty_ids_mean_nothing_before()
    {
        var (o, g, line) = await Three();
        Assert.Equal("applied", await RefundLines(o, g, "rf-1", line, 1, 4587));
        // the ids decide: rf-1 is applied, so the cancel runs; a wrong previouslyRefundedCents is an alarm in the log, not a stop
        Assert.Equal("applied", await Portal("order.cancelled", o, g, new { RefundDueCents = 9675, Partial = false, RefundIdsAccounted = new[] { "rf-1" }, PreviouslyRefundedCents = 1 }, id: "pe-ok"));
        var (o2, g2, _) = await SecondThree();
        Assert.Equal("applied", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 14262, Partial = false, RefundIdsAccounted = Array.Empty<string>(), PreviouslyRefundedCents = 0 }, id: "pe-empty"));
        await Drain();
        Assert.Equal([4587L, 9675L, 14262L], Stripe.Refunds.Select(r => r.AmountCents).Order().ToList());
        // a named refund we do not hold keeps the cancel waiting even when the figure says nothing was returned: the ids are what the portal accounted for
        var (o3, g3, _) = await SecondThree();
        Assert.Equal("parked", await Portal("order.cancelled", o3, g3, new { RefundDueCents = 14262, Partial = false, RefundIdsAccounted = new[] { "rf-unknown" }, PreviouslyRefundedCents = 0 }, id: "pe-ghost"));
    }

    [Fact]
    public async Task C14_predecessorDead_on_an_order_event_is_applied_and_leaves_a_reconcile_mark_on_the_stored_event()
    {
        var (o, g, line) = await Three();
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number },
            ["refundId"] = "rf-dead", ["amountCents"] = 4587, ["shippingCents"] = 0, ["lines"] = new[] { new { marketplaceLineId = line, quantity = 1, amountCents = 4587 } } };
        var body = Body("pe-dead", "refund.issued", _k.F.Clock.GetUtcNow(), data).Insert(1, "\"predecessorDead\":true,");
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", JsonDocument.Parse(json).RootElement.GetProperty("status").GetString());
        var row = await _k.Db(db => db.PortalInboundEvents.AsNoTracking().SingleAsync(e => e.Id == "pe-dead"));
        Assert.Contains("predecessor_dead", row.Note);
        // and it stays deduped on its signed id
        var (_, again) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal("duplicate", JsonDocument.Parse(again).RootElement.GetProperty("status").GetString());
        Assert.Single(await _k.Db(db => db.OrderRefunds.Where(r => r.Key == "portal:rf-dead").ToListAsync()));
    }

    // ---- review of 1725a68..c859113 (H1 and following; decision M3-40) ----

    [Fact]
    public async Task H1_a_pending_refund_of_ours_is_money_in_flight_and_never_becomes_an_external_row_when_another_refund_is_refused()
    {
        var (o, g, line) = await Three();                                                    // paid 14262
        Stripe.TimeoutAfterExecuteNext = true;                                               // A reaches Stripe, the answer is lost
        Assert.Equal("applied", await RefundLines(o, g, "rf-A", line, 1, 4587));
        await Drain();
        Stripe.SetListedStatus("portal:rf-A", "pending");                                    // Stripe lists A as pending: inside amount_refunded, not yet succeeded
        Stripe.RejectRefundWith = "charge_disputed";                                         // B is refused for an unrelated reason
        Assert.Equal("applied", await RefundLines(o, g, "rf-B", line, 1, 4587));
        await Drain();
        Assert.Equal("rejected", (await Refund("portal:rf-B")).Status);
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));   // A's 4587 is NOT an unknown dashboard refund
        var mid = await Order(o.Number);
        Assert.Equal((4587L, 4587L, 1), (mid.RefundedCents, mid.Groups.Single().RefundedCents, mid.Lines.Single().RefundedQuantity));   // A once, B given back
        // A finishes later (Stripe now says succeeded): still counted exactly once
        Stripe.SetListedStatus("portal:rf-A", "succeeded");
        _k.F.Clock.Advance(TimeSpan.FromMinutes(5));
        await Drain();
        Assert.Equal("succeeded", (await Refund("portal:rf-A")).Status);
        var done = await Order(o.Number);
        Assert.Equal((4587L, 1), (done.RefundedCents, done.Lines.Single().RefundedQuantity));
        Assert.Equal(0, await _k.Db(db => db.OrderRefunds.CountAsync(r => r.Reason == "external")));
    }

    [Fact]
    public async Task M3_a_failed_event_for_an_older_send_of_a_cut_refund_leaves_the_cut_amounts_alone_and_the_current_send_releases_exactly_them()
    {
        var (o, g, line) = await Three();
        await Stripe.RefundAsync(o.PaymentIntentId!, 6000, "dashboard", new Dictionary<string, string>(), default);        // a dashboard refund
        Assert.Equal("applied", await RefundLines(o, g, "rf-cut", line, 2, 9174));
        Assert.Equal(OutboxOutcome.Delivered, (await RunRefundHandler("portal:rf-cut", 0)).Outcome);                      // refused and CUT to 8262: attempt 1, reserved
        var cut = await Refund("portal:rf-cut");
        Assert.Equal((8262L, 1, "reserved", null), (cut.AmountCents, cut.Attempt, cut.Status, cut.StripeRefundId));
        var before = await Order(o.Number);

        string Failed(string id, int attempt) => "{\"id\":\"" + id + "\",\"type\":\"charge.refund.updated\",\"data\":{\"object\":{\"id\":\"re_old\",\"object\":\"refund\",\"status\":\"failed\",\"amount\":9174,\"metadata\":{\"refund_key\":\"portal:rf-cut\",\"refund_attempt\":\"" + attempt + "\"}}}}";
        // attempt 0's refund (never executed) is reported failed: it is not the 8262 that is reserved now
        Assert.Contains("ignored", await (await PostStripe(Failed("evt_old", 0))).Content.ReadAsStringAsync());
        var same = await Order(o.Number);
        Assert.Equal((before.RefundedCents, before.Groups.Single().RefundedCents), (same.RefundedCents, same.Groups.Single().RefundedCents));
        Assert.Equal("reserved", (await Refund("portal:rf-cut")).Status);
        // the current send failing releases exactly the cut amounts (never below zero)
        Assert.Contains("applied", await (await PostStripe(Failed("evt_now", 1))).Content.ReadAsStringAsync());
        Assert.Equal("failed", (await Refund("portal:rf-cut")).Status);
        var after = await Order(o.Number);
        Assert.Equal((6000L, 0L), (after.RefundedCents, after.Groups.Single().RefundedCents));                          // the dashboard money stays, the cut refund is fully given back
    }

    [Fact]
    public async Task M2_the_predecessor_dead_mark_survives_a_park_and_the_replay_and_always_comes_first()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter", 3);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);                                                    // paid, the ingest has NOT run: the seller orders are not stored yet
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = Guid.NewGuid(), sellerOrderNumber = "SO-X", marketplaceOrderId = number },
            ["refundId"] = "rf-park", ["amountCents"] = 500, ["shippingCents"] = 500 };
        var body = Body("pe-park-dead", "refund.issued", _k.F.Clock.GetUtcNow(), data).Insert(1, "\"predecessorDead\":true,");
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("parked", JsonDocument.Parse(json).RootElement.GetProperty("status").GetString());
        var first = await _k.Db(db => db.PortalInboundEvents.AsNoTracking().SingleAsync(e => e.Id == "pe-park-dead"));
        Assert.StartsWith(Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadMark, first.Note);
        await Drain();                                                                       // the ingest stores the seller orders and replays the parked event
        var replayed = await _k.Db(db => db.PortalInboundEvents.AsNoTracking().SingleAsync(e => e.Id == "pe-park-dead"));
        Assert.NotEqual("parked", replayed.Status);                                          // it was applied or skipped by the replay
        Assert.StartsWith(Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadMark, replayed.Note);   // the handler's note did not overwrite the mark
    }

    [Fact]
    public void M2_the_note_keeps_the_mark_first_and_never_exceeds_the_column()
    {
        var mark = Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadMark;
        var n = Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadNote(new string('x', 900));
        Assert.StartsWith(mark, n);
        Assert.Equal(500, n.Length);
        Assert.Equal(mark, Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadNote(null));
        Assert.Equal(Solvelt.Marketplace.Api.Portal.Webhooks.PortalEventProcessor.PredecessorDeadNote(mark), mark);          // not doubled on a second pass
    }
}
