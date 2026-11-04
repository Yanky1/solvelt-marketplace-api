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
        g.MapPost("", Place).AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("/orders/{number}", GetOrder).AddEndpointFilter<RequireBuyerFilter>();
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
            QuoteStatus.EmptyOrUnavailableCart => Problems.Json(409, "cart_not_ready", "Your cart is empty or has items that are no longer available."),
            QuoteStatus.InvalidSelection => Problems.Unprocessable("invalid_selection", detail ?? "That delivery choice is not available."),
            _ => Problems.Json(503, "shipping_unavailable", "Delivery options are unavailable right now. Please try again in a moment."),
        };
    }

    /// <summary>
    /// Creates our order as PendingPayment and the ONE PaymentIntent for it. Safe to repeat and to send twice in parallel: an identical checkout is the same order and the same
    /// intent (unique key in the database + the order number as Stripe's idempotency key). Amounts come from <see cref="QuoteService"/> only; the body has no money field.
    /// The buyer is signed in (guest checkout plugs in later through M1's order access links).
    /// </summary>
    private static async Task<IResult> Place(CheckoutRequest? body, HttpContext ctx, CheckoutService checkout, MarketplaceDb db, CancellationToken ct)
    {
        var errors = CheckoutService.Validate(body?.Address, body?.GiftMessage);
        if (errors.Count > 0) return Problems.Validation(errors);
        var buyer = ctx.Buyer();
        var relay = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == buyer.AccountId && a.DeletedAt == null).Select(a => a.RelayEmail).FirstOrDefaultAsync(ct);
        if (relay is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        var (status, result, detail) = await checkout.PlaceAsync(buyer, relay, body!, ct);
        return status switch
        {
            CheckoutStatus.Ok => Results.Json(result),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("InvalidAddress") => Problems.Validation(new Dictionary<string, string> { ["address"] = "Enter a delivery country, postal code and, for the US, a state." }),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("InvalidSelection") => Problems.Unprocessable("invalid_selection", "That delivery choice is not available."),
            CheckoutStatus.Quote when detail is not null && detail.StartsWith("ShippingUnavailable") => Problems.Json(503, "shipping_unavailable", "Delivery options are unavailable right now. Please try again in a moment."),
            CheckoutStatus.Quote when detail == "delivery_unavailable" => Problems.Json(409, "delivery_unavailable", "We can't deliver part of your cart to that address."),
            CheckoutStatus.Quote => Problems.Json(409, "cart_not_ready", "Your cart is empty or has items that are no longer available."),
            CheckoutStatus.PriceChanged => Results.Json(new { code = "price_changed", message = "The price changed. Review the new total and confirm to continue.", quote = result!.Quote }, statusCode: 409),
            CheckoutStatus.AmountTooSmall => Problems.Unprocessable("amount_too_small", "The total is below the minimum we can charge."),
            CheckoutStatus.Superseded => Problems.Json(409, "checkout_superseded", "Another checkout of this cart replaced this one. Review your cart and pay again."),
            CheckoutStatus.NotPending => Problems.Json(409, "order_not_pending", "This order has already been paid or closed."),
            CheckoutStatus.PaymentRejected => Problems.Json(502, "payment_rejected", "The payment could not be started."),
            _ => Problems.Json(503, "payments_unavailable", "Payments are unavailable right now. Please try again in a moment."),
        };
    }

    /// <summary>The buyer's own order (the confirmation page polls this). Another buyer's order is a 404.</summary>
    private static async Task<IResult> GetOrder(string number, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var o = await db.Orders.AsNoTracking().Include(x => x.Groups).Include(x => x.Lines).AsSplitQuery().FirstOrDefaultAsync(x => x.Number == number && x.AccountId == buyer.AccountId, ct);
        if (o is null) return Problems.NotFound();
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
