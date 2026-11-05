using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Orders and payments, refund and ingest retries, outbox, dead letters and Stripe failures for staff (decisions M7-10..M7-14).</summary>
public class StaffOpsTests
{
    private static string R => StaffKit.Reason;
    private sealed record W(StaffKit S, string Admin, string Support, string Finance, Guid OrderId, Guid RefundId, string Key, string Intent) : IDisposable { public void Dispose() => S.Dispose(); }

    /// <summary>A paid order with a RESERVED refund whose outbox message died (Stripe was unreachable for the whole backoff).</summary>
    private static async Task<W> Setup(AuditFault? fault = null)
    {
        var s = fault is null ? new StaffKit() : new StaffKit(services: x => x.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(fault)));
        var (admin, _, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        var fake = s.F.Services.GetRequiredService<FakePaymentProvider>();
        var intent = await fake.CreateIntentAsync(10_000, "USD", "k-int", new Dictionary<string, string>(), default);
        fake.Capture(intent.Id);
        var now = s.F.Clock.GetUtcNow();
        var order = new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-2001", Status = OrderStatus.Paid, PaymentIntentId = intent.Id, TotalCents = 10_000, PaidCents = 10_000, RefundedCents = 2_500, PaidAt = now.AddHours(-1), CreatedAt = now.AddHours(-1), UpdatedAt = now,
            BuyerName = "Jane Brown", ShipLine1 = "742 Evergreen Terrace", ShipCity = "Springfield", ShipPostalCode = "62704", ShipCountry = "US", Phone = "+1 415 555 0132", GuestEmail = "guest.jane@example.com", RelayEmail = "relay-abc@relay.test" };
        var refund = new OrderRefund { Id = Guid.NewGuid(), Key = "portal:r-1", OrderId = order.Id, AmountCents = 2_500, Reason = "seller_cancel", Status = RefundStatus.Reserved, CreatedAt = now, UpdatedAt = now };
        await s.Db(async db =>
        {
            db.Orders.Add(order); db.OrderRefunds.Add(refund);
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = RefundService.StripeRefundType, DedupeKey = "refund:portal:r-1", Payload = "{\"key\":\"portal:r-1\",\"attempt\":0}", Status = OutboxStatus.Dead, Attempts = 8, LastError = "stripe_unavailable", NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        return new W(s, admin, support, finance, order.Id, refund.Id, refund.Key, intent.Id);
    }

    private static string Order(string reason = "ticket 5: customer call") => $"/api/v1/staff/orders?number=MP-2001&reason={Uri.EscapeDataString(reason)}";

    // ---------- orders ----------

    [Fact]
    public async Task An_order_is_shown_masked_and_the_stripe_block_only_to_roles_that_may_see_payments()
    {
        using var w = await Setup();
        var support = await w.S.Get(Order(), w.Support);
        Assert.Equal(HttpStatusCode.OK, support.StatusCode);
        var body = await support.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "Brown", "Evergreen", "62704", "415 555", "guest.jane@example.com", "Springfield" }) Assert.DoesNotContain(secret, body);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("MP-2001", "Jane B.", "g***@e***.com", "US"), (j.GetProperty("number").GetString(), j.GetProperty("buyer").GetProperty("name").GetString(), j.GetProperty("buyer").GetProperty("guestEmail").GetString(), j.GetProperty("buyer").GetProperty("shipCountry").GetString()));
        Assert.Equal(JsonValueKind.Null, j.GetProperty("payment").ValueKind);            // support: no Stripe ids
        Assert.DoesNotContain(w.Intent, body);
        var finance = await StaffKit.Json(await w.S.Get(Order(), w.Finance));
        Assert.Equal(w.Intent, finance.GetProperty("payment").GetProperty("paymentIntentId").GetString());
        Assert.Equal(10_000, finance.GetProperty("paidCents").GetInt64());
        Assert.True(finance.GetProperty("refunds")[0].GetProperty("retryable").GetBoolean());
        var audit = await w.S.Audit();
        Assert.Equal(2, audit.Count(a => a.Action == "order.lookup" && a.TargetId == w.OrderId.ToString("D")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Get("/api/v1/staff/orders?number=MP-2001", w.Support)).StatusCode);   // a reason is required
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Get("/api/v1/staff/orders?number=NOPE&reason=ticket%205%3A%20x", w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get(Order(), null)).StatusCode);
    }

    // ---------- refund retry ----------

    [Fact]
    public async Task A_dead_refund_retries_through_the_existing_handler_and_stripe_sees_one_refund_however_often_it_is_clicked()
    {
        using var w = await Setup();
        var url = $"/api/v1/staff/orders/{w.OrderId}/refunds/{w.RefundId}/retry";
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post(url, new { reason = R }, w.Support)).StatusCode);                       // support may not
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Post(url, new { reason = "x" }, w.Finance)).StatusCode);
        var fake = w.S.F.Services.GetRequiredService<FakePaymentProvider>();
        var clicks = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.S.Post(url, new { reason = R }, w.Finance)));
        Assert.Equal(1, clicks.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.All(clicks.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await w.S.K.Dispatch();
        Assert.Equal(RefundStatus.Succeeded, await w.S.Db(db => db.OrderRefunds.Where(r => r.Id == w.RefundId).Select(r => r.Status).SingleAsync()));
        Assert.Single(fake.Refunds);
        Assert.Equal(2_500, fake.Refunds[0].AmountCents);
        // done: nothing left to retry, and Stripe is not called again
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post(url, new { reason = R }, w.Finance)).StatusCode);
        await w.S.K.Dispatch();
        Assert.Single(fake.Refunds);
        var entry = (await w.S.Audit()).Single(a => a.Action == "refund.retry");
        Assert.Equal((w.OrderId.ToString("D"), "order", R), (entry.TargetId, entry.TargetType, entry.Reason));
    }

    [Fact]
    public async Task A_refund_stripe_refused_or_another_orders_refund_is_not_retryable()
    {
        using var w = await Setup();
        await w.S.Db(async db => { await db.OrderRefunds.Where(r => r.Id == w.RefundId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, RefundStatus.Rejected)); return 0; });
        var r1 = await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{w.RefundId}/retry", new { reason = R }, w.Finance);
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        Assert.Equal("not_retryable", (await StaffKit.Json(r1)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Post($"/api/v1/staff/orders/{Guid.NewGuid()}/refunds/{w.RefundId}/retry", new { reason = R }, w.Finance)).StatusCode);
        Assert.DoesNotContain(await w.S.Audit(), a => a.Action == "refund.retry");
        // a message that is merely pending is "already queued"
        await w.S.Db(async db =>
        {
            await db.OrderRefunds.Where(r => r.Id == w.RefundId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, RefundStatus.Reserved));
            await db.OutboxMessages.Where(m => m.DedupeKey == "refund:portal:r-1").ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, OutboxStatus.Pending));
            return 0;
        });
        Assert.Equal("already_queued", (await StaffKit.Json(await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{w.RefundId}/retry", new { reason = R }, w.Finance))).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_refund_retry_needs_a_recent_passkey_check()
    {
        using var w = await Setup();
        w.S.F.Clock.Advance(TimeSpan.FromMinutes(6));
        var r = await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{w.RefundId}/retry", new { reason = R }, w.Finance);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("reauthentication_required", (await StaffKit.Json(r)).GetProperty("code").GetString());
    }

    // ---------- ingests ----------

    [Fact]
    public async Task A_paid_order_the_portal_never_took_shows_as_stuck_and_its_dead_ingest_can_be_retried_once()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        await w.S.Db(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = "order.ingest", DedupeKey = "ingest:MP-2001", Payload = $"{{\"orderId\":\"{w.OrderId}\"}}", Status = OutboxStatus.Dead, Attempts = 8, LastError = "invalid_shipping_speed", NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        var list = await StaffKit.Json(await w.S.Get("/api/v1/staff/ingests", w.Support));
        var o = list.GetProperty("orders")[0];
        Assert.Equal(("MP-2001", "dead", "invalid_shipping_speed", true), (o.GetProperty("number").GetString(), o.GetProperty("message").GetProperty("status").GetString(), o.GetProperty("message").GetProperty("lastError").GetString(), o.GetProperty("retryable").GetBoolean()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get("/api/v1/staff/ingests", null)).StatusCode);
        var url = $"/api/v1/staff/ingests/{w.OrderId}/retry";
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post(url, new { reason = R }, w.Finance)).StatusCode);
        var rs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => w.S.Post(url, new { reason = R }, w.Support)));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, (await w.S.Audit()).Count(a => a.Action == "ingest.retry"));
        Assert.Equal(OutboxStatus.Pending, await w.S.Db(db => db.OutboxMessages.Where(m => m.DedupeKey == "ingest:MP-2001").Select(m => m.Status).SingleAsync()));
        // an order that was placed meanwhile is nothing to retry
        await w.S.Db(async db => { await db.Orders.Where(x => x.Id == w.OrderId).ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, OrderStatus.Placed)); return 0; });
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post(url, new { reason = R }, w.Support)).StatusCode);
        Assert.Empty((await StaffKit.Json(await w.S.Get("/api/v1/staff/ingests", w.Support))).GetProperty("orders").EnumerateArray());
    }

    // ---------- outbox ----------

    [Fact]
    public async Task The_outbox_list_never_shows_a_payload_and_only_an_admin_retries()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        var mailId = Guid.NewGuid();
        await w.S.Db(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { Id = mailId, Type = "email.send", Payload = "{\"to\":\"secret.person@example.com\",\"body\":\"link\"}", Status = OutboxStatus.Dead, Attempts = 8, LastError = "SmtpException", NextAttemptAt = now, CreatedAt = now.AddMinutes(1), DeadAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        var r = await w.S.Get("/api/v1/staff/outbox?status=dead", w.Support);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("secret.person", body); Assert.DoesNotContain("payload", body.Replace("hasPayload", ""), StringComparison.OrdinalIgnoreCase);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal(2, j.GetProperty("counts").GetProperty("dead").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Get("/api/v1/staff/outbox?status=weird", w.Support)).StatusCode);
        var page = await StaffKit.Json(await w.S.Get("/api/v1/staff/outbox?limit=1", w.Support));
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        // retry: support and finance cannot, the admin can, once
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post($"/api/v1/staff/outbox/{mailId}/retry", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/outbox/{mailId}/retry", new { reason = R }, w.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post($"/api/v1/staff/outbox/{mailId}/retry", new { reason = R }, w.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Post($"/api/v1/staff/outbox/{Guid.NewGuid()}/retry", new { reason = R }, w.Admin)).StatusCode);
        var entry = (await w.S.Audit()).Single(a => a.Action == "outbox.retry");
        Assert.Equal((mailId.ToString("D"), R), (entry.TargetId, entry.Reason));
        Assert.DoesNotContain("secret.person", entry.Detail ?? "");
    }

    // ---------- dead letters ----------

    [Fact]
    public async Task Parked_inbound_events_are_listed_without_their_body_and_an_admin_can_replay_them()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        await w.S.Db(async db =>
        {
            db.PortalInboundEvents.Add(new PortalInboundEvent { Id = "evt-parked-1", Type = "order.shipped", PortalCreatedAt = now, OrderingAt = now, ReceivedAt = now.AddHours(-3), Status = InboundStatus.Parked, AggregateType = "order", AggregateKey = "MP-NOPE", PayloadJson = "{\"driver\":\"Sam Secret\"}" });
            await db.SaveChangesAsync();
            return 0;
        });
        var r = await w.S.Get("/api/v1/staff/dead-letters", w.Finance);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sam Secret", body);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("evt-parked-1", 3), (j.GetProperty("parked")[0].GetProperty("id").GetString(), j.GetProperty("parked")[0].GetProperty("ageHours").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post("/api/v1/staff/dead-letters/retry", new { eventId = "evt-parked-1", reason = R }, w.Support)).StatusCode);
        var ok = await StaffKit.Json(await w.S.Post("/api/v1/staff/dead-letters/retry", new { eventId = "evt-parked-1", reason = R }, w.Admin));
        Assert.Equal(0, ok.GetProperty("replayed").GetInt32());   // its order is still unknown: it stays parked, and the sweep tries again
        Assert.Equal(HttpStatusCode.Conflict, (await w.S.Post("/api/v1/staff/dead-letters/retry", new { eventId = "no-such-event", reason = R }, w.Admin)).StatusCode);
        Assert.Single(await w.S.Audit(), a => a.Action == "deadletter.retry" && a.TargetId == "evt-parked-1");
        Assert.Equal(1, await w.S.Db(db => db.PortalInboundEvents.CountAsync(e => e.Id == "evt-parked-1" && e.Status == InboundStatus.Parked)));
    }

    // ---------- Stripe failures ----------

    [Fact]
    public async Task Stripe_events_we_could_not_match_or_that_failed_are_listed_for_finance_and_admin_only()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        await w.S.Db(async db =>
        {
            db.StripeEvents.AddRange(
                new StripeEvent { Id = "evt_a", Type = "charge.refunded", Outcome = "unmatched", ObjectId = "ch_1", ReceivedAt = now.AddMinutes(-3) },
                new StripeEvent { Id = "evt_b", Type = "payment_intent.payment_failed", Outcome = "applied", ObjectId = "pi_1", ReceivedAt = now.AddMinutes(-2) },
                new StripeEvent { Id = "evt_c", Type = "payment_intent.succeeded", Outcome = "applied", ObjectId = "pi_2", ReceivedAt = now.AddMinutes(-1) });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Get("/api/v1/staff/stripe/failures", w.Support)).StatusCode);
        var j = await StaffKit.Json(await w.S.Get("/api/v1/staff/stripe/failures?limit=1", w.Finance));
        Assert.Equal(2, j.GetProperty("total").GetInt32());
        Assert.Equal("evt_b", j.GetProperty("events")[0].GetProperty("id").GetString());
        var next = await StaffKit.Json(await w.S.Get("/api/v1/staff/stripe/failures?before=" + j.GetProperty("next").GetString(), w.Admin));
        Assert.Equal("evt_a", next.GetProperty("events")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Get("/api/v1/staff/stripe/failures?before=!!", w.Finance)).StatusCode);
    }

    [Fact]
    public async Task A_buyers_orders_are_listed_with_a_reason_and_audited()
    {
        using var w = await Setup();
        var acct = Guid.NewGuid();
        await w.S.Db(async db => { await db.Orders.Where(o => o.Id == w.OrderId).ExecuteUpdateAsync(x => x.SetProperty(q => q.AccountId, acct)); return 0; });
        var j = await StaffKit.Json(await w.S.Get($"/api/v1/staff/buyers/{acct}/orders?reason=ticket%207%3A%20history", w.Support));
        Assert.Equal("MP-2001", j[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Get($"/api/v1/staff/buyers/{acct}/orders", w.Support)).StatusCode);
        Assert.Single(await w.S.Audit(), a => a.Action == "buyer.orders" && a.TargetId == acct.ToString("D"));
    }

    // ---------- the audit row and the action commit together (security review MED-2) ----------

    [Fact]
    public async Task A_failing_audit_write_leaves_a_refund_ingest_and_outbox_retry_undone_and_a_replay_unrun()
    {
        var fault = new AuditFault();
        using var w = await Setup(fault);
        var now = w.S.F.Clock.GetUtcNow();
        var mailId = Guid.NewGuid();
        await w.S.Db(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = "order.ingest", DedupeKey = "ingest:MP-2001", Payload = $"{{\"orderId\":\"{w.OrderId}\"}}", Status = OutboxStatus.Dead, Attempts = 8, NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            db.OutboxMessages.Add(new OutboxMessage { Id = mailId, Type = "email.send", Payload = "{}", Status = OutboxStatus.Dead, Attempts = 8, NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            db.PortalInboundEvents.Add(new PortalInboundEvent { Id = "evt-p", Type = "order.shipped", PortalCreatedAt = now, OrderingAt = now, ReceivedAt = now, Status = InboundStatus.Parked, AggregateType = "order", AggregateKey = "MP-NOPE", PayloadJson = "{}" });
            await db.SaveChangesAsync();
            return 0;
        });
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await w.S.Post($"/api/v1/staff/orders/{w.OrderId}/refunds/{w.RefundId}/retry", new { reason = R }, w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await w.S.Post($"/api/v1/staff/ingests/{w.OrderId}/retry", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await w.S.Post($"/api/v1/staff/outbox/{mailId}/retry", new { reason = R }, w.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await w.S.Post("/api/v1/staff/dead-letters/retry", new { eventId = "evt-p", reason = R }, w.Admin)).StatusCode);
        fault.On = false;
        Assert.All(await w.S.Db(db => db.OutboxMessages.Where(m => m.DedupeKey == "refund:portal:r-1" || m.DedupeKey == "ingest:MP-2001" || m.Id == mailId).ToListAsync()), m => Assert.Equal(OutboxStatus.Dead, m.Status));
        Assert.Null(await w.S.Db(db => db.PortalInboundEvents.Where(e => e.Id == "evt-p").Select(e => e.LastReplayAt).SingleAsync()));   // the replay never ran
        Assert.DoesNotContain(await w.S.Audit(), a => a.Action is "refund.retry" or "ingest.retry" or "outbox.retry" or "deadletter.retry");
    }

    // ---------- a dead booking confirm needs a person (M8-11) ----------

    [Fact]
    public async Task A_dead_booking_confirm_is_listed_among_the_dead_letters_with_its_booking_amount_and_the_steps()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        var bookingId = Guid.NewGuid(); var paymentId = Guid.NewGuid(); var deadId = Guid.NewGuid();
        await w.S.Db(async db =>
        {
            db.Bookings.Add(new Booking { Id = bookingId, MarketplaceBookingId = "BK-7001", AccountId = Guid.NewGuid(), ListingId = Guid.NewGuid(), Status = BookingStatus.Hold, Currency = "USD", CreatedAt = now, UpdatedAt = now });
            db.BookingPayments.Add(new BookingPayment { Id = paymentId, Key = "pay:BK-7001:booking", BookingId = bookingId, Kind = "booking", Currency = "USD", AmountCents = 12_500, CapturedCents = 12_500, IntentId = "pi_fake_booking", Status = BookingPaymentStatus.Captured, ReportedToPortal = false, CreatedAt = now, UpdatedAt = now });
            db.OutboxMessages.Add(new OutboxMessage { Id = deadId, Type = Solvelt.Marketplace.Api.Services.BookingConfirmHandler.Type, DedupeKey = "confirm:BK-7001", Payload = $"{{\"bookingId\":\"{bookingId}\",\"paymentId\":\"{paymentId}\"}}", Status = OutboxStatus.Dead, Attempts = 8, LastError = "confirm_unexpected_403", NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            // not listed: still pending, already delivered, and a dead message of another type
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = Solvelt.Marketplace.Api.Services.BookingConfirmHandler.Type, Payload = "{}", Status = OutboxStatus.Pending, NextAttemptAt = now, CreatedAt = now });
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = Solvelt.Marketplace.Api.Services.BookingConfirmHandler.Type, Payload = null, Status = OutboxStatus.Delivered, NextAttemptAt = now, CreatedAt = now, DeliveredAt = now });
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = "email.send", Payload = "{}", Status = OutboxStatus.Dead, LastError = "SmtpException", NextAttemptAt = now, CreatedAt = now, DeadAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        foreach (var token in new[] { w.Support, w.Finance, w.Admin })   // everyone who may look at dead letters sees it
        {
            var r = await w.S.Get("/api/v1/staff/dead-letters", token);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"payload\"", body, StringComparison.OrdinalIgnoreCase);
            var list = JsonDocument.Parse(body).RootElement.GetProperty("bookingConfirms");
            var one = Assert.Single(list.EnumerateArray());
            Assert.Equal((deadId, "BK-7001", "hold", "confirm_unexpected_403", 12_500, 12_500, "captured", true, true),
                (one.GetProperty("messageId").GetGuid(), one.GetProperty("bookingNumber").GetString(), one.GetProperty("bookingStatus").GetString()!.ToLowerInvariant(), one.GetProperty("lastError").GetString(),
                 one.GetProperty("amountCents").GetInt64(), one.GetProperty("capturedCents").GetInt64(), one.GetProperty("paymentStatus").GetString(), one.GetProperty("needsPerson").GetBoolean(), one.GetProperty("retryable").GetBoolean()));
            Assert.Equal(3, one.GetProperty("steps").GetArrayLength());
            Assert.Contains("Stripe dashboard", one.GetProperty("steps")[2].GetString());
        }
        // once the buyer is refunded or the sale reported, it no longer "needs a person"
        await w.S.Db(async db => { await db.BookingPayments.Where(x => x.Id == paymentId).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportedToPortal, true)); return 0; });
        var after = JsonDocument.Parse(await (await w.S.Get("/api/v1/staff/dead-letters", w.Finance)).Content.ReadAsStringAsync()).RootElement.GetProperty("bookingConfirms")[0];
        Assert.False(after.GetProperty("needsPerson").GetBoolean());
        // the replay goes through the existing audited outbox retry (an admin), and only an admin may
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post($"/api/v1/staff/outbox/{deadId}/retry", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/outbox/{deadId}/retry", new { reason = R }, w.Admin)).StatusCode);
        Assert.Single(await w.S.Audit(), a => a.Action == "outbox.retry" && a.TargetId == deadId.ToString("D"));
        Assert.Empty(JsonDocument.Parse(await (await w.S.Get("/api/v1/staff/dead-letters", w.Finance)).Content.ReadAsStringAsync()).RootElement.GetProperty("bookingConfirms").EnumerateArray());   // pending again: not dead
    }

    [Fact]
    public async Task A_dead_booking_confirm_whose_payload_was_purged_is_still_listed_without_a_booking()
    {
        using var w = await Setup();
        var now = w.S.F.Clock.GetUtcNow();
        await w.S.Db(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { Id = Guid.NewGuid(), Type = Solvelt.Marketplace.Api.Services.BookingConfirmHandler.Type, Payload = null, Status = OutboxStatus.Dead, Attempts = 8, LastError = "confirm_unreadable", NextAttemptAt = now, CreatedAt = now, DeadAt = now, PayloadPurgedAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        var one = JsonDocument.Parse(await (await w.S.Get("/api/v1/staff/dead-letters", w.Support)).Content.ReadAsStringAsync()).RootElement.GetProperty("bookingConfirms")[0];
        Assert.Equal("confirm_unreadable", one.GetProperty("lastError").GetString());
        Assert.Equal((JsonValueKind.Null, false, false), (one.GetProperty("bookingNumber").ValueKind, one.GetProperty("retryable").GetBoolean(), one.GetProperty("needsPerson").GetBoolean()));
    }
}
