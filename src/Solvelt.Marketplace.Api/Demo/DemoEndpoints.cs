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
        return app;
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
