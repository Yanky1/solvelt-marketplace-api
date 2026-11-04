using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class IngestTests : OrderFlowBase
{
    [Fact]
    public async Task A_paid_order_is_ingested_stored_per_seller_order_and_captured_once_per_seller_order()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        Assert.Equal("pending_payment", (await Order(number)).Status);
        await Pay(number, intent, total);
        var paid = await Order(number);
        Assert.Equal("paid", paid.Status);
        Assert.Equal(total, paid.PaidCents);
        Assert.Empty(Portal("orders"));                                   // nothing goes to the portal before the dispatcher runs
        await Drain();

        var placed = await Order(number);
        Assert.Equal("placed", placed.Status);
        Assert.All(placed.Groups, g => { Assert.NotNull(g.SellerOrderId); Assert.StartsWith("SO-2026-", g.SellerOrderNumber); });
        // the ingest body: ids only, our order number, the relay identity, and a total the buyer actually paid
        var call = Assert.Single(Portal("orders"));
        using var doc = JsonDocument.Parse(call.BodyText);
        var root = doc.RootElement;
        Assert.Equal(number, root.GetProperty("marketplaceOrderId").GetString());
        Assert.StartsWith("relay-", root.GetProperty("buyer").GetProperty("relayEmail").GetString());
        Assert.Equal("Jane B.", root.GetProperty("buyer").GetProperty("displayName").GetString());
        Assert.DoesNotContain("ing1@example.com", call.BodyText);
        Assert.All(root.GetProperty("lines").EnumerateArray(), l => { Assert.True(l.TryGetProperty("productId", out _)); Assert.False(l.TryGetProperty("sku", out _)); Assert.StartsWith(number + "-", l.GetProperty("marketplaceLineId").GetString()); });
        var lineSum = root.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("unitPriceCents").GetInt64() * l.GetProperty("quantity").GetInt64() + l.GetProperty("taxCents").GetInt64());
        var shipSum = root.GetProperty("shipping").EnumerateArray().Sum(s => s.GetProperty("amountCents").GetInt64());
        Assert.Equal(total, lineSum + shipSum);                           // shipping tax is folded into line tax, so the portal's total is what was charged
        Assert.Contains(paid.Groups, g => g.ShippingTaxCents > 0);       // (and this order really had tax on shipping)

        // payment.captured: one per seller order, after ingest, with our event id, the intent and the seller order's total
        var captured = Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() == "payment.captured").ToList();
        Assert.Equal(2, captured.Count);
        Assert.Equal(2, captured.Select(e => e.GetProperty("eventId").GetString()).Distinct().Count());
        foreach (var e in captured)
        {
            var d = e.GetProperty("data");
            var g = placed.Groups.Single(x => x.SellerOrderNumber == d.GetProperty("sellerOrderNumber").GetString());
            Assert.Equal("order", d.GetProperty("kind").GetString());
            Assert.Equal(number, d.GetProperty("marketplaceOrderId").GetString());
            Assert.Equal(intent, d.GetProperty("paymentRef").GetString());
            Assert.Equal(g.TotalCents, d.GetProperty("amountCents").GetInt64());
        }
        Assert.Equal(total, captured.Sum(e => e.GetProperty("data").GetProperty("amountCents").GetInt64()));
        // the cart was bought; the buyer is told
        Assert.Equal(0, await _k.Db(db => db.CartLines.CountAsync()));
        Assert.Contains(_k.Sink.Sent, m => m.To == "ing1@example.com" && m.Subject.Contains(number));
    }

    [Fact]
    public void Shipping_tax_is_allocated_to_lines_without_losing_or_inventing_a_cent()
    {
        foreach (var (tax, weights) in new[] { (44L, new long[] { 373, 100, 7 }), (1L, new long[] { 5, 5, 5 }), (999L, new long[] { 1, 0, 0 }), (7L, new long[] { 0, 0 }), (100L, new long[] { 3 }) })
        {
            var lines = weights.Select((w, i) => new OrderLine { MarketplaceLineId = "L" + i, TaxCents = w, LineTotalCents = 1000 }).ToList();
            var parts = OrderIngestHandler.AllocateShippingTax(tax, lines);
            Assert.Equal(tax, parts.Sum());
            Assert.All(parts, p => Assert.True(p >= 0));
        }
        Assert.Equal([0L, 0L], OrderIngestHandler.AllocateShippingTax(0, [new OrderLine { TaxCents = 5 }, new OrderLine { TaxCents = 5 }]));
    }

    [Fact]
    public async Task Replayed_and_parallel_webhooks_ingest_once()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Stripe.Capture(intent);
        var body = Event("payment_intent.succeeded", intent, number, total, id: "evt_same");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PostStripe(body)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await _k.Db(db => db.StripeEvents.CountAsync(e => e.Id == "evt_same")));
        Assert.Equal(1, await Outbox("order.ingest"));
        // a later replay of the same signed body, and a DIFFERENT event id for the same payment
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("payment_intent.succeeded", intent, number, total, id: "evt_other"))).StatusCode);
        Assert.Equal(1, await Outbox("order.ingest"));
        await Drain();
        Assert.Single(Portal("orders"));
        Assert.Single(Portal("payment-events"));
        Assert.Equal("placed", (await Order(number)).Status);
    }

    [Fact]
    public async Task An_ingest_retried_after_the_portal_failed_or_timed_out_yields_one_order_and_one_capture()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        // the portal is down for every attempt of the first delivery (the client makes 5), then answers 200 created:false as for a repeated id
        _onOrder = c => _orderCalls <= 5 ? throw new HttpRequestException("down") : Created(c, 200, false);
        await Pay(number, intent, total);
        await Drain();
        Assert.Equal("paid", (await Order(number)).Status);               // still waiting: nothing lost, nothing half done
        Assert.Empty(Portal("payment-events"));
        _k.F.Clock.Advance(TimeSpan.FromSeconds(31));
        await Drain();
        var order = await Order(number);
        Assert.Equal("placed", order.Status);
        Assert.Equal(6, Portal("orders").Count);
        Assert.Single(Portal("orders").Select(c => JsonDocument.Parse(c.BodyText).RootElement.GetProperty("marketplaceOrderId").GetString()).Distinct());   // always the same id
        Assert.Equal(Portal("orders").Count, Portal("orders").Select(c => c.Nonce).Distinct().Count());                                                       // but a fresh nonce each attempt
        Assert.Single(Portal("payment-events"));
        Assert.Single(_sellerOrders);
        // delivering the ingest message yet again (a lease that expired mid-flight) changes nothing
        await _k.Db(async db => { await db.OutboxMessages.Where(m => m.Type == "order.ingest").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.NextAttemptAt, _k.F.Clock.GetUtcNow())); return 0; });
        await Drain();
        Assert.Single(Portal("payment-events"));
        Assert.Equal("placed", (await Order(number)).Status);
    }

    [Fact]
    public async Task A_rejected_order_is_failed_refunded_in_full_once_and_the_buyer_is_told()
    {
        await Login();
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        _onOrder = _ => FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"invalid_order\",\"message\":\"x\",\"errors\":[{\"index\":0,\"code\":\"delivery_window_full\",\"message\":\"full\"}]}");
        await Pay(number, intent, total);
        await Drain();
        var order = await Order(number);
        Assert.Equal("failed", order.Status);
        Assert.Equal("delivery_window_full", order.FailureCode);
        Assert.Equal(total, order.RefundedCents);
        var refund = Assert.Single(Stripe.Refunds);
        Assert.Equal(total, refund.AmountCents);                          // everything goes back
        Assert.Equal(total, Stripe.Intents.Single().RefundedCents);
        Assert.Empty(Portal("payment-events"));                           // nothing was captured at the portal
        var mail = Assert.Single(_k.Sink.Sent, m => m.Subject.Contains("couldn't place"));
        Assert.Contains("delivery time", mail.TextBody);
        Assert.Contains("refunded", mail.TextBody);
        // replaying the Stripe event or the outbox changes nothing
        await PostStripe(Event("payment_intent.succeeded", intent, number, total, id: "evt_again"));
        await Drain();
        Assert.Single(Stripe.Refunds);
        Assert.Equal("failed", (await Order(number)).Status);
        // and no second refund can be reserved: the cap is what was paid
        using var scope = _k.F.Services.CreateScope();
        Assert.Equal(ReserveOutcome.ExceedsPaid, await scope.ServiceProvider.GetRequiredService<RefundService>().ReserveAsync(order.Id, null, 1, 0, "extra", "test", false, null, default));
    }

    [Fact]
    public async Task A_payment_that_does_not_match_the_priced_order_is_never_fulfilled_and_is_given_back()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Stripe.Capture(intent);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("payment_intent.succeeded", intent, number, total - 100))).StatusCode);   // Stripe says less was taken
        await Drain();
        var order = await Order(number);
        Assert.Equal("failed", order.Status);
        Assert.Equal("amount_mismatch", order.FailureCode);
        Assert.Equal(total, order.TotalCents);                            // the priced total is untouched
        Assert.Equal(total - 100, order.PaidCents);
        Assert.Equal(total - 100, order.RefundedCents);
        Assert.Empty(Portal("orders"));
    }

    [Fact]
    public async Task Events_in_any_order_leave_a_paid_order_paid_and_a_failed_attempt_only_leaves_a_decline_code()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        var failed = Event("payment_intent.payment_failed", intent, number, total, extra: new { last_payment_error = new { code = "card_declined" } });
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(failed)).StatusCode);
        var o = await Order(number);
        Assert.Equal(("pending_payment", "card_declined"), (o.Status, o.FailureCode));
        await Pay(number, intent, total);                                  // the buyer tried another card and it worked
        Assert.Equal("paid", (await Order(number)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("payment_intent.payment_failed", intent, number, total, extra: new { last_payment_error = new { code = "late_event" } }))).StatusCode);   // arrives late
        var after = await Order(number);
        Assert.Equal("paid", after.Status);
        Assert.Equal("card_declined", after.FailureCode);                  // a late failure does not even touch it
        await Drain();
        Assert.Equal("placed", (await Order(number)).Status);
    }

    [Fact]
    public async Task A_payment_on_a_superseded_order_is_still_honoured()
    {
        await Login();
        await AddToCart("challah-classic");
        var first = await Checkout();
        await Checkout(new { address = Address(), selections = new[] { new { sellerId = Mazel, brandId = ChallahCorner, speed = "overnight" } } });   // supersedes the first
        Assert.Equal("superseded", (await Order(first.Number)).Status);
        await Pay(first.Number, first.Intent, first.Total);               // the buyer paid the old tab
        await Drain();
        Assert.Equal("placed", (await Order(first.Number)).Status);
        Assert.Single(Portal("orders"));
    }

    [Fact]
    public async Task The_webhook_is_verified_with_its_own_secret_before_anything_is_read()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Stripe.Capture(intent);
        var body = Event("payment_intent.succeeded", intent, number, total);
        foreach (var secret in new[] { DevDefaults.PortalWebhookSecret, DevDefaults.PortalInboundSecret, DevDefaults.AdminApiKey, "whsec_wrong_wrong_wrong_wrong_wrong_0" })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostStripe(body, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostStripe(body, header: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostStripe(body, header: StripeSignature.Sign(body, DevDefaults.StripeWebhookSecret, _k.F.Clock.GetUtcNow().AddMinutes(-6)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostStripe(body + " ", header: StripeSignature.Sign(body, DevDefaults.StripeWebhookSecret, _k.F.Clock.GetUtcNow()))).StatusCode);   // body changed after signing
        Assert.Equal(HttpStatusCode.BadRequest, (await PostStripe("not json", DevDefaults.StripeWebhookSecret)).StatusCode);
        Assert.Equal("pending_payment", (await Order(number)).Status);
        Assert.Equal(0, await _k.Db(db => db.StripeEvents.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(body)).StatusCode);
        // an oversized body is refused before it is read
        var big = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhooks") { Content = new StringContent(new string('x', 262145)) };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _k.Http.SendAsync(big)).StatusCode);
    }

    [Fact]
    public async Task Events_that_are_not_ours_are_acknowledged_and_ones_for_an_order_we_have_not_saved_are_retried()
    {
        var res = await PostStripe(Event("payment_intent.succeeded", "pi_other", null, 500));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                   // another product on the same Stripe account: not ours
        Assert.Contains("unmatched", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("charge.dispute.created", "dp_1", null, 500))).StatusCode);   // unhandled types are stored and acknowledged
        var unknown = await PostStripe(Event("payment_intent.succeeded", "pi_x", "MP-20260101-ABCDEFGH", 500, id: "evt_unknown"));
        Assert.True((int)unknown.StatusCode >= 500);                      // 5xx: Stripe sends it again, and nothing was recorded
        Assert.Equal(0, await _k.Db(db => db.StripeEvents.CountAsync(e => e.Id == "evt_unknown")));
    }

    [Fact]
    public async Task An_event_naming_our_order_but_another_intent_matches_nothing()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        var res = await PostStripe(Event("payment_intent.succeeded", "pi_someone_elses", number, total));
        Assert.Contains("unmatched", await res.Content.ReadAsStringAsync());
        Assert.Equal("pending_payment", (await Order(number)).Status);
        _ = intent;
    }

    [Fact]
    public async Task A_portal_answer_that_does_not_match_our_groups_is_never_guessed_and_the_oversold_flag_is_kept()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        _onOrder = c => FakePortal.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { created = true, orders = new[] { new { sellerOrderId = Guid.NewGuid(), number = "SO-2026-000999", sellerId = Guid.NewGuid(), brandId = ShabbatTable, status = "New", oversold = false, totalCents = 1L, feeCents = 0L, netCents = 1L } } }));
        await Pay(number, intent, total);
        await Drain();
        Assert.Equal("paid", (await Order(number)).Status);               // still not placed
        Assert.Empty(Portal("payment-events"));
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "order.ingest" && m.Status == "dead" && m.LastError == "seller_order_mismatch")));

        // and an oversold seller order is stored and shown
        await _k.Db(db => db.OutboxMessages.Where(m => m.Type == "order.ingest").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.Attempts, 0)));
        _onOrder = null; _oversold = true;
        await Drain();
        var order = await Order(number);
        Assert.Equal("placed", order.Status);
        Assert.True(order.Groups.Single().Oversold);
    }

    // ---- refunds: the cap, the key and Stripe ----

    private async Task<(MarketplaceOrder Order, RefundService Svc, IServiceScope Scope)> PaidOrder()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var scope = _k.F.Services.CreateScope();
        return (await Order(number), scope.ServiceProvider.GetRequiredService<RefundService>(), scope);
    }

    [Fact]
    public async Task Refunds_never_exceed_what_was_paid_in_total_or_per_seller_order_even_in_parallel()
    {
        var (order, _, scope) = await PaidOrder();
        using var _ = scope;
        var g = order.Groups.OrderByDescending(x => x.TotalCents).First();
        var chunk = g.TotalCents * 6 / 10;
        // 8 parallel requests of 60 % of one seller order: exactly one fits, the rest are over the cap
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var s = _k.F.Services.CreateScope();
            try { return await s.ServiceProvider.GetRequiredService<RefundService>().ReserveAsync(order.Id, g.Id, chunk, 0, $"par-{i}", "test", false, null, default); }
            catch (DbUpdateException) { return ReserveOutcome.Duplicate; }
        }));
        Assert.Equal(1, outcomes.Count(o => o == ReserveOutcome.Reserved));
        Assert.Equal(7, outcomes.Count(o => o == ReserveOutcome.ExceedsPaid));
        var after = await Order(order.Number);
        Assert.Equal(chunk, after.RefundedCents);
        Assert.Equal(chunk, after.Groups.Single(x => x.Id == g.Id).RefundedCents);
        // the rest of that seller order fits, one cent more does not; the other seller order is unaffected by this one's cap
        using var s2 = _k.F.Services.CreateScope();
        var svc = s2.ServiceProvider.GetRequiredService<RefundService>();
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReserveAsync(order.Id, g.Id, g.TotalCents - chunk + 1, 0, "over", "test", false, null, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(order.Id, g.Id, g.TotalCents - chunk, 0, "rest", "test", false, null, default));
        var other = order.Groups.Single(x => x.Id != g.Id);
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReserveAsync(order.Id, other.Id, other.TotalCents + 1, 0, "other-over", "test", false, null, default));
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(order.Id, other.Id, other.TotalCents, 0, "other-all", "test", false, null, default));
        Assert.Equal(order.TotalCents, (await Order(order.Number)).RefundedCents);
        Assert.Equal(ReserveOutcome.ExceedsPaid, await svc.ReserveAsync(order.Id, null, 1, 0, "one-more", "test", false, null, default));   // the whole order is refunded: not a cent more
        Assert.Equal(ReserveOutcome.Duplicate, await svc.ReserveAsync(order.Id, null, 1, 0, "rest", "test", false, null, default));           // same key: the same refund
    }

    [Fact]
    public async Task A_refund_is_sent_to_stripe_once_with_its_key_and_reported_to_the_portal_once()
    {
        var (order, svc, scope) = await PaidOrder();
        using var _ = scope;
        var g = order.Groups.First();
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(order.Id, g.Id, 300, 100, "portal:rf_1", "damaged", true, "rf_1", default));
        await Drain();
        await Drain();
        var refund = Assert.Single(Stripe.Refunds);
        Assert.Equal(300, refund.AmountCents);
        var rows = await _k.Db(db => db.OrderRefunds.AsNoTracking().ToListAsync());
        Assert.Equal(("succeeded", refund.Id), (rows.Single().Status, rows.Single().StripeRefundId));
        var processed = Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() == "refund.processed").ToList();
        var e = Assert.Single(processed);
        var d = e.GetProperty("data");
        Assert.Equal(refund.Id, d.GetProperty("refundId").GetString());
        Assert.Equal(g.SellerOrderNumber, d.GetProperty("sellerOrderNumber").GetString());
        Assert.Equal(200, d.GetProperty("amountCents").GetInt64());       // items part
        Assert.Equal(100, d.GetProperty("shippingCents").GetInt64());
        // running the same outbox message again (a retry after a lost acknowledgement) neither refunds nor reports twice
        await _k.Db(db => db.OutboxMessages.Where(m => m.Type == "stripe.refund").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "pending").SetProperty(m => m.NextAttemptAt, _k.F.Clock.GetUtcNow())));
        await Drain();
        Assert.Single(Stripe.Refunds);
        Assert.Equal(1, Portal("payment-events").Count(c => c.BodyText.Contains("refund.processed")));
    }

    [Fact]
    public async Task A_refund_stripe_refuses_gives_its_reservation_back_and_waits_for_a_person_and_an_outage_is_retried()
    {
        var (order, svc, scope) = await PaidOrder();
        using var _ = scope;
        Stripe.FailNext = 1;
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(order.Id, null, 500, 0, "k-out", "test", false, null, default));
        await Drain();
        Assert.Empty(Stripe.Refunds);
        Assert.Equal("reserved", (await _k.Db(db => db.OrderRefunds.SingleAsync())).Status);   // still owed
        _k.F.Clock.Advance(TimeSpan.FromSeconds(31));
        await Drain();
        Assert.Single(Stripe.Refunds);                                    // the retry went through with the same key

        // Stripe says no (more than was captured, because of a refund made in its dashboard): the reservation is released and the message is dead
        Stripe.Capture(Stripe.Intents.Single().Id);
        var intent = Stripe.Intents.Single();
        await Stripe.RefundAsync(intent.Id, intent.CapturedCents - intent.RefundedCents, "dashboard", new Dictionary<string, string>(), default);
        Assert.Equal(ReserveOutcome.Reserved, await svc.ReserveAsync(order.Id, null, 700, 0, "k-no", "test", false, null, default));
        var before = (await Order(order.Number)).RefundedCents;
        await Drain();
        var rejected = await _k.Db(db => db.OrderRefunds.SingleAsync(r => r.Key == "k-no"));
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal(before - 700, (await Order(order.Number)).RefundedCents);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "stripe.refund" && m.Status == "dead" && m.LastError == "stripe_charge_exceeds_amount")));
    }

    [Fact]
    public async Task Stripe_webhook_retention_row_holds_no_payload()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Stripe.Capture(intent);
        await PostStripe(Event("payment_intent.succeeded", intent, number, total, extra: new { receipt_email = "secret-card-holder@example.com", payment_method = "pm_card_4242" }));
        var row = await _k.Db(db => db.StripeEvents.AsNoTracking().SingleAsync());
        Assert.Equal(("payment_intent.succeeded", "applied", intent), (row.Type, row.Outcome, row.ObjectId));
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("secret-card-holder") || l.Contains("pm_card_4242"));
    }

    [Fact]
    public async Task Pending_orders_expire_after_23_hours_release_their_key_and_cancel_the_intent_but_a_late_payment_still_counts()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        async Task<int> Expire() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<OrderMaintenance>().ExpirePendingAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromHours(22));
        Assert.Equal(0, await Expire());
        _k.F.Clock.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(1, await Expire());
        var o = await Order(number);
        Assert.Equal("expired", o.Status);
        Assert.Null(o.ActiveKey);
        Assert.True(Stripe.Intents.Single().Cancelled);
        Assert.Equal(0, await Expire());
        // the same cart can be checked out again: a new order and a new intent
        var again = await Checkout();
        Assert.NotEqual(number, again.Number);
        // a payment that still arrives for the expired order is honoured (money was taken)
        Stripe.Capture(intent);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("payment_intent.succeeded", intent, number, total))).StatusCode);
        Assert.Equal("paid", (await Order(number)).Status);
        // never-paid orders disappear after 30 days, paid ones never do
        _k.F.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await Expire());                                   // the second checkout was never paid either
        using var scope = _k.F.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrderMaintenance>().PurgeAbandonedAsync());
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Equal("paid", (await Order(number)).Status);
    }
}
