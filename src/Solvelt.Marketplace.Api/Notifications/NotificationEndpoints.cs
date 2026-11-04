using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Notifications;

public sealed record PreferencesBody(Dictionary<string, bool>? Email);
public sealed record UnsubscribeBody(string? Token);

/// <summary>The buyer's notification centre and email preferences (M5). Every route is the signed-in buyer's own, except the unsubscribe link, which carries its own signed token.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/notifications").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("", List).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/read-all", ReadAll).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/{id:guid}/read", Read).AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("/preferences", GetPrefs).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPut("/preferences", PutPrefs).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/unsubscribe", Unsubscribe);   // anonymous: the signed token is the credential; a POST, so a mail scanner opening the link changes nothing
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static async Task<IResult> List(HttpContext ctx, MarketplaceDb db, int? limit, DateTimeOffset? before, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var q = db.Notifications.AsNoTracking().Where(n => n.AccountId == buyer.AccountId);
        var unread = await q.CountAsync(n => n.ReadAt == null, ct);
        if (before is { } b) q = q.Where(n => n.CreatedAt < b);
        var rows = await q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        return Private(Results.Json(new
        {
            unreadCount = unread,
            notifications = page.Select(n => new
            {
                id = n.Id, kind = n.Kind, category = NotificationKinds.CategoryOf(n.Kind), orderNumber = n.OrderNumber, sellerOrderNumber = n.SellerOrderNumber,
                parameters = n.ParamsJson is null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(n.ParamsJson), createdAt = n.CreatedAt, read = n.ReadAt != null,
            }),
            next = rows.Count > take ? page[^1].CreatedAt : (DateTimeOffset?)null,
        }), ctx);
    }

    private static async Task<IResult> Read(Guid id, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var now = time.GetUtcNow().ToUniversalTime();
        if (!await db.Notifications.AnyAsync(n => n.Id == id && n.AccountId == buyer.AccountId, ct)) return Private(Problems.NotFound(), ctx);
        await db.Notifications.Where(n => n.Id == id && n.AccountId == buyer.AccountId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, (DateTimeOffset?)now), ct);
        return Private(Results.NoContent(), ctx);
    }

    private static async Task<IResult> ReadAll(HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Notifications.Where(n => n.AccountId == buyer.AccountId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, (DateTimeOffset?)now), ct);
        return Private(Results.NoContent(), ctx);
    }

    private static object View(NotificationPreference? p) => new { email = NotificationCategories.Switchable.ToDictionary(c => c, c => p?.Wants(c) ?? true) };

    private static async Task<IResult> GetPrefs(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
        => Private(Results.Json(View(await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.AccountId == ctx.Buyer().AccountId, ct))), ctx);

    private static async Task<IResult> PutPrefs(PreferencesBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (body?.Email is not { Count: > 0 } wanted || wanted.Keys.Any(k => !NotificationCategories.Switchable.Contains(k)))
            return Private(Problems.Validation(new Dictionary<string, string> { ["email"] = "Send a value for each category you want to change." }), ctx);
        var id = ctx.Buyer().AccountId;
        for (var attempt = 0; ; attempt++)
        {
            var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.AccountId == id, ct);
            if (p is null) db.NotificationPreferences.Add(p = new NotificationPreference { AccountId = id });
            foreach (var (c, on) in wanted) p.Set(c, on);
            p.UpdatedAt = time.GetUtcNow();
            try { await db.SaveChangesAsync(ct); return Private(Results.Json(View(p)), ctx); }
            catch (DbUpdateException) when (attempt < 2) { db.ChangeTracker.Clear(); }   // two first-time saves raced on the key: the second re-reads and updates
        }
    }

    /// <summary>One-click unsubscribe (RFC 8058 style). The token names an account and a category (or all) and is signed with its own key; it can only switch mail OFF.</summary>
    private static async Task<IResult> Unsubscribe(UnsubscribeBody? body, UnsubscribeTokens tokens, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (tokens.Parse(body?.Token) is not var (accountId, category) ) return Problems.Json(400, "invalid_token", "This link is not valid.");
        if (!await db.BuyerAccounts.AnyAsync(a => a.Id == accountId && a.DeletedAt == null, ct)) return Results.NoContent();
        for (var attempt = 0; ; attempt++)
        {
            var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
            if (p is null) db.NotificationPreferences.Add(p = new NotificationPreference { AccountId = accountId });
            foreach (var c in category == NotificationCategories.All ? NotificationCategories.Switchable : [category]) p.Set(c, false);
            p.UpdatedAt = time.GetUtcNow();
            try { await db.SaveChangesAsync(ct); return Results.NoContent(); }
            catch (DbUpdateException) when (attempt < 2) { db.ChangeTracker.Clear(); }
        }
    }
}
