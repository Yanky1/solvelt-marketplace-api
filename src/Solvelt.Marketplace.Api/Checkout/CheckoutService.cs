using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Checkout;

public sealed record CheckoutAddress(string? Name, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, string? Phone, double? Lat = null, double? Lng = null);
/// <summary>The buyer's choices only: where to, which delivery per group, a gift note. No money field exists.</summary>
/// <summary>
/// <paramref name="QuoteHash"/> is the token of the quote the buyer SAW and agreed to (<see cref="QuoteSigner"/>), required. It is never charged and never prices anything: it only guards consent. When it does not match
/// the server's fresh quote (a price, tax, shipping, delivery choice or destination changed) or has expired, nothing is created and the answer is <c>409 price_changed</c> with the new quote to show and confirm.
/// </summary>
public sealed record CheckoutRequest(CheckoutAddress? Address, IReadOnlyList<GroupSelection>? Selections, string? GiftMessage, string? QuoteHash = null, string? Email = null);

/// <summary>Who is checking out: a signed-in buyer (<see cref="AccountId"/>), or a guest (<see cref="GuestToken"/>, the cart token, and <see cref="GuestEmail"/>, trimmed and lower-cased) whose order gets a relay address of its own.</summary>
public sealed record CheckoutActor(Guid? AccountId, string? GuestToken, string? GuestEmail, string DisplayName, string RelayEmail);
public sealed record CheckoutPayment(string Provider, string ClientSecret, string? PublishableKey);
public sealed record CheckoutResult(string OrderNumber, string Status, QuoteView Quote, CheckoutPayment? Payment);

public enum CheckoutStatus { Ok, Invalid, Quote, NotPending, PaymentsUnavailable, AmountTooSmall, PaymentRejected, Superseded, PriceChanged, InProgress }

/// <summary>Test seam (one instance per application): awaited right after the intent was created and before it is stored on the order.</summary>
public sealed class CheckoutHooks
{
    public Func<Task>? AfterIntentCreated { get; set; }
    /// <summary>Test seam: awaited inside the checkout transaction, after supersede listed an old pending order and before it changes that order's status; gets the transaction's context.</summary>
    public Func<MarketplaceDb, Task>? BeforeSupersedeUpdate { get; set; }
}

public sealed class CheckoutService(MarketplaceDb db, QuoteService quotes, QuoteSigner signer, CartService carts, IPaymentProvider payments, IOptions<StripeOptions> stripe, Outbox.OutboxWriter outbox, CheckoutHooks hooks, TimeProvider time, ILogger<CheckoutService> log)
{
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();
    private static string Clean(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

    public static IReadOnlyDictionary<string, string> Validate(CheckoutAddress? a, string? gift)
    {
        var e = new Dictionary<string, string>();
        if (a is null) { e["address"] = "Enter a delivery address."; return e; }
        if (Clean(a.Name) is not { Length: > 0 and <= 120 }) e["address.name"] = "Enter the recipient's name.";
        if (Clean(a.Line1) is not { Length: > 0 and <= 200 }) e["address.line1"] = "Enter the street address.";
        if (Clean(a.Line2).Length > 200) e["address.line2"] = "That is too long.";
        if (Clean(a.City) is not { Length: > 0 and <= 100 }) e["address.city"] = "Enter the city.";
        if (Clean(a.Region).Length > 60) e["address.region"] = "That is too long.";
        if (Clean(a.PostalCode) is not { Length: > 0 and <= 20 }) e["address.postalCode"] = "Enter the postal code.";
        if (Clean(a.Country) is not { Length: 2 } c || !c.All(char.IsAsciiLetter)) e["address.country"] = "Choose the country.";
        if (a.Phone is { } p && (Clean(p).Length > 40 || Clean(p).Any(ch => !(char.IsAsciiDigit(ch) || ch is '+' or '-' or '(' or ')' or ' ' or '.')))) e["address.phone"] = "Enter a valid phone number.";
        if (a.Lat is { } la && (double.IsNaN(la) || la is < -90 or > 90) || a.Lng is { } lo && (double.IsNaN(lo) || lo is < -180 or > 180)) e["address.location"] = "That location is not valid.";
        if (gift is { Length: > 500 }) e["giftMessage"] = "Use at most 500 characters.";
        return e;
    }

    public async Task<(CheckoutStatus Status, CheckoutResult? Result, string? Detail)> PlaceAsync(CheckoutActor buyer, CheckoutRequest req, CancellationToken ct)
    {
        var a = req.Address!;
        var owner = new CartOwner(buyer.AccountId, buyer.GuestToken);
        var country = Clean(a.Country).ToUpperInvariant();
        string? region = Clean(a.Region) is { Length: > 0 } rg ? rg.ToUpperInvariant() : null;
        var postal = Clean(a.PostalCode);
        var q = await quotes.QuoteAsync(owner, new QuoteRequest(new QuoteAddress(country, region, postal, a.Lat, a.Lng), req.Selections), ct);
        if (q.Status != QuoteStatus.Ok) return (CheckoutStatus.Quote, null, q.Status.ToString() + (q.Detail is null ? "" : ":" + q.Detail));
        var quote = q.View!;
        if (!quote.CanPay) return (CheckoutStatus.Quote, null, "delivery_unavailable");
        if (!signer.Valid(req.QuoteHash, quote, country, region, postal, Now)) return (CheckoutStatus.PriceChanged, new CheckoutResult("", "price_changed", quote, null), null);
        if (quote.TotalCents < 50) return (CheckoutStatus.AmountTooSmall, null, null);   // Stripe's minimum charge
        var cartId = await carts.CartIdAsync(owner, ct);

        var order = Build(buyer, cartId, a, req.GiftMessage, quote);
        if (cartId is { } cid)
        {
            // Never replace an order whose money may still be on its way (decision M3-25): a delayed payment (status processing, or captured later) would arrive on a closed order and have to be refunded.
            // Asked at Stripe BEFORE the transaction (no provider call while the cart is locked). A different checkout of the same cart is only allowed once nothing is in flight.
            var key = order.ActiveKey;
            var others = await db.Orders.AsNoTracking().Where(o => o.CartId == cid && o.AccountId == buyer.AccountId && o.PaymentIntentId != null && (o.ActiveKey == null || o.ActiveKey != key)
                && (o.Status == OrderStatus.PendingPayment || o.Status == OrderStatus.PaymentProcessing)).Select(o => new { o.Status, o.PaymentIntentId }).ToListAsync(ct);
            foreach (var o in others)
            {
                if (o.Status == OrderStatus.PaymentProcessing) return (CheckoutStatus.InProgress, null, null);
                try
                {
                    if (await payments.GetIntentStatusAsync(o.PaymentIntentId!, ct) is "processing" or "requires_capture") return (CheckoutStatus.InProgress, null, null);
                }
                catch (PaymentsUnavailableException) { return (CheckoutStatus.PaymentsUnavailable, null, null); }
                catch (PaymentRejectedException ex) when (ex.Code == "resource_missing") { /* gone at Stripe: nothing in flight */ }
            }
        }
        var existing = await LockedInsertOrFindAsync(order, ct);
        if (existing.Status != OrderStatus.PendingPayment) return (CheckoutStatus.NotPending, null, null);

        try
        {
            // The key is the order number: a double-click, a retry and a parallel request all reach Stripe with the SAME key, so there is exactly one PaymentIntent per order.
            var intent = await payments.CreateIntentAsync(existing.TotalCents, existing.Currency, "pi:" + existing.Number,
                new Dictionary<string, string> { ["marketplace_order_id"] = existing.Number }, ct);
            if (hooks.AfterIntentCreated is { } hook) await hook();
            // Stored only while the order is still the pending one. A newer checkout of the same cart supersedes this order FIRST (status change) and only then reads the intent id to cancel
            // it, so one of the two always sees the other: either the superseder cancels this intent, or this check fails and cancels it here. No intent is left payable on a closed order.
            await db.Orders.Where(o => o.Id == existing.Id && o.PaymentIntentId == null && o.Status == OrderStatus.PendingPayment).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentIntentId, intent.Id), ct);
            var row = await db.Orders.AsNoTracking().Where(o => o.Id == existing.Id).Select(o => new { o.Status, o.PaymentIntentId }).FirstAsync(ct);
            if (row.Status is OrderStatus.Superseded or OrderStatus.Expired) { await QueueCancelAsync(intent.Id, ct); return (CheckoutStatus.Superseded, null, null); }
            if (row.Status != OrderStatus.PendingPayment) return (CheckoutStatus.NotPending, null, null);
            if (row.PaymentIntentId != intent.Id) { log.LogError("Order {Number} has two different payment intents (stored {Stored}, returned {Returned})", existing.Number, row.PaymentIntentId, intent.Id); await QueueCancelAsync(intent.Id, ct); return (CheckoutStatus.PaymentsUnavailable, null, "intent_mismatch"); }
            return (CheckoutStatus.Ok, new CheckoutResult(existing.Number, existing.Status, quote, new CheckoutPayment("stripe", intent.ClientSecret, stripe.Value.PublishableKey)), null);
        }
        catch (PaymentsUnavailableException) { return (CheckoutStatus.PaymentsUnavailable, null, null); }   // the order stays pending; the same request later finds it and retries the intent
        catch (PaymentRejectedException ex) { log.LogWarning("Payment provider refused order {Number}: {Code}", existing.Number, ex.Code); return (CheckoutStatus.PaymentRejected, null, ex.Code); }
    }

    private MarketplaceOrder Build(CheckoutActor buyer, Guid? cartId, CheckoutAddress a, string? gift, QuoteView quote)
    {
        var now = Now;
        var number = NewNumber(now);
        var id = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            Id = id, Number = number, AccountId = buyer.AccountId, CartId = cartId, Status = OrderStatus.PendingPayment, Currency = quote.Currency,
            ItemsCents = quote.ItemsCents, ShippingCents = quote.ShippingCents, TaxCents = quote.TaxCents, TotalCents = quote.TotalCents,
            BuyerName = RelayIdentity.PublicName(buyer.DisplayName), RelayEmail = buyer.RelayEmail, GuestEmail = buyer.GuestEmail, Phone = Clean(a.Phone) is { Length: > 0 } ph ? ph : null,
            ShipName = Clean(a.Name), ShipLine1 = Clean(a.Line1), ShipLine2 = Clean(a.Line2) is { Length: > 0 } l2 ? l2 : null, ShipCity = Clean(a.City),
            ShipRegion = Clean(a.Region) is { Length: > 0 } rg ? (Clean(a.Country).Equals("US", StringComparison.OrdinalIgnoreCase) ? rg.ToUpperInvariant() : rg) : null, ShipPostalCode = Clean(a.PostalCode), ShipCountry = Clean(a.Country).ToUpperInvariant(),
            ShipLat = a.Lat, ShipLng = a.Lng, GiftMessage = Clean(gift) is { Length: > 0 } gm ? gm : null, CreatedAt = now, UpdatedAt = now,
        };
        var n = 0;
        foreach (var g in quote.Groups)
        {
            var group = new OrderGroup
            {
                Id = Guid.NewGuid(), OrderId = id, SellerId = g.SellerId, BrandId = g.BrandId, SellerName = g.SellerName, BrandName = g.BrandName,
                DeliveryKind = g.Selected!.Kind, ShippingSpeed = g.Selected.Speed, LocalWindowId = g.Selected.WindowId, LocalDate = g.Selected.Date,
                ItemsCents = g.ItemsCents, ShippingCents = g.ShippingCents, ShippingTaxCents = g.ShippingTaxCents, TaxCents = g.TaxCents, TotalCents = g.TotalCents,
            };
            order.Groups.Add(group);
            foreach (var l in g.Lines)
                order.Lines.Add(new OrderLine
                {
                    Id = Guid.NewGuid(), OrderId = id, GroupId = group.Id, MarketplaceLineId = $"{number}-{++n}", ListingId = l.ListingId, VariantId = l.VariantId, SellerId = g.SellerId, BrandId = g.BrandId,
                    Title = l.Title, VariantName = l.VariantName, Quantity = l.Quantity, UnitPriceCents = l.UnitPriceCents, LineTotalCents = l.LineTotalCents, TaxCents = l.TaxCents,
                });
        }
        order.ActiveKey = Fingerprint(order);
        return order;
    }

    /// <summary>Everything the buyer agreed to, as one value. Same checkout twice (a double-submit) gives the same value; any change of price, delivery, address or cart gives another.</summary>
    public static string Fingerprint(MarketplaceOrder o)
    {
        // every field length-prefixed (Canon): a `|` or `;` typed into the name or the address can never make two different checkouts the same value (review K3)
        var sb = new StringBuilder().Field(o.AccountId).Field(o.GuestEmail).Field(o.CartId).Field(o.Currency).Field(o.TotalCents)
          .Field(o.ShipName).Field(o.ShipLine1).Field(o.ShipLine2).Field(o.ShipCity).Field(o.ShipRegion).Field(o.ShipPostalCode).Field(o.ShipCountry).Field(o.Phone).Field(o.GiftMessage).Field(o.ShipLat).Field(o.ShipLng);
        foreach (var g in o.Groups.OrderBy(g => g.SellerId).ThenBy(g => g.BrandId))
            sb.Field("G").Field(g.SellerId).Field(g.BrandId).Field(g.DeliveryKind).Field(g.ShippingSpeed).Field(g.LocalWindowId).Field(g.LocalDate).Field(g.ShippingCents).Field(g.TaxCents);
        foreach (var l in o.Lines.OrderBy(l => l.VariantId))
            sb.Field("L").Field(l.VariantId).Field(l.Quantity).Field(l.UnitPriceCents).Field(l.TaxCents);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// One transaction per checkout of a cart. The first statement writes the cart row: on PostgreSQL that makes checkouts of one cart queue up, so an identical one finds the other's order instead of
    /// racing it (SQLite serialises all writers anyway). Correctness does NOT depend on that lock: the unique ActiveKey (an identical checkout is one order) and the partial unique index (one pending order
    /// per cart) are what make two payable orders impossible, and the retry below re-reads after losing a race (review F5: the lock alone is redundant and tests show the mutant without it survives). An identical pending order
    /// is returned as is (a double-submit); otherwise every other pending order of the cart is superseded and the new one inserted.
    /// </summary>
    private async Task<MarketplaceOrder> LockedInsertOrFindAsync(MarketplaceOrder order, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                if (order.CartId is { } cart)
                {
                    var now = Now;
                    await db.Carts.Where(c => c.Id == cart).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), ct);
                }
                var found = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.ActiveKey == order.ActiveKey, ct);
                if (found is not null) { await tx.CommitAsync(ct); return found; }
                await SupersedeOthersAsync(order, ct);
                db.Orders.Add(order);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return order;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();   // lost a race on a unique key (or, rarely, drew an existing order number): look again, with a new number if needed
                if (await db.Orders.AnyAsync(o => o.Number == order.Number, ct)) RenumberFor(order);
            }
        }
    }

    private static void RenumberFor(MarketplaceOrder order)
    {
        var old = order.Number;
        order.Number = NewNumber(order.CreatedAt);
        foreach (var l in order.Lines) l.MarketplaceLineId = order.Number + l.MarketplaceLineId[old.Length..];
    }

    /// <summary>
    /// Every other pending order of the cart is closed (status first, then the intent id is read: see the comment in <see cref="PlaceAsync"/>) and its intent is cancelled through the outbox, which
    /// retries. Called inside the checkout transaction; the caller saves. A payment that still lands on a closed order is refunded in full by the webhook, never fulfilled (decision M3-18).
    /// </summary>
    private async Task SupersedeOthersAsync(MarketplaceOrder fresh, CancellationToken ct)
    {
        if (fresh.CartId is not { } cart) return;
        var old = await db.Orders.AsNoTracking().Where(o => o.CartId == cart && o.AccountId == fresh.AccountId && o.Status == OrderStatus.PendingPayment).Select(o => new { o.Id, o.Number }).ToListAsync(ct);
        var now = Now;
        foreach (var o in old)
        {
            if (hooks.BeforeSupersedeUpdate is { } before) await before(db);
            var changed = await db.Orders.Where(x => x.Id == o.Id && x.Status == OrderStatus.PendingPayment)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Superseded).SetProperty(x => x.ActiveKey, (string?)null).SetProperty(x => x.UpdatedAt, now), ct);
            if (changed == 0) continue;
            var pi = await db.Orders.AsNoTracking().Where(x => x.Id == o.Id).Select(x => x.PaymentIntentId).FirstAsync(ct);   // read AFTER the status change
            await OrderMaintenance.EnqueueCancelAsync(outbox, o.Number, pi, ct);   // an order with no intent stored gets the "orphan" cancel that finds the intent again by its key
        }
    }

    private async Task EnqueueCancelAsync(string intentId, CancellationToken ct)
    {
        var key = StripeCancelHandler.DedupeKey(intentId);
        if (!await outbox.ExistsAsync(key, ct)) outbox.Enqueue(StripeCancelHandler.Type, new StripeCancelHandler.Payload(intentId), key);
    }

    /// <summary>Queues the cancel on its own (outside any transaction); a duplicate key means it is already queued.</summary>
    private async Task QueueCancelAsync(string intentId, CancellationToken ct)
    {
        await EnqueueCancelAsync(intentId, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    public static string NewNumber(DateTimeOffset at)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(8);
        return "MP-" + at.ToString("yyyyMMdd") + "-" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
