using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Solvelt.Marketplace.Api.Auth;

namespace Solvelt.Marketplace.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Sign-up, sign-in, reset, verification: strict, per client IP.</summary>
    public const string Auth = "auth";
    /// <summary>Anonymous catalog/public reads: generous, per client IP.</summary>
    public const string Public = "public";
    /// <summary>Operations endpoints: its own bucket, per client IP, so buyer traffic and operator traffic never share a limit.</summary>
    public const string Admin = "admin";
    /// <summary>The staff area: its own bucket per client IP (<c>RateLimits:StaffPerMinute</c>, 60).</summary>
    public const string Staff = "staff";

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
            o.AddPolicy(Staff, ctx => Fixed(ctx, "staff", config.GetValue("RateLimits:StaffPerMinute", 60)));
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

/// <summary>
/// Sending and reporting messages: <c>RateLimits:MessagesPerMinute</c> (20) per SIGNED-IN ACCOUNT, counted only after the sign-in check (M5 review G4). It runs as an endpoint filter after
/// <see cref="Solvelt.Marketplace.Api.Auth.RequireBuyerFilter"/>: requests without a valid session never count, and two buyers behind one address do not share a bucket. The general per-address
/// limit (<c>Public</c>) still applies in front of it.
/// </summary>
public sealed class MessageRateLimiter(IConfiguration config) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(account =>
        RateLimitPartition.GetFixedWindowLimiter(account, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, config.GetValue("RateLimits:MessagesPerMinute", 20)), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    public bool TryAcquire(Guid accountId) { using var lease = _limiter.AttemptAcquire(accountId); return lease.IsAcquired; }
    public void Dispose() => _limiter.Dispose();
}

public sealed class MessageRateLimitFilter(MessageRateLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!limiter.TryAcquire(context.HttpContext.Buyer().AccountId))
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return Problems.Json(429, "rate_limited", "Too many requests. Try again shortly.");
        }
        return await next(context);
    }
}
