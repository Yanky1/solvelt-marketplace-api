using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Lists;

public sealed record WishlistNameBody(string? Name);

/// <summary>
/// <c>/api/v1/wishlists</c> (M6): the signed-in buyer's own lists, private by default. Another buyer's list is 404. A list is shared read-only through a random link
/// (<c>GET /api/v1/shared-wishlists/{key}</c>, anonymous); only the SHA-256 of the key is stored, so the link is shown once, when it is made, and "make a new link" replaces (revokes) the old one.
/// Time is ours; nothing the client sends decides an order or a date.
/// </summary>
public static class WishlistEndpoints
{
    public static IEndpointRouteBuilder MapWishlists(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/wishlists").RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapGet("/membership", Membership);
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Rename);
        g.MapDelete("/{id:guid}", Delete);
        g.MapPut("/{id:guid}/items/{listingId:guid}", AddItem);
        g.MapDelete("/{id:guid}/items/{listingId:guid}", RemoveItem);
        g.MapPost("/{id:guid}/share", Share);
        g.MapDelete("/{id:guid}/share", Unshare);
        app.MapGet("/api/v1/shared-wishlists/{key}", Shared).RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    public static string NameKey(string name) => name.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    internal static string? NameProblem(string? raw, out string name)
    {
        name = (raw ?? "").Normalize(NormalizationForm.FormC).Trim();
        if (name.Length == 0) return "Give the list a name.";
        if (name.EnumerateRunes().Any(r => System.Text.Rune.IsControl(r)) || name.Contains((char)0x2028) || name.Contains((char)0x2029)) return "The name has characters we can't use.";
        if (new System.Globalization.StringInfo(name).LengthInTextElements > ListLimits.MaxNameLength) return $"Use at most {ListLimits.MaxNameLength} characters.";
        return null;
    }

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static object Summary(Wishlist w, int count) => new { id = w.Id, name = w.Name, itemCount = count, shared = w.ShareKeyHash != null, sharedAt = w.SharedAt, createdAt = w.CreatedAt, updatedAt = w.UpdatedAt };

    private static async Task<IResult> List(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var id = ctx.Buyer().AccountId;
        var lists = await db.Wishlists.AsNoTracking().Where(w => w.AccountId == id).OrderBy(w => w.CreatedAt).ThenBy(w => w.Id).ToListAsync(ct);
        var ids = lists.Select(l => l.Id).ToList();
        var counts = await db.WishlistItems.AsNoTracking().Where(i => ids.Contains(i.WishlistId)).GroupBy(i => i.WishlistId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return Private(Results.Json(new { limit = ListLimits.MaxLists, itemLimit = ListLimits.MaxItemsPerList, wishlists = lists.Select(l => Summary(l, counts.GetValueOrDefault(l.Id))) }), ctx);
    }

    private static async Task<IResult> Create(WishlistNameBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var accountId = ctx.Buyer().AccountId;
        if (NameProblem(body?.Name, out var name) is { } p) return Private(Problems.Validation(new Dictionary<string, string> { ["name"] = p }), ctx);
        var now = time.GetUtcNow().ToUniversalTime();
        var w = new Wishlist { Id = Guid.NewGuid(), AccountId = accountId, Name = name, NameKey = NameKey(name), CreatedAt = now, UpdatedAt = now };
        db.Wishlists.Add(w);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Solvelt.Marketplace.Api.Account.AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return Private(Problems.Json(409, "name_taken", "You already have a list with that name."), ctx); }
        // The count is checked after the insert, so parallel requests cannot all slip under the limit; the list that went over is removed again.
        if (await db.Wishlists.CountAsync(x => x.AccountId == accountId, ct) > ListLimits.MaxLists)
        {
            await db.Wishlists.Where(x => x.Id == w.Id).ExecuteDeleteAsync(ct);
            return Private(Problems.Unprocessable("too_many_lists", $"You can have up to {ListLimits.MaxLists} lists."), ctx);
        }
        return Private(Results.Json(Summary(w, 0), statusCode: 201), ctx);
    }

    private static async Task<Wishlist?> Own(MarketplaceDb db, HttpContext ctx, Guid id, CancellationToken ct, bool track = false)
    {
        var accountId = ctx.Buyer().AccountId;
        var q = track ? db.Wishlists : db.Wishlists.AsNoTracking();
        return await q.FirstOrDefaultAsync(w => w.Id == id && w.AccountId == accountId, ct);
    }

    private static async Task<IResult> Get(Guid id, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is not { } w) return Private(Problems.NotFound(), ctx);
        var items = await db.WishlistItems.AsNoTracking().Where(i => i.WishlistId == id).OrderByDescending(i => i.AddedAt).ThenBy(i => i.Id).ToListAsync(ct);
        var cards = await ListCards.LoadAsync(db, items.Select(i => i.ListingId).ToList(), ct);
        return Private(Results.Json(new
        {
            wishlist = Summary(w, items.Count),
            // A listing that is no longer on the storefront stays on the list (it may come back) but is shown as unavailable, without its details.
            items = items.Select(i => new { listingId = i.ListingId, addedAt = i.AddedAt, listing = cards.GetValueOrDefault(i.ListingId), unavailable = !cards.ContainsKey(i.ListingId) }),
        }), ctx);
    }

    private static async Task<IResult> Rename(Guid id, WishlistNameBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct, track: true) is not { } w) return Private(Problems.NotFound(), ctx);
        if (NameProblem(body?.Name, out var name) is { } p) return Private(Problems.Validation(new Dictionary<string, string> { ["name"] = p }), ctx);
        w.Name = name; w.NameKey = NameKey(name); w.UpdatedAt = time.GetUtcNow().ToUniversalTime();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Solvelt.Marketplace.Api.Account.AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return Private(Problems.Json(409, "name_taken", "You already have a list with that name."), ctx); }
        return Private(Results.Json(Summary(w, await db.WishlistItems.CountAsync(i => i.WishlistId == id, ct))), ctx);
    }

    private static async Task<IResult> Delete(Guid id, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is null) return Private(Problems.NotFound(), ctx);
        await db.WishlistItems.Where(i => i.WishlistId == id).ExecuteDeleteAsync(ct);
        await db.Wishlists.Where(w => w.Id == id && w.AccountId == ctx.Buyer().AccountId).ExecuteDeleteAsync(ct);
        return Private(Results.NoContent(), ctx);
    }

    /// <summary>Adding is idempotent: the same listing twice (also in parallel) is one row. The listing must be on the storefront now.</summary>
    private static async Task<IResult> AddItem(Guid id, Guid listingId, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is null) return Private(Problems.NotFound(), ctx);
        var listing = await CatalogEndpoints.Visible(db).Where(l => l.Id == listingId).Select(l => new { l.Currency, l.Available }).FirstOrDefaultAsync(ct);
        if (listing is null) return Private(Problems.NotFound("That item is not available."), ctx);
        var lowest = await WishlistWatchHandler.LowestPriceAsync(db, listingId, ct) ?? 0;   // the baseline is the lowest price over ALL variants (M6-03)
        if (await db.WishlistItems.AnyAsync(i => i.WishlistId == id && i.ListingId == listingId, ct)) return Private(Results.NoContent(), ctx);
        var item = new WishlistItem { Id = Guid.NewGuid(), WishlistId = id, ListingId = listingId, AddedAt = time.GetUtcNow().ToUniversalTime(), BaselinePriceCents = lowest, BaselineCurrency = listing.Currency, BaselineAvailable = listing.Available };
        db.WishlistItems.Add(item);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Solvelt.Marketplace.Api.Account.AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return Private(Results.NoContent(), ctx); }   // a parallel request added it first
        if (await db.WishlistItems.CountAsync(i => i.WishlistId == id, ct) > ListLimits.MaxItemsPerList)
        {
            await db.WishlistItems.Where(i => i.Id == item.Id).ExecuteDeleteAsync(ct);
            return Private(Problems.Unprocessable("list_full", $"A list holds up to {ListLimits.MaxItemsPerList} items."), ctx);
        }
        await db.Wishlists.Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.UpdatedAt, item.AddedAt), ct);
        return Private(Results.NoContent(), ctx);
    }

    private static async Task<IResult> RemoveItem(Guid id, Guid listingId, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is null) return Private(Problems.NotFound(), ctx);
        var now = time.GetUtcNow().ToUniversalTime();
        if (await db.WishlistItems.Where(i => i.WishlistId == id && i.ListingId == listingId).ExecuteDeleteAsync(ct) > 0)
            await db.Wishlists.Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.UpdatedAt, now), ct);
        return Private(Results.NoContent(), ctx);
    }

    /// <summary>For the heart buttons of a page of cards: which of the buyer's lists hold each of up to 50 listings. <c>?listingIds=a,b,c</c>.</summary>
    private static async Task<IResult> Membership(string? listingIds, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var parts = (listingIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > ListLimits.MaxMembershipQuery || parts.Any(p => !Guid.TryParse(p, out _))) return Private(Problems.Validation(new Dictionary<string, string> { ["listingIds"] = $"Send up to {ListLimits.MaxMembershipQuery} listing ids." }), ctx);
        var ids = parts.Select(Guid.Parse).Distinct().ToList();
        var accountId = ctx.Buyer().AccountId;
        var rows = await (from i in db.WishlistItems.AsNoTracking() join w in db.Wishlists.AsNoTracking() on i.WishlistId equals w.Id
                          where w.AccountId == accountId && ids.Contains(i.ListingId) select new { i.ListingId, ListId = w.Id }).ToListAsync(ct);
        return Private(Results.Json(new { membership = ids.ToDictionary(x => x.ToString(), x => rows.Where(r => r.ListingId == x).Select(r => r.ListId).Order().ToList()) }), ctx);
    }

    private static async Task<IResult> Share(Guid id, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is null) return Private(Problems.NotFound(), ctx);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Wishlists.Where(w => w.Id == id && w.AccountId == ctx.Buyer().AccountId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ShareKeyHash, HashKey(key)).SetProperty(w => w.SharedAt, (DateTimeOffset?)now).SetProperty(w => w.UpdatedAt, now), ct);
        return Private(Results.Json(new { shareKey = key }), ctx);   // shown once: only the hash is kept
    }

    private static async Task<IResult> Unshare(Guid id, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (await Own(db, ctx, id, ct) is null) return Private(Problems.NotFound(), ctx);
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Wishlists.Where(w => w.Id == id && w.AccountId == ctx.Buyer().AccountId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ShareKeyHash, (string?)null).SetProperty(w => w.SharedAt, (DateTimeOffset?)null).SetProperty(w => w.UpdatedAt, now), ct);
        return Private(Results.NoContent(), ctx);
    }

    /// <summary>Anonymous, read-only. Shows the list name and the listings still on the storefront, nothing about the owner. A wrong or revoked key is 404; the answer is never cached or indexed.</summary>
    private static async Task<IResult> Shared(string key, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (key.Length is < 20 or > 64) return Problems.NotFound();
        var hash = HashKey(key);
        if (await db.Wishlists.AsNoTracking().FirstOrDefaultAsync(w => w.ShareKeyHash == hash, ct) is not { } w) return Problems.NotFound();
        var items = await db.WishlistItems.AsNoTracking().Where(i => i.WishlistId == w.Id).OrderByDescending(i => i.AddedAt).ThenBy(i => i.Id).ToListAsync(ct);
        var cards = await ListCards.LoadAsync(db, items.Select(i => i.ListingId).ToList(), ct);
        return Results.Json(new { name = w.Name, items = items.Where(i => cards.ContainsKey(i.ListingId)).Select(i => new { listing = cards[i.ListingId] }) });
    }
}
