using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Solvelt.Marketplace.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Sign-up, sign-in, reset, verification: strict, per client IP.</summary>
    public const string Auth = "auth";
    /// <summary>Anonymous catalog/public reads: generous, per client IP.</summary>
    public const string Public = "public";

    public static IServiceCollection AddMarketplaceRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        // Resolved lazily so configuration (including test overrides) is read from the final IConfiguration.
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((o, config) =>
        {
            var authPerMinute = config.GetValue("RateLimits:AuthPerMinute", 10);
            var publicPerMinute = config.GetValue("RateLimits:PublicPerMinute", 240);
            o.RejectionStatusCode = 429;
            o.AddPolicy(Auth, ctx => Fixed(ctx, "auth", authPerMinute));
            o.AddPolicy(Public, ctx => Fixed(ctx, "public", publicPerMinute));
            o.OnRejected = async (c, _) =>
            {
                c.HttpContext.Response.Headers.RetryAfter = "60";
                await ProblemExceptionMiddleware.Write(c.HttpContext, 429, "rate_limited", "Too many requests. Try again shortly.");
            };
        });
        return services;
    }

    private static RateLimitPartition<string> Fixed(HttpContext ctx, string bucket, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter($"{bucket}:{ctx.Connection.RemoteIpAddress}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
