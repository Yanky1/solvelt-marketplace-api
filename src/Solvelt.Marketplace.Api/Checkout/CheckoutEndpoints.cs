using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Checkout;

public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckout(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/checkout").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapPost("/quote", Quote);
        g.MapPost("", Place);
        g.MapGet("/orders/{number}", GetOrder);
        return app;
    }

    /// <summary>
    /// Prices the buyer's cart for an address and the delivery choices made so far. Safe to repeat; reserves nothing. The body has no money fields,
    /// and any a client adds are never read (they are not even deserialised).
    /// </summary>
    private static async Task<IResult> Quote(QuoteRequest? body, HttpContext ctx, AuthService auth, QuoteService quotes, CancellationToken ct)
    {
        if (await CartEndpoints.Owner(ctx, auth, ct) is not { } owner) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        if (body is null) return Problems.Validation(new Dictionary<string, string> { ["address"] = "Enter a delivery address." });
        var (status, view, detail) = await quotes.QuoteAsync(owner, body, ct);
        return status switch
        {
            QuoteStatus.Ok => Results.Json(view),
            QuoteStatus.InvalidAddress => Problems.Validation(new Dictionary<string, string> { ["address"] = "Enter a delivery country (2 letters), a postal code and, for the US, a state." }),
            QuoteStatus.DemoCart => DemoCartRefusal,
            QuoteStatus.EmptyOrUnavailableCart => Problems.Json(409, "cart_not_ready", "Your cart is empty or has items that are no longer available."),
            QuoteStatus.InvalidSelection => Problems.Unprocessable("invalid_selection", detail ?? "That delivery choice is not available."),
            QuoteStatus.TotalTooLarge => Problems.Unprocessable("amount_too_large", "The total is above the maximum we can charge. Split the order."),
            _ => Problems.Json(503, "shipping_unavailable", "Delivery options are unavailable right now. Please try again in a moment."),
        };
    }

    private static IResult DemoCartRefusal => Problems.Json(409, "demo_data", "Your cart holds demo items, so checkout is not available. Remove the demo data from your account first.");

    /// <summary>The header that carries a guest's order-access link (decision M3-24): it names ONE order and is mailed to the checkout address only.</summary>
    public const string GuestOrderHeader = "X-Guest-Order";

    /// <summary>
    /// Creates our order as PendingPayment and the ONE PaymentIntent for it. Safe to repeat and to send twice in parallel: an identical checkout is the same order and the same
    /// intent (unique key in the database + the order number as Stripe's idempotency key). Amounts come from <see cref="QuoteService"/> only; the body has no money field.
    /// The buyer is signed in (Bearer) or a guest: the cart token (<c>X-Solvelt-Cart-Token</c>, <see cref="Carts.CartEndpoints.GuestHeader"/>) and an <c>email</c> in the body. A guest's order carries that address, gets a relay address of its own, and the answer
    /// includes <c>guestOrderToken</c> (the order-access link's token, also mailed to the address once the payment is confirmed).
    /// </summary>
    private static async Task<IResult> Place(CheckoutRequest? body, HttpContext ctx, AuthService auth, CheckoutService checkout, MarketplaceDb db, GuestLinks links,
        Microsoft.Extensions.Options.IOptions<MarketplaceOptions> marketplace, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(CheckoutService.Validate(body?.Address, body?.GiftMessage));
        CheckoutActor actor;
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.Length > 0)
        {
            var principal = await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null, ct);
            if (principal is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
            var relay = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == principal.AccountId && a.DeletedAt == null).Select(a => a.RelayEmail).FirstOrDefaultAsync(ct);
            if (relay is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
            actor = new CheckoutActor(principal.AccountId, null, null, principal.DisplayName, relay);
        }
        else
        {
            var token = ctx.Request.Headers[CartEndpoints.GuestHeader].ToString();
            if (token.Length == 0) return Problems.Json(401, "unauthorized", "Sign in or check out as a guest to continue.");
            var email = body?.Email is { } rawEmail ? AuthValidation.NormaliseEmail(rawEmail) : null;   // NFC, trimmed, lower-cased: the same function as the account address (M1-81)
            if (AuthValidation.EmailProblem(email) is { } problem) errors["email"] = problem;
            if (errors.Count == 0)
                actor = new CheckoutActor(null, token, email, body!.Address!.Name!.Trim(), RelayIdentity.NewRelayEmail(marketplace.Value.RelayDomain ?? throw new InvalidOperationException("Marketplace:RelayDomain is not configured.")));
            else actor = null!;
        }
        if (errors.Count > 0) return Problems.Validation(errors);
        var (status, result, detail) = await checkout.PlaceAsync(actor, body!, ct);
        return status switch
        {
            CheckoutStatus.Ok => Results.Json(await WithGuestTokenAsync(result!, actor, db, links, ct)),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("DemoCart") => DemoCartRefusal,
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("InvalidAddress") => Problems.Validation(new Dictionary<string, string> { ["address"] = "Enter a delivery country, postal code and, for the US, a state." }),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("InvalidSelection") => Problems.Unprocessable("invalid_selection", "That delivery choice is not available."),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("ShippingUnavailable") => Problems.Json(503, "shipping_unavailable", "Delivery options are unavailable right now. Please try again in a moment."),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("TotalTooLarge") => Problems.Unprocessable("amount_too_large", "The total is above the maximum we can charge. Split the order."),
            CheckoutStatus.Quote when detail == "delivery_unavailable" => Problems.Json(409, "delivery_unavailable", "We can't deliver part of your cart to that address."),
            CheckoutStatus.Quote => Problems.Json(409, "cart_not_ready", "Your cart is empty or has items that are no longer available."),
            CheckoutStatus.PriceChanged => Results.Json(new { code = "price_changed", message = "The price changed. Review the new total and confirm to continue.", quote = result!.Quote }, statusCode: 409),
            CheckoutStatus.InProgress => Problems.Json(409, "checkout_in_progress", "A payment for this cart is still being processed. Wait for it to finish before changing your checkout."),
            CheckoutStatus.AmountTooSmall => Problems.Unprocessable("amount_too_small", "The total is below the minimum we can charge."),
            CheckoutStatus.Superseded => Problems.Json(409, "checkout_superseded", "Another checkout of this cart replaced this one. Review your cart and pay again."),
            CheckoutStatus.NotPending => Problems.Json(409, "order_not_pending", "This order has already been paid or closed."),
            CheckoutStatus.PaymentRejected => Problems.Json(502, "payment_rejected", "The payment could not be started."),
            _ => Problems.Json(503, "payments_unavailable", "Payments are unavailable right now. Please try again in a moment."),
        };
    }

    /// <summary>The buyer's own order (the confirmation page polls this). Another buyer's order is a 404.</summary>
    private static async Task<object> WithGuestTokenAsync(CheckoutResult result, CheckoutActor actor, MarketplaceDb db, GuestLinks links, CancellationToken ct)
    {
        if (actor.AccountId is not null) return result;
        var id = await db.Orders.AsNoTracking().Where(o => o.Number == result.OrderNumber).Select(o => o.Id).FirstAsync(ct);
        var (token, expires) = links.Make(id);
        return new { orderNumber = result.OrderNumber, status = result.Status, quote = result.Quote, payment = result.Payment, guestOrderToken = token, guestOrderTokenExpiresAt = expires };
    }

    /// <summary>
    /// The order for the confirmation page, which polls it: the signed-in buyer's own order, or a guest's through its order-access link (<c>X-Guest-Order</c>; a link of another order is a 404, a bad or expired
    /// one a 401). Once a guest order has been CLAIMED by an account, the link shows only the number, status and date (decision M3-24): nothing of the owner's can be read through it.
    /// Another buyer's order is a 404.
    /// </summary>
    private static async Task<IResult> GetOrder(string number, HttpContext ctx, AuthService auth, MarketplaceDb db, GuestLinks links, CancellationToken ct)
    {
        IQueryable<MarketplaceOrder> q = db.Orders.AsNoTracking().Where(x => x.Number == number);
        var header = ctx.Request.Headers.Authorization.ToString();
        var asGuest = header.Length == 0;
        if (!asGuest)
        {
            var principal = await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null, ct);
            if (principal is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
            q = q.Where(x => x.AccountId == principal.AccountId);
        }
        else
        {
            if (links.Parse(ctx.Request.Headers[GuestOrderHeader].ToString()) is not { } orderId) return Problems.Json(401, "unauthorized", "Open the link from your order email to continue.");
            q = q.Where(x => x.Id == orderId);
        }
        var o = await q.Include(x => x.Groups).Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(ct);
        if (o is null) return Problems.NotFound();
        if (asGuest && (o.ClaimedAt is not null || o.AccountId is not null)) return Results.Json(new { number = o.Number, status = o.Status, createdAt = o.CreatedAt, claimed = true });   // frozen: no address, no lines, no money
        return Results.Json(new
        {
            number = o.Number, status = o.Status, currency = o.Currency, itemsCents = o.ItemsCents, shippingCents = o.ShippingCents, taxCents = o.TaxCents, totalCents = o.TotalCents, createdAt = o.CreatedAt, paidAt = o.PaidAt,
            groups = o.Groups.Select(g => new
            {
                sellerName = g.SellerName, brandName = g.BrandName, deliveryKind = g.DeliveryKind, shippingSpeed = g.ShippingSpeed, localDate = g.LocalDate, itemsCents = g.ItemsCents, shippingCents = g.ShippingCents, taxCents = g.TaxCents, totalCents = g.TotalCents,
                sellerOrderNumber = g.SellerOrderNumber,
                lines = o.Lines.Where(l => l.GroupId == g.Id).Select(l => new { title = l.Title, variantName = l.VariantName, quantity = l.Quantity, unitPriceCents = l.UnitPriceCents, lineTotalCents = l.LineTotalCents, taxCents = l.TaxCents }),
            }),
        });
    }
}
