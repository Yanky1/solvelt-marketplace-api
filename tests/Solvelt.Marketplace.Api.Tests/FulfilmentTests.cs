using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M5 slice 1: the portal's order events as the buyer sees them, and the buyer's actions (cancel, return, confirm delivery) towards the portal.</summary>
public sealed class FulfilmentTests : OrderFlowBase
{
    private int _n;
    private string _cancelAnswer = "requested", _returnAnswer = "requested", _deliveryAnswer = "delivered";

    public FulfilmentTests()
    {
        _onOther = c =>
        {
            string Answer(string result) => JsonSerializer.Serialize(new { orders = _sellerOrders.Values.Select(v => new { number = v.Number, result, message = result == "rejected" ? "Not allowed." : (string?)null }) });
            if (c.PathAndQuery.EndsWith("/cancel-requests")) return FakePortal.Json(HttpStatusCode.OK, Answer(_cancelAnswer));
            if (c.PathAndQuery.EndsWith("/return-requests")) return FakePortal.Json(HttpStatusCode.OK, Answer(_returnAnswer));
            if (c.PathAndQuery.EndsWith("/delivery-confirmations")) return FakePortal.Json(HttpStatusCode.OK, Answer(_deliveryAnswer));
            if (c.PathAndQuery.EndsWith("/label-link")) return FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { labelUrl = "https://portal.test/label/1?sig=x", labelUrlExpiresAt = _k.F.Clock.GetUtcNow().AddHours(24), trackingNumber = "T1", carrier = "USPS" }));
            return null;
        };
    }

    private async Task<MarketplaceOrder> Placed()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        return await Order(number);
    }

    private static OrderGroup Bakery(MarketplaceOrder o) => o.Groups.Single(g => g.ItemsCents == 1300);
    private static OrderGroup JudGroup(MarketplaceOrder o) => o.Groups.Single(g => g.ItemsCents == 4200);
    private static OrderLine LineOf(MarketplaceOrder o, OrderGroup g) => o.Lines.Single(l => l.GroupId == g.Id);

    private async Task<string> Evt(string type, MarketplaceOrder o, OrderGroup g, object? extra = null, DateTimeOffset? at = null, string? id = null, Guid? sellerOrderId = null)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = sellerOrderId ?? g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body(id ?? $"fe-{++_n:D3}", type, at ?? _k.F.Clock.GetUtcNow().AddSeconds(_n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private Task<string> Ship(MarketplaceOrder o, OrderGroup g, int qty, string shipment = "sh-1", bool partial = false, DateTimeOffset? at = null)
        => Evt("order.shipped", o, g, new { partial, shipmentId = shipment, carrier = "USPS", service = "Priority", trackingNumber = "9400-" + shipment, trackingUrl = "https://tools.usps.com/x", shippedAt = DateTimeOffset.UtcNow,
            lines = new[] { new { marketplaceLineId = LineOf(o, g).MarketplaceLineId, quantity = qty } } }, at);

    private async Task<HttpResponseMessage> Call(HttpMethod m, string path, object? body = null, string? session = null, string? idem = null)
    {
        var r = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        if (idem is not null) r.Headers.Add("Idempotency-Key", idem);
        return await _k.Http.SendAsync(r);
    }

    private async Task<JsonElement> Detail(string number, string? session = null)
    {
        var res = await Call(HttpMethod.Get, $"/api/v1/orders/{number}", session: session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement GroupOf(JsonElement detail, OrderGroup g) => detail.GetProperty("groups").EnumerateArray().Single(x => x.GetProperty("sellerOrderNumber").GetString() == g.SellerOrderNumber);

    private Task<FulfilmentRow> Row(Guid groupId) => _k.Db(async db => new FulfilmentRow(
        (await db.SellerOrderStates.AsNoTracking().SingleOrDefaultAsync(s => s.GroupId == groupId))?.Status,
        await db.OrderLineFulfilments.AsNoTracking().Join(db.OrderLines, f => f.LineId, l => l.Id, (f, l) => new { f, l.GroupId }).Where(x => x.GroupId == groupId).Select(x => x.f).ToListAsync()));
    private sealed record FulfilmentRow(string? Status, List<OrderLineFulfilment> Lines);

    // ------------------------------------------------------------------ projections

    [Fact]
    public async Task Shipping_and_delivery_show_up_on_the_order_and_a_delivered_order_is_never_reopened()
    {
        var o = await Placed();
        var g = Bakery(o);
        Assert.Equal("new", GroupOf(await Detail(o.Number), g).GetProperty("status").GetString());
        Assert.Equal("applied", await Evt("order.accepted", o, g, new { acceptedAt = DateTimeOffset.UtcNow, auto = false }));
        Assert.Equal("applied", await Ship(o, g, 2));
        var shipped = GroupOf(await Detail(o.Number), g);
        Assert.Equal("shipped", shipped.GetProperty("status").GetString());
        var s = shipped.GetProperty("shipments").EnumerateArray().Single();
        Assert.Equal(("USPS", "9400-sh-1"), (s.GetProperty("carrier").GetString(), s.GetProperty("trackingNumber").GetString()));
        Assert.Equal(2, shipped.GetProperty("lines")[0].GetProperty("shippedQty").GetInt32());
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { deliveredAt = DateTimeOffset.UtcNow, by = "seller" }));
        Assert.Equal("delivered", GroupOf(await Detail(o.Number), g).GetProperty("status").GetString());
        // a cancellation and a hold after delivery: terminal states are not reopened
        Assert.Equal("skipped", await Evt("order.cancelled", o, g, new { refundDueCents = 0 }));
        Assert.Equal("skipped", await Evt("order.held", o, g));
        Assert.Equal("delivered", (await Row(g.Id)).Status);
        // the other seller order is untouched
        Assert.Equal("new", GroupOf(await Detail(o.Number), JudGroup(o)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_in_the_wrong_order_do_not_overwrite_newer_state_but_the_parcel_is_still_recorded()
    {
        var o = await Placed();
        var g = Bakery(o);
        var t = _k.F.Clock.GetUtcNow();
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { by = "buyer" }, at: t.AddMinutes(3)));
        Assert.Equal("skipped", await Evt("order.accepted", o, g, at: t.AddMinutes(1)));            // older, and the order is delivered
        Assert.Equal("applied", await Ship(o, g, 2, at: t.AddMinutes(2)));                          // older than delivered: the parcel is a fact, the status step is not taken
        var d = GroupOf(await Detail(o.Number), g);
        Assert.Equal("delivered", d.GetProperty("status").GetString());
        Assert.Equal(1, d.GetProperty("shipments").GetArrayLength());
        Assert.Equal(2, d.GetProperty("lines")[0].GetProperty("shippedQty").GetInt32());
    }

    [Fact]
    public async Task An_event_is_matched_by_our_order_id_and_the_portal_id_never_by_tracking_number()
    {
        var o = await Placed();
        var g = Bakery(o);
        // the right marketplace id with a seller order id that is none of ours
        Assert.Equal("skipped", await Evt("order.shipped", o, g, new { shipmentId = "x", lines = Array.Empty<object>() }, sellerOrderId: Guid.NewGuid()));
        // another seller's tracking number on this order's shipment event changes nothing about matching: only the named seller order is touched
        Assert.Equal("applied", await Ship(o, JudGroup(o), 1, "sh-j"));
        Assert.Equal("new", (await Row(g.Id)).Status ?? "new");
        Assert.Equal("shipped", (await Row(JudGroup(o).Id)).Status);
        // an unknown marketplace order
        var (status, json) = await Post(_k.Http, Request(_k.F, Body("fe-ghost", "order.shipped", _k.F.Clock.GetUtcNow(), new { order = new { sellerOrderId = g.SellerOrderId, marketplaceOrderId = "MP-20990101-ZZZZZZZZ" }, shipmentId = "z" })));
        Assert.Equal("skipped", StatusOf(json));
        Assert.Equal(0, await _k.Db(db => db.OrderShipments.CountAsync(s => s.PortalShipmentId == "z")));
    }

    [Fact]
    public async Task An_event_before_the_seller_orders_are_stored_waits_and_is_applied_when_they_are()
    {
        await Login();
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        var gate = false;
        _onOrder = c => gate ? Created(c, 201, true) : throw new HttpRequestException("down");
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        var g = o.Groups.Single();
        var soid = Guid.NewGuid();
        Assert.Equal("parked", await Evt("order.accepted", o, g, sellerOrderId: soid));
        gate = true;
        _sellerOrders[$"{number}|{g.SellerId}|{g.BrandId}"] = (soid, "SO-2026-000099");
        _k.F.Clock.Advance(TimeSpan.FromSeconds(31));
        await Drain(); await Drain();
        Assert.Equal("accepted", (await Row(g.Id)).Status);
    }

    [Fact]
    public async Task Cancelled_by_the_portal_closes_the_open_request_and_a_partial_remainder_cancel_does_not_end_the_order()
    {
        var o = await Placed();
        var g = Bakery(o);
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "changed_mind" })).StatusCode);
        await Drain();
        // partial remainder: the order continues
        Assert.Equal("applied", await Ship(o, g, 1, partial: true));
        await Evt("order.cancelled", o, g, new { partial = true, refundDueCents = 0, lines = new[] { new { marketplaceLineId = LineOf(o, g).MarketplaceLineId, quantity = 1 } } });   // the event's outcome is M3's money part ("no refund due"); the projection is what we look at
        var f = await Row(g.Id);
        Assert.Equal(("partially_shipped", 1), (f.Status, f.Lines.Single().CancelledQty));
        await Evt("order.cancelled", o, g, new { refundDueCents = 0 });
        Assert.Equal("cancelled", (await Row(g.Id)).Status);
        Assert.Equal(CancelRequestStatus.Approved, await _k.Db(db => db.OrderCancelRequests.Select(r => r.Status).SingleAsync()));
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task A_cancel_request_is_stored_first_sent_once_and_a_second_click_is_the_same_request()
    {
        var o = await Placed();
        var g = Bakery(o);
        var body = new { sellerOrderNumber = g.SellerOrderNumber, reason = "changed_mind", note = "Sorry" };
        var a = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", body);
        var b = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", body);
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (a.StatusCode, b.StatusCode));
        var idA = (await a.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString();
        Assert.Equal(idA, (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetString());
        Assert.Equal(1, await _k.Db(db => db.OrderCancelRequests.CountAsync()));
        Assert.Empty(Portal($"orders/{o.Number}/cancel-requests"));       // nothing was sent from the request thread: it is in the outbox
        await Drain();
        var sent = Assert.Single(Portal($"orders/{o.Number}/cancel-requests"));
        var j = JsonDocument.Parse(sent.BodyText).RootElement;
        Assert.Equal((idA, "changed_mind", g.SellerOrderNumber), (j.GetProperty("requestId").GetString(), j.GetProperty("reason").GetString(), j.GetProperty("sellerOrderNumber").GetString()));
        Assert.Equal("requested", GroupOf(await Detail(o.Number), g).GetProperty("cancelRequest").GetProperty("status").GetString());
        // the seller says no
        Assert.Equal("applied", await Evt("cancellation.decided", o, g, new { requestId = idA, decision = "rejected", note = "Already packed" }));
        var after = GroupOf(await Detail(o.Number), g);
        Assert.Equal(("rejected", "Already packed"), (after.GetProperty("cancelRequest").GetProperty("status").GetString(), after.GetProperty("cancelRequest").GetProperty("decisionNote").GetString()));
        // a decision naming a request of another seller order changes nothing
        Assert.Equal("skipped", await Evt("cancellation.decided", o, JudGroup(o), new { requestId = idA, decision = "rejected" }));
    }

    [Fact]
    public async Task Parallel_cancel_clicks_make_one_request_and_a_shipped_order_cannot_be_cancelled()
    {
        var o = await Placed();
        var g = Bakery(o);
        var calls = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "other" })));
        Assert.All(calls, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await _k.Db(db => db.OrderCancelRequests.CountAsync()));
        Assert.Equal(1, await Outbox("portal.cancel-request"));
        Assert.Equal("applied", await Ship(o, g, 2));
        var j = JudGroup(o);
        Assert.Equal("applied", await Ship(o, j, 1, "sh-j"));
        var res = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = j.SellerOrderNumber, reason = "other" });
        Assert.Equal((HttpStatusCode.Conflict, "not_cancellable"), (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()));
        // validation: reason, and with two seller orders the caller must say which
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "because" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { reason = "other" })).StatusCode);
    }

    [Fact]
    public async Task The_portal_declining_a_cancel_because_it_shipped_is_shown_to_the_buyer()
    {
        var o = await Placed();
        var g = Bakery(o);
        _cancelAnswer = "auto_rejected";
        await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "other" });
        await Drain();
        var d = GroupOf(await Detail(o.Number), g);
        Assert.Equal("rejected", d.GetProperty("cancelRequest").GetProperty("status").GetString());
        Assert.True(d.GetProperty("canCancel").GetBoolean());          // closed: the buyer may ask again while the status still allows it
    }

    // ------------------------------------------------------------------ returns

    [Fact]
    public async Task A_return_cannot_ask_for_more_than_shipped_minus_returned_minus_refunded_even_in_parallel()
    {
        var o = await Placed();
        var g = Bakery(o);
        var lid = LineOf(o, g).MarketplaceLineId;
        await Ship(o, g, 2);
        object Req(int q) => new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = lid, quantity = q } } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", Req(3))).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", Req(1))));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(2, (await Row(g.Id)).Lines.Single().ReturnPendingQty);
        Assert.Equal(2, await _k.Db(db => db.OrderReturns.CountAsync()));
        Assert.Equal(0, GroupOf(await Detail(o.Number), g).GetProperty("lines")[0].GetProperty("returnableQty").GetInt32());
    }

    [Fact]
    public async Task A_rejected_return_frees_its_units_and_a_received_one_counts_as_returned()
    {
        var o = await Placed();
        var g = Bakery(o);
        var lid = LineOf(o, g).MarketplaceLineId;
        await Ship(o, g, 2);
        var body = new { sellerOrderNumber = g.SellerOrderNumber, reason = "wrong_item", comment = "Got plain", lines = new[] { new { lineId = lid, quantity = 2 } } };
        _returnAnswer = "rejected";
        var r1 = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", body);
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        await Drain();
        Assert.Equal(("rejected", 0), (await _k.Db(db => db.OrderReturns.Select(r => r.Status).SingleAsync()), (await Row(g.Id)).Lines.Single().ReturnPendingQty));
        _returnAnswer = "requested";
        var id = (await (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", body)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnId").GetString();
        await Drain();
        var sent = JsonDocument.Parse(Portal($"orders/{o.Number}/return-requests").Last().BodyText).RootElement;
        Assert.Equal((id, 2), (sent.GetProperty("returnId").GetString(), sent.GetProperty("lines")[0].GetProperty("quantity").GetInt32()));
        Assert.Equal("applied", await Evt("return.decided", o, g, new { returnId = id, decision = "approved" }));
        Assert.Equal("applied", await Evt("return.received", o, g, new { returnId = id, restocked = true }));
        await Evt("return.received", o, g, new { returnId = id }, id: "fe-again-1");   // a repeated event under another id is a no-op, not a second count
        var f = (await Row(g.Id)).Lines.Single();
        Assert.Equal((0, 2), (f.ReturnPendingQty, f.ReturnedQty));
        // a return event of another seller order's id cannot touch it
        Assert.Equal("skipped", await Evt("return.decided", o, JudGroup(o), new { returnId = id, decision = "rejected" }));
    }

    [Fact]
    public async Task The_same_return_sent_twice_with_one_idempotency_key_is_one_return()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Ship(o, g, 2);
        var body = new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = LineOf(o, g).MarketplaceLineId, quantity = 1 } } };
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", body, idem: "click-1")));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await _k.Db(db => db.OrderReturns.CountAsync()));
        Assert.Equal(1, (await Row(g.Id)).Lines.Single().ReturnPendingQty);
        Assert.Equal(1, await Outbox("portal.return-request"));
    }

    [Fact]
    public async Task A_return_names_only_lines_of_that_seller_order_and_only_once_shipped()
    {
        var o = await Placed();
        var g = Bakery(o);
        var other = LineOf(o, JudGroup(o)).MarketplaceLineId;
        // nothing shipped yet
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = LineOf(o, g).MarketplaceLineId, quantity = 1 } } })).StatusCode);
        await Ship(o, g, 2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = other, quantity = 1 } } })).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.OrderReturns.CountAsync()));
    }

    [Fact]
    public async Task A_label_that_is_already_expired_is_not_kept_and_an_approved_return_can_ask_for_a_fresh_one()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Ship(o, g, 2);
        var id = (await (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/return-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "damaged", lines = new[] { new { lineId = LineOf(o, g).MarketplaceLineId, quantity = 1 } } })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnId").GetString();
        await Drain();
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/{id}/label-link")).StatusCode);   // not approved yet
        await Evt("return.decided", o, g, new { returnId = id, decision = "approved" });
        await Evt("return.label_created", o, g, new { returnId = id, labelUrl = "https://portal.test/old", labelUrlExpiresAt = _k.F.Clock.GetUtcNow().AddMinutes(-1), carrier = "USPS" });
        await Evt("return.label_created", o, g, new { returnId = id, labelUrl = "javascript:alert(1)", labelUrlExpiresAt = _k.F.Clock.GetUtcNow().AddHours(1) });
        Assert.Null(await _k.Db(db => db.OrderReturns.Select(r => r.LabelUrl).SingleAsync()));
        var res = await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/{id}/label-link");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("https://portal.test/label/1?sig=x", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("labelUrl").GetString());
        var shown = GroupOf(await Detail(o.Number), g).GetProperty("returns")[0].GetProperty("label");
        Assert.Equal("USPS", shown.GetProperty("carrier").GetString());
        _k.F.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(JsonValueKind.Null, GroupOf(await Detail(o.Number), g).GetProperty("returns")[0].GetProperty("label").ValueKind);   // never shown past expiry
    }

    // ------------------------------------------------------------------ delivery confirmation

    [Fact]
    public async Task Confirming_delivery_uses_server_time_and_parallel_clicks_send_one_call()
    {
        var o = await Placed();
        var g = Bakery(o);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/delivery-confirmations", new { sellerOrderNumber = g.SellerOrderNumber })).StatusCode);   // not shipped
        await Ship(o, g, 2);
        var now = _k.F.Clock.GetUtcNow();
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/delivery-confirmations",
            new { sellerOrderNumber = g.SellerOrderNumber, deliveredAt = "2001-01-01T00:00:00Z" })));
        Assert.All(rs, r => Assert.True(r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        await Drain();
        var sent = Assert.Single(Portal($"orders/{o.Number}/delivery-confirmations"));
        Assert.DoesNotContain("deliveredAt", sent.BodyText);
        Assert.DoesNotContain("2001", sent.BodyText);
        Assert.Equal(now, await _k.Db(async db => (await db.SellerOrderStates.SingleAsync(s => s.GroupId == g.Id)).BuyerConfirmedAt));
    }

    [Fact]
    public async Task A_confirmation_the_portal_refuses_can_be_tried_again()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Ship(o, g, 2);
        _deliveryAnswer = "rejected";
        await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/delivery-confirmations", new { sellerOrderNumber = g.SellerOrderNumber });
        await Drain();
        Assert.Null(await _k.Db(async db => (await db.SellerOrderStates.SingleAsync(s => s.GroupId == g.Id)).BuyerConfirmedAt));
        _deliveryAnswer = "delivered";
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/delivery-confirmations", new { sellerOrderNumber = g.SellerOrderNumber })).StatusCode);
    }

    // ------------------------------------------------------------------ other delivery facts and refunds

    [Fact]
    public async Task Failed_attempts_and_a_switch_to_local_delivery_are_shown_and_replays_do_not_inflate_them()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Evt("order.delivery_method_changed", o, g, new { method = "local", deliveryCode = "x", feeCents = 500 });
        await Ship(o, g, 2);
        await Evt("delivery.attempt_failed", o, g, new { shipmentId = "sh-1", attempt = 1, reason = "no_one_home", occurredAt = DateTimeOffset.UtcNow }, id: "fe-att");
        await Evt("delivery.attempt_failed", o, g, new { shipmentId = "sh-1", attempt = 1, reason = "no_one_home" }, id: "fe-att-2");
        var d = GroupOf(await Detail(o.Number), g);
        Assert.Equal(("local", 1, "no_one_home"), (d.GetProperty("deliveryKind").GetString(), d.GetProperty("deliveryAttemptsFailed").GetInt32(), d.GetProperty("lastAttemptFailedReason").GetString()));
    }

    [Fact]
    public async Task A_voided_shipment_no_longer_counts_as_shipped_and_a_full_refund_ends_the_order_as_refunded()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Ship(o, g, 2, "sh-1");
        await Evt("shipment.voided", o, g, new { shipmentId = "sh-1" });
        Assert.Equal(0, (await Row(g.Id)).Lines.Single().ShippedQty);
        await Ship(o, g, 2, "sh-2");
        await Evt("order.delivered", o, g, new { by = "seller" });
        Assert.Equal("applied", await Evt("refund.issued", o, g, new { refundId = "rf-1", amountCents = 1300, shippingCents = 0, full = true, lines = new[] { new { marketplaceLineId = LineOf(o, g).MarketplaceLineId, quantity = 2, amountCents = 1300 } } }));
        var f = await Row(g.Id);
        Assert.Equal(("refunded", 2), (f.Status, f.Lines.Single().RefundedQty));
        var d = GroupOf(await Detail(o.Number), g);
        Assert.Equal(1, d.GetProperty("refunds").GetArrayLength());         // the money side is M3's refund row, only shown here
        Assert.False(d.GetProperty("canReturn").GetBoolean());
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Another_buyers_order_is_a_404_on_every_route_and_the_list_shows_only_your_own()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Ship(o, g, 2);
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/orders/{o.Number}", session: other)).StatusCode);
        foreach (var path in new[] { "cancel-requests", "return-requests", "delivery-confirmations" })
            Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/{path}", new { sellerOrderNumber = g.SellerOrderNumber, reason = "other", lines = new[] { new { lineId = "x", quantity = 1 } } }, session: other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/returns/RT-X/label-link", session: other)).StatusCode);
        var list = await (await Call(HttpMethod.Get, "/api/v1/orders", session: other)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetProperty("orders").GetArrayLength());
        var mine = await (await Call(HttpMethod.Get, "/api/v1/orders")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(o.Number, mine.GetProperty("orders")[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.GetAsync("/api/v1/orders")).StatusCode);
        var res = await Call(HttpMethod.Get, $"/api/v1/orders/{o.Number}");
        Assert.Contains("private", res.Headers.CacheControl!.ToString());
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public async Task Ninety_days_after_every_seller_order_closed_the_address_and_the_buyers_notes_are_redacted()
    {
        var o = await Placed();
        var g = Bakery(o);
        await Call(HttpMethod.Post, $"/api/v1/orders/{o.Number}/cancel-requests", new { sellerOrderNumber = g.SellerOrderNumber, reason = "other", note = "My private note" });
        await Evt("order.cancelled", o, g, new { refundDueCents = 0 });
        async Task<FulfilmentMaintenance.Result> Run() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromDays(100));
        Assert.Equal(0, (await Run()).OrdersRedacted);                         // the other seller order is still open: nothing is redacted
        Assert.Equal("Jane Brown", (await Order(o.Number)).ShipName);
        await Evt("order.cancelled", o, JudGroup(o), new { refundDueCents = 0 });
        Assert.Equal(0, (await Run()).OrdersRedacted);                         // closed just now
        _k.F.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, (await Run()).OrdersRedacted);
        var after = await Order(o.Number);
        Assert.Equal(("Redacted", "Redacted", null, "", null), (after.ShipName, after.ShipLine1, after.Phone, after.ShipPostalCode, after.GiftMessage));
        Assert.Null(await _k.Db(db => db.OrderCancelRequests.Select(r => r.Note).SingleAsync()));
        Assert.Equal(0, (await Run()).OrdersRedacted);                         // once
    }
}
