using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Demo;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Xunit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M9: "Fill with demo data", "Remove demo data" and the test-only verify. One test per rule of the brief.</summary>
public sealed class DemoDataTests : IDisposable
{
    private readonly AuthKit _k = new(new() { ["Demo:Enabled"] = "true" });
    public void Dispose() => _k.Dispose();

    private async Task<(string Token, Guid Account)> Buyer(string email)
    {
        var token = await _k.VerifiedSession(email);
        var id = await _k.Db(db => db.BuyerAccounts.Where(a => a.Email == email).Select(a => a.Id).FirstAsync());
        return (token, id);
    }
    private async Task SyncCatalog() { using var scope = _k.F.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Catalog.CatalogSyncService>().RunAsync(); }
    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    private Task<HttpResponseMessage> Fill(string token) => _k.Post("/api/v1/demo/fill", null, token);
    private Task<HttpResponseMessage> Remove(string token) => _k.Post("/api/v1/demo/remove", null, token);

    private Task<(int Orders, int Bookings, int Threads, int Notifications, int Addresses, int Carts, int Out)> Totals(Guid? account = null) => _k.Db(async db => (
        await db.Orders.CountAsync(o => account == null || o.AccountId == account), await db.Bookings.CountAsync(b => account == null || b.AccountId == account),
        await db.MessageThreads.CountAsync(t => account == null || t.AccountId == account), await db.Notifications.CountAsync(n => account == null || n.AccountId == account),
        await db.BuyerAddresses.CountAsync(a => account == null || a.AccountId == account), await db.Carts.CountAsync(c => account == null || c.AccountId == account), await db.OutboxMessages.CountAsync()));

    // ---------- the demo data itself ----------
    [Fact]
    public async Task Fill_creates_an_address_a_cart_orders_in_three_states_bookings_a_thread_and_notifications_all_marked_with_one_batch()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demo1@example.com");
        var res = await Fill(token);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await Body(res);
        var batch = body.GetProperty("batchId").GetGuid();
        Assert.True(body.GetProperty("created").GetBoolean());
        await _k.Db(async db =>
        {
            var address = await db.BuyerAddresses.SingleAsync(a => a.AccountId == account && a.IsDemo);
            Assert.Equal(batch, address.DemoBatchId);
            Assert.Contains("555", address.Phone);                                                                      // a 555 number
            var cart = await db.Carts.SingleAsync(c => c.AccountId == account);
            Assert.True(cart.IsDemo);
            Assert.InRange(await db.CartLines.CountAsync(l => l.CartId == cart.Id), 2, 3);                              // 2 to 3 items from the live catalog
            var orders = await db.Orders.Where(o => o.AccountId == account).Include(o => o.Groups).ToListAsync();
            Assert.Equal(3, orders.Count);
            Assert.All(orders, o => { Assert.True(o.IsDemo); Assert.Equal(batch, o.DemoBatchId); Assert.StartsWith("pi_demo_", o.PaymentIntentId); Assert.EndsWith("@example.com", o.RelayEmail); Assert.NotNull(o.PaidAt); });
            var orderIds = orders.Select(o => o.Id).ToList();
            var states = await db.SellerOrderStates.Where(s => orderIds.Contains(s.OrderId)).Select(s => s.Status).ToListAsync();
            Assert.Equivalent(new[] { FulfilmentStatus.Delivered, FulfilmentStatus.Shipped, FulfilmentStatus.Refunded }, states);
            var groupIds = orders.SelectMany(o => o.Groups).Select(g => g.Id).ToList();
            Assert.All(await db.OrderShipments.Where(s => groupIds.Contains(s.GroupId)).ToListAsync(), s => Assert.StartsWith("DEMO", s.TrackingNumber));   // with tracking
            var refund = await db.OrderRefunds.SingleAsync(r => orderIds.Contains(r.OrderId));
            Assert.Equal(RefundStatus.Succeeded, refund.Status);                                                        // already settled, so no job picks it up
            Assert.StartsWith("re_demo_", refund.StripeRefundId);
            Assert.False(refund.ReportToPortal);
            var bookings = await db.Bookings.Where(b => b.AccountId == account).ToListAsync();
            Assert.Equal(2, bookings.Count);
            Assert.Contains(bookings, b => b.Status == BookingStatus.Confirmed && b.StartAt > _k.F.Clock.GetUtcNow());     // one upcoming
            Assert.Contains(bookings, b => b.Status == BookingStatus.Completed && b.StartAt < _k.F.Clock.GetUtcNow());     // one completed
            Assert.All(bookings, b => Assert.True(b.IsDemo && b.DemoBatchId == batch));
            var thread = await db.MessageThreads.SingleAsync(t => t.AccountId == account);
            Assert.True(thread.IsDemo);
            Assert.True(await db.ThreadMessages.CountAsync(m => m.ThreadId == thread.Id) >= 2);
            var notes = await db.Notifications.Where(n => n.AccountId == account).ToListAsync();
            Assert.NotEmpty(notes);
            Assert.All(notes, n => Assert.True(n.IsDemo && n.DemoBatchId == batch));
            return 0;
        });
    }

    [Fact]
    public async Task Listings_the_feed_marks_as_demo_are_the_ones_used_first()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demo9@example.com");
        // the alphabetically last visible product would never be picked first by name alone
        var last = await _k.Db(db => db.CatalogListings.Where(l => l.Visible && l.Available && l.Type == "product").OrderByDescending(l => l.Slug).Select(l => l.Id).FirstAsync());
        await _k.Db(async db => await db.CatalogListings.Where(l => l.Id == last).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsDemoListing, true)));
        await Fill(token);
        var used = await _k.Db(db => (from l in db.OrderLines join o in db.Orders on l.OrderId equals o.Id where o.AccountId == account select l.ListingId).ToListAsync());
        Assert.Contains(last, used);
    }

    [Fact]
    public async Task A_second_press_does_nothing_while_a_batch_exists()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demo2@example.com");
        var first = await Body(await Fill(token));
        var before = await Totals(account);
        var res = await Fill(token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var second = await Body(res);
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("batchId").GetGuid(), second.GetProperty("batchId").GetGuid());
        Assert.Equal(before, await Totals(account));
        // and after a removal a press starts a new batch
        await Remove(token);
        var third = await Body(await Fill(token));
        Assert.True(third.GetProperty("created").GetBoolean());
        Assert.NotEqual(first.GetProperty("batchId").GetGuid(), third.GetProperty("batchId").GetGuid());
    }

    [Fact]
    public async Task With_an_empty_catalog_there_is_no_cart_order_or_booking_and_it_says_so()
    {
        var (token, account) = await Buyer("demo3@example.com");
        await _k.Db(async db => await db.CatalogListings.ExecuteUpdateAsync(s => s.SetProperty(l => l.Visible, false)));
        var body = await Body(await Fill(token));
        Assert.Equal(0, (await Totals(account)).Orders + (await Totals(account)).Carts + (await Totals(account)).Bookings);
        Assert.Contains(body.GetProperty("notes").EnumerateArray().Select(n => n.GetString()), n => n!.Contains("no products"));
        Assert.Equal(1, (await Totals(account)).Addresses);                                                            // the address, thread-less notifications still work
    }

    [Fact]
    public async Task A_real_cart_is_never_touched_and_a_real_default_address_stays_the_default()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demo4@example.com");
        var now = _k.F.Clock.GetUtcNow();
        var cartId = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.Carts.Add(new Cart { Id = cartId, AccountId = account, CreatedAt = now, UpdatedAt = now });
            db.BuyerAddresses.Add(new BuyerAddress { Id = Guid.NewGuid(), AccountId = account, FullName = "Real Person", Line1 = "1 Real Rd", City = "Town", PostalCode = "10001", Country = "US", IsDefault = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(); return 0;
        });
        var body = await Body(await Fill(token));
        Assert.Contains(body.GetProperty("notes").EnumerateArray().Select(n => n.GetString()), n => n!.Contains("already have a cart"));
        await _k.Db(async db =>
        {
            Assert.False((await db.Carts.SingleAsync(c => c.AccountId == account)).IsDemo);
            Assert.Equal(cartId, (await db.Carts.SingleAsync(c => c.AccountId == account)).Id);
            Assert.Equal("Real Person", (await db.BuyerAddresses.SingleAsync(a => a.AccountId == account && a.IsDefault)).FullName);
            Assert.False((await db.BuyerAddresses.SingleAsync(a => a.AccountId == account && a.IsDemo)).IsDefault);
            return 0;
        });
    }

    // ---------- nothing real leaves the system ----------
    [Fact]
    public async Task Filling_and_removing_queue_no_outbox_message_no_mail_and_no_portal_call()
    {
        await SyncCatalog();
        var (token, _) = await Buyer("demo5@example.com");
        await _k.Dispatch();                                                                                              // sign-up mail is done and out of the way
        var outbox = await _k.Db(db => db.OutboxMessages.CountAsync());
        var mails = _k.Sink.Sent.Count;
        var portal = _k.F.Portal.Calls.Count;
        await Fill(token);
        await Fill(token);
        await _k.Dispatch();
        await Remove(token);
        await _k.Dispatch();
        Assert.Equal(outbox, await _k.Db(db => db.OutboxMessages.CountAsync()));                                           // the outbox stays empty for demo rows
        Assert.Equal(mails, _k.Sink.Sent.Count);                                                                          // no email
        Assert.Equal(portal, _k.F.Portal.Calls.Count);                                                                    // no portal call
    }

    // ---------- actions on demo rows queue nothing ----------
    [Fact]
    public async Task Buyer_actions_on_demo_orders_bookings_and_threads_answer_409_and_queue_nothing()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demo6@example.com");
        await Fill(token);
        await _k.Dispatch();
        var outbox = await _k.Db(db => db.OutboxMessages.CountAsync());
        var portal = _k.F.Portal.Calls.Count;
        var number = await _k.Db(db => db.Orders.Where(o => o.AccountId == account).Select(o => o.Number).FirstAsync());
        var booking = await _k.Db(db => db.Bookings.Where(b => b.AccountId == account && b.Status == BookingStatus.Confirmed).Select(b => b.Id).FirstAsync());
        var (thread, listing) = await _k.Db(async db => { var t = await db.MessageThreads.FirstAsync(x => x.AccountId == account); return (t.Id, t.ListingId!.Value); });
        var message = await _k.Db(db => db.ThreadMessages.Where(m => m.ThreadId == thread).Select(m => m.Id).FirstAsync());
        var calls = new (string Path, object Body)[]
        {
            ($"/api/v1/orders/{number}/cancel-requests", new { reason = "changed my mind", lines = Array.Empty<object>() }),
            ($"/api/v1/orders/{number}/return-requests", new { reason = "damaged", lines = Array.Empty<object>() }),
            ($"/api/v1/orders/{number}/delivery-confirmations", new { }),
            ($"/api/v1/bookings/{booking}/cancel", new { reason = "x" }),
            ($"/api/v1/bookings/{booking}/proposal", new { accept = true }),
            ($"/api/v1/bookings/{booking}/acknowledge-reschedule", new { }),
            ("/api/v1/messages", new { threadId = thread, text = "Hello?" }),
            ("/api/v1/messages", new { listingId = listing, text = "Hello again?" }),
            ($"/api/v1/messages/{message}/report", new { reason = "spam" }),
        };
        foreach (var (path, body) in calls)
        {
            var res = await _k.Post(path, body, token);
            Assert.True(res.StatusCode == HttpStatusCode.Conflict, $"{path} -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
            Assert.Equal("demo_data", (await Body(res)).GetProperty("code").GetString());
        }
        await _k.Dispatch();
        Assert.Equal(outbox, await _k.Db(db => db.OutboxMessages.CountAsync()));                                           // nothing was queued
        Assert.Equal(portal, _k.F.Portal.Calls.Count);                                                                    // nothing was sent
    }

    [Fact]
    public async Task The_guard_only_covers_demo_rows_and_only_the_buyers_own()
    {
        await SyncCatalog();
        var (tokenA, a) = await Buyer("demo7a@example.com");
        var (tokenB, _) = await Buyer("demo7b@example.com");
        await Fill(tokenA);
        var demoNumber = await _k.Db(db => db.Orders.Where(o => o.AccountId == a).Select(o => o.Number).FirstAsync());
        // somebody else asking about A's demo order does not get "demo_data" (that would say the order exists): it is the ordinary answer for an order that is not theirs
        var other = await _k.Post($"/api/v1/orders/{demoNumber}/cancel-requests", new { reason = "x", lines = Array.Empty<object>() }, tokenB);
        Assert.NotEqual(HttpStatusCode.Conflict, other.StatusCode);
        // and an order number that does not exist is not "demo data" either
        var none = await _k.Post("/api/v1/orders/NOPE-1/cancel-requests", new { reason = "x", lines = Array.Empty<object>() }, tokenA);
        Assert.NotEqual("demo_data", (await Body(none)).TryGetProperty("code", out var c) ? c.GetString() : null);
    }

    // ---------- jobs that call the outside world never pick a demo row ----------
    [Fact]
    public async Task The_maintenance_jobs_leave_aged_demo_rows_alone_and_still_handle_real_ones()
    {
        await SyncCatalog();
        var (_, account) = await Buyer("demo8@example.com");
        var old = _k.F.Clock.GetUtcNow().AddDays(-3);
        MarketplaceOrder Pending(string number, bool demo) => new()
        {
            Id = Guid.NewGuid(), Number = number, AccountId = account, Status = OrderStatus.PendingPayment, PaymentIntentId = "pi_x_" + number, BuyerName = "B", RelayEmail = number + "@example.com", ShipName = "B",
            ShipLine1 = "1 St", ShipCity = "T", ShipPostalCode = "10001", ShipCountry = "US", CreatedAt = old, UpdatedAt = old, IsDemo = demo,
        };
        var hold = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.Orders.Add(Pending("DEMO-OLD", true)); db.Orders.Add(Pending("REAL-OLD", false));
            db.Bookings.Add(new Booking { Id = hold, MarketplaceBookingId = "demo-hold", AccountId = account, IdempotencyKey = "demo-hold-1", RequestHash = "h", ListingId = Guid.NewGuid(), Status = BookingStatus.Hold, HoldExpiresAt = old, Location = "online", CreatedAt = old, UpdatedAt = old, IsDemo = true, Version = 1 });
            await db.SaveChangesAsync(); return 0;
        });
        var portal = _k.F.Portal.Calls.Count;
        using (var scope = _k.F.Services.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Checkout.OrderMaintenance>().ExpirePendingAsync());     // only the real one
            await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Services.BookingMaintenance>().RunAsync();
        }
        await _k.Db(async db =>
        {
            Assert.Equal(OrderStatus.PendingPayment, (await db.Orders.SingleAsync(o => o.Number == "DEMO-OLD")).Status);
            Assert.Equal(OrderStatus.Expired, (await db.Orders.SingleAsync(o => o.Number == "REAL-OLD")).Status);
            var cancels = await db.OutboxMessages.Where(m => m.Payload != null && m.Payload.Contains("DEMO-OLD")).CountAsync();
            Assert.Equal(0, cancels);                                                                                    // no payment-provider cancel was queued for the demo order
            Assert.Equal(BookingStatus.Hold, (await db.Bookings.SingleAsync(b => b.Id == hold)).Status);
            return 0;
        });
        Assert.Equal(portal, _k.F.Portal.Calls.Count);                                                                    // the demo hold was never released at the portal
    }

    // ---------- removal ----------
    [Fact]
    public async Task Remove_deletes_only_the_demo_rows_of_this_account_in_a_mixed_account_and_leaves_other_accounts_alone()
    {
        await SyncCatalog();
        var (tokenA, a) = await Buyer("demoa@example.com");
        var (tokenB, b) = await Buyer("demob@example.com");
        var now = _k.F.Clock.GetUtcNow();
        // account A also has real rows of every kind
        var realOrder = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.BuyerAddresses.Add(new BuyerAddress { Id = Guid.NewGuid(), AccountId = a, FullName = "Real", Line1 = "1 Real Rd", City = "Town", PostalCode = "10001", Country = "US", CreatedAt = now, UpdatedAt = now });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = a, Kind = NotificationKinds.OrderShipped, DedupeKey = "real-1", CreatedAt = now });
            db.Orders.Add(new MarketplaceOrder { Id = realOrder, Number = "REAL-1", AccountId = a, Status = OrderStatus.Placed, BuyerName = "Real", RelayEmail = "real@relay.test", ShipName = "Real", ShipLine1 = "1 Real Rd", ShipCity = "Town", ShipPostalCode = "10001", ShipCountry = "US", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(); return 0;
        });
        await Fill(tokenA); await Fill(tokenB);
        var bBefore = await Totals(b);
        var res = await Remove(tokenA);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await Body(res)).GetProperty("removed").GetProperty("orders").GetInt32() == 3);
        await _k.Db(async db =>
        {
            Assert.Equal(0, await db.Orders.CountAsync(o => o.AccountId == a && o.IsDemo));
            Assert.Equal(0, await db.BuyerAddresses.CountAsync(x => x.AccountId == a && x.IsDemo) + await db.Notifications.CountAsync(n => n.AccountId == a && n.IsDemo) + await db.Bookings.CountAsync(x => x.AccountId == a && x.IsDemo)
                + await db.MessageThreads.CountAsync(t => t.AccountId == a && t.IsDemo) + await db.Carts.CountAsync(c => c.AccountId == a && c.IsDemo));
            Assert.Equal(1, await db.Orders.CountAsync(o => o.Id == realOrder && !o.IsDemo));                              // the real order is still there
            Assert.Equal(1, await db.BuyerAddresses.CountAsync(x => x.AccountId == a && x.FullName == "Real"));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.DedupeKey == "real-1"));
            // no orphans of the removed orders
            Assert.Equal(0, await db.OrderLines.CountAsync(l => !db.Orders.Any(o => o.Id == l.OrderId)) + await db.OrderGroups.CountAsync(g => !db.Orders.Any(o => o.Id == g.OrderId))
                + await db.SellerOrderStates.CountAsync(s => !db.Orders.Any(o => o.Id == s.OrderId)) + await db.ThreadMessages.CountAsync(m => !db.MessageThreads.Any(t => t.Id == m.ThreadId)) + await db.CartLines.CountAsync(l => !db.Carts.Any(c => c.Id == l.CartId)));
            return 0;
        });
        Assert.Equal(bBefore, await Totals(b));                                                                           // another account's demo data is untouched
    }

    // ---------- access ----------
    [Fact]
    public async Task The_account_comes_from_the_session_whatever_the_request_says()
    {
        await SyncCatalog();
        var (tokenA, a) = await Buyer("democ@example.com");
        var (_, b) = await Buyer("demod@example.com");
        var res = await _k.Post($"/api/v1/demo/fill?accountId={b}&AccountId={b}", new { accountId = b, id = b }, tokenA);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(0, (await Totals(b)).Orders);
        Assert.Equal(3, (await Totals(a)).Orders);
    }

    [Theory]
    [InlineData("GET", "/api/v1/demo/status")] [InlineData("POST", "/api/v1/demo/fill")] [InlineData("POST", "/api/v1/demo/remove")]
    public async Task Without_a_session_the_buyer_routes_answer_401(string method, string path)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Send(new HttpMethod(method), path, null, null)).StatusCode);

    [Fact]
    public async Task Status_says_what_exists_and_never_caches()
    {
        await SyncCatalog();
        var (token, _) = await Buyer("demoe@example.com");
        var none = await _k.Get("/api/v1/demo/status", token);
        Assert.False((await Body(none)).GetProperty("hasDemoData").GetBoolean());
        Assert.Contains("no-store", none.Headers.CacheControl!.ToString());
        await Fill(token);
        var some = await Body(await _k.Get("/api/v1/demo/status", token));
        Assert.True(some.GetProperty("hasDemoData").GetBoolean());
        Assert.Equal(3, some.GetProperty("counts").GetProperty("orders").GetInt32());
    }

    [Fact]
    public async Task Every_press_is_audited_with_who_what_and_the_batch_id_and_no_personal_data()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demof@example.com");
        var batch = (await Body(await Fill(token))).GetProperty("batchId").GetGuid();
        await Remove(token);
        var lines = _k.F.Logs.Lines.Where(m => m.Contains("Demo data")).ToList();
        Assert.Contains(lines, l => l.Contains("filled") && l.Contains(account.ToString()) && l.Contains(batch.ToString()));
        Assert.Contains(lines, l => l.Contains("removed") && l.Contains(account.ToString()));
        Assert.DoesNotContain(lines, l => l.Contains("demof@example.com"));
    }

    // ---------- erasure ----------
    [Fact]
    public async Task Deleting_the_account_removes_the_demo_rows_outright()
    {
        await SyncCatalog();
        var (token, account) = await Buyer("demog@example.com");
        await Fill(token);
        using (var scope = _k.F.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Account.AccountService>();
            await _k.Db(async db => { await db.BuyerAccounts.Where(x => x.Id == account).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, (DateTimeOffset?)_k.F.Clock.GetUtcNow())); return 0; });
            Assert.True(await accounts.RedactAsync(account, scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountEraser>(), default));
        }
        var t = await Totals(account);
        Assert.Equal((0, 0, 0, 0, 0, 0), (t.Orders, t.Bookings, t.Threads, t.Notifications, t.Addresses, t.Carts));
    }

    // ---------- verify ----------
    [Fact]
    public async Task Verify_marks_an_unverified_account_verified_when_the_credentials_are_right_and_not_otherwise()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("demoh@example.com")).StatusCode);                          // never verified: it cannot sign in
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/auth/sign-in", new { email = "demoh@example.com", password = AuthKit.Password })).StatusCode);
        var wrong = await _k.Post("/api/v1/demo/verify", new { email = "demoh@example.com", password = "wrong password here" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var unknown = await _k.Post("/api/v1/demo/verify", new { email = "nobody@example.com", password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());                  // the same answer: no account is revealed
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/auth/sign-in", new { email = "demoh@example.com", password = AuthKit.Password })).StatusCode);   // still unverified
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "demoh@example.com", password = AuthKit.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.Post("/api/v1/auth/sign-in", new { email = "demoh@example.com", password = AuthKit.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "demoh@example.com", password = AuthKit.Password })).StatusCode);   // again: harmless
    }

    [Fact]
    public async Task Verify_goes_through_the_sign_in_budget_so_it_cannot_be_used_to_guess_a_password()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("demoi@example.com")).StatusCode);
        HttpStatusCode last = default;
        for (var i = 0; i < 30 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await _k.Post("/api/v1/demo/verify", new { email = "demoi@example.com", password = $"wrong password {i}" })).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Null(await _k.Db(db => db.BuyerAccounts.Where(a => a.Email == "demoi@example.com").Select(a => a.EmailVerifiedAt).FirstAsync()));          // and never became verified by the attempts
    }
}

/// <summary>While the flag is off every demo route is a 404, to anyone, signed in or not, and nothing is created.</summary>
public sealed class DemoDisabledTests : IDisposable
{
    private readonly AuthKit _k = new();
    public void Dispose() => _k.Dispose();

    [Theory]
    [InlineData("GET", "/api/v1/demo/status")] [InlineData("POST", "/api/v1/demo/fill")] [InlineData("POST", "/api/v1/demo/remove")] [InlineData("POST", "/api/v1/demo/verify")]
    public async Task Every_demo_route_answers_404_signed_in_or_not(string method, string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Send(new HttpMethod(method), path, new { email = "x@example.com", password = "x" }, null)).StatusCode);
        var token = await _k.VerifiedSession("demoz@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Send(new HttpMethod(method), path, new { email = "x@example.com", password = "x" }, token)).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync(o => o.IsDemo)));
    }

    [Fact]
    public async Task A_disabled_verify_does_not_verify_anything()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("demoy@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Post("/api/v1/demo/verify", new { email = "demoy@example.com", password = AuthKit.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/auth/sign-in", new { email = "demoy@example.com", password = AuthKit.Password })).StatusCode);
    }
}
