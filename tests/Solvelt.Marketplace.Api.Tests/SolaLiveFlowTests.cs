using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Sola-paid phone orders, live flow (decision V-50): automatic refunds through Sola's gateway (a FAKE gateway here: approve, decline, duplicate, timeout), no blind retry after an ambiguous answer,
/// the staff chargeback record, and the start-up mode line. Test and sandbox money only; nothing here talks to Sola.
/// </summary>
public class SolaLiveFlowTests
{
    private const string R = "ticket 7: customer called about the charge";
    private const string SolaRef = "10984432761";

    private sealed record W(StaffKit S, string Admin, string Finance, string Support, Guid OrderId, string OrderNumber) : IDisposable
    {
        public void Dispose() => S.Dispose();
        public FakeSolaRefundGateway Sola => S.F.Services.GetRequiredService<FakeSolaRefundGateway>();
    }

    private static async Task<W> Setup(string provider = "sola", long paid = 10_000)
    {
        var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var now = s.F.Clock.GetUtcNow();
        var order = new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-3001", Status = OrderStatus.Paid, PaymentIntentId = "pi_unused_" + Guid.NewGuid().ToString("N")[..8], TotalCents = paid, PaidCents = paid, PaidAt = now.AddHours(-1), Currency = "USD",
            PaymentProvider = provider, PaymentRef = provider == "sola" ? SolaRef : null, GuestEmail = "guest@example.com", RelayEmail = "relay@relay.test" };
        var group = new OrderGroup { Id = Guid.NewGuid(), OrderId = order.Id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), SellerName = "S", BrandName = "B", ItemsCents = paid - 1000, ShippingCents = 1000, TotalCents = paid, SellerOrderNumber = "SO-3001" };
        await s.Db(async db => { db.Orders.Add(order); db.OrderGroups.Add(group); await db.SaveChangesAsync(); return 0; });
        return new W(s, admin, finance, support, order.Id, order.Number);
    }

    /// <summary>Reserves a refund the way every refund is reserved (counters + the queued message), then returns its id.</summary>
    private static async Task<Guid> Reserve(W w, long cents, string key)
    {
        await w.S.Db(async db =>
        {
            var gid = await db.OrderGroups.Where(g => g.OrderId == w.OrderId).Select(g => g.Id).SingleAsync();
            var r = await new RefundService(db, new Solvelt.Marketplace.Api.Outbox.OutboxWriter(db, w.S.F.Clock), w.S.F.Clock).ReserveAsync(w.OrderId, gid, cents, 0, key, "return", true, "r-" + key, default);
            Assert.Equal(ReserveOutcome.Reserved, r);
            return 0;
        });
        return await w.S.Db(db => db.OrderRefunds.Where(x => x.Key == key).Select(x => x.Id).SingleAsync());
    }

    private static Task<OrderRefund> RefundRow(W w, Guid id) => w.S.Db(db => db.OrderRefunds.AsNoTracking().SingleAsync(r => r.Id == id));
    private static Task<long> Refunded(W w) => w.S.Db(db => db.Orders.Where(o => o.Id == w.OrderId).Select(o => o.RefundedCents).SingleAsync());
    private static async Task<List<JsonElement>> Events(W w, string type) => (await w.S.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == "portal.payment-event" && m.Payload != null).ToListAsync()))
        .Select(m => JsonDocument.Parse(m.Payload!).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() == type).ToList();

    // ---------- refunds ----------

    [Fact]
    public async Task A_partial_refund_of_a_sola_order_calls_sola_with_its_amount_and_reports_the_refunds_own_xrefnum_to_the_portal()
    {
        using var w = await Setup();
        var id = await Reserve(w, 2_500, "portal:p1");
        await w.S.K.Dispatch();
        Assert.Equal([new FakeSolaRefundGateway.Call(long.Parse(SolaRef), 2_500, false)], w.Sola.Calls);          // cc:refund with xAmount, of the ORIGINAL transaction
        var row = await RefundRow(w, id);
        Assert.Equal(RefundStatus.Succeeded, row.Status);
        Assert.Equal("900000000001", row.StripeRefundId);                                                            // the refund's own xRefNum (a BIGINT-sized number kept as text)
        var evt = (await Events(w, "refund.processed")).Single().GetProperty("data");
        Assert.Equal(("sola", "900000000001", "MP-3001", "SO-3001", 2_500L), (evt.GetProperty("provider").GetString(), evt.GetProperty("refundId").GetString(), evt.GetProperty("marketplaceOrderId").GetString(), evt.GetProperty("sellerOrderNumber").GetString(), evt.GetProperty("amountCents").GetInt64()));
        Assert.Equal(2_500, await Refunded(w));
    }

    [Fact]
    public async Task A_full_refund_of_a_sola_order_is_a_voidrefund_and_the_stripe_provider_is_never_called()
    {
        using var w = await Setup();
        var stripe = w.S.F.Services.GetRequiredService<FakePaymentProvider>();
        var id = await Reserve(w, 10_000, "portal:p2");
        await w.S.K.Dispatch();
        var call = Assert.Single(w.Sola.Calls);
        Assert.True(call.Full);                                                                                      // cc:voidrefund: voids while unsettled, else refunds in full
        Assert.Equal(RefundStatus.Succeeded, (await RefundRow(w, id)).Status);
        Assert.Empty(stripe.Refunds);
    }

    [Theory]
    [InlineData("05")]
    [InlineData("duplicate")]
    public async Task A_refund_sola_declines_or_rejects_as_a_duplicate_moves_nothing_gives_the_reservation_back_and_is_logged_by_code_only(string code)
    {
        using var w = await Setup();
        w.Sola.Script.Enqueue(new SolaRefundResult(SolaRefundOutcome.Declined, null, code));
        var id = await Reserve(w, 2_500, "portal:p3");
        Assert.Equal(2_500, await Refunded(w));
        await w.S.K.Dispatch();
        var row = await RefundRow(w, id);
        Assert.Equal((RefundStatus.Rejected, null), (row.Status, row.StripeRefundId));
        Assert.Equal(0, await Refunded(w));                                                                          // the reservation went back
        Assert.Equal((OutboxStatus.Dead, "sola_" + code), await w.S.Db(async db => { var m = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == RefundService.StripeRefundType); return (m.Status, m.LastError); }));
        Assert.Empty(await Events(w, "refund.processed"));                                                           // nothing is reported as returned
        Assert.Contains(w.S.F.Logs.Lines, l => l.Contains("refused by Sola") && l.Contains(code));
        Assert.Single(w.Sola.Calls);
    }

    [Fact]
    public async Task A_timeout_is_never_retried_blindly_staff_must_check_at_sola_and_the_refund_is_finished_once_with_the_reference_they_give()
    {
        using var w = await Setup();
        w.Sola.Script.Enqueue(new SolaRefundResult(SolaRefundOutcome.Ambiguous, null, "timeout"));
        var id = await Reserve(w, 2_500, "portal:p4");
        await w.S.K.Dispatch();
        Assert.Equal(StripeRefundHandler.SolaSending, (await RefundRow(w, id)).StripeRefundId);                      // written BEFORE the call: proof it may have gone through
        Assert.Equal(2_500, await Refunded(w));                                                                      // still counted: the money may have left
        await w.S.K.Dispatch(); await w.S.K.Dispatch();
        Assert.Single(w.Sola.Calls);                                                                                 // the outbox never calls again by itself
        var url = $"/api/v1/staff/orders/{w.OrderId}/refunds/{id}/retry";
        var retry = await w.S.Post(url, new { reason = R }, w.Finance);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);                                                     // the plain retry would be a blind one: refused
        Assert.Contains("sola_needs_reconcile", await retry.Content.ReadAsStringAsync());
        var resolve = $"/api/v1/staff/orders/{w.OrderId}/refunds/{id}/sola-resolve";
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post(resolve, new { outcome = "refunded", refNum = "900000000777", reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Post(resolve, new { outcome = "refunded", refNum = "abc", reason = R }, w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post(resolve, new { outcome = "refunded", refNum = "900000000777", reason = R }, w.Finance)).StatusCode);
        await w.S.K.Dispatch();
        Assert.Single(w.Sola.Calls);                                                                                 // confirmed by a person: finished without calling Sola again
        Assert.Equal(("succeeded", "sola:confirmed:900000000777"[StripeRefundHandler.SolaConfirmedPrefix.Length..]), ((await RefundRow(w, id)).Status, (await RefundRow(w, id)).StripeRefundId));
        Assert.Equal("900000000777", (await Events(w, "refund.processed")).Single().GetProperty("data").GetProperty("refundId").GetString());
        Assert.Contains(await w.S.Audit(), a => a.Action == "refund.sola_resolve" && a.Reason == R);
    }

    [Fact]
    public async Task When_staff_find_the_refund_never_happened_at_sola_it_is_sent_again_once()
    {
        using var w = await Setup();
        w.Sola.Script.Enqueue(new SolaRefundResult(SolaRefundOutcome.Ambiguous, null, "timeout"));
        var id = await Reserve(w, 2_500, "portal:p5");
        await w.S.K.Dispatch();
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{id}/sola-resolve", new { outcome = "not_refunded", reason = R }, w.Finance)).StatusCode);
        await w.S.K.Dispatch();
        Assert.Equal(2, w.Sola.Calls.Count);
        Assert.Equal(RefundStatus.Succeeded, (await RefundRow(w, id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{id}/sola-resolve", new { outcome = "not_refunded", reason = R }, w.Finance)).StatusCode);   // nothing left to resolve
    }

    [Theory]
    [InlineData("{\"xResult\":\"A\",\"xStatus\":\"Approved\",\"xRefNum\":\"10984433110\"}", SolaRefundOutcome.Approved, 10984433110L, null)]
    [InlineData("{\"xResult\":\"D\",\"xStatus\":\"Declined\",\"xError\":\"Card declined (secret words)\",\"xErrorCode\":\"00005\"}", SolaRefundOutcome.Declined, null, "00005")]
    [InlineData("{\"xResult\":\"E\",\"xErrorCode\":\"00014\"}", SolaRefundOutcome.Declined, null, "00014")]
    [InlineData("{\"xResult\":\"A\",\"xRefNum\":\"x\"}", SolaRefundOutcome.Ambiguous, null, "approved_without_ref")]   // money moved, reference lost: a person looks
    [InlineData("not json", SolaRefundOutcome.Ambiguous, null, "unreadable")]
    [InlineData("{\"xResult\":\"?\"}", SolaRefundOutcome.Ambiguous, null, "unreadable")]
    public void The_gateway_answer_is_read_per_the_docs_and_anything_unclear_is_ambiguous(string body, SolaRefundOutcome outcome, long? refNum, string? code)
        => Assert.Equal(new SolaRefundResult(outcome, refNum, code), SolaRefundHttpGateway.Parse(body));

    // ---------- chargebacks ----------

    private static object Cb(string status = "needs_response", long amount = 6_000, long fee = 0) => new { reference = "CB778812", amountCents = amount, feeCents = fee, status, solaReason = "fraud", reason = R };

    [Fact]
    public async Task A_staff_chargeback_is_told_to_the_portal_as_a_sola_dispute_with_its_fee_once_per_status_and_audited()
    {
        using var w = await Setup();
        var url = $"/api/v1/staff/orders/{w.OrderId}/sola-chargebacks";
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post(url, Cb(), w.Support)).StatusCode);                  // staff only, with the refund permission
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Post(url, Cb())).StatusCode);                          // never public
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Post(url, new { reference = "CB778812", amountCents = 6_000, feeCents = 0, status = "needs_response", reason = "x" }, w.Finance)).StatusCode);   // a reason is required
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Post(url, new { reference = "CB778812", amountCents = 6_000, status = "needs_response", reason = R }, w.Finance)).StatusCode);   // the fee is required
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Post(url, Cb(status: "pending"), w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post(url, Cb(), w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post(url, Cb(), w.Finance)).StatusCode);                   // a repeated click queues nothing new
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post(url, Cb(status: "lost"), w.Finance)).StatusCode);
        var created = (await Events(w, "dispute.created")).Single().GetProperty("data");
        Assert.Equal(("sola", "CB778812", 6_000L, 0L, "needs_response", "SO-3001"), (created.GetProperty("provider").GetString(), created.GetProperty("disputeId").GetString(), created.GetProperty("amountCents").GetInt64(), created.GetProperty("feeCents").GetInt64(), created.GetProperty("status").GetString(), created.GetProperty("sellerOrderNumber").GetString()));
        Assert.Equal("lost", (await Events(w, "dispute.closed")).Single().GetProperty("data").GetProperty("status").GetString());
        var audit = (await w.S.Audit()).Where(a => a.Action == "dispute.sola_record").ToList();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal((w.OrderId.ToString("D"), R), (a.TargetId, a.Reason)));
    }

    [Fact]
    public async Task A_chargeback_cannot_be_recorded_on_a_stripe_order_or_above_what_was_sold()
    {
        using var w = await Setup(provider: "stripe");
        var url = $"/api/v1/staff/orders/{w.OrderId}/sola-chargebacks";
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post(url, Cb(), w.Finance)).StatusCode);
        Assert.Empty(await Events(w, "dispute.created"));
        using var v = await Setup();
        Assert.Equal(HttpStatusCode.Conflict, (await v.S.Post($"/api/v1/staff/orders/{v.OrderId}/sola-chargebacks", Cb(amount: 99_999), v.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v.S.Post($"/api/v1/staff/orders/{Guid.NewGuid()}/sola-chargebacks", Cb(), v.Finance)).StatusCode);
    }

    // ---------- start-up: the mode and live keys ----------

    [Fact]
    public void The_mode_is_never_guessed_and_a_key_that_names_itself_live_is_refused()
    {
        Assert.Equal("Sola: mode unknown", new SolaOptions { ApiKey = "xk_abc123" }.ModeText);
        Assert.Equal("Sola: mode unknown", new SolaOptions { ApiKey = "deliver_abc" }.ModeText);                     // "deliver" is not the word live
        Assert.StartsWith("Sola: LIVE mode", new SolaOptions { ApiKey = "acct_live_abc" }.ModeText);
        Assert.StartsWith("Sola: not configured", new SolaOptions().ModeText);
        Assert.Contains(SolaOptions.Problems(new SolaOptions { ApiKey = "LIVE-abc" }), p => p.Contains("LIVE key"));
        Assert.DoesNotContain(SolaOptions.Problems(new SolaOptions { ApiKey = "xk_abc123" }), p => p.Contains("LIVE key"));
    }

    [Fact]
    public void Booking_payment_code_never_names_sola_so_no_booking_event_can_carry_provider_sola()
    {
        // bookings are Stripe-only: the portal answers provider_not_supported for Sola with any BOOKING kind. The booking services (src/.../Services) build every event without a provider and never mention Sola.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Solvelt.Marketplace.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir!, "src", "Solvelt.Marketplace.Api", "Services"), "*.cs");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("PaymentProviders.Sola", text);
            Assert.DoesNotContain("SolaMoneyEvents", text);
            Assert.DoesNotContain("Provider:", text);   // no named provider argument on a booking's event
        }
        var booking = new Solvelt.Marketplace.Api.Portal.PaymentCapturedData(Solvelt.Marketplace.Api.Portal.PaymentKinds.Booking, null, "BK-1", null, "pi_1", "USD", null, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("provider", JsonSerializer.Serialize(booking, Solvelt.Marketplace.Api.Portal.PortalJson.Options));   // and a capture without one carries no provider field at all (absent means Stripe)
    }
}
