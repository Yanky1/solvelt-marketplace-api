using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Carts;

/// <summary>
/// <c>/api/v1/cart</c>. The owner is the signed-in buyer (<c>Authorization: Bearer</c>) or a guest (<c>X-Solvelt-Cart-Token</c>, the token the API returned
/// as <c>cartToken</c> when the guest cart was created, kept by the web server in its own cookie). A bad Bearer token is 401, never a silent guest cart.
/// </summary>
public static class CartEndpoints
{
    public const string GuestHeader = "X-Solvelt-Cart-Token";
    public sealed record AddBody(Guid? VariantId, int? Quantity);
    public sealed record QuantityBody(int? Quantity);
    public sealed record MergeBody(string? GuestToken);

    public static IEndpointRouteBuilder MapCart(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/cart").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("", async (HttpContext ctx, AuthService auth, CartService cart, CancellationToken ct) =>
            await Owner(ctx, auth, ct) is { } o ? Results.Json(await cart.GetAsync(o, ct)) : Unauthorized());
        g.MapPost("/lines", Add);
        g.MapPatch("/lines/{lineId:guid}", SetQuantity);
        g.MapDelete("/lines/{lineId:guid}", async (Guid lineId, HttpContext ctx, AuthService auth, CartService cart, CancellationToken ct) =>
            await Owner(ctx, auth, ct) is not { } o ? Unauthorized() : await cart.SetQuantityAsync(o, lineId, 0, ct) is { } v ? Results.Json(v) : Problems.NotFound());
        g.MapPost("/merge", Merge).AddEndpointFilter<RequireBuyerFilter>();
        return app;
    }

    private static IResult Unauthorized() => Problems.Json(401, "unauthorized", "Sign in to continue.");

    internal static async Task<CartOwner?> Owner(HttpContext ctx, AuthService auth, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.Length > 0)
        {
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            return await auth.AuthenticateAsync(token, ct) is { } p ? new CartOwner(p.AccountId, null) : null;
        }
        return new CartOwner(null, ctx.Request.Headers[GuestHeader].ToString());
    }

    private static async Task<IResult> Add(AddBody? body, HttpContext ctx, AuthService auth, CartService cart, CancellationToken ct)
    {
        if (await Owner(ctx, auth, ct) is not { } o) return Unauthorized();
        if (body?.VariantId is not { } variant || variant == Guid.Empty) return Problems.Validation(new Dictionary<string, string> { ["variantId"] = "Choose an option." });
        var (status, view) = await cart.AddAsync(o, variant, body.Quantity ?? 1, ct);
        return status switch
        {
            AddStatus.Ok => Results.Json(view),
            AddStatus.NotFound => Problems.NotFound("That item is not available."),
            AddStatus.NotPurchasable => Problems.Unprocessable("not_purchasable", "That option can't be added to the cart right now."),
            AddStatus.QuantityLimit => Problems.Unprocessable("quantity_limit", $"Choose between 1 and {CartLimits.MaxQuantityPerLine}."),
            AddStatus.TooManyLines => Problems.Unprocessable("cart_full", "Your cart is full. Remove something first."),
            AddStatus.Busy => Problems.Json(503, "cart_busy", "Your cart is being updated. Please try again."),
            _ => Problems.Unprocessable("currency_mismatch", "Items in different currencies can't share a cart."),
        };
    }

    private static async Task<IResult> SetQuantity(Guid lineId, QuantityBody? body, HttpContext ctx, AuthService auth, CartService cart, CancellationToken ct)
    {
        if (await Owner(ctx, auth, ct) is not { } o) return Unauthorized();
        if (body?.Quantity is not { } q || q < 0 || q > CartLimits.MaxQuantityPerLine) return Problems.Unprocessable("quantity_limit", $"Choose between 0 and {CartLimits.MaxQuantityPerLine}.");
        return await cart.SetQuantityAsync(o, lineId, q, ct) is { } v ? Results.Json(v) : Problems.NotFound();
    }

    private static async Task<IResult> Merge(MergeBody? body, HttpContext ctx, CartService cart, CancellationToken ct)
        => Results.Json(await cart.MergeGuestAsync(ctx.Buyer().AccountId, body?.GuestToken ?? "", ct));
}
