using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Messaging;
using Solvelt.Marketplace.Api.Notifications;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The review of the data export (MED-2, MED-3, L7, L8): audited, limited, complete, demo rows marked, nothing of another buyer.</summary>
public sealed class DataExportReviewTests : OrderFlowBase
{
    private const string Email = "ing1@example.com";

    private async Task<HttpResponseMessage> Export(string? session = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/data-export");
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private async Task<string> SignInAgain()
    {
        var r = await _k.Http.PostAsJsonAsync("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
    }

    private static async Task<JsonElement> Sections(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("sections");

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

    // ---------------------------------------------------------------- MED-2

    [Fact]
    public async Task MED2_one_export_per_ten_minutes_each_logged_with_counts_only_and_announced_by_one_mail()
    {
        var (o, _) = await Placed();
        var account = await _k.Account(Email);
        var res = await Export();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // the log line: the account id and the counts, never content
        var line = Assert.Single(_k.F.Logs.Lines, l => l.Contains("Account data export: account"));
        Assert.Contains(account.Id.ToString(), line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders=1", line);
        Assert.DoesNotContain(Email, line);
        Assert.DoesNotContain(o.Number, line);

        // one notification (always on) and one mail that says so, without an unsubscribe link and without any of the data
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.AccountId == account.Id && n.Kind == NotificationKinds.DataExported)));
        var mail = Assert.Single(await _k.Mail(Email), m => m.Subject.Contains("data was downloaded"));
        Assert.Contains("/account/privacy", mail.TextBody);
        Assert.DoesNotContain("unsubscribe", mail.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(o.Number, mail.TextBody);

        // a second export inside ten minutes is refused, says when to come back, and adds neither a notification nor a log line nor a mail
        var again = await Export();
        Assert.Equal((HttpStatusCode)429, again.StatusCode);
        Assert.InRange(int.Parse(again.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString("0")), 1, 600);
        Assert.Contains("export_rate_limited", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.AccountId == account.Id && n.Kind == NotificationKinds.DataExported)));
        Assert.Single(_k.F.Logs.Lines, l => l.Contains("Account data export: account"));

        // the limit is per account: another buyer is not held back
        var other = await _k.VerifiedSession("other-export@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Export(other)).StatusCode);
    }

    [Fact]
    public async Task MED2_the_limit_is_ten_minutes_from_the_last_export_also_across_the_edge_of_a_ten_minute_window()
    {
        await Placed();
        _k.F.Clock.Advance(TimeSpan.FromSeconds(9 * 60 + 30 - 0));                           // still inside the sign-in window, at the end of the first ten-minute window
        Assert.Equal(HttpStatusCode.OK, (await Export()).StatusCode);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(1));                                          // the next window; a new sign-in (the old one is eleven minutes old)
        var again = await SignInAgain();
        var res = await Export(again);
        Assert.Equal((HttpStatusCode)429, res.StatusCode);                                    // one minute after the last export: still refused
        Assert.InRange(res.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 540);            // and it says how long is left, not a full window
        _k.F.Clock.Advance(TimeSpan.FromMinutes(9));
        var later = await SignInAgain();
        Assert.Equal(HttpStatusCode.OK, (await Export(later)).StatusCode);                    // ten minutes after the first: allowed again
    }

    [Fact]
    public async Task MED2_two_requests_that_pass_the_check_together_are_not_both_answered()
    {
        await Placed();
        var account = await _k.Account(Email);
        // the other request already won this ten-minute window (its row is older than the check's cut-off, so only the unique key stops us)
        var now = _k.F.Clock.GetUtcNow();
        var bucket = now.ToUnixTimeSeconds() / (long)AccountDataExportEndpoint.MinInterval.TotalSeconds;
        await _k.Db(async db =>
        {
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = account.Id, Kind = NotificationKinds.DataExported, DedupeKey = $"data-export:{account.Id:N}:{bucket}:{NotificationKinds.DataExported}", CreatedAt = now.AddMinutes(-11) });
            await db.SaveChangesAsync();
            return 0;
        });
        var res = await Export();
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        Assert.DoesNotContain("sections", await res.Content.ReadAsStringAsync());          // no file was sent
    }

    [Fact]
    public void MED2_the_mail_is_transactional_and_points_at_the_privacy_page()
    {
        Assert.Equal(NotificationCategories.Transactional, NotificationKinds.CategoryOf(NotificationKinds.DataExported));
        var m = NotificationEmails.Build("a@example.com", "Jane", new Notification { Kind = NotificationKinds.DataExported }, "https://shop.example", null, null);
        Assert.Equal("Your data was downloaded from Solvelt", m.Subject);
        Assert.Contains("https://shop.example/account/privacy", m.TextBody);
        Assert.Contains("cannot be switched off", m.TextBody);
    }

    [Fact]
    public void MED2_counts_are_the_items_of_the_lists_of_a_section()
    {
        Assert.Equal(3, AccountDataExportEndpoint.Count(new[] { 1, 2, 3 }));
        Assert.Equal(5, AccountDataExportEndpoint.Count(new { a = new[] { 1, 2 }, b = new[] { 1, 2, 3 }, c = "x", d = (object?)null }));
        Assert.Equal(0, AccountDataExportEndpoint.Count(null));
    }

    // ---------------------------------------------------------------- MED-3 and L7

    [Fact]
    public async Task MED3_the_file_has_carts_passkeys_and_tracking_and_nothing_of_the_other_buyer_or_any_secret()
    {
        var (o, g) = await Placed();
        await AddToCart("challah-classic", 3);                                                              // a cart with a line
        var account = await _k.Account(Email);
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db =>
        {
            db.BuyerPasskeys.Add(new BuyerPasskey { Id = Guid.NewGuid(), AccountId = account.Id, CredentialId = [9, 9, 9, 1], PublicKey = [7, 7, 7, 2], Name = "My iPhone", CreatedAt = now.AddDays(-2), LastUsedAt = now.AddDays(-1) });
            db.SellerOrderStates.Add(new SellerOrderState { GroupId = g.Id, OrderId = o.Id, Status = FulfilmentStatus.Shipped, ShippedAt = now.AddHours(-3), UpdatedAt = now });
            db.OrderShipments.Add(new OrderShipment { Id = Guid.NewGuid(), GroupId = g.Id, PortalShipmentId = "PORTAL-SH-1", Method = "carrier", Carrier = "UPS", TrackingNumber = "1Z999AA10123456784", ShippedAt = now.AddHours(-3),
                TrackingStatus = "in_transit", DriverFirstName = "DriverDan", PositionLat = 40.123456, PositionLng = -73.654321, PositionAt = now, ProofJson = "[\"/proof/SECRET-PROOF\"]", TrackingEventsJson = "[{\"status\":\"in_transit\",\"at\":\"2026-11-04T09:00:00Z\"}]", UpdatedAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        // another buyer: a passkey, a cart, a seller order with a shipment of their own
        var other = await _k.VerifiedSession("other-export@example.com");
        var otherAccount = await _k.Account("other-export@example.com");
        await _k.Db(async db =>
        {
            db.BuyerPasskeys.Add(new BuyerPasskey { Id = Guid.NewGuid(), AccountId = otherAccount.Id, CredentialId = [5], PublicKey = [5], Name = "OTHER-PASSKEY-NAME", CreatedAt = now });
            db.Carts.Add(new Cart { Id = Guid.NewGuid(), AccountId = otherAccount.Id, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            return 0;
        });

        var res = await Export();
        var text = await res.Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(text).RootElement.GetProperty("sections");
        Assert.Equal(new[] { "account", "bookings", "carts", "cancellations-and-returns", "messages", "notifications", "orders", "passkeys", "recently-viewed", "reviews", "tracking", "wishlists" }.Order().ToArray(), d.EnumerateObject().Select(p => p.Name).Order().ToArray());

        var cart = Assert.Single(d.GetProperty("carts").GetProperty("carts").EnumerateArray());
        Assert.Equal(3, Assert.Single(cart.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetInt32());
        Assert.False(cart.GetProperty("demo").GetBoolean());

        var pk = Assert.Single(d.GetProperty("passkeys").GetProperty("passkeys").EnumerateArray());
        Assert.Equal("My iPhone", pk.GetProperty("name").GetString());
        Assert.True(pk.TryGetProperty("createdAt", out _));

        var so = Assert.Single(d.GetProperty("tracking").GetProperty("sellerOrders").EnumerateArray());
        Assert.Equal(g.SellerOrderNumber, so.GetProperty("sellerOrder").GetString());
        Assert.Equal("Shipped", so.GetProperty("state").GetProperty("status").GetString(), ignoreCase: true);
        var ship = Assert.Single(so.GetProperty("shipments").EnumerateArray());
        Assert.Equal(("UPS", "1Z999AA10123456784"), (ship.GetProperty("carrier").GetString(), ship.GetProperty("trackingNumber").GetString()));
        Assert.Equal(JsonValueKind.Array, ship.GetProperty("timeline").ValueKind);

        // never in the file: the passkey's secrets, the driver (name and position), the proof links, the portal's ids, and anything of the other buyer
        foreach (var leak in new[] { "OTHER-PASSKEY-NAME", "other-export@example.com", "DriverDan", "40.123456", "-73.654321", "SECRET-PROOF", "PORTAL-SH-1", "credentialId", "publicKey", "signCount" })
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task L7_demo_rows_are_marked_demo_in_the_file_and_real_rows_are_not()
    {
        await Placed();
        var account = await _k.Account(Email);
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db =>
        {
            await db.Carts.Where(c => c.AccountId == account.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.IsDemo, true));   // one cart per account
            db.MessageThreads.Add(new MessageThread { Id = Guid.NewGuid(), AccountId = account.Id, ConversationKey = "CV-DEMO", Kind = ThreadKinds.Product, Subject = "demo thread", SellerName = "Demo Seller", IsDemo = true, CreatedAt = now, LastMessageAt = now });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = account.Id, Kind = NotificationKinds.OrderAccepted, DedupeKey = "demo:1", IsDemo = true, CreatedAt = now.AddMinutes(1) });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = account.Id, Kind = NotificationKinds.OrderShipped, DedupeKey = "real:1", CreatedAt = now });
            db.BuyerAddresses.Add(new BuyerAddress { Id = Guid.NewGuid(), AccountId = account.Id, FullName = "Demo Person", Line1 = "1 Demo St", City = "Demoville", Country = "US", IsDemo = true });
            await db.SaveChangesAsync();
            return 0;
        });
        var d = await Sections(await Export());
        Assert.True(Assert.Single(d.GetProperty("carts").GetProperty("carts").EnumerateArray()).GetProperty("demo").GetBoolean());
        Assert.True(Assert.Single(d.GetProperty("messages").EnumerateArray()).GetProperty("demo").GetBoolean());
        Assert.Single(d.GetProperty("notifications").GetProperty("notifications").EnumerateArray(), n => n.GetProperty("demo").GetBoolean());
        Assert.Contains(d.GetProperty("notifications").GetProperty("notifications").EnumerateArray(), n => !n.GetProperty("demo").GetBoolean());
        Assert.True(Assert.Single(d.GetProperty("account").GetProperty("addresses").EnumerateArray(), a => a.GetProperty("demo").GetBoolean()).GetProperty("line1").GetString() == "1 Demo St");
        Assert.False(Assert.Single(d.GetProperty("orders").GetProperty("orders").EnumerateArray()).GetProperty("demo").GetBoolean());
    }

    // ---------------------------------------------------------------- L8

    [Fact]
    public void L8_only_the_listed_notification_parameters_are_exported()
    {
        var p = NotificationsDataExporter.Allowed("{\"sellerName\":\"Challah Corner\",\"subject\":\"Order\",\"threadId\":\"11111111-1111-1111-1111-111111111111\",\"bookingId\":\"b\",\"marketplaceBookingId\":\"BK-1\",\"amountCents\":700,\"carrier\":\"UPS\",\"internal\":{\"x\":1}}");
        Assert.Equal(new[] { "amountCents", "carrier", "sellerName", "subject" }, p.Keys.Order().ToArray());
        Assert.Equal((object)700L, p["amountCents"]);
        Assert.Empty(NotificationsDataExporter.Allowed(null));
        Assert.Empty(NotificationsDataExporter.Allowed("not json"));
        Assert.Empty(NotificationsDataExporter.Allowed("[1,2]"));
    }

    // ---------------------------------------------------------------- the registry

    /// <summary>Which sections of the file cover which module of <see cref="PiiRegistry"/>. A new module, or a new exporter, makes this test fail until someone decides where it belongs.</summary>
    private static readonly Dictionary<string, string[]> SectionsOf = new()
    {
        ["account"] = ["account", "passkeys"],
        ["messaging"] = ["messages", "notifications"],
        ["orders-and-carts"] = ["orders", "carts", "tracking"],
        ["order-cancellations-and-returns"] = ["cancellations-and-returns"],
        ["bookings"] = ["bookings"],
        ["wishlists-and-recently-viewed"] = ["wishlists", "recently-viewed"],
        ["reviews"] = ["reviews"],
    };

    /// <summary>Modules that hold buyer data and are NOT in the file, each with the reason (M5-32). Sessions and tokens are in the account module and are not data about the person.</summary>
    private static readonly Dictionary<string, string> NotExported = new()
    {
        ["demo-data"] = "marked rows of the other modules; they are exported there, marked demo: true",
        ["portal-inbound-events"] = "raw bodies of the portal's events (our copy of what the seller side told us); the facts in them are in orders, tracking, messages and bookings",
    };

    [Fact]
    public async Task MED3_every_module_that_holds_buyer_data_has_a_section_in_the_file_or_a_stated_reason_not_to()
    {
        using var scope = _k.F.Services.CreateScope();
        var registered = scope.ServiceProvider.GetRequiredService<IEnumerable<IAccountDataExporter>>().Select(e => e.Section).Order().ToArray();
        await Task.CompletedTask;

        foreach (var m in PiiRegistry.Modules)
            Assert.True(SectionsOf.ContainsKey(m.Name) ^ NotExported.ContainsKey(m.Name), $"module '{m.Name}' ({m.Owner}) must be either in SectionsOf or in NotExported (with a reason), and not both");
        Assert.All(NotExported.Values, reason => Assert.True(reason.Length > 20));
        Assert.All(SectionsOf.Keys.Concat(NotExported.Keys), name => Assert.Contains(PiiRegistry.Modules, m => m.Name == name));   // no stale entry

        var covered = SectionsOf.Values.SelectMany(s => s).Order().ToArray();
        Assert.Equal(covered, registered);                                                              // every section has an exporter and every exporter belongs to a module
        Assert.Equal(registered.Length, registered.Distinct().Count());
    }
}
