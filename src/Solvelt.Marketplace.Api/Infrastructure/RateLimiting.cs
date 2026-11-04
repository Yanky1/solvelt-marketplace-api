using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Solvelt.Marketplace.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Sign-up, sign-in, reset, verification: strict, per client IP.</summary>
    public const string Auth = "auth";
    /// <summary>Anonymous catalog/public reads: generous, per client IP.</summary>
    public const string Public = "public";
    /// <summary>Operations endpoints: its own bucket, per client IP, so buyer traffic and operator traffic never share a limit.</summary>
    public const string Admin = "admin";

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
            var adminPerMinute = config.GetValue("RateLimits:AdminPerMinute", 30);
            o.AddPolicy(Public, ctx => Fixed(ctx, "public", publicPerMinute));
            o.AddPolicy(Admin, ctx => Fixed(ctx, "admin", adminPerMinute));
            o.OnRejected = async (c, _) =>
            {
                c.HttpContext.Response.Headers.RetryAfter = "60";
                await ProblemExceptionMiddleware.Write(c.HttpContext, 429, "rate_limited", "Too many requests. Try again shortly.");
            };
        });
        return services;
    }

    /// <summary>One bucket per (policy, end user); the end user comes from <see cref="ClientIpResolver"/>, not the socket.</summary>
    internal static string PartitionKey(HttpContext ctx, string bucket)
        => $"{bucket}:{ctx.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(ctx)}";

    private static RateLimitPartition<string> Fixed(HttpContext ctx, string bucket, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx, bucket),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
