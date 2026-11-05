using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Demo;

/// <summary>
/// Test-mode demo data (M9). `GET /api/v1/config` is the public configuration the web reads (today only whether test mode is on). Everything under `/api/v1/demo` is the
/// signed-in buyer's own and answers 404, to everyone and before anything else is looked at, while <c>Demo:Enabled</c> is off.
/// </summary>
public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/config", Config).RequireRateLimiting(RateLimitPolicies.Public);
        var g = app.MapGroup("/api/v1/demo").RequireRateLimiting(RateLimitPolicies.Public);
        g.AddEndpointFilter<DemoEnabledFilter>();                                  // first: a disabled feature does not even say whether you are signed in
        g.MapGet("/status", Status).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/fill", Fill).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/remove", Remove).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/verify", Verify).RequireRateLimiting(RateLimitPolicies.Auth);   // not signed in (an unverified account has no session): the credentials are the proof, and the sign-in budget applies
        g.Map("/{**rest}", () => Results.NotFound());                              // last: any other path or method is a plain 404 (never a 405 that would say the feature exists while it is off)
        return app;
    }

    public sealed record VerifyBody(string? Email, string? Password);

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }
    private static object Shape(DemoCounts c) => new { addresses = c.Addresses, carts = c.Carts, cartLines = c.CartLines, orders = c.Orders, bookings = c.Bookings, threads = c.Threads, messages = c.Messages, notifications = c.Notifications };

    // The account is ALWAYS the signed-in buyer's (ctx.Buyer()), never a value from the request: there is no id to forge.
    private static async Task<IResult> Status(HttpContext ctx, DemoService demo, CancellationToken ct)
    {
        var counts = await demo.CountAsync(ctx.Buyer().AccountId, ct);
        return Private(Results.Json(new { hasDemoData = counts.Total > 0, counts = Shape(counts) }), ctx);
    }

    private static IResult? Throttled(HttpContext ctx, DemoActionLimiter limiter)
    {
        if (limiter.TryAcquire(ctx.Buyer().AccountId) is not { } retry) return null;
        ctx.Response.Headers.RetryAfter = retry.ToString();
        return Private(Problems.Json(429, "too_many_attempts", "Too many requests. Please wait a moment and try again."), ctx);
    }

    private static async Task<IResult> Fill(HttpContext ctx, DemoService demo, DemoActionLimiter limiter, CancellationToken ct)
    {
        if (Throttled(ctx, limiter) is { } slow) return slow;
        var r = await demo.FillAsync(ctx.Buyer().AccountId, ct);
        return Private(Results.Json(new { batchId = r.BatchId, created = r.Created, counts = Shape(r.Counts), notes = r.Notes }, statusCode: r.Created ? 201 : 200), ctx);
    }

    private static async Task<IResult> Remove(HttpContext ctx, DemoService demo, DemoActionLimiter limiter, CancellationToken ct)
    {
        if (Throttled(ctx, limiter) is { } slow) return slow;
        var removed = await demo.RemoveAsync(ctx.Buyer().AccountId, ct);
        return Private(Results.Json(new { removed = Shape(removed) }), ctx);
    }

    private static async Task<IResult> Verify(VerifyBody? body, DemoVerifier verifier, HttpContext ctx, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Email) || string.IsNullOrEmpty(body.Password) || body.Password.Length > 128 || body.Email.Length > 254)
            return Problems.Json(401, "invalid_credentials", "The email or password is not correct.");
        var (status, retry) = await verifier.VerifyAsync(body.Email, body.Password, ips.Resolve(ctx), ct);
        switch (status)
        {
            case DemoVerifyStatus.Verified: return Private(Results.NoContent(), ctx);
            case DemoVerifyStatus.TooManyAttempts:
                if (retry is { } s) ctx.Response.Headers.RetryAfter = s.ToString();
                return Problems.Json(429, "too_many_attempts", "Too many attempts. Please wait a moment and try again.");
            default: return Problems.Json(401, "invalid_credentials", "The email or password is not correct.");
        }
    }

    private static IResult Config(HttpContext ctx, IOptions<DemoOptions> demo)
    {
        ctx.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(new { demoEnabled = demo.Value.Enabled });
    }
}

/// <summary>404 for every demo route while demo data is switched off.</summary>
public sealed class DemoEnabledFilter(IOptions<DemoOptions> demo) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => demo.Value.Enabled ? next(context) : ValueTask.FromResult<object?>(Results.NotFound());
}
