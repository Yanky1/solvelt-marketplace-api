using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Outbox;

/// <summary>
/// Operations endpoints. They exist only on their own host (<c>Admin:Host</c>, decision O-12), need their own key, never return a payload, and answer
/// <c>404</c> to anything that is not an authorised request on that host: a wrong key, a missing key, the public host. Refusals are counted on /health.
/// </summary>
public static class OutboxAdmin
{
    public const string KeyHeader = "X-Solvelt-Admin-Key";
    public const int PageSize = 50, MaxPageSize = 200;

    public static IEndpointRouteBuilder MapOutboxAdmin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/outbox", List).RequireRateLimiting(RateLimitPolicies.Admin);
        app.MapPost("/api/admin/outbox/{id:guid}/retry", Retry).RequireRateLimiting(RateLimitPolicies.Admin);
        return app;
    }

    /// <summary>Null when authorised, otherwise the refusal (always 404). The key compare is constant-time; the key value is never logged.</summary>
    internal static IResult? Authorise(HttpContext ctx, IOptions<AdminOptions> options, OpsCounters counters)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey)) return Problems.NotFound();            // disabled: not even a refusal worth counting
        if (!string.IsNullOrWhiteSpace(o.Host) && !string.Equals(ctx.Request.Host.Value, o.Host.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            counters.AdminRefused();
            return Problems.NotFound();                                                  // the public host does not know these routes
        }
        var given = ctx.Request.Headers[KeyHeader].ToString();
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(o.ApiKey));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        if (CryptographicOperations.FixedTimeEquals(a, b)) return null;
        counters.AdminRefused();
        return Problems.NotFound();
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{createdAt.UtcTicks}|{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default; id = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id)) return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static async Task<IResult> List(HttpContext ctx, IOptions<AdminOptions> admin, OpsCounters counters, MarketplaceDb db, string? status, int? limit, string? cursor, CancellationToken ct)
    {
        if (Authorise(ctx, admin, counters) is { } refused) return refused;
        string? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            filter = new[] { OutboxStatus.Pending, OutboxStatus.Delivered, OutboxStatus.Dead }.FirstOrDefault(x => string.Equals(x, status, StringComparison.OrdinalIgnoreCase));
            if (filter is null) return Problems.BadRequest("invalid_status", "status must be pending, delivered or dead.");
        }
        DateTimeOffset after = default; Guid afterId = default;
        var paged = !string.IsNullOrEmpty(cursor);
        if (paged && !TryDecodeCursor(cursor!, out after, out afterId)) return Problems.BadRequest("invalid_cursor", "The cursor is not valid.");
        var take = Math.Clamp(limit ?? PageSize, 1, MaxPageSize);

        var counts = (await db.OutboxMessages.GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        // Newest first, keyset paging on (created_at, id): stable while messages are added.
        var query = db.OutboxMessages.AsNoTracking().Where(m => filter == null || m.Status == filter);
        if (paged) query = query.Where(m => m.CreatedAt < after || (m.CreatedAt == after && m.Id.CompareTo(afterId) < 0));
        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1)
            .Select(m => new { m.Id, m.Type, m.Status, m.Attempts, m.NextAttemptAt, m.LastError, m.CreatedAt, m.DeliveredAt, m.DeadAt }).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return Results.Json(new
        {
            counts = new { pending = counts.GetValueOrDefault(OutboxStatus.Pending), delivered = counts.GetValueOrDefault(OutboxStatus.Delivered), dead = counts.GetValueOrDefault(OutboxStatus.Dead) },
            messages = rows.Select(m => new { id = m.Id, type = m.Type, status = m.Status, attempts = m.Attempts, nextAttemptAt = m.NextAttemptAt, lastError = m.LastError, createdAt = m.CreatedAt, deliveredAt = m.DeliveredAt, deadAt = m.DeadAt }),
            nextCursor = more ? EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null,
        });
    }

    private static async Task<IResult> Retry(HttpContext ctx, IOptions<AdminOptions> admin, OpsCounters counters, MarketplaceDb db, TimeProvider time, Guid id, CancellationToken ct)
    {
        if (Authorise(ctx, admin, counters) is { } refused) return refused;
        var now = time.GetUtcNow().ToUniversalTime();
        // Only a dead message with its payload still present can be re-sent; the compare is in the UPDATE so two clicks revive it once.
        // LastError is kept on purpose: it says why the message died until the next attempt replaces it.
        var rows = await db.OutboxMessages.Where(m => m.Id == id && m.Status == OutboxStatus.Dead && m.Payload != null).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.Attempts, 0).SetProperty(m => m.NextAttemptAt, now).SetProperty(m => m.DeadAt, (DateTimeOffset?)null)
            .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null).SetProperty(m => m.Version, m => m.Version + 1), ct);
        if (rows == 1) return Results.NoContent();
        var exists = await db.OutboxMessages.AnyAsync(m => m.Id == id, ct);
        return exists ? Problems.Json(409, "not_retryable", "Only a dead message that still has its payload can be retried.") : Problems.NotFound();
    }
}
