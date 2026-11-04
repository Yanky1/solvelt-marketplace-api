using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Outbox;

/// <summary>Operations endpoints. Authenticated by an own key; payloads are never returned (they hold addresses and links).</summary>
public static class OutboxAdmin
{
    public const string KeyHeader = "X-Solvelt-Admin-Key";

    public static IEndpointRouteBuilder MapOutboxAdmin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/outbox", List).RequireRateLimiting(RateLimitPolicies.Auth);
        app.MapPost("/api/admin/outbox/{id:guid}/retry", Retry).RequireRateLimiting(RateLimitPolicies.Auth);
        return app;
    }

    /// <summary>Null when authorised, otherwise the refusal. Disabled (404) when no key is configured; the compare is constant-time.</summary>
    internal static IResult? Authorise(HttpContext ctx, IOptions<AdminOptions> options)
    {
        var key = options.Value.ApiKey;
        if (string.IsNullOrEmpty(key)) return Problems.NotFound();
        var given = ctx.Request.Headers[KeyHeader].ToString();
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        return CryptographicOperations.FixedTimeEquals(a, b) ? null : Problems.Json(401, "unauthorized", "A valid admin key is required.");
    }

    private static async Task<IResult> List(HttpContext ctx, IOptions<AdminOptions> admin, MarketplaceDb db, string? status, int? limit, CancellationToken ct)
    {
        if (Authorise(ctx, admin) is { } refused) return refused;
        string? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            filter = new[] { OutboxStatus.Pending, OutboxStatus.Delivered, OutboxStatus.Dead }.FirstOrDefault(x => string.Equals(x, status, StringComparison.OrdinalIgnoreCase));
            if (filter is null) return Problems.BadRequest("invalid_status", "status must be pending, delivered or dead.");
        }
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var counts = (await db.OutboxMessages.GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var rows = await db.OutboxMessages.AsNoTracking().Where(m => filter == null || m.Status == filter)
            .OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id).Take(take)
            .Select(m => new { m.Id, m.Type, m.Status, m.Attempts, m.NextAttemptAt, m.LastError, m.CreatedAt, m.DeliveredAt, m.DeadAt }).ToListAsync(ct);
        return Results.Json(new
        {
            counts = new { pending = counts.GetValueOrDefault(OutboxStatus.Pending), delivered = counts.GetValueOrDefault(OutboxStatus.Delivered), dead = counts.GetValueOrDefault(OutboxStatus.Dead) },
            messages = rows.Select(m => new { id = m.Id, type = m.Type, status = m.Status, attempts = m.Attempts, nextAttemptAt = m.NextAttemptAt, lastError = m.LastError, createdAt = m.CreatedAt, deliveredAt = m.DeliveredAt, deadAt = m.DeadAt }),
        });
    }

    private static async Task<IResult> Retry(HttpContext ctx, IOptions<AdminOptions> admin, MarketplaceDb db, TimeProvider time, Guid id, CancellationToken ct)
    {
        if (Authorise(ctx, admin) is { } refused) return refused;
        var now = time.GetUtcNow().ToUniversalTime();
        // Only a dead message with its payload still present can be re-sent; the compare is in the UPDATE so two clicks revive it once.
        var rows = await db.OutboxMessages.Where(m => m.Id == id && m.Status == OutboxStatus.Dead && m.Payload != null).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.Attempts, 0).SetProperty(m => m.NextAttemptAt, now).SetProperty(m => m.DeadAt, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null).SetProperty(m => m.Version, m => m.Version + 1), ct);
        if (rows == 1) return Results.NoContent();
        var exists = await db.OutboxMessages.AnyAsync(m => m.Id == id, ct);
        return exists ? Problems.Json(409, "not_retryable", "Only a dead message that still has its payload can be retried.") : Problems.NotFound();
    }
}
