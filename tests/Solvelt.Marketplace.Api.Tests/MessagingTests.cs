using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M5 slice 4a: buyer ⇄ seller messaging relay (threads, messages, receipts, reports).</summary>
public sealed class MessagingTests : OrderFlowBase
{
    private int _n;
    private HttpStatusCode _messageAnswer = HttpStatusCode.Created;
    private HttpStatusCode _reportAnswer = HttpStatusCode.Created;

    public MessagingTests()
    {
        _onOther = c =>
        {
            if (c.PathAndQuery == "/api/internal/v1/messages") return FakePortal.Json(_messageAnswer, _messageAnswer is HttpStatusCode.Created or HttpStatusCode.OK ? "{\"duplicate\":false}" : "{\"code\":\"not_found\"}");
            if (c.PathAndQuery.EndsWith("/receipts")) return FakePortal.Json(HttpStatusCode.OK, "{\"status\":\"delivered\"}");
            if (c.PathAndQuery == "/api/internal/v1/content/reports") return FakePortal.Json(_reportAnswer, "{\"status\":\"open\"}", r => { if (_reportAnswer == HttpStatusCode.TooManyRequests) r.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); });
            return null;
        };
    }

    private async Task<(MarketplaceOrder O, OrderGroup G)> Placed()
    {
        await Login();
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        return (o, o.Groups.Single());
    }

    private async Task<HttpResponseMessage> Call(HttpMethod m, string path, object? body = null, string? session = null)
    {
        var r = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    private async Task<string> SellerMessage(MarketplaceOrder o, OrderGroup g, string text, string? pid = null, string? conv = null, string? sellerOrderNumber = "same", string kind = "order", object? extra = null, string? eventId = null)
    {
        var data = new Dictionary<string, object?> { ["messageId"] = pid ?? $"pm-{++_n}", ["kind"] = kind, ["text"] = text, ["sentAt"] = _k.F.Clock.GetUtcNow().AddSeconds(30),
            ["marketplaceConversationId"] = conv, ["marketplaceOrderId"] = o?.Number, ["sellerOrderNumber"] = sellerOrderNumber == "same" ? g?.SellerOrderNumber : sellerOrderNumber };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body(eventId ?? $"me-{++_n:D3}", "message.sent", _k.F.Clock.GetUtcNow().AddSeconds(-500 + _n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private IReadOnlyList<JsonElement> PortalMessages() => Portal("messages").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).ToList();

    // ------------------------------------------------------------------ buyer -> seller

    [Fact]
    public async Task A_buyer_message_is_stored_first_sent_once_with_our_ids_and_the_thread_is_one_per_seller_order()
    {
        var (o, g) = await Placed();
        var res = await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "  Is it nut free?  " });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var j = await Json(res);
        var threadId = j.GetProperty("threadId").GetGuid();
        Assert.Equal("pending", j.GetProperty("status").GetString());
        Assert.Empty(PortalMessages());                                                          // nothing is sent from the request thread
        await Drain();
        var sent = Assert.Single(PortalMessages());
        var t = await _k.Db(db => db.MessageThreads.AsNoTracking().SingleAsync());
        Assert.Equal((threadId, "order"), (t.Id, t.Kind));
        Assert.Equal(("Is it nut free?", "order", o.Number, g.SellerOrderNumber, t.ConversationKey, "Jane Brown"),
            (sent.GetProperty("text").GetString(), sent.GetProperty("kind").GetString(), sent.GetProperty("marketplaceOrderId").GetString(), sent.GetProperty("sellerOrderNumber").GetString(), sent.GetProperty("conversationId").GetString(), sent.GetProperty("buyerName").GetString()));
        Assert.StartsWith("MG-", sent.GetProperty("messageId").GetString());
        Assert.Equal("sent", await _k.Db(db => db.ThreadMessages.Select(m => m.Status).SingleAsync()));
        // a second message joins the same thread
        var again = await Json(await Call(HttpMethod.Post, "/api/v1/messages", new { threadId, text = "Thanks!" }));
        Assert.Equal(threadId, again.GetProperty("threadId").GetGuid());
        Assert.Equal(1, await _k.Db(db => db.MessageThreads.CountAsync()));
    }

    [Fact]
    public async Task Parallel_first_messages_make_one_thread_and_every_message_is_kept()
    {
        var (o, _) = await Placed();
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "hello " + i })));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await _k.Db(db => db.MessageThreads.CountAsync()));
        Assert.Equal(5, await _k.Db(db => db.ThreadMessages.CountAsync()));
        await Drain();
        Assert.Equal(5, PortalMessages().Count);
        Assert.Equal(5, PortalMessages().Select(m => m.GetProperty("messageId").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Off_platform_contact_or_payment_details_warn_first_and_send_when_acknowledged()
    {
        var (o, _) = await Placed();
        foreach (var text in new[] { "call me on +1 718 555 0100", "mail jane@example.com", "pay me on PayPal", "see http://evil.example/x" })
        {
            var res = await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("contact_details_warning", (await Json(res)).GetProperty("code").GetString());
        }
        Assert.Equal(0, await _k.Db(db => db.ThreadMessages.CountAsync()));                      // nothing stored, no thread
        Assert.Equal(0, await _k.Db(db => db.MessageThreads.CountAsync()));
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "mail jane@example.com", acknowledgeWarning = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "Is order 12345 and 3 items ready?" })).StatusCode);   // numbers alone are not a phone
    }

    [Fact]
    public async Task Text_and_target_are_validated_and_another_buyers_order_is_a_404()
    {
        var (o, _) = await Placed();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = new string('a', 4001) })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/messages", new { text = "hi" })).StatusCode);
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "hi" }, other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, sellerOrderNumber = "SO-9999", text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.PostAsJsonAsync("/api/v1/messages", new { orderNumber = o.Number, text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task A_message_the_portal_refuses_is_shown_as_not_delivered_and_a_busy_portal_is_retried()
    {
        var (o, _) = await Placed();
        _messageAnswer = HttpStatusCode.ServiceUnavailable;
        await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "hello" });
        await Drain();
        Assert.Equal("pending", await _k.Db(db => db.ThreadMessages.Select(m => m.Status).SingleAsync()));   // retried later, not lost
        _messageAnswer = HttpStatusCode.NotFound;
        _k.F.Clock.Advance(TimeSpan.FromHours(2));
        await Drain();
        Assert.Equal("failed", await _k.Db(db => db.ThreadMessages.Select(m => m.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_pre_sale_question_about_a_listing_makes_a_product_thread_and_an_unknown_listing_is_a_404()
    {
        await Login();
        var listing = await _k.Db(db => db.CatalogListings.AsNoTracking().Where(l => l.Visible).OrderBy(l => l.Slug).FirstAsync());
        var res = await Call(HttpMethod.Post, "/api/v1/messages", new { listingId = listing.Id, text = "Does it ship to Canada?" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await Drain();
        var sent = Assert.Single(PortalMessages());
        Assert.Equal(("product", listing.Id.ToString()), (sent.GetProperty("kind").GetString(), sent.GetProperty("productId").GetString()));
        Assert.False(sent.TryGetProperty("marketplaceOrderId", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/messages", new { listingId = Guid.NewGuid(), text = "hi" })).StatusCode);
        // the seller answers: matched by OUR conversation id and the product, never by the conversation id alone
        var key = await _k.Db(db => db.MessageThreads.Select(t => t.ConversationKey).SingleAsync());
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: key, extra: new { productId = Guid.NewGuid() }));
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: "CV-UNKNOWN", extra: new { productId = listing.Id }));
        // G1: our conversation id plus the product is not enough either: the product id must be a valid id, and the seller must be the listing's seller
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: key));                                                                                       // no productId
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: key, extra: new { productId = "xyz", sellerId = listing.SellerId }));                        // not an id
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: key, extra: new { productId = listing.Id }));                                               // no seller
        Assert.Equal("skipped", await SellerMessage(null!, null!, "No.", kind: "product", conv: key, extra: new { productId = listing.Id, sellerId = Guid.NewGuid() }));                   // another seller
        Assert.Equal(0, await _k.Db(db => db.ThreadMessages.CountAsync(m => m.Direction == "seller")));
        Assert.Equal("applied", await SellerMessage(null!, null!, "Yes, we do.", kind: "product", conv: key, extra: new { productId = listing.Id, sellerId = listing.SellerId }));
        var thread = await _k.Db(db => db.MessageThreads.Select(t => t.Id).SingleAsync());
        var view = await Json(await Call(HttpMethod.Get, $"/api/v1/messages/threads/{thread}"));
        Assert.Equal(new[] { "buyer", "seller" }, view.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("direction").GetString()!).ToArray());
    }

    // ------------------------------------------------------------------ seller -> buyer

    [Fact]
    public async Task G3_An_order_message_without_the_sellers_order_number_is_skipped_even_for_a_single_seller_order()
    {
        var (o, g) = await Placed();
        await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "Hi" });
        await Drain();
        Assert.Equal("skipped", await SellerMessage(o, g, "Hello", sellerOrderNumber: null));
        Assert.Equal(0, await _k.Db(db => db.ThreadMessages.CountAsync(m => m.Direction == "seller")));
        Assert.Equal("applied", await SellerMessage(o, g, "Hello"));
    }

    [Fact]
    public async Task A_seller_reply_is_stored_once_notified_acknowledged_and_counted_unread()
    {
        var (o, g) = await Placed();
        var threadId = (await Json(await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "Hi" }))).GetProperty("threadId").GetGuid();
        await Drain();
        var key = await _k.Db(db => db.MessageThreads.Select(t => t.ConversationKey).SingleAsync());
        Assert.Equal("applied", await SellerMessage(o, g, "It is nut free.", pid: "pm-A", conv: key, eventId: "me-1"));
        Assert.Equal("applied", await SellerMessage(o, g, "It is nut free.", pid: "pm-A", conv: key, eventId: "me-2"));      // the same message under another event id
        Assert.Equal("duplicate", await SellerMessage(o, g, "It is nut free.", pid: "pm-A", conv: key, eventId: "me-1"));   // the same event
        Assert.Equal(1, await _k.Db(db => db.ThreadMessages.CountAsync(m => m.Direction == "seller")));
        await Drain();
        var receipts = Portal("messages/pm-A/receipts").Select(c => JsonDocument.Parse(c.BodyText).RootElement.GetProperty("status").GetString()).ToList();
        Assert.Equal(new[] { "delivered" }, receipts);
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == "message_received")));
        var mail = _k.Sink.Sent.Single(m => m.Subject.StartsWith("New message from"));
        Assert.DoesNotContain("nut free", mail.TextBody);                                          // the text never goes by email
        Assert.Contains($"/account/messages/{threadId}", mail.TextBody);
        var list = await Json(await Call(HttpMethod.Get, "/api/v1/messages/threads"));
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        var th = list.GetProperty("threads")[0];
        Assert.Equal((1, "It is nut free.", o.Number), (th.GetProperty("unreadCount").GetInt32(), th.GetProperty("preview").GetString(), th.GetProperty("orderNumber").GetString()));
    }

    [Fact]
    public async Task A_seller_can_start_an_order_thread_and_the_buyers_reply_joins_it()
    {
        var (o, g) = await Placed();
        Assert.Equal("applied", await SellerMessage(o, g, "Your order is delayed a day."));
        var thread = await _k.Db(db => db.MessageThreads.SingleAsync());
        Assert.Equal(g.Id, thread.GroupId);
        var reply = await Json(await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "No problem" }));
        Assert.Equal(thread.Id, reply.GetProperty("threadId").GetGuid());
        await Drain();
        Assert.Equal(thread.ConversationKey, Assert.Single(PortalMessages()).GetProperty("conversationId").GetString());   // the portal joins it by this id
    }

    [Fact]
    public async Task A_message_is_matched_by_our_order_number_and_the_stored_seller_order_number_and_nothing_else()
    {
        var (o, g) = await Placed();
        Assert.Equal("skipped", await SellerMessage(o, g, "x", sellerOrderNumber: "SO-2026-999999"));            // a number that is not this order's
        Assert.Equal("skipped", await SellerMessage(new MarketplaceOrder { Number = "MP-20990101-ZZZZZZZZ" }, g, "x"));   // not our order
        Assert.Equal("skipped", await SellerMessage(o, g, "", pid: "pm-empty"));                                  // empty text
        Assert.Equal("skipped", await SellerMessage(o, g, "x", kind: "booking"));                                 // bookings arrive with M4
        Assert.Equal("skipped", await SellerMessage(o, g, "x", kind: "newsletter"));
        Assert.Equal(0, await _k.Db(db => db.MessageThreads.CountAsync()));                                      // none of those created a thread
        await Call(HttpMethod.Post, "/api/v1/messages", new { orderNumber = o.Number, text = "Hello" });         // now the order has a thread with OUR conversation key
        Assert.Equal("skipped", await SellerMessage(o, g, "x", conv: "CV-OTHER", pid: "pm-1"));                   // another conversation id for the same order: refused
        Assert.Equal(0, await _k.Db(db => db.ThreadMessages.CountAsync(m => m.Direction == "seller")));
    }

    // ------------------------------------------------------------------ read, report, access

    [Fact]
    public async Task Marking_a_thread_read_sends_each_read_receipt_once_even_in_parallel()
    {
        var (o, g) = await Placed();
        await SellerMessage(o, g, "one", pid: "pm-1");
        await SellerMessage(o, g, "two", pid: "pm-2");
        var thread = await _k.Db(db => db.MessageThreads.Select(t => t.Id).SingleAsync());
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Call(HttpMethod.Post, $"/api/v1/messages/threads/{thread}/read")));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        await Drain();
        foreach (var id in new[] { "pm-1", "pm-2" })
            Assert.Equal(1, Portal($"messages/{id}/receipts").Count(c => c.BodyText.Contains("\"read\"")));
        Assert.Equal(0, (await Json(await Call(HttpMethod.Get, "/api/v1/messages/threads"))).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task A_message_can_be_reported_once_with_an_opaque_reporter_and_a_rate_limited_report_waits_a_minute()
    {
        var (o, g) = await Placed();
        await SellerMessage(o, g, "pay me elsewhere", pid: "pm-bad");
        var thread = await _k.Db(db => db.MessageThreads.Select(t => t.Id).SingleAsync());
        var mid = (await Json(await Call(HttpMethod.Get, $"/api/v1/messages/threads/{thread}"))).GetProperty("messages")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/messages/{mid}/report", new { })).StatusCode);
        _reportAnswer = HttpStatusCode.TooManyRequests;
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/messages/{mid}/report", new { reason = "Asks for payment off-platform" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/messages/{mid}/report", new { reason = "again" })).StatusCode);   // idempotent
        await Drain();
        Assert.Single(Portal("content/reports"));
        _reportAnswer = HttpStatusCode.Created;
        _k.F.Clock.Advance(TimeSpan.FromSeconds(61));
        await Drain();
        var sent = Portal("content/reports").Select(c => JsonDocument.Parse(c.BodyText).RootElement).Last();
        Assert.Equal(("MR-pm-bad", "message", "pm-bad", "Asks for payment off-platform"), (sent.GetProperty("reportId").GetString(), sent.GetProperty("kind").GetString(), sent.GetProperty("targetId").GetString(), sent.GetProperty("reason").GetString()));
        var reporter = sent.GetProperty("reporterRef").GetString()!;
        Assert.DoesNotContain("@", reporter);
        Assert.DoesNotContain("Jane", reporter);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "portal.content-report")));
    }

    [Fact]
    public async Task Another_buyers_threads_messages_and_reports_are_404_and_the_inbox_is_private()
    {
        var (o, g) = await Placed();
        await SellerMessage(o, g, "secret", pid: "pm-s");
        var thread = await _k.Db(db => db.MessageThreads.Select(t => t.Id).SingleAsync());
        var mid = await _k.Db(db => db.ThreadMessages.Select(m => m.Id).SingleAsync());
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/messages/threads/{thread}", session: other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/messages/threads/{thread}/read", session: other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/messages/{mid}/report", new { reason = "x" }, other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/messages", new { threadId = thread, text = "hi" }, other)).StatusCode);
        Assert.Equal(0, (await Json(await Call(HttpMethod.Get, "/api/v1/messages/threads", session: other))).GetProperty("threads").GetArrayLength());
        Assert.Equal(1, await _k.Db(db => db.ThreadMessages.CountAsync(m => m.ReadAt == null)));   // the other buyer's read changed nothing
        Assert.Contains("no-store", (await Call(HttpMethod.Get, "/api/v1/messages/threads")).Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.GetAsync("/api/v1/messages/threads")).StatusCode);
    }

    [Fact]
    public async Task Sending_and_reporting_messages_has_its_own_tighter_per_user_limit()
    {
        using var k = new AuthKit(new Dictionary<string, string?> { ["RateLimits:MessagesPerMinute"] = "3" });
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await k.Post("/api/v1/messages", new { text = "x" })).StatusCode);
        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests }, codes);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/messages/threads", null)).StatusCode);   // reading is on the generous bucket
    }
}
