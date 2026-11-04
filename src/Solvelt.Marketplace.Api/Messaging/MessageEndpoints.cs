using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Messaging;

public sealed record SendBody(Guid? ThreadId, string? OrderNumber, string? SellerOrderNumber, Guid? ListingId, string? Text, bool? AcknowledgeWarning);
public sealed record ReportBody(string? Reason, string? Note);

/// <summary>The buyer's messages inbox (M5). Every route is the signed-in buyer's own: another buyer's thread or message is a 404. Responses are <c>private, no-store</c>.</summary>
public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessages(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/messages").AddEndpointFilter<RequireBuyerFilter>();
        g.MapGet("/threads", Threads).RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("/threads/{id:guid}", Thread).RequireRateLimiting(RateLimitPolicies.Public);
        g.MapPost("/threads/{id:guid}/read", Read).RequireRateLimiting(RateLimitPolicies.Public);
        g.MapPost("", Send).RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<MessageRateLimitFilter>();
        g.MapPost("/{id:guid}/report", Report).RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<MessageRateLimitFilter>();
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static async Task<IResult> Threads(HttpContext ctx, MarketplaceDb db, int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var q = db.MessageThreads.AsNoTracking().Where(t => t.AccountId == buyer.AccountId && t.RedactedAt == null);
        if (before is { } b) q = beforeId is { } bid ? q.Where(t => t.LastMessageAt < b || (t.LastMessageAt == b && t.Id.CompareTo(bid) < 0)) : q.Where(t => t.LastMessageAt < b);
        var rows = await q.OrderByDescending(t => t.LastMessageAt).ThenByDescending(t => t.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var ids = page.Select(t => t.Id).ToList();
        var unread = await db.ThreadMessages.AsNoTracking().Where(m => ids.Contains(m.ThreadId) && m.Direction == MessageDirections.Seller && m.ReadAt == null && m.RedactedAt == null)
            .GroupBy(m => m.ThreadId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var totalUnread = await (from m in db.ThreadMessages.AsNoTracking() join t in db.MessageThreads.AsNoTracking() on m.ThreadId equals t.Id
                                 where t.AccountId == buyer.AccountId && t.RedactedAt == null && m.Direction == MessageDirections.Seller && m.ReadAt == null && m.RedactedAt == null select m.Id).CountAsync(ct);
        var last = new Dictionary<Guid, string>();
        foreach (var t in page)
        {
            var m = await db.ThreadMessages.AsNoTracking().Where(x => x.ThreadId == t.Id && x.RedactedAt == null).OrderByDescending(x => x.SentAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            if (m is not null) last[t.Id] = m.HiddenAt is not null ? "" : m.Text.Length > 120 ? m.Text[..120] : m.Text;
        }
        var orderNumbers = await db.Orders.AsNoTracking().Where(o => page.Select(t => t.OrderId).Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        return Private(Results.Json(new
        {
            unreadCount = totalUnread,
            threads = page.Select(t => new
            {
                id = t.Id, kind = t.Kind, subject = t.Subject, sellerName = t.SellerName, orderNumber = t.OrderId is { } o && orderNumbers.TryGetValue(o, out var n) ? n : null, listingId = t.ListingId,
                lastMessageAt = t.LastMessageAt, unreadCount = unread.GetValueOrDefault(t.Id), preview = last.GetValueOrDefault(t.Id),
            }),
            next = rows.Count > take ? new { before = page[^1].LastMessageAt, beforeId = page[^1].Id } : null,
        }), ctx);
    }

    private static async Task<IResult> Thread(Guid id, HttpContext ctx, MarketplaceDb db, int? limit, DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var buyer = ctx.Buyer();
        var t = await db.MessageThreads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AccountId == buyer.AccountId && x.RedactedAt == null, ct);
        if (t is null) return Private(Problems.NotFound(), ctx);
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var q = db.ThreadMessages.AsNoTracking().Include(m => m.Attachments).Where(m => m.ThreadId == id && m.RedactedAt == null);
        // keyset on (SentAt, Id): messages with the same time are neither skipped nor repeated between pages (M5 review, info)
        if (before is { } b) q = beforeId is { } bi ? q.Where(m => m.SentAt < b || (m.SentAt == b && m.Id.CompareTo(bi) < 0)) : q.Where(m => m.SentAt < b);
        var rows = await q.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).Reverse().ToList();
        return Private(Results.Json(new
        {
            thread = new { id = t.Id, kind = t.Kind, subject = t.Subject, sellerName = t.SellerName, listingId = t.ListingId },
            messages = page.Select(m => new
            {
                id = m.Id, direction = m.Direction, status = m.Status, sentAt = m.SentAt, read = m.ReadAt != null, reported = m.ReportedAt != null,
                hidden = m.HiddenAt != null, text = m.HiddenAt != null ? null : m.Text,
                attachments = m.HiddenAt != null ? [] : m.Attachments.Select(a => new { id = a.Id, fileName = a.FileName, size = a.Size, type = a.SniffedType, available = a.Status == AttachmentStatuses.Stored }).ToArray<object>(),
            }),
            older = rows.Count > take ? (DateTimeOffset?)page[0].SentAt : null,
            olderId = rows.Count > take ? (Guid?)page[0].Id : null,
        }), ctx);
    }

    private static async Task<IResult> Send(SendBody? body, HttpContext ctx, MessageService svc, CancellationToken ct)
        => OrderEndpoints.Respond(await svc.SendAsync(ctx.Buyer(), new SendMessageInput(body?.ThreadId, body?.OrderNumber, body?.SellerOrderNumber, body?.ListingId, body?.Text, body?.AcknowledgeWarning == true), ct), ctx);

    private static async Task<IResult> Read(Guid id, HttpContext ctx, MessageService svc, CancellationToken ct)
        => Private(await Task.FromResult(Map(await svc.MarkReadAsync(ctx.Buyer(), id, ct))), ctx);

    private static async Task<IResult> Report(Guid id, ReportBody? body, HttpContext ctx, MessageService svc, CancellationToken ct)
        => OrderEndpoints.Respond(await svc.ReportAsync(ctx.Buyer(), id, body?.Reason, body?.Note, ct), ctx);

    private static IResult Map(ActionResult r) => r.Status == 204 ? Results.NoContent() : r.Status == 404 ? Problems.NotFound() : Problems.Json(r.Status, r.Code ?? "error", r.Message ?? "");
}
