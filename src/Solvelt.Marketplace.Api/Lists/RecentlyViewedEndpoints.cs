using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Lists;

public sealed record MergeViewedBody(List<Guid>? ListingIds);

/// <summary>
/// <c>/api/v1/recently-viewed</c> (M6): the listings a signed-in buyer looked at, newest first, at most <see cref="ListLimits.MaxRecentlyViewed"/>. Guests keep theirs in the browser (ids only, no PII)
/// and send them once on sign-in ("merge"). The view time is ours: a merged list gets times just before "now" in the order the client sent them and never replaces a newer view the server has.
/// </summary>
public static class RecentlyViewedEndpoints
{
    public static IEndpointRouteBuilder MapRecentlyViewed(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/recently-viewed").RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("", List);
        g.MapDelete("", Clear);
        g.MapPost("/merge", Merge);
        g.MapPut("/{listingId:guid}", Record);
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static async Task<IResult> List(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var id = ctx.Buyer().AccountId;
        var rows = await db.RecentlyViewed.AsNoTracking().Where(r => r.AccountId == id).OrderByDescending(r => r.ViewedAt).ThenBy(r => r.Id).Take(ListLimits.MaxRecentlyViewed).ToListAsync(ct);
        var cards = await ListCards.LoadAsync(db, rows.Select(r => r.ListingId).ToList(), ct);
        return Private(Results.Json(new { items = rows.Where(r => cards.ContainsKey(r.ListingId)).Select(r => new { viewedAt = r.ViewedAt, listing = cards[r.ListingId] }) }), ctx);
    }

    private static async Task<IResult> Clear(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        await db.RecentlyViewed.Where(r => r.AccountId == ctx.Buyer().AccountId).ExecuteDeleteAsync(ct);
        return Private(Results.NoContent(), ctx);
    }

    private static async Task<IResult> Record(Guid listingId, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (!await CatalogEndpoints.Visible(db).AnyAsync(l => l.Id == listingId, ct)) return Private(Problems.NotFound(), ctx);
        var accountId = ctx.Buyer().AccountId;
        await UpsertAsync(db, accountId, listingId, time.GetUtcNow().ToUniversalTime(), newer: true, ct);
        await TrimAsync(db, accountId, ct);
        return Private(Results.NoContent(), ctx);
    }

    private static async Task<IResult> Merge(MergeViewedBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var accountId = ctx.Buyer().AccountId;
        var wanted = (body?.ListingIds ?? []).Where(g => g != Guid.Empty).Distinct().Take(ListLimits.MaxRecentlyViewed).ToList();   // newest first, as the browser keeps them
        var visible = (await CatalogEndpoints.Visible(db).Where(l => wanted.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow().ToUniversalTime();
        var rank = 0;
        foreach (var id in wanted.Where(visible.Contains))
            await UpsertAsync(db, accountId, id, now.AddSeconds(-(++rank)), newer: false, ct);   // existing views keep their own (server) time
        await TrimAsync(db, accountId, ct);
        return Private(Results.NoContent(), ctx);
    }

    /// <summary>Insert or move to the front. Parallel requests for the same listing end as one row (unique index): the loser of the insert race updates instead.</summary>
    private static async Task UpsertAsync(MarketplaceDb db, Guid accountId, Guid listingId, DateTimeOffset at, bool newer, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var updated = newer
                ? await db.RecentlyViewed.Where(r => r.AccountId == accountId && r.ListingId == listingId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ViewedAt, at), ct)
                : await db.RecentlyViewed.AnyAsync(r => r.AccountId == accountId && r.ListingId == listingId, ct) ? 1 : 0;
            if (updated > 0) return;
            db.RecentlyViewed.Add(new RecentlyViewedItem { Id = Guid.NewGuid(), AccountId = accountId, ListingId = listingId, ViewedAt = at });
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
    }

    private static async Task TrimAsync(MarketplaceDb db, Guid accountId, CancellationToken ct)
    {
        var keep = await db.RecentlyViewed.AsNoTracking().Where(r => r.AccountId == accountId).OrderByDescending(r => r.ViewedAt).ThenBy(r => r.Id).Take(ListLimits.MaxRecentlyViewed).Select(r => r.Id).ToListAsync(ct);
        await db.RecentlyViewed.Where(r => r.AccountId == accountId && !keep.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }
}
