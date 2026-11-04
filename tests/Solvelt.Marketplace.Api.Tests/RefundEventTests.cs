using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Checkout;
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
        Assert.Equal("applied", await Portal("order.cancelled", o2, g2, new { RefundDueCents = 99999, Partial = true }, id: "pe-big"));    // claims more than is left: cut to what is left, never above
        Assert.Equal(14262, (await Order(o2.Number)).Groups.Single().RefundedCents);
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
        // the money is already gone: releasing the reservation would let the cap allow the same money to be refunded again, so it STAYS (a person resolves it)
        Assert.Equal((4587L, 387L, 1), (after.Groups.Single().RefundedCents, after.Groups.Single().RefundedTaxCents, after.Lines.Single().RefundedQuantity));
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
    public async Task A_multi_seller_dispute_is_held_until_the_portal_keys_disputes_per_seller_order_and_then_split_exactly()
    {
        var o = await Paid();
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

    [Fact]
    public async Task A_single_seller_dispute_goes_out_at_once_and_nothing_follows_a_closed_one_and_older_events_are_dropped()
    {
        var (o, g, _) = await Three();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        await PostStripe(Dispute("charge.dispute.created", o, "needs_response", amount: 14000, id: "evt_s1"));
        await Drain();
        var created = Assert.Single(await Sent("dispute.created"));                       // one seller order: no C10 needed, no hold
        Assert.Equal(g.SellerOrderNumber, created.GetProperty("data").GetProperty("sellerOrderNumber").GetString());
        Assert.Equal(12859, created.GetProperty("data").GetProperty("amountCents").GetInt64());   // 14000 x (12600 + 500) / 14262, rounded down: the seller share, tax excluded
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

    private async Task<(Guid OrderId, Guid GroupId)> HandmadeOrder(long paid, long groupTotal, long groupTax, int qty = 3)
    {
        var order = new MarketplaceOrder
        {
            Id = Guid.NewGuid(), Number = "MP-20261104-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), AccountId = Guid.NewGuid(), Status = "placed", Currency = "USD",
            PaidAt = _k.F.Clock.GetUtcNow(), PaidCents = paid, TotalCents = paid, ItemsCents = groupTotal - groupTax, TaxCents = groupTax, CreatedAt = _k.F.Clock.GetUtcNow(), UpdatedAt = _k.F.Clock.GetUtcNow(),
            BuyerName = "x", RelayEmail = "x", ShipName = "x", ShipLine1 = "x", ShipCity = "x", ShipPostalCode = "x", ShipCountry = "US", PaymentIntentId = "pi_hand_" + Guid.NewGuid().ToString("N")[..8],
        };
        var group = new OrderGroup { Id = Guid.NewGuid(), OrderId = order.Id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), DeliveryKind = "carrier", ItemsCents = groupTotal - groupTax, ShippingCents = 0, TaxCents = groupTax, TotalCents = groupTotal, SellerOrderNumber = "SO-HAND" };
        order.Groups.Add(group);
        order.Lines.Add(new OrderLine { Id = Guid.NewGuid(), GroupId = group.Id, MarketplaceLineId = order.Number + "-1", SellerId = group.SellerId, BrandId = group.BrandId, Quantity = qty, UnitPriceCents = (groupTotal - groupTax) / qty, LineTotalCents = groupTotal - groupTax, TaxCents = groupTax, ListingId = Guid.NewGuid(), VariantId = Guid.NewGuid() });
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
}
