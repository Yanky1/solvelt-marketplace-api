using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M5 slice 3: the notification centre, emails per preference, and unsubscribe links signed with their own key.</summary>
public sealed class NotificationTests : OrderFlowBase
{
    private int _n;
    private const string Email = "ing1@example.com";

    private async Task<(MarketplaceOrder O, OrderGroup G)> Placed()
    {
        await Login(Email);
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        return (o, o.Groups.Single());
    }

    private async Task<string> Evt(string type, MarketplaceOrder o, OrderGroup g, object? extra = null, string? id = null)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body(id ?? $"ne-{++_n:D3}", type, _k.F.Clock.GetUtcNow().AddSeconds(-600 + _n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private Task<string> Ship(MarketplaceOrder o, OrderGroup g, string? id = null)
        => Evt("order.shipped", o, g, new { shipmentId = "sh-1", carrier = "USPS", trackingNumber = "9400", lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2 } } }, id);

    private async Task<HttpResponseMessage> Call(HttpMethod m, string path, object? body = null, string? session = null)
    {
        var r = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (session != "none") r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private async Task<JsonElement> Centre(string? session = null) => (await (await Call(HttpMethod.Get, "/api/v1/notifications", session: session)).Content.ReadFromJsonAsync<JsonElement>());
    private async Task<List<EmailMessage>> Mails(string subjectStart) { await Drain(); return _k.Sink.Sent.Where(m => m.To == Email && m.Subject.StartsWith(subjectStart)).ToList(); }

    [Fact]
    public async Task Shipping_creates_one_notification_and_one_email_with_its_own_unsubscribe_link_and_the_queue_holds_only_an_id()
    {
        var (o, g) = await Placed();
        Assert.Equal("applied", await Ship(o, g, "ne-same"));
        Assert.Equal("duplicate", await Ship(o, g, "ne-same"));       // the same event again: still one
        var centre = await Centre();
        Assert.Equal(1, centre.GetProperty("unreadCount").GetInt32());
        var n = centre.GetProperty("notifications")[0];
        Assert.Equal(("order_shipped", "shipped", o.Number), (n.GetProperty("kind").GetString(), n.GetProperty("category").GetString(), n.GetProperty("orderNumber").GetString()));
        Assert.Equal("USPS", n.GetProperty("parameters").GetProperty("carrier").GetString());
        // the outbox message holds the notification id, no address, no link
        var payload = await _k.Db(db => db.OutboxMessages.Where(m => m.Type == "notification.mail").Select(m => m.Payload).SingleAsync());
        Assert.DoesNotContain("@", payload);
        Assert.DoesNotContain("unsubscribe", payload, StringComparison.OrdinalIgnoreCase);
        var mail = Assert.Single(await Mails("Your order"));
        Assert.Contains(o.Number, mail.TextBody);
        Assert.Contains("with USPS", mail.TextBody);
        Assert.DoesNotContain("9400", mail.TextBody);                  // no tracking number in mail
        var link = Regex.Match(mail.TextBody, @"Stop these emails: \S+/unsubscribe\?token=([A-Za-z0-9_.-]+)").Groups[1].Value;
        Assert.True(link.Length > 40);
        // and it is not any other link: the verify/reset key cannot mint it, and it cannot be replayed on another account or category
        var res = await Call(HttpMethod.Post, "/api/v1/notifications/unsubscribe", new { token = link }, "none");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.False((await Prefs()).GetProperty("email").GetProperty("shipped").GetBoolean());
        Assert.True((await Prefs()).GetProperty("email").GetProperty("delivered").GetBoolean());
    }

    private async Task<JsonElement> Prefs() => await (await Call(HttpMethod.Get, "/api/v1/notifications/preferences")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_category_switched_off_sends_no_email_but_the_in_app_entry_stays_and_a_switch_made_while_it_waited_counts()
    {
        var (o, g) = await Placed();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/notifications/preferences", new { email = new { shipped = false } })).StatusCode);
        await Ship(o, g);
        await Evt("order.delivered", o, g, new { by = "seller" });
        Assert.Empty(await Mails("Your order " + o.Number + " has shipped"));
        Assert.Single(await Mails("Your order " + o.Number + " was delivered"));
        Assert.Equal(2, (await Centre()).GetProperty("notifications").GetArrayLength());     // both are in the centre
        // switch delivered off AFTER the mail is queued but before it is sent
        _k.Sink.Clear();
        await Evt("refund.issued", o, g, new { refundId = "rf-1", amountCents = 500, shippingCents = 0 }, id: "ne-rf");
        await Call(HttpMethod.Put, "/api/v1/notifications/preferences", new { email = new { refunds = false } });
        await Drain();
        Assert.DoesNotContain(_k.Sink.Sent, m => m.Subject.StartsWith("A refund"));
    }

    [Fact]
    public async Task Preferences_default_to_on_reject_unknown_categories_and_are_private_to_the_buyer()
    {
        await Login(Email);
        var p = (await Prefs()).GetProperty("email");
        Assert.All(new[] { "order_updates", "shipped", "out_for_delivery", "delivered", "refunds", "messages", "bookings" }, c => Assert.True(p.GetProperty(c).GetBoolean()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, "/api/v1/notifications/preferences", new { email = new { marketing = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, "/api/v1/notifications/preferences", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/notifications/preferences", session: "none")).StatusCode);
        // two parallel first-time saves for one account both succeed (no 500 from the key race)
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Call(HttpMethod.Put, "/api/v1/notifications/preferences", new { email = new Dictionary<string, bool> { [i % 2 == 0 ? "shipped" : "delivered"] = false } })));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var after = (await Prefs()).GetProperty("email");
        Assert.False(after.GetProperty("shipped").GetBoolean());
        Assert.False(after.GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public async Task Out_for_delivery_is_told_once_not_on_every_position_update_and_a_refund_the_money_side_refused_is_not_announced()
    {
        var (o, g) = await Placed();
        await Ship(o, g);
        for (var i = 0; i < 3; i++)
            await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        Assert.Equal(1, (await Centre()).GetProperty("notifications").EnumerateArray().Count(x => x.GetProperty("kind").GetString() == "out_for_delivery"));
        // more than was paid: M3 refuses to move it, so the buyer is not told a refund happened
        Assert.Equal("skipped", await Evt("refund.issued", o, g, new { refundId = "rf-big", amountCents = 999_999, shippingCents = 0 }));
        Assert.DoesNotContain(((await Centre()).GetProperty("notifications")).EnumerateArray(), x => x.GetProperty("kind").GetString() == "refund_issued");
        Assert.Equal("applied", await Evt("refund.issued", o, g, new { refundId = "rf-ok", amountCents = 500, shippingCents = 0 }));
        var refund = (await Centre()).GetProperty("notifications").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "refund_issued");
        Assert.Equal(500, refund.GetProperty("parameters").GetProperty("amountCents").GetInt64());
        Assert.Contains("$5.00", (await Mails("A refund")).Single().TextBody);
    }

    [Fact]
    public async Task The_centre_lists_newest_first_marks_read_and_never_shows_or_changes_another_buyers_entries()
    {
        var (o, g) = await Placed();
        await Evt("order.accepted", o, g);
        _k.F.Clock.Advance(TimeSpan.FromSeconds(5));      // the fake clock does not move by itself: entries made in the same instant have no order
        await Ship(o, g);
        var c = await Centre();
        Assert.Equal(2, c.GetProperty("unreadCount").GetInt32());
        Assert.Equal("order_shipped", c.GetProperty("notifications")[0].GetProperty("kind").GetString());
        var id = c.GetProperty("notifications")[0].GetProperty("id").GetGuid();
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/notifications/{id}/read", session: other)).StatusCode);
        Assert.Equal(0, (await Centre(other)).GetProperty("notifications").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/notifications/{id}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/notifications/{id}/read")).StatusCode);     // idempotent
        Assert.Equal(1, (await Centre()).GetProperty("unreadCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, "/api/v1/notifications/read-all", session: other)).StatusCode);
        Assert.Equal(1, (await Centre()).GetProperty("unreadCount").GetInt32());                                               // the other buyer's read-all did not touch mine
        await Call(HttpMethod.Post, "/api/v1/notifications/read-all");
        Assert.Equal(0, (await Centre()).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task An_unsubscribe_link_works_only_with_its_own_key_for_its_own_account_and_category_and_can_only_switch_mail_off()
    {
        await Login(Email);
        var id = (await _k.Account(Email)).Id;
        string Sign(string key, Guid acc, string cat, long? exp = null) { var e = exp ?? _k.F.Clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds(); return $"{acc:N}.{cat}.{e}." + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"unsub|{acc:N}|{cat}|{e}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        Task<HttpResponseMessage> Unsub(string? t) => Call(HttpMethod.Post, "/api/v1/notifications/unsubscribe", new { token = t }, "none");
        // the right key (the dev default of the Testing app) works for one category
        Assert.Equal(HttpStatusCode.NoContent, (await Unsub(Sign(DevDefaults.UnsubscribeKey, id, "refunds"))).StatusCode);
        var p = (await Prefs()).GetProperty("email");
        Assert.False(p.GetProperty("refunds").GetBoolean());
        Assert.True(p.GetProperty("shipped").GetBoolean());
        // the email-link key and every other key cannot mint it
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(Sign(DevDefaults.EmailLinkKey, id, "shipped"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(Sign(DevDefaults.WebForwardingKey, id, "shipped"))).StatusCode);
        // another account, another category and garbage
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(Sign(DevDefaults.UnsubscribeKey, id, "shipped").Replace("shipped", "delivered"))).StatusCode);   // category swapped, MAC not
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(Sign(DevDefaults.UnsubscribeKey, id, "marketing"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub("x.y.z")).StatusCode);
        // the canonical id only: the same MAC-valid token with an upper-case id is refused (review P4)
        var up = Sign(DevDefaults.UnsubscribeKey, id, "shipped"); up = up.Replace(id.ToString("N"), id.ToString("N").ToUpperInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub(up)).StatusCode);
        // the expiry is part of the signature: moving it later does not extend a link
        var t = Sign(DevDefaults.UnsubscribeKey, id, "shipped", 100).Split('.');
        Assert.Equal(HttpStatusCode.BadRequest, (await Unsub($"{t[0]}.{t[1]}.{_k.F.Clock.GetUtcNow().AddYears(5).ToUnixTimeSeconds()}.{t[3]}")).StatusCode);
        Assert.True((await Prefs()).GetProperty("email").GetProperty("shipped").GetBoolean());
        // "all" switches every category off; an unknown (deleted) account is a quiet no-op, not an error that reveals anything
        Assert.Equal(HttpStatusCode.NoContent, (await Unsub(Sign(DevDefaults.UnsubscribeKey, id, "all"))).StatusCode);
        Assert.All(new[] { "order_updates", "shipped", "out_for_delivery", "delivered", "refunds", "messages", "bookings" }, c => Assert.False((Prefs().Result).GetProperty("email").GetProperty(c).GetBoolean()));
        Assert.Equal(HttpStatusCode.NoContent, (await Unsub(Sign(DevDefaults.UnsubscribeKey, Guid.NewGuid(), "all"))).StatusCode);
    }

    [Fact]
    public async Task An_unsubscribe_link_expires_after_90_days_with_its_own_error_and_changes_nothing()
    {
        var (o, g) = await Placed();
        await Ship(o, g, "ne-exp");
        var mail = Assert.Single(await Mails("Your order"));
        var token = Regex.Match(mail.TextBody, @"Stop these emails: \S+/unsubscribe\?token=([A-Za-z0-9_.-]+)").Groups[1].Value;
        var exp = long.Parse(token.Split('.')[2]);
        Assert.Equal(_k.F.Clock.GetUtcNow().AddDays(90).ToUnixTimeSeconds(), exp);
        _k.F.Clock.Advance(TimeSpan.FromDays(91));
        var res = await Call(HttpMethod.Post, "/api/v1/notifications/unsubscribe", new { token }, "none");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("expired_token", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await _k.Db(db => db.NotificationPreferences.CountAsync(x => !x.EmailShipped)));     // nothing was switched off (the session itself is long gone)
    }

    [Fact]
    public async Task A_notification_mail_carries_RFC_8058_one_click_headers_and_that_url_works_with_the_mail_clients_form_post_only()
    {
        var (o, g) = await Placed();
        await Ship(o, g, "ne-8058");
        var mail = Assert.Single(await Mails("Your order"));
        var url = mail.Headers!["List-Unsubscribe"];
        Assert.Equal("List-Unsubscribe=One-Click", mail.Headers["List-Unsubscribe-Post"]);
        Assert.StartsWith("<", url); Assert.EndsWith(">", url);
        var uri = new Uri(url.Trim('<', '>'));
        Assert.Equal("/api/v1/notifications/unsubscribe", uri.AbsolutePath);
        var pathAndQuery = uri.PathAndQuery;
        // a GET (a link scanner) changes nothing and is not even routed
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _k.Http.GetAsync(pathAndQuery)).StatusCode);
        Assert.True((await Prefs()).GetProperty("email").GetProperty("shipped").GetBoolean());
        // a POST without the one-click marker is refused
        var bare = await _k.Http.PostAsync(pathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "y" }));
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.True((await Prefs()).GetProperty("email").GetProperty("shipped").GetBoolean());
        // the mail client's POST: form body, token in the URL, no cookie and no Authorization header
        var ok = await _k.Http.PostAsync(pathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" }));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.False((await Prefs()).GetProperty("email").GetProperty("shipped").GetBoolean());
        Assert.True((await Prefs()).GetProperty("email").GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public void A_seller_name_with_line_breaks_cannot_add_a_header_line_to_the_subject()
    {
        var n = new Notification { Kind = NotificationKinds.OrderAccepted, OrderNumber = "O-1", ParamsJson = JsonSerializer.Serialize(new { sellerName = "Evil\r\nBcc: victim@example.com\u2028x" }) };
        var mail = Solvelt.Marketplace.Api.Notifications.NotificationEmails.Build("a@example.com", "A", n, "https://shop.test", "https://shop.test/u", "https://shop.test/u2");
        Assert.DoesNotContain('\r', mail.Subject); Assert.DoesNotContain('\n', mail.Subject); Assert.DoesNotContain('\u2028', mail.Subject);
        Assert.Contains("Evil", mail.Subject);
    }

    [Fact]
    public async Task No_mail_goes_to_a_deleted_account_and_old_notifications_are_deleted_by_the_job()
    {
        var (o, g) = await Placed();
        await Ship(o, g);
        await _k.Db(async db => await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)_k.F.Clock.GetUtcNow())));
        Assert.Empty(await Mails("Your order"));
        _k.F.Clock.Advance(TimeSpan.FromDays(181));
        using var scope = _k.F.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync();
        Assert.Equal(1, r.NotificationsDeleted);
        Assert.Equal(0, await _k.Db(db => db.Notifications.CountAsync()));
    }
}
