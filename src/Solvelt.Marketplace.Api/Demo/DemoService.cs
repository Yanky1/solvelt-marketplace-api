using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Demo;

public sealed record DemoCounts(int Addresses, int Carts, int CartLines, int Orders, int Bookings, int Threads, int Messages, int Notifications)
{
    public int Total => Addresses + Carts + Orders + Bookings + Threads + Notifications;
}

public sealed record DemoFillResult(Guid BatchId, bool Created, DemoCounts Counts, IReadOnlyList<string> Notes);

/// <summary>
/// "Fill with demo data" and "Remove demo data" (M9). Everything is written straight to the database, never through the services that talk to the outside world, so no outbox
/// message, mail, payment-provider call or portal call is ever made for a demo row; payment and refund ids are fake and say so. Every top-level row is marked
/// (<see cref="IDemoMarked"/>), and removal deletes only marked rows of the one account. The account always comes from the caller's session.
/// </summary>
public sealed class DemoService(MarketplaceDb db, TimeProvider time, ILogger<DemoService> log)
{
    // One press at a time per account in this process. Demo data is a test-mode feature: a second instance racing the same account would at worst be caught by the unique keys below.
    /// <summary>One lock per account, from a fixed set (security review L4): the old dictionary of one semaphore per account ever seen only grew. Two accounts that share a slot just take turns.</summary>
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim LockFor(Guid accountId) => Locks[(int)((uint)accountId.GetHashCode() % (uint)Locks.Length)];

    public async Task<DemoCounts> CountAsync(Guid accountId, CancellationToken ct) => new(
        await db.BuyerAddresses.CountAsync(a => a.AccountId == accountId && a.IsDemo, ct),
        await db.Carts.CountAsync(c => c.AccountId == accountId && c.IsDemo, ct),
        await (from l in db.CartLines join c in db.Carts on l.CartId equals c.Id where c.AccountId == accountId && l.IsDemo select l.Id).CountAsync(ct),
        await db.Orders.CountAsync(o => o.AccountId == accountId && o.IsDemo, ct),
        await db.Bookings.CountAsync(b => b.AccountId == accountId && b.IsDemo, ct),
        await db.MessageThreads.CountAsync(t => t.AccountId == accountId && t.IsDemo, ct),
        await (from m in db.ThreadMessages join t in db.MessageThreads on m.ThreadId equals t.Id where t.AccountId == accountId && t.IsDemo select m.Id).CountAsync(ct),
        await db.Notifications.CountAsync(n => n.AccountId == accountId && n.IsDemo, ct));

    private async Task<Guid?> ExistingBatchAsync(Guid accountId, CancellationToken ct)
    {
        var ids = new List<Guid?>
        {
            await db.Orders.Where(o => o.AccountId == accountId && o.IsDemo).Select(o => o.DemoBatchId).FirstOrDefaultAsync(ct),
            await db.BuyerAddresses.Where(a => a.AccountId == accountId && a.IsDemo).Select(a => a.DemoBatchId).FirstOrDefaultAsync(ct),
            await db.Bookings.Where(b => b.AccountId == accountId && b.IsDemo).Select(b => b.DemoBatchId).FirstOrDefaultAsync(ct),
            await db.MessageThreads.Where(t => t.AccountId == accountId && t.IsDemo).Select(t => t.DemoBatchId).FirstOrDefaultAsync(ct),
            await db.Notifications.Where(n => n.AccountId == accountId && n.IsDemo).Select(n => n.DemoBatchId).FirstOrDefaultAsync(ct),
            await db.Carts.Where(c => c.AccountId == accountId && c.IsDemo).Select(c => c.DemoBatchId).FirstOrDefaultAsync(ct),
        };
        return ids.FirstOrDefault(i => i is not null);
    }

    public async Task<DemoFillResult> FillAsync(Guid accountId, CancellationToken ct)
    {
        var gate = LockFor(accountId);
        await gate.WaitAsync(ct);
        try
        {
            if (await ExistingBatchAsync(accountId, ct) is { } existing)
                return new DemoFillResult(existing, false, await CountAsync(accountId, ct), ["Demo data is already there. Remove it first to start again."]);

            var now = time.GetUtcNow().ToUniversalTime();
            var batch = Guid.NewGuid();
            var notes = new List<string>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The listings we use: visible, buyable, from the live catalog, a stable order. Listings the feed marks as demo come first (when the catalog carries that flag).
            var products = await Pick(ListingTypes.Product, 8, ct);
            var services = await Pick(ListingTypes.Service, 4, ct);
            if (products.Count == 0) notes.Add("The catalog has no products yet, so there is no demo cart or order.");
            if (services.Count == 0) notes.Add("The catalog has no services yet, so there is no demo booking.");

            // 1. A saved address: an example buyer in an example street, a 555 phone number.
            var hasDefault = await db.BuyerAddresses.AnyAsync(a => a.AccountId == accountId && a.IsDefault, ct);
            db.BuyerAddresses.Add(new BuyerAddress
            {
                Id = Guid.NewGuid(), AccountId = accountId, FullName = "Demo Buyer", Line1 = "123 Example Street", Line2 = "Apt 4", City = "Brooklyn", Region = "NY", PostalCode = "11201", Country = "US",
                Phone = "+1 555 0100", IsDefault = !hasDefault, CreatedAt = now, UpdatedAt = now, IsDemo = true, DemoBatchId = batch,
            });

            // 2. A cart with two or three items, only when the buyer has no cart of their own (one cart per buyer; a real cart is never touched).
            if (products.Count > 0)
            {
                if (await db.Carts.AnyAsync(c => c.AccountId == accountId, ct)) notes.Add("You already have a cart, so no demo cart was added.");
                else
                {
                    var cart = new Cart { Id = Guid.NewGuid(), AccountId = accountId, CreatedAt = now, UpdatedAt = now, IsDemo = true, DemoBatchId = batch };
                    db.Carts.Add(cart);
                    foreach (var (l, v) in products.Take(3))
                        db.CartLines.Add(new CartLine { Id = Guid.NewGuid(), CartId = cart.Id, ListingId = l.Id, VariantId = v.Id, Quantity = 1, SeenUnitPriceCents = v.PriceCents, AddedAtMs = now.ToUnixTimeMilliseconds(), IsDemo = true });
                }
            }

            // 3. Past orders in three states, paid with fake ids that are never sent to a payment provider.
            var states = new[] { (Status: FulfilmentStatus.Delivered, DaysAgo: 21), (Status: FulfilmentStatus.Shipped, DaysAgo: 3), (Status: FulfilmentStatus.Refunded, DaysAgo: 40) };
            var orderNumbers = new List<string>();
            for (var i = 0; i < states.Length && i < products.Count; i++)
                orderNumbers.Add(await AddOrderAsync(accountId, batch, products[i], states[i].Status, now.AddDays(-states[i].DaysAgo), i, ct));

            // 4. Bookings: one upcoming, one completed.
            var bookingNumbers = new List<string>();
            for (var i = 0; i < 2 && i < services.Count; i++)
                bookingNumbers.Add(AddBooking(accountId, batch, services[i].Listing, services[i].Variant, upcoming: i == 0, now, i).Number!);

            // 5. One message thread with a seller about a listing the buyer has no thread for yet.
            var threaded = await db.MessageThreads.Where(t => t.AccountId == accountId && t.ListingId != null).Select(t => t.ListingId!.Value).ToListAsync(ct);
            var threadListing = products.Select(p => p.Listing).Concat(services.Select(s => s.Listing)).FirstOrDefault(l => !threaded.Contains(l.Id));
            if (threadListing is not null) await AddThreadAsync(accountId, batch, threadListing, now, ct);

            // 6. Notifications that match what was created.
            AddNotifications(accountId, batch, now, orderNumbers, bookingNumbers);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var counts = await CountAsync(accountId, ct);
            log.LogInformation("Demo data filled: account {AccountId}, batch {BatchId}, {Orders} orders, {Bookings} bookings, {Threads} threads at {At}", accountId, batch, counts.Orders, counts.Bookings, counts.Threads, now);
            return new DemoFillResult(batch, true, counts, notes);
        }
        finally { gate.Release(); }
    }

    /// <summary>Deletes the demo rows of this account and nothing else: children first, then the marked parents, each scoped by account AND the marker.</summary>
    public async Task<DemoCounts> RemoveAsync(Guid accountId, CancellationToken ct)
    {
        var gate = LockFor(accountId);
        await gate.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var before = await RemoveRowsAsync(accountId, ct);
            await tx.CommitAsync(ct);
            log.LogInformation("Demo data removed: account {AccountId}, {Orders} orders, {Bookings} bookings, {Threads} threads, {Notifications} notifications at {At}", accountId, before.Orders, before.Bookings, before.Threads, before.Notifications, time.GetUtcNow());
            return before;
        }
        finally { gate.Release(); }
    }

    /// <summary>The deletes without a transaction of their own, for the account eraser (which runs inside the erasure transaction).</summary>
    public async Task<DemoCounts> RemoveRowsAsync(Guid accountId, CancellationToken ct)
    {
        var before = await CountAsync(accountId, ct);
        var orderIds = db.Orders.Where(o => o.AccountId == accountId && o.IsDemo).Select(o => o.Id);
        var groupIds = db.OrderGroups.Where(g => orderIds.Contains(g.OrderId)).Select(g => g.Id);
        var lineIds = db.OrderLines.Where(l => orderIds.Contains(l.OrderId)).Select(l => l.Id);
        var shipmentIds = db.OrderShipments.Where(s => groupIds.Contains(s.GroupId)).Select(s => s.Id);
        await db.OrderShipmentLines.Where(l => shipmentIds.Contains(l.ShipmentId)).ExecuteDeleteAsync(ct);
        await db.OrderShipments.Where(s => groupIds.Contains(s.GroupId)).ExecuteDeleteAsync(ct);
        await db.OrderLineFulfilments.Where(f => lineIds.Contains(f.LineId)).ExecuteDeleteAsync(ct);
        await db.SellerOrderStates.Where(s => orderIds.Contains(s.OrderId)).ExecuteDeleteAsync(ct);
        await db.OrderRefunds.Where(r => orderIds.Contains(r.OrderId)).ExecuteDeleteAsync(ct);
        await db.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync(ct);
        await db.OrderGroups.Where(g => orderIds.Contains(g.OrderId)).ExecuteDeleteAsync(ct);
        await db.Orders.Where(o => o.AccountId == accountId && o.IsDemo).ExecuteDeleteAsync(ct);

        // Only the MARKED lines go (security review M2): a real line the buyer added to the demo cart stays. A cart left with real lines is the buyer's own cart now (marker cleared);
        // one left empty was the demo cart and goes.
        var cartIds = db.Carts.Where(c => c.AccountId == accountId).Select(c => c.Id);
        await db.CartLines.Where(l => l.IsDemo && cartIds.Contains(l.CartId)).ExecuteDeleteAsync(ct);
        await db.Carts.Where(c => c.AccountId == accountId && c.IsDemo && !db.CartLines.Any(l => l.CartId == c.Id)).ExecuteDeleteAsync(ct);
        await db.Carts.Where(c => c.AccountId == accountId && c.IsDemo).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDemo, false).SetProperty(c => c.DemoBatchId, (Guid?)null), ct);

        var threadIds = db.MessageThreads.Where(t => t.AccountId == accountId && t.IsDemo).Select(t => t.Id);
        await db.ThreadMessages.Where(m => threadIds.Contains(m.ThreadId)).ExecuteDeleteAsync(ct);
        await db.MessageThreads.Where(t => t.AccountId == accountId && t.IsDemo).ExecuteDeleteAsync(ct);

        await db.Bookings.Where(b => b.AccountId == accountId && b.IsDemo).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => n.AccountId == accountId && n.IsDemo).ExecuteDeleteAsync(ct);
        await db.BuyerAddresses.Where(a => a.AccountId == accountId && a.IsDemo).ExecuteDeleteAsync(ct);
        return before;
    }

    // ---------- helpers ----------
    private async Task<List<(CatalogListing Listing, CatalogVariant Variant)>> Pick(string type, int take, CancellationToken ct)
    {
        var rows = await db.CatalogListings.AsNoTracking().Where(l => l.Visible && l.Available && l.Type == type).Include(l => l.Variants)
            .OrderByDescending(l => l.IsDemoListing).ThenBy(l => l.Slug).Take(take * 3).AsSplitQuery().ToListAsync(ct);
        var picked = new List<(CatalogListing, CatalogVariant)>();
        foreach (var l in rows)
            if (l.Variants.Where(v => v.Available && v.PriceCents > 0).OrderBy(v => v.SortOrder).FirstOrDefault() is { } v) picked.Add((l, v));
        return picked.Take(take).ToList();
    }

    private async Task<string> AddOrderAsync(Guid accountId, Guid batch, (CatalogListing Listing, CatalogVariant Variant) pick, string status, DateTimeOffset at, int index, CancellationToken ct)
    {
        var (l, v) = pick;
        var seller = await db.CatalogSellers.AsNoTracking().Where(s => s.Id == l.SellerId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct) ?? "Demo seller";
        var brand = await db.CatalogBrands.AsNoTracking().Where(b => b.Id == l.BrandId).Select(b => b.Name).FirstOrDefaultAsync(ct) ?? "Demo brand";
        const int qty = 1;
        var items = v.PriceCents * qty; var shipping = 700L; var tax = 0L; var total = items + shipping + tax;
        var order = new MarketplaceOrder
        {
            Id = Guid.NewGuid(), Number = CheckoutService.NewNumber(at), AccountId = accountId, Status = OrderStatus.Placed, Currency = l.Currency, ItemsCents = items, ShippingCents = shipping, TaxCents = tax, TotalCents = total,
            PaymentIntentId = $"pi_demo_{batch:N}_{index}", PaidCents = total, BuyerName = "Demo Buyer", RelayEmail = $"demo-{batch:N}-{index}@example.com", Phone = "+1 555 0100",
            ShipName = "Demo Buyer", ShipLine1 = "123 Example Street", ShipLine2 = "Apt 4", ShipCity = "Brooklyn", ShipRegion = "NY", ShipPostalCode = "11201", ShipCountry = "US",
            CreatedAt = at, UpdatedAt = at, PaidAt = at.AddMinutes(1), PlacedAt = at.AddMinutes(2), IsDemo = true, DemoBatchId = batch,
        };
        var refunded = status == FulfilmentStatus.Refunded;
        if (refunded) order.RefundedCents = total;
        var group = new OrderGroup
        {
            Id = Guid.NewGuid(), OrderId = order.Id, SellerId = l.SellerId, BrandId = l.BrandId, SellerName = seller, BrandName = brand, DeliveryKind = "carrier", ShippingSpeed = "standard",
            ItemsCents = items, ShippingCents = shipping, ShippingTaxCents = 0, TaxCents = tax, TotalCents = total, SellerOrderId = Guid.NewGuid(), SellerOrderNumber = $"DEMO-{order.Number}", RefundedCents = refunded ? total : 0,
            RefundedShippingCents = refunded ? shipping : 0,
        };
        var line = new OrderLine
        {
            Id = Guid.NewGuid(), OrderId = order.Id, GroupId = group.Id, MarketplaceLineId = $"demo-{order.Number}-1", ListingId = l.Id, VariantId = v.Id, SellerId = l.SellerId, BrandId = l.BrandId,
            Title = l.Title, VariantName = v.Name, Quantity = qty, UnitPriceCents = v.PriceCents, LineTotalCents = items, TaxCents = 0, RefundedQuantity = refunded ? qty : 0,
        };
        db.Orders.Add(order); db.OrderGroups.Add(group); db.OrderLines.Add(line);
        var shippedAt = at.AddDays(2);
        db.SellerOrderStates.Add(new SellerOrderState
        {
            GroupId = group.Id, OrderId = order.Id, Status = status, AcceptedAt = at.AddHours(3), ShippedAt = shippedAt,
            DeliveredAt = status == FulfilmentStatus.Shipped ? null : shippedAt.AddDays(3), DeliveredBy = status == FulfilmentStatus.Shipped ? null : "carrier", UpdatedAt = at.AddDays(5),
        });
        var shipment = new OrderShipment
        {
            Id = Guid.NewGuid(), GroupId = group.Id, PortalShipmentId = $"demo-sh-{order.Number}", Method = "carrier", Carrier = "Demo Express", Service = "Ground", TrackingNumber = $"DEMO{index + 1}0000{index + 7}",
            ShippedAt = shippedAt, DeliveredAt = status == FulfilmentStatus.Shipped ? null : shippedAt.AddDays(3), TrackingStatus = status == FulfilmentStatus.Shipped ? "in_transit" : "delivered",
            TrackingStatusDetail = status == FulfilmentStatus.Shipped ? "On its way" : "Delivered to the front door", Eta = status == FulfilmentStatus.Shipped ? shippedAt.AddDays(4) : null, UpdatedAt = at.AddDays(5),
            TrackingEventsJson = JsonSerializer.Serialize(new[] { new { at = shippedAt, status = "picked_up", text = "Picked up by Demo Express" }, new { at = shippedAt.AddDays(1), status = "in_transit", text = "Arrived at the sorting centre" } }),
        };
        db.OrderShipments.Add(shipment);
        db.OrderLineFulfilments.Add(new OrderLineFulfilment { LineId = line.Id, ShippedQty = qty, RefundedQty = refunded ? qty : 0 });
        if (refunded)
            db.OrderRefunds.Add(new OrderRefund
            {
                Id = Guid.NewGuid(), Key = $"demo-{order.Number}", OrderId = order.Id, GroupId = group.Id, AmountCents = items, ShippingCents = shipping, Reason = "Demo refund", Attempt = 1,
                Status = RefundStatus.Succeeded, StripeRefundId = $"re_demo_{batch:N}", ReportToPortal = false, CreatedAt = at.AddDays(6), UpdatedAt = at.AddDays(6),
            });
        return order.Number;
    }

    private Booking AddBooking(Guid accountId, Guid batch, CatalogListing l, CatalogVariant v, bool upcoming, DateTimeOffset now, int index)
    {
        var start = upcoming ? now.AddDays(5).Date.AddHours(14) : now.AddDays(-9).Date.AddHours(10);
        var id = Guid.NewGuid();
        return db.Bookings.Add(new Booking
        {
            Id = id, MarketplaceBookingId = $"demo-bk-{batch:N}-{index}", Number = $"DEMO-B{index + 1}", AccountId = accountId, IdempotencyKey = $"demo-{batch:N}-{index}", RequestHash = "demo",
            ListingId = l.Id, Status = upcoming ? BookingStatus.Confirmed : BookingStatus.Completed, Location = "online", StartAt = start, EndAt = start.AddMinutes(60), DurationMinutes = 60, Currency = l.Currency,
            PriceCents = v.PriceCents, TotalCents = v.PriceCents, OnlineInstructions = "This is a demo booking: there is no meeting link.", CreatedAt = now.AddDays(upcoming ? -1 : -12), UpdatedAt = now.AddDays(upcoming ? -1 : -9),
            IsDemo = true, DemoBatchId = batch, Version = 1,
        }).Entity;
    }

    private async Task AddThreadAsync(Guid accountId, Guid batch, CatalogListing l, DateTimeOffset now, CancellationToken ct)
    {
        var sellerName = await db.CatalogSellers.AsNoTracking().Where(s => s.Id == l.SellerId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct) ?? "Demo seller";
        var thread = new MessageThread
        {
            Id = Guid.NewGuid(), ConversationKey = $"CV-DEMO-{batch:N}", AccountId = accountId, Kind = ThreadKinds.Product, ListingId = l.Id, SellerName = sellerName, Subject = l.Title,
            CreatedAt = now.AddDays(-2), LastMessageAt = now.AddDays(-2).AddHours(3), IsDemo = true, DemoBatchId = batch,
        };
        db.MessageThreads.Add(thread);
        db.ThreadMessages.Add(new ThreadMessage { Id = Guid.NewGuid(), ThreadId = thread.Id, Direction = MessageDirections.Buyer, Text = "Hello! Is this still available? (Demo message)", Status = MessageStatuses.Sent, SentAt = thread.CreatedAt, ReadAt = thread.CreatedAt.AddHours(1) });
        db.ThreadMessages.Add(new ThreadMessage { Id = Guid.NewGuid(), ThreadId = thread.Id, Direction = MessageDirections.Seller, Text = "Hi, yes it is, and it ships this week. (Demo reply)", Status = MessageStatuses.Received, SentAt = thread.LastMessageAt });
    }

    private void AddNotifications(Guid accountId, Guid batch, DateTimeOffset now, List<string> orders, List<string> bookings)
    {
        var n = 0;
        void Add(string kind, string? order, DateTimeOffset at, bool read) => db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(), AccountId = accountId, Kind = kind, OrderNumber = order, DedupeKey = $"demo:{batch:N}:{n++}", CreatedAt = at, ReadAt = read ? at.AddHours(2) : null, IsDemo = true, DemoBatchId = batch,
        });
        if (orders.Count > 0) { Add(NotificationKinds.OrderDelivered, orders[0], now.AddDays(-18), true); }
        if (orders.Count > 1) { Add(NotificationKinds.OrderShipped, orders[1], now.AddDays(-1), false); }
        if (orders.Count > 2) { Add(NotificationKinds.RefundIssued, orders[2], now.AddDays(-33), true); }
        if (bookings.Count > 0) { Add(NotificationKinds.BookingConfirmed, null, now.AddDays(-1), false); }
        Add(NotificationKinds.MessageReceived, null, now.AddDays(-2), false);
    }
}
