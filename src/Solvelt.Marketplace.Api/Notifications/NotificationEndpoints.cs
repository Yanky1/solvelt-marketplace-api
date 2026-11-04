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

    private static async Task<IResult> List(HttpContext ctx, MarketplaceDb db, int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var q = db.Notifications.AsNoTracking().Where(n => n.AccountId == buyer.AccountId);
        var unread = await q.CountAsync(n => n.ReadAt == null, ct);
        if (before is { } b) q = beforeId is { } bid ? q.Where(n => n.CreatedAt < b || (n.CreatedAt == b && n.Id.CompareTo(bid) < 0)) : q.Where(n => n.CreatedAt < b);
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
            next = rows.Count > take ? new { before = page[^1].CreatedAt, beforeId = page[^1].Id } : null,
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

    /// <summary>
    /// One-click unsubscribe. The token (query, form field or JSON body) names an account and a category (or all), is signed with its own key and expires; it can only switch mail OFF. Accepts the
    /// RFC 8058 mail-client POST (<c>application/x-www-form-urlencoded</c> body <c>List-Unsubscribe=One-Click</c>, token in the URL) as well as the website's JSON POST. An expired token is a distinct
    /// 400 <c>expired_token</c> (the website then sends the person to the signed-in preferences page).
    /// </summary>
    private static async Task<IResult> Unsubscribe(HttpContext ctx, UnsubscribeTokens tokens, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        string? token = ctx.Request.Query["token"].FirstOrDefault();
        if (ctx.Request.HasFormContentType)
        {
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(ct); } catch (InvalidDataException) { return Problems.Json(400, "invalid_token", "This link is not valid."); }
            if (form["List-Unsubscribe"].FirstOrDefault() != "One-Click") return Problems.Json(400, "invalid_request", "Not a one-click unsubscribe.");
            token ??= form["token"].FirstOrDefault();
        }
        else if (token is null)
        {
            try { token = (await ctx.Request.ReadFromJsonAsync<UnsubscribeBody>(ct))?.Token; } catch (Exception e) when (e is System.Text.Json.JsonException or BadHttpRequestException or InvalidOperationException) { token = null; }
        }
        var (state, accountId, category) = tokens.Parse(token);
        if (state == UnsubscribeTokens.State.Expired) return Problems.Json(400, "expired_token", "This link has expired. Sign in to change your email settings.");
        if (state != UnsubscribeTokens.State.Ok) return Problems.Json(400, "invalid_token", "This link is not valid.");
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
