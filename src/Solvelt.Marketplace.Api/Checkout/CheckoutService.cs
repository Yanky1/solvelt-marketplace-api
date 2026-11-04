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
public sealed record CheckoutRequest(CheckoutAddress? Address, IReadOnlyList<GroupSelection>? Selections, string? GiftMessage);
public sealed record CheckoutPayment(string Provider, string ClientSecret, string? PublishableKey);
public sealed record CheckoutResult(string OrderNumber, string Status, QuoteView Quote, CheckoutPayment Payment);

public enum CheckoutStatus { Ok, Invalid, Quote, NotPending, PaymentsUnavailable, AmountTooSmall, PaymentRejected }

public sealed class CheckoutService(MarketplaceDb db, QuoteService quotes, CartService carts, IPaymentProvider payments, IOptions<StripeOptions> stripe, TimeProvider time, ILogger<CheckoutService> log)
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

    public async Task<(CheckoutStatus Status, CheckoutResult? Result, string? Detail)> PlaceAsync(BuyerPrincipal buyer, string relayEmail, CheckoutRequest req, CancellationToken ct)
    {
        var a = req.Address!;
        var owner = new CartOwner(buyer.AccountId, null);
        var q = await quotes.QuoteAsync(owner, new QuoteRequest(new QuoteAddress(Clean(a.Country), Clean(a.Region) is { Length: > 0 } region ? region : null, Clean(a.PostalCode), a.Lat, a.Lng), req.Selections), ct);
        if (q.Status != QuoteStatus.Ok) return (CheckoutStatus.Quote, null, q.Status.ToString() + (q.Detail is null ? "" : ":" + q.Detail));
        var quote = q.View!;
        if (!quote.CanPay) return (CheckoutStatus.Quote, null, "delivery_unavailable");
        if (quote.TotalCents < 50) return (CheckoutStatus.AmountTooSmall, null, null);   // Stripe's minimum charge
        var cartId = await carts.CartIdAsync(owner, ct);

        var order = Build(buyer, relayEmail, cartId, a, req.GiftMessage, quote);
        var existing = await InsertOrFindAsync(order, ct);
        if (existing.Status != OrderStatus.PendingPayment) return (CheckoutStatus.NotPending, null, null);

        if (existing.Id == order.Id) await SupersedeOthersAsync(existing, ct);
        try
        {
            // The key is the order number: a double-click, a retry and a parallel request all reach Stripe with the SAME key, so there is exactly one PaymentIntent.
            var intent = await payments.CreateIntentAsync(existing.TotalCents, existing.Currency, "pi:" + existing.Number,
                new Dictionary<string, string> { ["marketplace_order_id"] = existing.Number }, ct);
            await db.Orders.Where(o => o.Id == existing.Id && o.PaymentIntentId == null).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentIntentId, intent.Id), ct);
            var stored = await db.Orders.AsNoTracking().Where(o => o.Id == existing.Id).Select(o => o.PaymentIntentId).FirstAsync(ct);
            if (stored != intent.Id) { log.LogError("Order {Number} has two different payment intents (stored {Stored}, returned {Returned})", existing.Number, stored, intent.Id); return (CheckoutStatus.PaymentsUnavailable, null, "intent_mismatch"); }
            return (CheckoutStatus.Ok, new CheckoutResult(existing.Number, existing.Status, quote, new CheckoutPayment("stripe", intent.ClientSecret, stripe.Value.PublishableKey)), null);
        }
        catch (PaymentsUnavailableException) { return (CheckoutStatus.PaymentsUnavailable, null, null); }   // the order stays pending; the same request later finds it and retries the intent
        catch (PaymentRejectedException ex) { log.LogWarning("Payment provider refused order {Number}: {Code}", existing.Number, ex.Code); return (CheckoutStatus.PaymentRejected, null, ex.Code); }
    }

    private MarketplaceOrder Build(BuyerPrincipal buyer, string relayEmail, Guid? cartId, CheckoutAddress a, string? gift, QuoteView quote)
    {
        var now = Now;
        var number = NewNumber(now);
        var id = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            Id = id, Number = number, AccountId = buyer.AccountId, CartId = cartId, Status = OrderStatus.PendingPayment, Currency = quote.Currency,
            ItemsCents = quote.ItemsCents, ShippingCents = quote.ShippingCents, TaxCents = quote.TaxCents, TotalCents = quote.TotalCents,
            BuyerName = RelayIdentity.PublicName(buyer.DisplayName), RelayEmail = relayEmail, Phone = Clean(a.Phone) is { Length: > 0 } ph ? ph : null,
            ShipName = Clean(a.Name), ShipLine1 = Clean(a.Line1), ShipLine2 = Clean(a.Line2) is { Length: > 0 } l2 ? l2 : null, ShipCity = Clean(a.City),
            ShipRegion = Clean(a.Region) is { Length: > 0 } rg ? rg : null, ShipPostalCode = Clean(a.PostalCode), ShipCountry = Clean(a.Country).ToUpperInvariant(),
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
        var sb = new StringBuilder();
        sb.Append(o.AccountId).Append('|').Append(o.CartId).Append('|').Append(o.Currency).Append('|').Append(o.TotalCents).Append('|')
          .Append(o.ShipName).Append('|').Append(o.ShipLine1).Append('|').Append(o.ShipLine2).Append('|').Append(o.ShipCity).Append('|').Append(o.ShipRegion).Append('|')
          .Append(o.ShipPostalCode).Append('|').Append(o.ShipCountry).Append('|').Append(o.Phone).Append('|').Append(o.GiftMessage).Append('|').Append(o.ShipLat).Append('|').Append(o.ShipLng);
        foreach (var g in o.Groups.OrderBy(g => g.SellerId).ThenBy(g => g.BrandId))
            sb.Append("|G").Append(g.SellerId).Append(g.BrandId).Append(g.DeliveryKind).Append(g.ShippingSpeed).Append(g.LocalWindowId).Append(g.LocalDate).Append(g.ShippingCents).Append(g.TaxCents);
        foreach (var l in o.Lines.OrderBy(l => l.VariantId))
            sb.Append("|L").Append(l.VariantId).Append(':').Append(l.Quantity).Append(':').Append(l.UnitPriceCents).Append(':').Append(l.TaxCents);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Inserts the order; when the unique <c>ActiveKey</c> says an identical pending order exists (a parallel or repeated submit) returns that one.</summary>
    private async Task<MarketplaceOrder> InsertOrFindAsync(MarketplaceOrder order, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var found = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.ActiveKey == order.ActiveKey, ct);
            if (found is not null) return found;
            db.Orders.Add(order);
            try { await db.SaveChangesAsync(ct); return order; }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();   // lost a race on ActiveKey (or, rarely, drew an existing order number): look again, with a new number if needed
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

    /// <summary>A newer pending order for the same cart replaces the older ones; their intents are cancelled (best effort: a payment that still lands is honoured by the payment webhook).</summary>
    private async Task SupersedeOthersAsync(MarketplaceOrder fresh, CancellationToken ct)
    {
        if (fresh.CartId is not { } cart) return;
        var old = await db.Orders.Where(o => o.CartId == cart && o.AccountId == fresh.AccountId && o.Id != fresh.Id && o.Status == OrderStatus.PendingPayment)
            .Select(o => new { o.Id, o.Number, o.PaymentIntentId }).ToListAsync(ct);
        var now = Now;
        foreach (var o in old)
        {
            var changed = await db.Orders.Where(x => x.Id == o.Id && x.Status == OrderStatus.PendingPayment)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Superseded).SetProperty(x => x.ActiveKey, (string?)null).SetProperty(x => x.UpdatedAt, now), ct);
            if (changed == 1 && o.PaymentIntentId is { } pi)
                try { await payments.CancelIntentAsync(pi, "cancel:" + o.Number, ct); }
                catch (Exception ex) when (ex is PaymentsUnavailableException or PaymentRejectedException) { log.LogWarning("Could not cancel the intent of superseded order {Number}", o.Number); }
        }
    }

    public static string NewNumber(DateTimeOffset at)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(8);
        return "MP-" + at.ToString("yyyyMMdd") + "-" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
