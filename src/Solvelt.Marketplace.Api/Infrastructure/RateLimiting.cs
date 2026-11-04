using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Solvelt.Marketplace.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Sign-up, sign-in, reset, verification: strict, per client IP.</summary>
    public const string Auth = "auth";
    /// <summary>Anonymous catalog/public reads: generous, per client IP.</summary>
    public const string Public = "public";
    /// <summary>Sending and reporting messages: tighter, per end user (the signed forwarded client identity, not the web server).</summary>
    public const string Messages = "messages";
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
            var messagesPerMinute = config.GetValue("RateLimits:MessagesPerMinute", 20);
            o.AddPolicy(Messages, ctx => Fixed(ctx, "messages", messagesPerMinute));
            o.AddPolicy(Admin, ctx => Fixed(ctx, "admin", adminPerMinute));
            o.OnRejected = async (c, _) => await ProblemExceptionMiddleware.Write(c.HttpContext, 429, "rate_limited", "Too many requests. Try again shortly.",
                headers: r => { r.Headers.RetryAfter = "60"; r.Headers.CacheControl = "no-store"; });     // set after Write's Clear(), which would otherwise drop them
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
