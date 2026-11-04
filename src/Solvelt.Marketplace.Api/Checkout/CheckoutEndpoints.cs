using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Checkout;

public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckout(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/checkout").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapPost("/quote", Quote);
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
}
