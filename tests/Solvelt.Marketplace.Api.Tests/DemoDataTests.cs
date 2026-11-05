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
    public async Task The_buyers_own_order_list_and_detail_show_the_demo_orders_in_their_real_states_and_with_tracking()
    {
        await SyncCatalog();
        var (token, _) = await Buyer("demo10@example.com");
        await Fill(token);
        var list = await Body(await _k.Get("/api/v1/orders", token));
        var orders = list.GetProperty("orders").EnumerateArray().ToList();
        Assert.Equal(3, orders.Count);
        var statuses = orders.SelectMany(o => o.GetProperty("groups").EnumerateArray()).Select(g => g.GetProperty("status").GetString()).ToList();
        Assert.Equivalent(new[] { "delivered", "shipped", "refunded" }, statuses);                                             // never "processing": a group without a seller order id would read as "being placed"
        var shipped = orders.First(o => o.GetProperty("groups").EnumerateArray().Any(g => g.GetProperty("status").GetString() == "shipped"));
        var detail = await Body(await _k.Get($"/api/v1/orders/{shipped.GetProperty("number").GetString()}", token));
        Assert.Contains("DEMO", detail.GetRawText());                                                                          // the tracking number is there
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
        var marketplaceBookingId = await _k.Db(db => db.Bookings.Where(b => b.Id == booking).Select(b => b.MarketplaceBookingId).FirstAsync());
        var sellerOrderNumber = await _k.Db(db => db.OrderGroups.Where(g => g.SellerOrderNumber != null && db.Orders.Any(o => o.Id == g.OrderId && o.AccountId == account && o.IsDemo)).Select(g => g.SellerOrderNumber).FirstAsync());
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
            ($"/api/v1/bookings/{booking}/extra-charge/accept", new { }),
            ($"/api/v1/bookings/{booking}/extra-charge/decline", new { }),
            ($"/api/v1/bookings/{booking}/checkout", new { }),
            ("/api/v1/messages", new { sellerOrderNumber, text = "About my seller order" }),
            ("/api/v1/messages", new { marketplaceBookingId, text = "About my booking" }),
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
        var realOrder = Guid.NewGuid(); var realGroup = Guid.NewGuid(); var realLine = Guid.NewGuid(); var realShipment = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.BuyerAddresses.Add(new BuyerAddress { Id = Guid.NewGuid(), AccountId = a, FullName = "Real", Line1 = "1 Real Rd", City = "Town", PostalCode = "10001", Country = "US", CreatedAt = now, UpdatedAt = now });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = a, Kind = NotificationKinds.OrderShipped, DedupeKey = "real-1", CreatedAt = now });
            db.Orders.Add(new MarketplaceOrder { Id = realOrder, Number = "REAL-1", AccountId = a, Status = OrderStatus.Placed, BuyerName = "Real", RelayEmail = "real@relay.test", ShipName = "Real", ShipLine1 = "1 Real Rd", ShipCity = "Town", ShipPostalCode = "10001", ShipCountry = "US", CreatedAt = now, UpdatedAt = now });
            // the real order is a whole order: group, line, seller state, shipment, line fulfilment and a refund (security review L1), none of which Remove may touch
            var anySeller = Guid.NewGuid();
            db.OrderGroups.Add(new OrderGroup { Id = realGroup, OrderId = realOrder, SellerId = anySeller, BrandId = Guid.NewGuid(), SellerName = "Real seller", BrandName = "Real brand", DeliveryKind = "carrier", ShippingSpeed = "standard", ItemsCents = 1000, ShippingCents = 500, TotalCents = 1500, SellerOrderId = Guid.NewGuid(), SellerOrderNumber = "REAL-SO-1", RefundedCents = 200 });
            db.OrderLines.Add(new OrderLine { Id = realLine, OrderId = realOrder, GroupId = realGroup, MarketplaceLineId = "real-1-1", ListingId = Guid.NewGuid(), VariantId = Guid.NewGuid(), SellerId = anySeller, BrandId = Guid.NewGuid(), Title = "Real thing", VariantName = "One", Quantity = 1, UnitPriceCents = 1000, LineTotalCents = 1000 });
            db.SellerOrderStates.Add(new SellerOrderState { GroupId = realGroup, OrderId = realOrder, Status = FulfilmentStatus.Shipped, AcceptedAt = now, ShippedAt = now, UpdatedAt = now });
            db.OrderShipments.Add(new OrderShipment { Id = realShipment, GroupId = realGroup, PortalShipmentId = "real-sh-1", Method = "carrier", Carrier = "Real Express", Service = "Ground", TrackingNumber = "REAL123", ShippedAt = now, TrackingStatus = "in_transit", UpdatedAt = now, TrackingEventsJson = "[]" });
            db.OrderLineFulfilments.Add(new OrderLineFulfilment { LineId = realLine, ShippedQty = 1, RefundedQty = 0 });
            db.OrderRefunds.Add(new OrderRefund { Id = Guid.NewGuid(), Key = "real-refund-1", OrderId = realOrder, GroupId = realGroup, AmountCents = 200, ShippingCents = 0, Reason = "Real refund", Attempt = 1, Status = RefundStatus.Succeeded, StripeRefundId = "re_real_1", ReportToPortal = false, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(); return 0;
        });
        var (_, realCartAccount) = await Buyer("demorealcart@example.com");
        await _k.Db(async db =>
        {
            var cart = new Cart { Id = Guid.NewGuid(), AccountId = realCartAccount, CreatedAt = now, UpdatedAt = now };
            db.Carts.Add(cart);
            var picks = await db.CatalogVariants.Select(v => new { v.Id, v.ListingId }).Take(2).ToListAsync();
            foreach (var p in picks) db.CartLines.Add(new CartLine { Id = Guid.NewGuid(), CartId = cart.Id, ListingId = p.ListingId, VariantId = p.Id, Quantity = 1, SeenUnitPriceCents = 100, AddedAtMs = now.ToUnixTimeMilliseconds() });
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
            Assert.Equal((1, 1, 1, 1, 1, 1), (await db.OrderGroups.CountAsync(g => g.Id == realGroup), await db.OrderLines.CountAsync(l => l.Id == realLine), await db.SellerOrderStates.CountAsync(x => x.GroupId == realGroup),
                await db.OrderShipments.CountAsync(x => x.Id == realShipment), await db.OrderLineFulfilments.CountAsync(x => x.LineId == realLine), await db.OrderRefunds.CountAsync(x => x.Key == "real-refund-1")));   // all of it survived
            Assert.Equal(1, await db.Carts.CountAsync(c => c.AccountId == realCartAccount && !c.IsDemo));                    // a real cart of somebody else
            Assert.Equal(2, await db.CartLines.CountAsync(l => db.Carts.Any(c => c.Id == l.CartId && c.AccountId == realCartAccount)));
            Assert.Equal(1, await db.BuyerAddresses.CountAsync(x => x.AccountId == a && x.FullName == "Real"));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.DedupeKey == "real-1"));
            // no orphans of the removed orders
            Assert.Equal(0, await db.OrderLines.CountAsync(l => !db.Orders.Any(o => o.Id == l.OrderId)) + await db.OrderGroups.CountAsync(g => !db.Orders.Any(o => o.Id == g.OrderId))
                + await db.SellerOrderStates.CountAsync(s => !db.Orders.Any(o => o.Id == s.OrderId)) + await db.ThreadMessages.CountAsync(m => !db.MessageThreads.Any(t => t.Id == m.ThreadId)) + await db.CartLines.CountAsync(l => !db.Carts.Any(c => c.Id == l.CartId)));
            return 0;
        });
        Assert.Equal(bBefore, await Totals(b));                                                                           // another account's demo data is untouched
    }

    [Fact]
    public async Task Remove_keeps_a_real_line_the_buyer_added_to_the_demo_cart_and_hands_the_cart_back_to_the_buyer()
    {
        await SyncCatalog();
        var (token, a) = await Buyer("demoe@example.com");
        Assert.Equal(HttpStatusCode.Created, (await Fill(token)).StatusCode);
        var now = _k.F.Clock.GetUtcNow();
        var real = Guid.NewGuid();
        await _k.Db(async db =>
        {
            var cart = await db.Carts.SingleAsync(c => c.AccountId == a);
            Assert.True(cart.IsDemo);
            var used = await db.CartLines.Where(l => l.CartId == cart.Id).Select(l => l.VariantId).ToListAsync();
            var spare = await db.CatalogVariants.Where(v => !used.Contains(v.Id)).Select(v => new { v.Id, v.ListingId }).FirstAsync();   // a variant the demo cart does not hold
            db.CartLines.Add(new CartLine { Id = real, CartId = cart.Id, ListingId = spare.ListingId, VariantId = spare.Id, Quantity = 2, SeenUnitPriceCents = 100, AddedAtMs = now.ToUnixTimeMilliseconds() });
            await db.SaveChangesAsync(); return 0;
        });
        var res = await Remove(token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await _k.Db(async db =>
        {
            var cart = await db.Carts.AsNoTracking().SingleAsync(c => c.AccountId == a);                                   // the cart stays: it holds a real line
            Assert.False(cart.IsDemo); Assert.Null(cart.DemoBatchId);                                                       // and it is the buyer's own now
            var lines = await db.CartLines.AsNoTracking().Where(l => l.CartId == cart.Id).ToListAsync();
            var only = Assert.Single(lines);
            Assert.Equal((real, 2, false), (only.Id, only.Quantity, only.IsDemo));
            return 0;
        });
        // a second Remove finds nothing marked and changes nothing; a second Fill adds no demo cart over the buyer's cart
        Assert.Equal(HttpStatusCode.OK, (await Remove(token)).StatusCode);
        Assert.Equal(1, await _k.Db(db => db.CartLines.CountAsync(l => l.Id == real)));
        // with only demo lines left the cart is removed as before
        await Remove(token);
        await _k.Db(async db => { await db.CartLines.Where(l => l.Id == real).ExecuteDeleteAsync(); await db.Carts.Where(c => c.AccountId == a).ExecuteDeleteAsync(); return 0; });
        Assert.Equal(HttpStatusCode.Created, (await Fill(token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Remove(token)).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == a) ));
        Assert.Equal(0, await _k.Db(db => db.CartLines.CountAsync(l => !db.Carts.Any(c => c.Id == l.CartId))));
    }

    [Fact]
    public async Task Editing_a_demo_address_makes_it_the_buyers_own_and_Remove_leaves_it()
    {
        await SyncCatalog();
        var (token, a) = await Buyer("demof@example.com");
        Assert.Equal(HttpStatusCode.Created, (await Fill(token)).StatusCode);
        var id = await _k.Db(db => db.BuyerAddresses.Where(x => x.AccountId == a && x.IsDemo).Select(x => x.Id).SingleAsync());
        var edit = await _k.Send(HttpMethod.Put, $"/api/v1/account/addresses/{id}", new { fullName = "Dana Real", line1 = "9 Real Street", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", phone = "+1 718 555 0199" }, token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var row = await _k.Db(db => db.BuyerAddresses.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.False(row.IsDemo); Assert.Null(row.DemoBatchId);
        Assert.Equal(HttpStatusCode.OK, (await Remove(token)).StatusCode);
        var kept = await _k.Db(db => db.BuyerAddresses.AsNoTracking().SingleAsync(x => x.Id == id));                          // still there, with the buyer's words
        Assert.Equal("9 Real Street", kept.Line1);
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
    public async Task Verify_never_claims_a_guest_order_and_refuses_an_address_that_has_one_the_attacker_cannot_take_the_victims_order()
    {
        await SyncCatalog();
        // the victim placed a guest order with victim@example.com (an order of another account, turned into a guest order)
        var (seedToken, _) = await Buyer("demoseed@example.com");
        Assert.Equal(HttpStatusCode.Created, (await Fill(seedToken)).StatusCode);
        var guest = await _k.Db(async db =>
        {
            var o = await db.Orders.Where(x => x.IsDemo).OrderBy(x => x.Number).FirstAsync();
            await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, (Guid?)null).SetProperty(x => x.GuestEmail, "victim@example.com"));
            return o.Id;
        });
        // the attacker signs up as victim@example.com with their own password and tries the test-only verify
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("victim@example.com")).StatusCode);
        var attempt = await _k.Post("/api/v1/demo/verify", new { email = "victim@example.com", password = AuthKit.Password });
        var wrong = await _k.Post("/api/v1/demo/verify", new { email = "victim@example.com", password = "wrong password here" });
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await attempt.Content.ReadAsStringAsync());                    // the generic refusal: nothing says an order exists
        await _k.Db(async db =>
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == guest);
            Assert.Null(order.AccountId); Assert.Null(order.ClaimedAt);                                                         // the order is still the guest's
            Assert.Null((await db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "victim@example.com")).EmailVerifiedAt);   // and the account is still unverified
            return 0;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/auth/sign-in", new { email = "victim@example.com", password = AuthKit.Password })).StatusCode);
        // an address without guest orders still verifies, and verifying changes only the verification time: a guest order placed later is not claimed by it either
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("clean@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "clean@example.com", password = AuthKit.Password })).StatusCode);
        await _k.Db(async db =>
        {
            await db.Orders.Where(o => o.Id == guest).ExecuteUpdateAsync(s => s.SetProperty(x => x.GuestEmail, "clean@example.com"));
            return 0;
        });
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "clean@example.com", password = AuthKit.Password })).StatusCode);   // already verified: harmless
        Assert.Null((await _k.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == guest))).AccountId);
    }

    [Fact]
    public async Task Fill_and_Remove_share_a_per_account_limit_that_resets_with_time_and_does_not_touch_other_accounts()
    {
        await SyncCatalog();
        var (token, _) = await Buyer("demol1@example.com");
        var (other, _) = await Buyer("demol2@example.com");
        for (var i = 0; i < DemoActionLimiter.Limit; i++)
            Assert.Equal(HttpStatusCode.OK, (await Remove(token)).StatusCode);                                              // nothing to remove: cheap, but counted
        var refused = await Remove(token);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) is >= 1 and <= 60);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Fill(token)).StatusCode);                                       // fill and remove count together
        Assert.Equal(0, (await Totals()).Orders);                                                                           // and the refused fill created nothing
        Assert.Equal(HttpStatusCode.OK, (await Remove(other)).StatusCode);                                                  // another account has its own budget
        Assert.Equal(HttpStatusCode.OK, (await _k.Send(HttpMethod.Get, "/api/v1/demo/status", null, token)).StatusCode);   // reading is never limited
        _k.F.Clock.Advance(DemoActionLimiter.Window + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await Fill(token)).StatusCode);                                               // the window passed
    }

    [Fact]
    public async Task A_failed_verify_is_logged_with_a_hash_of_the_client_and_never_the_address_or_password()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("demologged@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Post("/api/v1/demo/verify", new { email = "demologged@example.com", password = "wrong password here" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Post("/api/v1/demo/verify", new { email = "nobodylogged@example.com", password = "wrong password here" })).StatusCode);
        var lines = _k.F.Logs.Lines.Where(l => l.Contains("Demo verify failed")).ToList();
        Assert.Equal(2, lines.Count);                                                                                       // known and unknown address are logged alike
        Assert.All(lines, l => Assert.Matches(@"ClientHash=[0-9a-f]{12}", l));
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("demologged@example.com") || l.Contains("nobodylogged@example.com") || l.Contains("wrong password here"));
    }

    // ---------- M9-07: a demo-verified address was never proven, so it never receives a guest order ----------
    private async Task<Guid> GuestOrderFor(string guestEmail)
    {
        // an order of another account, turned into a guest order placed with <guestEmail> (a victim who checks out as a guest AFTER the attacker's sign-up)
        var (seedToken, seedAccount) = await Buyer($"seed-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Created, (await Fill(seedToken)).StatusCode);
        return await _k.Db(async db =>
        {
            var o = await db.Orders.Where(x => x.AccountId == seedAccount && x.IsDemo).OrderBy(x => x.Number).FirstAsync();
            await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, (Guid?)null).SetProperty(x => x.GuestEmail, guestEmail));
            return o.Id;
        });
    }
    private Task<MarketplaceOrder> OrderRow(Guid id) => _k.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == id));

    [Fact]
    public async Task A_demo_verified_account_never_claims_a_guest_order_placed_later_not_at_sign_in_and_not_with_a_passkey_path()
    {
        await SyncCatalog();
        // the attacker signs up as victim@example.com and demo-verifies while there are no guest orders
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("later@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "later@example.com", password = AuthKit.Password })).StatusCode);
        var account = await _k.Account("later@example.com");
        Assert.NotNull(account.EmailVerifiedAt); Assert.NotNull(account.DemoVerifiedAt);                                   // signs in, and is marked as never proven
        // later the real owner of the mailbox checks out as a guest; the attacker signs in
        var guest = await GuestOrderFor("later@example.com");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await _k.Post("/api/v1/auth/sign-in", new { email = "later@example.com", password = AuthKit.Password })).StatusCode);
        var order = await OrderRow(guest);
        Assert.Null(order.AccountId); Assert.Null(order.ClaimedAt);                                                         // still the guest's
        // the claimer itself refuses (every path ends there: sign-in, passkey sign-in, verify, reset)
        using (var scope = _k.F.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Auth.GuestClaims>().ClaimAsync(account.Id, "later@example.com", default));
        Assert.Null((await OrderRow(guest)).AccountId);
    }

    [Fact]
    public async Task A_real_mailbox_proof_replaces_a_demo_verification_through_the_link_and_through_a_reset_and_then_guest_orders_are_claimed()
    {
        await SyncCatalog();
        // A) the verification link
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("proofa@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "proofa@example.com", password = AuthKit.Password })).StatusCode);
        var guestA = await GuestOrderFor("proofa@example.com");
        await _k.Post("/api/v1/auth/sign-in", new { email = "proofa@example.com", password = AuthKit.Password });
        Assert.Null((await OrderRow(guestA)).AccountId);
        var link = AuthKit.TokenIn((await _k.Mail("proofa@example.com")).Last(m => m.Subject.StartsWith("Confirm")), "/verify-email");
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/auth/verify-email", new { token = link, password = AuthKit.Password })).StatusCode);
        var a = await _k.Account("proofa@example.com");
        Assert.Null(a.DemoVerifiedAt);                                                                                       // proven for real now
        Assert.Equal(a.Id, (await OrderRow(guestA)).AccountId);                                                              // and the order is claimed by the proof
        // B) a password reset through the mailbox
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("proofb@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/demo/verify", new { email = "proofb@example.com", password = AuthKit.Password })).StatusCode);
        var guestB = await GuestOrderFor("proofb@example.com");
        Assert.NotNull((await _k.Account("proofb@example.com")).DemoVerifiedAt);
        await _k.Post("/api/v1/auth/forgot-password", new { email = "proofb@example.com" });
        var reset = AuthKit.TokenIn((await _k.Mail("proofb@example.com")).Single(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/auth/reset-password", new { token = reset, password = "a brand new password" })).StatusCode);
        var b = await _k.Account("proofb@example.com");
        Assert.Null(b.DemoVerifiedAt);
        Assert.Equal(b.Id, (await OrderRow(guestB)).AccountId);
    }

    [Fact]
    public async Task An_ordinary_verified_account_still_claims_its_guest_orders_at_sign_in()
    {
        await SyncCatalog();
        var token = await _k.VerifiedSession("ordinary@example.com");
        var account = await _k.Account("ordinary@example.com");
        Assert.Null(account.DemoVerifiedAt);
        var guest = await GuestOrderFor("ordinary@example.com");
        Assert.Equal(HttpStatusCode.OK, (await _k.Post("/api/v1/auth/sign-in", new { email = "ordinary@example.com", password = AuthKit.Password })).StatusCode);
        Assert.Equal(account.Id, (await OrderRow(guest)).AccountId);
        _ = token;
    }

    [Fact]
    public async Task A_refused_verify_does_not_clear_the_sign_in_gate_but_a_verify_that_worked_does()
    {
        await SyncCatalog();
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("gate@example.com")).StatusCode);
        await GuestOrderFor("gate@example.com");
        // refused (an unclaimed guest order): counted against the address like a wrong password, never reset by the refusal
        HttpStatusCode last = default;
        for (var i = 0; i < 40 && last != HttpStatusCode.TooManyRequests; i++)
            last = (await _k.Post("/api/v1/demo/verify", new { email = "gate@example.com", password = AuthKit.Password })).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Null((await _k.Account("gate@example.com")).EmailVerifiedAt);
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

    [Theory]
    [InlineData("GET", "/api/v1/demo/fill")] [InlineData("GET", "/api/v1/demo/remove")] [InlineData("GET", "/api/v1/demo/verify")] [InlineData("POST", "/api/v1/demo/status")]
    [InlineData("DELETE", "/api/v1/demo/fill")] [InlineData("GET", "/api/v1/demo/nothing/here")] [InlineData("POST", "/api/v1/demo/anything")] [InlineData("PUT", "/api/v1/demo")]
    public async Task Any_other_method_or_path_under_demo_is_a_plain_404_never_a_405_while_it_is_off(string method, string path)
    {
        var token = await _k.VerifiedSession("demow@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Send(new HttpMethod(method), path, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Send(new HttpMethod(method), path, null, token)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_verify_does_not_verify_anything()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _k.SignUp("demoy@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Post("/api/v1/demo/verify", new { email = "demoy@example.com", password = AuthKit.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/auth/sign-in", new { email = "demoy@example.com", password = AuthKit.Password })).StatusCode);
    }
}
