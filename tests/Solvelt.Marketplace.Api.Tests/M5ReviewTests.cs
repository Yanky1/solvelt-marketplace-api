using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The orchestrator's review of slice 1 (a33ca41): every finding has a test that fails without its fix.</summary>
public sealed class M5ReviewTests : OrderFlowBase
{
    private int _n;
    private string _labelAnswer = "";
    private int _orderReads;
    private Func<ReceivedCall, HttpResponseMessage>? _orderState;

    public M5ReviewTests()
    {
        _onOther = c =>
        {
            string Ok() => JsonSerializer.Serialize(new { orders = _sellerOrders.Values.Select(v => new { number = v.Number, result = "requested" }) });
            if (c.PathAndQuery.EndsWith("/return-requests") || c.PathAndQuery.EndsWith("/cancel-requests")) return FakePortal.Json(HttpStatusCode.OK, Ok());
            if (c.PathAndQuery.EndsWith("/label-link")) return FakePortal.Json(HttpStatusCode.OK, _labelAnswer);
            if (c.Method == "GET" && c.PathAndQuery.StartsWith("/api/internal/v1/orders/MP-")) { _orderReads++; return _orderState?.Invoke(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}"); }
            return null;
        };
    }

    private async Task<(MarketplaceOrder O, OrderGroup G)> Placed(int qty = 2)
    {
        await Login();
        await AddToCart("challah-classic", qty);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        return (o, o.Groups.Single());
    }

    private async Task<string> Evt(string type, MarketplaceOrder o, OrderGroup g, object? extra = null, string? id = null, DateTimeOffset? at = null, bool rawLines = false)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body(id ?? $"re-{++_n:D3}", type, at ?? _k.F.Clock.GetUtcNow().AddSeconds(-3000 + _n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private Task<string> Ship(MarketplaceOrder o, OrderGroup g, int qty, string sh = "sh-1", object? extra = null, DateTimeOffset? at = null)
        => Evt("order.shipped", o, g, new { shipmentId = sh, carrier = "USPS", trackingNumber = "T-" + sh, lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = qty } } }, at: at);

    private async Task<HttpResponseMessage> Call(HttpMethod m, string path, object? body = null, string? session = null, string? raw = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (raw is not null) r.Content = new StringContent(raw, Encoding.UTF8, "application/json"); else if (body is not null) r.Content = JsonContent.Create(body);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private async Task<string> RequestReturn(MarketplaceOrder o, OrderGroup g, int qty)
    {
        var res = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = o.Lines.Single().MarketplaceLineId, quantity = qty } } });
        return res.StatusCode == HttpStatusCode.Accepted ? (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnId").GetString()! : ((int)res.StatusCode).ToString();
    }

    private Task<OrderLineFulfilment> Counters(MarketplaceOrder o) => _k.Db(db => db.OrderLineFulfilments.AsNoTracking().SingleAsync(f => f.LineId == o.Lines.Single().Id));
    private Task<string?> Status(OrderGroup g) => _k.Db(async db => (await db.SellerOrderStates.AsNoTracking().SingleOrDefaultAsync(s => s.GroupId == g.Id))?.Status);
    private async Task<JsonElement> Detail(MarketplaceOrder o) => JsonDocument.Parse(await (await Call(HttpMethod.Get, $"/api/v1/orders/{o.Number}")).Content.ReadAsStringAsync()).RootElement.Clone();

    // ---------------------------------------------------------------- H1

    [Fact]
    public async Task H1_A_full_return_works_after_the_order_is_delivered_decided_labelled_received_and_refunded()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { by = "seller" }));
        var rid = await RequestReturn(o, g, 2);
        await Drain();
        Assert.Equal("applied", await Evt("return.decided", o, g, new { returnId = rid, decision = "approved" }));
        Assert.Equal("applied", await Evt("return.label_created", o, g, new { returnId = rid, labelUrl = new LabelHelper(_k.F).Url("L9"), labelUrlExpiresAt = _k.F.Clock.GetUtcNow().AddHours(24), carrier = "USPS" }));
        Assert.Equal("applied", await Evt("return.received", o, g, new { returnId = rid }));
        var f = await Counters(o);
        Assert.Equal((0, 2), (f.ReturnPendingQty, f.ReturnedQty));                               // not reserved for ever
        // tracking and attempts are not frozen by the delivered state either (attempt and method events of other aggregates)
        Assert.Equal("applied", await Evt("delivery.attempt_failed", o, g, new { shipmentId = "sh-1", attempt = 2, reason = "refused" }));
        Assert.Equal("delivered", await Status(g));                                              // and the ORDER status is still terminal
        Assert.Equal("skipped", await Evt("order.held", o, g));
        var d = (await Detail(o)).GetProperty("groups")[0].GetProperty("returns")[0];
        Assert.Equal("received", d.GetProperty("status").GetString());
    }

    [Fact]
    public async Task H1_A_rejected_return_after_delivery_frees_its_units()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Evt("order.delivered", o, g, new { by = "buyer" });
        var rid = await RequestReturn(o, g, 2);
        Assert.Equal("applied", await Evt("return.decided", o, g, new { returnId = rid, decision = "rejected", note = "no" }));
        Assert.Equal(0, (await Counters(o)).ReturnPendingQty);
        Assert.NotEqual("422", await RequestReturn(o, g, 2));
    }

    // ---------------------------------------------------------------- H2

    [Fact]
    public async Task H2_A_malformed_number_in_an_event_never_fails_the_webhook_and_never_blocks_a_refund()
    {
        var (o, g) = await Placed();
        var lid = o.Lines.Single().MarketplaceLineId;
        // events only the projection reads: numbers where strings belong and strings where numbers belong must be skipped, not thrown (a throw is a 500 and the portal retries for ever)
        Assert.Equal("applied", await Evt("order.shipped", o, g, new { shipmentId = "sh-1", carrier = "USPS", lines = new object[] { new { marketplaceLineId = lid, quantity = "2" }, new { marketplaceLineId = lid, quantity = 1.5 }, new { marketplaceLineId = lid, quantity = 2 } } }));
        Assert.Equal(2, (await Counters(o)).ShippedQty);                                            // the good line counted, the malformed ones skipped
        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", status = "out_for_delivery", position = new { lat = "40.1", lng = "x" }, eta = 7 }));
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { deliveredAt = 5, by = 7 }));
        Assert.Equal("applied", await Evt("delivery.attempt_failed", o, g, new { shipmentId = "sh-1", attempt = "two", reason = 3 }));
        Assert.Equal("applied", await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "/api/public/delivery-proof/p?x=1" }, expiresInMinutes = "soon" } }));
        // and a valid refund is reserved as always
        Assert.Equal("applied", await Evt("refund.issued", o, g, new { refundId = "rf-ok", amountCents = 500, shippingCents = 0, full = false, lines = new[] { new { marketplaceLineId = lid, quantity = 1, amountCents = 500 } } }));
        await Drain();
        Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync()));
        Assert.Single(Stripe.Refunds);
        Assert.Equal(1, (await Counters(o)).RefundedQty);
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("projection of a"));
    }

    [Fact]
    public async Task H2_Whatever_the_projection_does_wrong_the_money_part_commits_in_a_savepoint()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        // break the projection's own table: it fails with a database error inside the webhook transaction
        await _k.Db(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE order_line_fulfilment RENAME TO order_line_fulfilment_x"));
        try
        {
            Assert.Equal("applied", await Evt("refund.issued", o, g, new { refundId = "rf-sp", amountCents = 500, shippingCents = 0, full = false,
                lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 1, amountCents = 500 } } }));
            await Drain();
            Assert.Single(Stripe.Refunds);                                                           // the refund went to Stripe
            Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync()));
            Assert.Contains(_k.F.Logs.Lines, l => l.Contains("projection of a refund.issued event failed"));
            Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("rf-sp"));                       // type only, never the payload
        }
        finally { await _k.Db(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE order_line_fulfilment_x RENAME TO order_line_fulfilment")); }
    }

    [Fact]
    public async Task P5_A_projection_that_fails_after_it_wrote_leaves_nothing_behind_and_is_replayed_from_the_stored_event()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        var lid = o.Lines.Single().MarketplaceLineId;
        // the notification insert is the LAST thing the projection does, so the projection fails with a database error after it changed the counts and the state.
        // On PostgreSQL a failed statement aborts the whole transaction until it is rolled back to the savepoint: without RollbackToSavepointAsync the replay's enqueue below fails there (CI runs this on PG).
        await _k.Db(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications RENAME TO notifications_x"));
        try
        {
            Assert.Equal("applied", await Evt("refund.issued", o, g, new { refundId = "rf-p5", amountCents = o.TotalCents, shippingCents = 0, full = true, lines = new[] { new { marketplaceLineId = lid, quantity = 2, amountCents = o.TotalCents } } }, id: "re-p5"));
            await Drain();
            Assert.Equal(1, await _k.Db(db => db.OrderRefunds.CountAsync()));                          // the money part committed
            Assert.Equal(0, (await Counters(o)).RefundedQty);                                         // the projection's counts did not
            Assert.Equal("shipped", await Status(g));
            var queued = await _k.Db(db => db.OutboxMessages.Where(m => m.Type == "m5.reproject").Select(m => m.Payload).ToListAsync());
            Assert.Single(queued);
            Assert.DoesNotContain("@", queued[0]);                                                   // an event id, no personal data
        }
        finally { await _k.Db(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications_x RENAME TO notifications")); }
        _k.F.Clock.Advance(TimeSpan.FromMinutes(5));
        await Drain();
        Assert.Equal(2, (await Counters(o)).RefundedQty);                                              // replayed: the ordering row had been rolled back, so it applies
        Assert.Equal("refunded", await Status(g));
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == "refund_issued")));
        Assert.Single(Stripe.Refunds);                                                                // and the money was not moved twice
    }

    // ---------------------------------------------------------------- X (review of the replay)

#pragma warning disable EF1002   // table names are literals of this test file
    private static async Task Rename(AuthKit k, string from, string to) => await k.Db(async db => await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {from} RENAME TO {to}"));
#pragma warning restore EF1002

    private async Task<OutboxMessage> QueuedReplay() => await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "m5.reproject"));

    private static ReprojectHandler Replay(IServiceScope scope) => scope.ServiceProvider.GetServices<IOutboxHandler>().OfType<ReprojectHandler>().Single();

    [Fact]
    public async Task X1_A_failed_replay_leaves_nothing_in_the_change_tracker_for_the_next_handler_of_the_same_scope()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Rename(_k, "notifications", "notifications_x");
        try
        {
            await Evt("refund.issued", o, g, new { refundId = "rf-x1", amountCents = o.TotalCents, shippingCents = 0, full = true, lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2, amountCents = o.TotalCents } } });
            var msg = await QueuedReplay();
            using var scope = _k.F.Services.CreateScope();
            await Assert.ThrowsAnyAsync<Exception>(() => Replay(scope).HandleAsync(msg, default));         // the notifications table is still gone: the replay fails
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);   // nothing half-applied is left to be saved by the next handler
        }
        finally { await Rename(_k, "notifications_x", "notifications"); }
    }

    [Fact]
    public async Task X2_Replaying_the_same_event_twice_changes_nothing_the_second_time()
    {
        var (o, g) = await Placed();
        var lid = o.Lines.Single().MarketplaceLineId;
        await Rename(_k, "order_line_fulfilment", "order_line_fulfilment_x");
        try { await Evt("order.cancelled", o, g, new { partial = true, refundDueCents = 0, lines = new[] { new { marketplaceLineId = lid, quantity = 1 } } }); }   // the projection fails inside its savepoint (the event's own outcome is M3's money part)
        finally { await Rename(_k, "order_line_fulfilment_x", "order_line_fulfilment"); }
        var msg = await QueuedReplay();
        for (var i = 0; i < 2; i++) { using var scope = _k.F.Services.CreateScope(); Assert.Equal(OutboxOutcome.Delivered, (await Replay(scope).HandleAsync(msg, default)).Outcome); }
        Assert.Equal(1, (await Counters(o)).CancelledQty);                                                 // once, not twice (an additive count has no ordering guard of its own)
    }

    [Fact]
    public async Task X5_A_tracked_entity_the_projection_changed_before_it_failed_is_re_read_not_left_modified()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Rename(_k, "notifications", "notifications_x");
        try
        {
            using var scope = _k.F.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var state = await db.SellerOrderStates.FindAsync(g.Id);                                         // tracked BEFORE the projection, like the money part's entities
            Assert.Equal("shipped", state!.Status);
            await using var tx = await db.Database.BeginTransactionAsync();
            var data = JsonSerializer.SerializeToElement(new { order = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number }, refundId = "rf-x5", amountCents = o.TotalCents, shippingCents = 0, full = true,
                lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2, amountCents = o.TotalCents } } });
            var ctx = new Solvelt.Marketplace.Api.Portal.Webhooks.ProjectionContext(db, new Solvelt.Marketplace.Api.Portal.Webhooks.PortalEvent("e-x5", "refund.issued", _k.F.Clock.GetUtcNow().AddMinutes(-1), data),
                scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Portal.Webhooks.ProjectionStore>(), _k.F.Clock);
            await scope.ServiceProvider.GetRequiredService<FulfilmentProjector>().ApplyIsolatedAsync(ctx, true, default);
            Assert.Contains(_k.F.Logs.Lines, l => l.Contains("projection of a refund.issued event failed"));   // it really failed inside the savepoint
            Assert.Equal(("shipped", EntityState.Unchanged), (state.Status, db.Entry(state).State));          // the in-memory "refunded" is gone (setting Unchanged discards a Modified entity's values; the reload also covers one that was SAVED before the failure)
            await tx.RollbackAsync();
        }
        finally { await Rename(_k, "notifications_x", "notifications"); }
    }

    [Fact]
    public async Task X5_A_replay_is_ordered_by_the_ordering_time_a_cancel_that_arrives_late_does_not_undo_a_delivery_and_a_future_dated_event_is_clamped()
    {
        var (o, g) = await Placed();
        var t = _k.F.Clock.GetUtcNow();
        await Rename(_k, "notifications", "notifications_x");
        try { await Evt("order.cancelled", o, g, new { refundDueCents = 0 }, id: "re-late"); }   // projection fails; the replay is queued
        finally { await Rename(_k, "notifications_x", "notifications"); }
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { by = "seller" }, id: "re-deliv", at: t.AddSeconds(-10)));
        Assert.Equal("delivered", await Status(g));
        using (var scope = _k.F.Services.CreateScope()) await Replay(scope).HandleAsync(await QueuedReplay(), default);
        Assert.Equal("delivered", await Status(g));                                                          // the stale cancel is skipped, not forced
    }

    [Fact]
    public async Task X5_A_future_dated_event_is_replayed_with_the_clamped_ordering_time_never_its_own_claim()
    {
        var (o2, g2) = await Placed();
        var t = _k.F.Clock.GetUtcNow();
        await Rename(_k, "notifications", "notifications_x");
        try { Assert.Equal("applied", await Evt("refund.issued", o2, g2, new { refundId = "rf-fut", amountCents = o2.TotalCents, shippingCents = 0, full = true, lines = new[] { new { marketplaceLineId = o2.Lines.Single().MarketplaceLineId, quantity = 2, amountCents = o2.TotalCents } } }, id: "re-future", at: t.AddDays(30))); }
        finally { await Rename(_k, "notifications_x", "notifications"); }
        var fut = (await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == "m5.reproject").ToListAsync())).Single(m => m.Payload!.Contains("re-future"));
        using (var scope = _k.F.Services.CreateScope()) await Replay(scope).HandleAsync(fut, default);
        var at = await _k.Db(db => db.ProjectionStates.AsNoTracking().Where(x => x.LastEventId == "re-future").Select(x => x.LastAppliedAt).ToListAsync());
        Assert.NotEmpty(at);
        Assert.All(at, x => Assert.True(x <= _k.F.Clock.GetUtcNow().AddMinutes(6)));
    }

    // ---------------------------------------------------------------- guest-link order view (decision M5-30)

    private async Task<HttpResponseMessage> GuestGet(string number, string? token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest-orders/{number}");
        if (token is not null) r.Headers.Add("X-Guest-Order", token);
        return await _k.Http.SendAsync(r);
    }

    [Fact]
    public async Task Guest_a_guest_sees_the_progress_of_their_order_never_the_address_or_any_action_and_after_a_claim_only_the_frozen_shape()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", carrier = "USPS", trackingNumber = "T-sh-1", status = "in_transit", position = new { lat = 40.693, lng = -73.99 } });
        // make it a guest order: no account, the checkout address on the order
        await _k.Db(async db => { await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, (Guid?)null).SetProperty(x => x.GuestEmail, "guest@example.com")); return 0; });
        string Token(Guid id) { using var sc = _k.F.Services.CreateScope(); return sc.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Auth.GuestLinks>().Make(id).Token; }
        var token = Token(o.Id);

        var res = await GuestGet(o.Number, token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var text = await res.Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(text).RootElement;
        Assert.False(d.GetProperty("claimed").GetBoolean());
        var group = d.GetProperty("groups")[0];
        Assert.Equal("shipped", group.GetProperty("status").GetString());
        var ship = group.GetProperty("shipments")[0];
        Assert.Equal(("USPS", "T-sh-1", "in_transit"), (ship.GetProperty("carrier").GetString(), ship.GetProperty("trackingNumber").GetString(), ship.GetProperty("trackingStatus").GetString()));
        Assert.Equal(2, group.GetProperty("lines")[0].GetProperty("shippedQty").GetInt32());
        // nothing personal and no action: no address, phone, gift note, driver position, proofs, returns, cancel/return buttons, guest email
        foreach (var leak in new[] { o.ShipLine1, o.ShipName, "guest@example.com", "40.693", "-73.99", "position", "proofCount", "canCancel", "canReturn", "canConfirm", "shipTo", "giftMessage", "phone" })
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);

        // credentials: none, a forged token, and a genuine token for ANOTHER order
        Assert.Equal(HttpStatusCode.Unauthorized, (await GuestGet(o.Number, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GuestGet(o.Number, token + "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GuestGet(o.Number, Token(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GuestGet("MP-NOPE", token)).StatusCode);

        // after a claim the link shows only the frozen shape
        await _k.Db(async db => { await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClaimedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)); return 0; });
        var frozen = JsonDocument.Parse(await (await GuestGet(o.Number, token)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new[] { "claimed", "createdAt", "number", "status" }, frozen.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(frozen.GetProperty("claimed").GetBoolean());
    }

    // ---------------------------------------------------------------- portal C16 / N13: order reconcile

    private async Task<string> DeadEvt(string type, MarketplaceOrder o, OrderGroup g, object? extra, string id)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var body = Body(id, type, _k.F.Clock.GetUtcNow().AddSeconds(-30 + ++_n), data);
        body = "{\"predecessorDead\":true," + body.TrimStart()[1..];
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private HttpResponseMessage State(MarketplaceOrder o, OrderGroup g, string status, int shipped, int cancelled = 0, int refunded = 0, bool shipment = true)
        => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            marketplaceOrderId = o.Number,
            sellerOrders = new[] { new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, status, updatedAt = _k.F.Clock.GetUtcNow(), currency = "USD", totalCents = o.TotalCents, refundedCents = 0,
                lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2, shippedQty = shipped, cancelledQty = cancelled, refundedQty = refunded, returnedQty = 0 } },
                shipments = shipment ? new[] { new { shipmentId = "sh-r1", method = "carrier", carrier = "USPS", trackingNumber = "9400R", status = "in_transit", shippedAt = _k.F.Clock.GetUtcNow().AddMinutes(-5), deliveredAt = (DateTimeOffset?)null } } : [], returns = Array.Empty<object>(), lastEventSequence = 9 } },
        }));

    [Fact]
    public async Task C16_An_event_flagged_predecessorDead_makes_us_read_the_orders_state_once_and_catch_up_never_lowering_a_counter_or_leaving_a_final_state()
    {
        var (o, g) = await Placed();
        _orderState = _ => State(o, g, "shipped", shipped: 2);
        Assert.Equal("applied", await DeadEvt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow }, "dead-1"));        // accepted arrives, "shipped" was lost
        Assert.Equal("duplicate", await DeadEvt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow }, "dead-1"));      // the same signed event again: one reconcile only
        await Drain();
        Assert.Equal(1, _orderReads);
        Assert.Equal("shipped", await Status(g));                                                          // the gap is closed
        var c = await Counters(o);
        Assert.Equal(2, c.ShippedQty);
        var sh = await _k.Db(db => db.OrderShipments.AsNoTracking().SingleAsync());
        Assert.Equal(("sh-r1", "USPS", "9400R"), (sh.PortalShipmentId, sh.Carrier, sh.TrackingNumber));
        Assert.NotNull(sh.ShippedAt);
        // counters never go down: the portal (momentarily) reports less than we hold
        _orderState = _ => State(o, g, "accepted", shipped: 0, shipment: false);
        await DeadEvt("delivery.attempt_failed", o, g, new { attempt = 1, reason = "x" }, "dead-2");
        await Drain();
        Assert.Equal(2, (await Counters(o)).ShippedQty);
        Assert.Equal("shipped", await Status(g));                                                          // an older status never moves us back
        // a final state is never left: our delivered stays delivered even if the portal still says shipped
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { by = "seller" }, at: _k.F.Clock.GetUtcNow().AddSeconds(-1)));
        _orderState = _ => State(o, g, "shipped", shipped: 2);
        await DeadEvt("delivery.attempt_failed", o, g, new { attempt = 2, reason = "x" }, "dead-3");
        await Drain();
        Assert.Equal("delivered", await Status(g));
    }

    [Fact]
    public async Task C16_Observers_see_the_portals_state_after_the_apply_and_one_that_throws_does_not_fail_the_reconcile()
    {
        var (o, g) = await Placed();
        _orderState = _ => State(o, g, "shipped", shipped: 2);
        _k.F.ObserverThrows = true;
        await DeadEvt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow }, "dead-obs");
        await Drain();
        var seen = Assert.Single(_k.F.Observed);
        Assert.Equal(o.Number, seen.Order);
        Assert.Equal(g.SellerOrderId, seen.State.SellerOrders!.Single().SellerOrderId);
        Assert.Equal("shipped", await Status(g));                                                          // applied before the observer ran
        var msg = await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "portal.order-reconcile"));
        Assert.Equal(("delivered", 1), (msg.Status, msg.Attempts));                                        // the observer's failure did not fail or retry the job
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("observer failed"));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(501)]
    [InlineData(405)]
    public async Task C16_A_portal_that_cannot_show_order_state_is_logged_as_an_error_and_not_retried(int status)
    {
        var (o, g) = await Placed();
        _orderState = _ => FakePortal.Json((HttpStatusCode)status, "{\"code\":\"not_implemented\"}");
        await DeadEvt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow }, "dead-na");
        await Drain();
        _k.F.Clock.Advance(TimeSpan.FromHours(3));
        await Drain();
        var msg = await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "portal.order-reconcile"));
        Assert.Equal(("delivered", 1), (msg.Status, msg.Attempts));                                        // done after ONE call: nothing is retried against a portal without the endpoint
        Assert.Equal(1, _orderReads);
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("cannot show order state yet"));                  // the alert
        Assert.Equal("accepted", await Status(g));                                                         // and the event itself was applied as usual
    }

    [Fact]
    public async Task C16_A_portal_that_is_down_is_retried_and_an_ordinary_event_does_not_trigger_a_read()
    {
        var (o, g) = await Placed();
        await Evt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow });                    // no predecessorDead: no read
        await Drain();
        Assert.Equal(0, _orderReads);
        _orderState = _ => FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
        await DeadEvt("delivery.attempt_failed", o, g, new { attempt = 1, reason = "x" }, "dead-down");
        await Drain();
        var msg = await _k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "portal.order-reconcile"));
        Assert.Equal("pending", msg.Status);                                                               // retried later with backoff
    }

    // ---------------------------------------------------------------- M1

    [Fact]
    public async Task M1_A_duplicate_refund_under_another_event_id_and_a_refused_one_are_not_counted_or_announced()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        var lid = o.Lines.Single().MarketplaceLineId;
        object Refund(long cents) => new { refundId = "rf-1", amountCents = cents, shippingCents = 0, lines = new[] { new { marketplaceLineId = lid, quantity = 1, amountCents = cents } } };
        Assert.Equal("applied", await Evt("refund.issued", o, g, Refund(500), id: "re-a"));
        Assert.Equal("applied", await Evt("refund.issued", o, g, Refund(500), id: "re-b"));          // the same refund id again
        Assert.Equal(1, (await Counters(o)).RefundedQty);
        // more than was paid: refused, not counted
        Assert.Equal("skipped", await Evt("refund.issued", o, g, new { refundId = "rf-huge", amountCents = 99_999_999, shippingCents = 0, full = true, lines = new[] { new { marketplaceLineId = lid, quantity = 2, amountCents = 1 } } }));
        Assert.Equal(1, (await Counters(o)).RefundedQty);
        Assert.Equal("shipped", await Status(g));                                                    // not 'refunded' without money
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == "refund_issued")));
    }

    // ---------------------------------------------------------------- M2

    [Fact]
    public async Task M2_An_older_shipped_event_does_not_overwrite_newer_tracking()
    {
        var (o, g) = await Placed();
        var t = _k.F.Clock.GetUtcNow();
        await Ship(o, g, 2, at: t.AddMinutes(-50));
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", carrier = "FedEx", trackingNumber = "NEW-1", trackingUrl = "https://fedex.example/NEW-1", status = "in_transit" }, at: t.AddMinutes(-40));
        // a retried copy of the shipped event, older than the tracking update
        await Evt("order.shipped", o, g, new { shipmentId = "sh-1", carrier = "USPS", trackingNumber = "OLD-1", trackingUrl = "https://usps.example/OLD-1", lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2 } } }, at: t.AddMinutes(-45));
        var s = (await Detail(o)).GetProperty("groups")[0].GetProperty("shipments")[0];
        Assert.Equal(("FedEx", "NEW-1"), (s.GetProperty("carrier").GetString(), s.GetProperty("trackingNumber").GetString()));
    }

    // ---------------------------------------------------------------- M3

    [Fact]
    public async Task M3_A_label_link_must_be_on_the_portals_origin_on_the_label_path_and_alive_in_the_projection_and_the_refresh()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        var rid = await RequestReturn(o, g, 1);
        await Drain();
        await Evt("return.decided", o, g, new { returnId = rid, decision = "approved" });
        var exp = _k.F.Clock.GetUtcNow().AddHours(1);
        foreach (var bad in new[] { "https://evil.example/api/v1/shipping/public/labels/L1?exp=" + exp.ToUnixTimeSeconds(), "https://portal.test/phish?exp=" + exp.ToUnixTimeSeconds(),
            "https://portal.test/api/v1/shipping/public/labels/L1", "https://portal.test/api/v1/shipping/public/labels/L1?exp=1", "http://portal.test/api/v1/shipping/public/labels/L1?exp=" + exp.ToUnixTimeSeconds(),
            "https://portal.test.evil.example/api/v1/shipping/public/labels/L1?exp=" + exp.ToUnixTimeSeconds(), "https://portal.test/api/v1/shipping/public/labels/../x?exp=" + exp.ToUnixTimeSeconds() })
        {
            await Evt("return.label_created", o, g, new { returnId = rid, labelUrl = bad, labelUrlExpiresAt = exp });
            Assert.Null(await _k.Db(db => db.OrderReturns.Select(r => r.LabelUrl).SingleAsync()));
            _labelAnswer = JsonSerializer.Serialize(new { labelUrl = bad, labelUrlExpiresAt = exp, carrier = "USPS" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/{rid}/label-link")).StatusCode);
            Assert.Null(await _k.Db(db => db.OrderReturns.Select(r => r.LabelUrl).SingleAsync()));
        }
        var good = new LabelHelper(_k.F).Url("L1", exp);
        await Evt("return.label_created", o, g, new { returnId = rid, labelUrl = good, labelUrlExpiresAt = exp });
        Assert.Equal(good, await _k.Db(db => db.OrderReturns.Select(r => r.LabelUrl).SingleAsync()));
    }

    [Fact]
    public async Task M3_Label_refresh_of_another_buyers_real_return_is_a_404()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        var rid = await RequestReturn(o, g, 1);
        await Drain();
        await Evt("return.decided", o, g, new { returnId = rid, decision = "approved" });
        var other = await _k.VerifiedSession("other@example.com");
        _labelAnswer = JsonSerializer.Serialize(new { labelUrl = new LabelHelper(_k.F).Url("L1"), labelUrlExpiresAt = _k.F.Clock.GetUtcNow().AddHours(1) });
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/{rid}/label-link", session: other)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/{rid}/label-link")).StatusCode);
    }

    // ---------------------------------------------------------------- M4

    [Fact]
    public async Task M4_Redaction_drains_a_backlog_behind_open_orders_and_waits_for_an_open_return_and_the_90_day_boundary()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Evt("order.delivered", o, g, new { by = "seller" });
        var rid = await RequestReturn(o, g, 1);        // an open return keeps the address
        async Task<FulfilmentMaintenance.Result> Run() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromDays(89));
        Assert.Equal(0, (await Run()).OrdersRedacted);                                              // 89 days: too early
        _k.F.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, (await Run()).OrdersRedacted);                                              // old enough, but the return is still open
        await Evt("return.decided", o, g, new { returnId = rid, decision = "rejected" }, at: _k.F.Clock.GetUtcNow().AddMinutes(-1));
        Assert.Equal(1, (await Run()).OrdersRedacted);
        // many open orders in front do not stall closed ones behind them
        for (var i = 0; i < 3; i++)
        {
            var a = Guid.NewGuid();
            await _k.Db<int>(async db =>
            {
                var tmpl = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == o.Id);
                tmpl.Id = a; tmpl.Number = "MP-OPEN-" + i; tmpl.ShipName = "Open Person"; tmpl.ActiveKey = null; tmpl.PaymentIntentId = null; tmpl.CreatedAt = tmpl.CreatedAt.AddDays(-10 - i);
                db.Orders.Add(tmpl);
                db.OrderGroups.Add(new OrderGroup { Id = Guid.NewGuid(), OrderId = a, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), SellerOrderId = Guid.NewGuid() });
                return await db.SaveChangesAsync();
            });
        }
        Assert.Equal(0, (await Run()).OrdersRedacted);                                              // open orders (no state) are never redacted
    }

    [Fact]
    public async Task M4_The_90_day_boundary_is_exact()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        await Evt("order.delivered", o, g, new { by = "seller" });
        var closedAt = await _k.Db(async db => (await db.SellerOrderStates.SingleAsync(s => s.GroupId == g.Id)).UpdatedAt);
        async Task<int> Run() { using var s = _k.F.Services.CreateScope(); return (await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync()).OrdersRedacted; }
        _k.F.Clock.Advance(closedAt.AddDays(90) - _k.F.Clock.GetUtcNow() - TimeSpan.FromMinutes(1));
        Assert.Equal(0, await Run());                                                                // 1 minute short of 90 days
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await Run());                                                                // exactly 90 days
    }

    // ---------------------------------------------------------------- survivors and lows

    [Fact]
    public async Task S1_The_return_cap_counts_returned_and_refunded_units_too()
    {
        var (o, g) = await Placed();
        await Ship(o, g, 2);
        var rid = await RequestReturn(o, g, 1);
        await Evt("return.decided", o, g, new { returnId = rid, decision = "approved" });
        await Evt("return.received", o, g, new { returnId = rid });                                  // 1 returned
        Assert.Equal("422", await RequestReturn(o, g, 2));
        var lid = o.Lines.Single().MarketplaceLineId;
        await Evt("refund.issued", o, g, new { refundId = "rf-q", amountCents = 100, shippingCents = 0, lines = new[] { new { marketplaceLineId = lid, quantity = 1, amountCents = 100 } } });   // 1 refunded
        Assert.Equal("422", await RequestReturn(o, g, 1));                                           // shipped 2 - returned 1 - refunded 1 = 0
    }

    [Fact]
    public async Task S2_Delivery_confirmation_needs_a_fully_shipped_order()
    {
        var (o, g) = await Placed();
        string Url() => $"/api/v1/orders/{o.Number}/delivery-confirmations";
        object Body() => new { sellerOrderNumber = g.SellerOrderNumber };
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, Url(), Body())).StatusCode);                         // new
        await Evt("order.accepted", o, g);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, Url(), Body())).StatusCode);                         // accepted
        await Ship(o, g, 1);
        await Evt("order.shipped", o, g, new { partial = true, shipmentId = "sh-p", lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 1 } } });
        Assert.Equal("partially_shipped", await Status(g));
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, Url(), Body())).StatusCode);                         // partially shipped
        await Evt("order.delivered", o, g, new { by = "seller" });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, Url(), Body())).StatusCode);                               // delivered: idempotent, nothing queued
        Assert.Equal(0, await Outbox("portal.delivery-confirmation"));
    }

    [Fact]
    public async Task S3_A_cancelled_seller_order_is_terminal()
    {
        var (o, g) = await Placed();
        await Evt("order.cancelled", o, g, new { refundDueCents = 0 });
        Assert.Equal("skipped", await Evt("order.accepted", o, g));
        Assert.Equal("skipped", await Evt("order.delivered", o, g));
        Assert.Equal("cancelled", await Status(g));
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "other" })).StatusCode);
    }

    [Fact]
    public async Task L1_Malformed_json_answers_400_with_no_store_and_the_shipped_time_is_kept_when_shipped_arrives_after_delivered()
    {
        var (o, g) = await Placed();
        var bad = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", raw: "{ not json");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("no-store", bad.Headers.CacheControl!.ToString());
        Assert.Contains("no-store", (await Call(HttpMethod.Put, "/api/v1/notifications/preferences", raw: "[[")).Headers.CacheControl!.ToString());
        var t = _k.F.Clock.GetUtcNow();
        await Evt("order.delivered", o, g, new { by = "seller" }, at: t.AddMinutes(-10));
        await Ship(o, g, 2, at: t.AddMinutes(-20));
        Assert.NotNull(await _k.Db(async db => (await db.SellerOrderStates.SingleAsync(s => s.GroupId == g.Id)).ShippedAt));
    }

    [Fact]
    public async Task L2_Tracking_for_an_unknown_shipment_or_a_return_parcel_does_not_show_as_a_forward_shipment()
    {
        var (o, g) = await Placed();
        await Evt("order.tracking_updated", o, g, new { shipmentId = "ghost", status = "in_transit" });
        await Evt("order.tracking_updated", o, g, new { shipmentId = "ret-1", kind = "return", returnId = "RT-X", status = "in_transit" });
        Assert.Equal(0, (await Detail(o)).GetProperty("groups")[0].GetProperty("shipments").GetArrayLength());
        Assert.Equal(0, await _k.Db(db => db.OrderShipments.CountAsync(s => s.PortalShipmentId == "ret-1")));
        await Ship(o, g, 2, "ghost");                                                                // the shipped event arrives: it shows now
        Assert.Equal(1, (await Detail(o)).GetProperty("groups")[0].GetProperty("shipments").GetArrayLength());
    }

    [Fact]
    public async Task L3_The_list_cursor_has_a_tie_break_so_orders_made_in_one_instant_are_not_skipped()
    {
        await Login();
        var numbers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await AddToCart("challah-classic");
            var (number, intent, total) = await Checkout(new { address = Address(), giftMessage = "g" + i });
            await Pay(number, intent, total);
            numbers.Add(number);
        }
        await Drain();
        var same = _k.F.Clock.GetUtcNow();
        await _k.Db(async db => await db.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, same)));   // the same instant
        var seen = new List<string>();
        string? q = "?limit=1";
        for (var i = 0; i < 5 && q is not null; i++)
        {
            var page = await (await Call(HttpMethod.Get, "/api/v1/orders" + q)).Content.ReadFromJsonAsync<JsonElement>();
            seen.AddRange(page.GetProperty("orders").EnumerateArray().Select(x => x.GetProperty("number").GetString()!));
            q = page.GetProperty("next").ValueKind == JsonValueKind.Object ? $"?limit=1&before={Uri.EscapeDataString(page.GetProperty("next").GetProperty("before").GetDateTimeOffset().ToString("O"))}&beforeId={page.GetProperty("next").GetProperty("beforeId").GetGuid()}" : null;
        }
        Assert.Equal(numbers.OrderBy(x => x), seen.OrderBy(x => x));                                // each exactly once
    }

    private sealed class LabelHelper(Infrastructure.TestFactory f)
    {
        public string Url(string id, DateTimeOffset? exp = null) => $"https://portal.test/api/v1/shipping/public/labels/{id}?exp={(exp ?? f.Clock.GetUtcNow().AddHours(1)).ToUnixTimeSeconds()}&sig=x";
    }
}
