using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Sola-paid phone orders on the portal's money events (the portal contract C19, decision V-49): <c>provider</c> on capture, refund and dispute events, Sola's references (at most 100 characters), a
/// <c>feeCents</c> on every Sola dispute, the new 422 answers dead-lettered and never retried, and Stripe left exactly as it was. Test and sandbox money only; nothing talks to Sola.
/// </summary>
public sealed class SolaProviderTests : OrderFlowBase
{
    public SolaProviderTests() : base(new Dictionary<string, string?> { ["Portal:PerGroupShipping"] = "false" }) { }

    private const string Ref = "10984432761";
    private int _portalEvent;

    private async Task<MarketplaceOrder> PaidOrder(bool sola, string? solaRef = Ref, bool drain = true)
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        if (sola) await _k.Db(async db => { await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(u => u.SetProperty(o => o.PaymentProvider, PaymentProviders.Sola).SetProperty(o => o.PaymentRef, solaRef)); return 0; });
        await Pay(number, intent, total);
        if (drain) await Drain();
        return await Order(number);
    }

    private List<JsonElement> Sent(string type) => Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() == type).ToList();

    private static MarketplaceOrder Fake(string provider, string? payRef) => new() { Number = "MP-1", Currency = "USD", PaymentIntentId = "pi_123", PaymentProvider = provider, PaymentRef = payRef, PaidAt = DateTimeOffset.Parse("2026-11-22T15:04:05Z") };

    private static string Json(object o) => JsonSerializer.Serialize(o, PortalJson.Options);

    // ---------- the shapes, per provider ----------

    [Fact]
    public void A_stripe_capture_is_exactly_what_it_always_was_and_a_sola_capture_names_sola_and_its_reference()
    {
        var stripe = JsonDocument.Parse(Json(SolaMoneyEvents.Capture(Fake("stripe", null), 4200, "SO-1")!)).RootElement;
        Assert.False(stripe.TryGetProperty("provider", out _));                                              // absent means Stripe (contract C19.1 rule 1): no website event changes
        Assert.Equal(("order", "MP-1", "SO-1", "pi_123", "USD", 4200L), (stripe.GetProperty("kind").GetString(), stripe.GetProperty("marketplaceOrderId").GetString(), stripe.GetProperty("sellerOrderNumber").GetString(), stripe.GetProperty("paymentRef").GetString(), stripe.GetProperty("currency").GetString(), stripe.GetProperty("amountCents").GetInt64()));
        var sola = JsonDocument.Parse(Json(SolaMoneyEvents.Capture(Fake("sola", Ref), 4200, "SO-1")!)).RootElement;
        Assert.Equal(("sola", Ref, "order"), (sola.GetProperty("provider").GetString(), sola.GetProperty("paymentRef").GetString(), sola.GetProperty("kind").GetString()));   // xRefNum, not the (never-used) Stripe intent
        Assert.DoesNotContain("pi_123", Json(SolaMoneyEvents.Capture(Fake("sola", Ref), 4200, "SO-1")!));
        Assert.Null(SolaMoneyEvents.ProviderField("stripe")); Assert.Equal("sola", SolaMoneyEvents.ProviderField("sola")); Assert.Null(SolaMoneyEvents.ProviderField(null));
    }

    [Fact]
    public void A_sola_refund_and_a_sola_dispute_carry_provider_sola_and_the_dispute_always_carries_its_fee()
    {
        var refund = JsonDocument.Parse(Json(SolaMoneyEvents.Refund("10984433110", "MP-20311", "A-1042", 2400, 0, "usd", "return")!)).RootElement;
        Assert.Equal(("sola", "10984433110", "MP-20311", "A-1042", 2400L, "USD"), (refund.GetProperty("provider").GetString(), refund.GetProperty("refundId").GetString(), refund.GetProperty("marketplaceOrderId").GetString(), refund.GetProperty("sellerOrderNumber").GetString(), refund.GetProperty("amountCents").GetInt64(), refund.GetProperty("currency").GetString()));
        var dispute = JsonDocument.Parse(Json(SolaMoneyEvents.Dispute("CB778812", "MP-20311", "A-1042", 5900, 1500, "usd", "needs_response", "fraudulent")!)).RootElement;
        Assert.Equal(("sola", "CB778812", 5900L, 1500L, "needs_response"), (dispute.GetProperty("provider").GetString(), dispute.GetProperty("disputeId").GetString(), dispute.GetProperty("amountCents").GetInt64(), dispute.GetProperty("feeCents").GetInt64(), dispute.GetProperty("status").GetString()));
        // a zero fee is still SENT: left out, the portal would charge its default of 1500
        Assert.Equal(0L, JsonDocument.Parse(Json(SolaMoneyEvents.Dispute("CB1", "MP-1", "A-1", 100, 0, "usd", "won")!)).RootElement.GetProperty("feeCents").GetInt64());
        Assert.Null(SolaMoneyEvents.Dispute("CB1", "MP-1", "A-1", 100, -1, "usd", "won"));                   // a negative fee is a bug in the sender
        Assert.True(SolaMoneyEvents.DisputeStatuses.SetEquals(["needs_response", "under_review", "won", "lost", "warning_closed"]));
        Assert.Equal(("dispute.created", "dispute.updated", "dispute.closed", "dispute.closed"), (SolaMoneyEvents.DisputeType("needs_response", true), SolaMoneyEvents.DisputeType("under_review", false), SolaMoneyEvents.DisputeType("won", false), SolaMoneyEvents.DisputeType("warning_closed", false)));
        Assert.True(SolaMoneyEvents.DisputeEventId(new string('x', 90), "needs_response", "SO-2026-000001").Length <= 100);   // an event id never exceeds the portal's limit
        Assert.NotEqual(SolaMoneyEvents.DisputeEventId(new string('x', 90), "won", "SO-1"), SolaMoneyEvents.DisputeEventId(new string('x', 90), "lost", "SO-1"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("10984432761", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(" 123", false)]
    [InlineData("12\n3", false)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345", false)]   // 101 characters: no order row can hold it (below), so the ingest theory above cannot seed it on PostgreSQL
    public void A_sola_reference_is_one_to_a_hundred_plain_characters(string? reference, bool valid) => Assert.Equal(valid, SolaMoneyEvents.IsValidSolaRef(reference));

    [Fact]
    public void The_reference_limit_is_a_hundred_characters_for_a_capture_a_refund_and_a_dispute_and_null_is_never_sent()
    {
        var ok = new string('9', 100); var long101 = new string('9', 101);
        Assert.True(SolaMoneyEvents.IsValidSolaRef(ok)); Assert.False(SolaMoneyEvents.IsValidSolaRef(long101)); Assert.False(SolaMoneyEvents.IsValidSolaRef(null));
        Assert.NotNull(SolaMoneyEvents.Capture(Fake("sola", ok), 1, "SO-1")); Assert.Null(SolaMoneyEvents.Capture(Fake("sola", long101), 1, "SO-1")); Assert.Null(SolaMoneyEvents.Capture(Fake("sola", null), 1, "SO-1"));
        Assert.NotNull(SolaMoneyEvents.Refund(ok, "MP-1", "A-1", 1, 0, "USD", null)); Assert.Null(SolaMoneyEvents.Refund(long101, "MP-1", "A-1", 1, 0, "USD", null));
        Assert.NotNull(SolaMoneyEvents.Dispute(ok, "MP-1", "A-1", 1, 0, "USD", "won")); Assert.Null(SolaMoneyEvents.Dispute(long101, "MP-1", "A-1", 1, 0, "USD", "won"));
        Assert.NotNull(SolaMoneyEvents.Capture(Fake("stripe", null), 1, "SO-1"));                            // a Stripe capture is never judged by the Sola limit
    }

    [Fact]
    public void Every_order_made_before_C19_is_a_stripe_order_by_the_column_default()
    {
        var p = _k.F.Services.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<MarketplaceDb>().Model.FindEntityType(typeof(MarketplaceOrder))!.FindProperty(nameof(MarketplaceOrder.PaymentProvider))!;
        Assert.Equal("stripe", p.GetDefaultValue());
        Assert.False(p.IsNullable);
        Assert.Equal(("stripe", "sola"), (PaymentProviders.Stripe, PaymentProviders.Sola));
        Assert.Equal(PaymentProviders.Stripe, new MarketplaceOrder().PaymentProvider);
    }

    // ---------- through the real ingest, per provider ----------

    [Fact]
    public async Task A_website_order_is_captured_as_before_with_no_provider_and_the_stripe_intent()
    {
        var o = await PaidOrder(sola: false);
        Assert.Equal(PaymentProviders.Stripe, o.PaymentProvider);
        var events = Sent("payment.captured");
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.False(e.GetProperty("data").TryGetProperty("provider", out _)); Assert.Equal(o.PaymentIntentId, e.GetProperty("data").GetProperty("paymentRef").GetString()); });
    }

    [Fact]
    public async Task A_sola_order_is_captured_once_per_seller_order_with_provider_sola_and_the_sola_reference_under_the_same_event_ids()
    {
        var o = await PaidOrder(sola: true);
        var events = Sent("payment.captured");
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            var d = e.GetProperty("data");
            Assert.Equal(("sola", Ref, "order", o.Number), (d.GetProperty("provider").GetString(), d.GetProperty("paymentRef").GetString(), d.GetProperty("kind").GetString(), d.GetProperty("marketplaceOrderId").GetString()));
        });
        Assert.Equal(o.Groups.Select(g => $"captured:{o.Number}:{g.SellerOrderNumber}").Order(), events.Select(e => e.GetProperty("eventId").GetString()!).Order());
        Assert.DoesNotContain(Portal("payment-events"), c => c.BodyText.Contains(o.PaymentIntentId!));        // the intent of a Sola order never reaches the portal
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_sola_order_without_a_usable_reference_is_never_sent_and_dies_where_a_person_sees_it(string? bad)
    {
        var o = await PaidOrder(sola: true, solaRef: bad);
        Assert.Equal("paid", o.Status);                                                                      // paid, but held back
        Assert.Empty(Portal("orders")); Assert.Empty(Portal("payment-events"));                              // the portal is not even asked
        var dead = await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.DedupeKey == "ingest:" + o.Number));
        Assert.Equal((OutboxStatus.Dead, "invalid_payment_ref"), (dead.Status, dead.LastError));
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains(o.Number) && l.Contains("Sola reference"));
    }

    [Fact]
    public async Task The_orders_reference_column_holds_at_most_one_hundred_characters_like_the_portals_limit()
    {
        await _k.Db(async db => { Assert.Equal(PaymentProviders.MaxSolaRefLength, db.Model.FindEntityType(typeof(MarketplaceOrder))!.FindProperty(nameof(MarketplaceOrder.PaymentRef))!.GetMaxLength()); return 0; });
    }

    // ---------- the answers ----------

    [Theory]
    [InlineData("invalid_provider")]
    [InlineData("provider_not_supported")]
    [InlineData("provider_mismatch")]
    [InlineData("invalid_payment_ref")]
    [InlineData("exceeds_sale")]
    public async Task A_422_for_a_sola_event_is_dead_lettered_at_once_with_the_code_only_and_never_retried(string code)
    {
        var calls = 0;
        _onPaymentEvent = _ => { calls++; return FakePortal.Json((HttpStatusCode)422, $"{{\"code\":\"{code}\",\"message\":\"nope\"}}"); };
        var o = await PaidOrder(sola: true);
        for (var i = 0; i < 3; i++) await Drain();                                                           // a retry would show as more calls
        Assert.Equal(2, calls);                                                                              // one attempt per seller order, no more
        var captured = await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == PaymentEventOutboxHandler.Type).ToListAsync());
        Assert.All(captured, m => Assert.Equal((OutboxStatus.Dead, $"portal_422_{code}", 1), (m.Status, m.LastError, m.Attempts)));
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains($"portal_422_{code}"));                             // the error code is logged ...
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains(Ref) || l.Contains("nope"));                  // ... the payload and the portal's message never
    }

    [Theory]
    [InlineData(404, "order_not_found")]
    [InlineData(409, "sale_not_recorded")]
    [InlineData(409, "concurrent_update")]
    public async Task The_retryable_answers_are_retried_with_backoff(int status, string code)
    {
        _onPaymentEvent = _ => FakePortal.Json((HttpStatusCode)status, $"{{\"code\":\"{code}\",\"message\":\"later\"}}");
        await PaidOrder(sola: true);
        var captured = await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == PaymentEventOutboxHandler.Type).ToListAsync());
        Assert.All(captured, m => Assert.Equal((OutboxStatus.Pending, 1), (m.Status, m.Attempts)));          // still pending, tried again later
    }

    [Theory]
    [InlineData(422, false)]
    [InlineData(404, true)]
    [InlineData(409, true)]
    [InlineData(401, false)]
    public void The_outbox_maps_the_portal_answers_per_the_contract(int status, bool retry)
        => Assert.Equal(retry ? OutboxOutcome.Retry : OutboxOutcome.Dead, PortalOutcome.From(new PortalResult<object>(status, null, new PortalError("c", "m"), 1) { Retryable = status is 404 or 409 }).Outcome);

    // ---------- a Sola order never touches Stripe ----------

    [Fact]
    public async Task A_refund_of_a_sola_order_goes_to_sola_never_to_stripe_and_refund_processed_goes_out_with_provider_sola_and_the_refunds_xrefnum()
    {
        var o = await PaidOrder(sola: true);
        var g = o.Groups.Single(x => x.ItemsCents == 4200);
        var body = Body($"pe-{++_portalEvent:D3}", "refund.issued", _k.F.Clock.GetUtcNow(), new Dictionary<string, object?>
        {
            ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number },
            ["refundId"] = "rf-sola-1", ["amountCents"] = 1000, ["shippingCents"] = 0,
            ["lines"] = new[] { new { marketplaceLineId = o.Lines.First(l => l.GroupId == g.Id).MarketplaceLineId, quantity = 1, amountCents = 1000 } },
        });
        var (status, _) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        var before = Stripe.Refunds.Count;
        await Drain();
        Assert.Equal(before, Stripe.Refunds.Count);                                                          // Stripe was never called for this money
        var sola = _k.F.Services.GetRequiredService<FakeSolaRefundGateway>();
        var call = Assert.Single(sola.Calls);                                                                // Sola was (V-50: it used to die as sola_refund_not_automatic)
        Assert.Equal((long.Parse(Ref), false), (call.OriginalRefNum, call.Full));
        var refundJob = await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == RefundService.StripeRefundType));
        Assert.Equal(OutboxStatus.Delivered, refundJob.Status);
        var processed = Sent("refund.processed").Single().GetProperty("data");
        Assert.Equal(("sola", "900000000001"), (processed.GetProperty("provider").GetString(), processed.GetProperty("refundId").GetString()));
    }

    [Fact]
    public async Task A_stripe_dispute_or_refund_naming_a_sola_orders_intent_matches_nothing_and_moves_no_money()
    {
        var o = await PaidOrder(sola: true);
        var processor = _k.F.Services.GetRequiredService<StripeEventProcessor>();
        StripeEnvelope Parse(string type, object obj) => StripeEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = "evt_" + type.Replace('.', '_') + "_1", type, created = _k.F.Clock.GetUtcNow().ToUnixTimeSeconds(), data = new { @object = obj } })));
        var before = await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == PaymentEventOutboxHandler.Type));
        Assert.Equal(StripeOutcome.Unmatched, await processor.ProcessAsync(Parse("charge.dispute.created", new { id = "dp_1", payment_intent = o.PaymentIntentId, amount = 5000, status = "needs_response" }), default));
        Assert.Equal(StripeOutcome.Unmatched, await processor.ProcessAsync(Parse("charge.refunded", new { id = "ch_1", payment_intent = o.PaymentIntentId, amount_refunded = 1000 }), default));
        Assert.Equal(before, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == PaymentEventOutboxHandler.Type)));   // nothing queued for the portal
        Assert.Equal(0, (await Order(o.Number)).RefundedCents);
    }

    [Fact]
    public async Task The_same_stripe_dispute_on_a_website_order_still_works_as_before()
    {
        var o = await PaidOrder(sola: false);
        var processor = _k.F.Services.GetRequiredService<StripeEventProcessor>();
        var evt = StripeEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = "evt_d_1", type = "charge.dispute.created", created = _k.F.Clock.GetUtcNow().ToUnixTimeSeconds(), data = new { @object = new { id = "dp_9", payment_intent = o.PaymentIntentId, amount = 5000, status = "needs_response" } } })));
        Assert.Equal(StripeOutcome.Applied, await processor.ProcessAsync(evt, default));
    }
}
