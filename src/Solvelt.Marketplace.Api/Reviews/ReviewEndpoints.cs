using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Reviews;

public sealed record ReviewBody(string? Kind, Guid? TargetId, int? Rating, string? Text);
public sealed record ReviewEditBody(int? Rating, string? Text);
public sealed record ReviewReportBody(string? Reason, string? Note);

/// <summary>
/// <c>/api/v1/reviews</c> (M6). Reading published reviews of a listing is public; writing needs a signed-in buyer. Another buyer's review is 404 for edits. Times are ours; the buyer chooses
/// only the rating and the text. See DECISIONS M6-04.
/// </summary>
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviews(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/reviews").RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("", List);
        g.MapGet("/eligible", Eligible).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("", Create).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPatch("/{id:guid}", Edit).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPut("/{id:guid}/helpful", (Guid id, HttpContext c, ReviewService s, CancellationToken ct) => Vote(id, true, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>();
        g.MapDelete("/{id:guid}/helpful", (Guid id, HttpContext c, ReviewService s, CancellationToken ct) => Vote(id, false, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/{id:guid}/report", (Guid id, ReviewReportBody? b, HttpContext c, ReviewService s, CancellationToken ct) => Report(id, b, false, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>();
        g.MapPost("/{id:guid}/reply/report", (Guid id, ReviewReportBody? b, HttpContext c, ReviewService s, CancellationToken ct) => Report(id, b, true, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>();
        app.MapGet("/api/v1/account/reviews", Mine).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static object View(Review r, bool mine = false) => new
    {
        id = r.Id, listingId = r.ListingId, rating = r.Rating, text = r.Status == ReviewStatus.Published || mine ? r.Text : "", displayName = r.DisplayName, verified = true,
        createdAt = r.CreatedAt, editedAt = r.EditedAt, helpfulCount = r.HelpfulCount, status = mine ? r.Status : null, moderationNote = mine ? r.ModerationNote : null,
        canEdit = mine && r.Status == ReviewStatus.Published,
        reply = r.ReplyText is null || r.Status != ReviewStatus.Published ? null : new { id = r.ReplyId, text = r.ReplyText, at = r.ReplyAt },
    };

    /// <summary><c>GET ?listingId=&amp;sort=recent|helpful|high|low&amp;rating=1..5&amp;page=</c>: the summary (count, average, distribution) and a page of published reviews. Public.</summary>
    private static async Task<IResult> List(Guid? listingId, string? sort, int? rating, int? page, MarketplaceDb db, CancellationToken ct)
    {
        if (listingId is null || listingId == Guid.Empty) return Problems.Validation(new Dictionary<string, string> { ["listingId"] = "Send the listing id." });
        if (rating is < 1 or > 5) return Problems.Validation(new Dictionary<string, string> { ["rating"] = "Use 1 to 5." });
        var p = Math.Clamp(page ?? 1, 1, 1000);
        var q = db.Reviews.AsNoTracking().Where(r => r.ListingId == listingId && r.Status == ReviewStatus.Published);
        if (rating is { } star) q = q.Where(r => r.Rating == star);
        var total = await q.CountAsync(ct);
        q = sort switch
        {
            "helpful" => q.OrderByDescending(r => r.HelpfulCount).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "high" => q.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "low" => q.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            _ => q.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
        };
        var rows = await q.Skip((p - 1) * ReviewLimits.PageSize).Take(ReviewLimits.PageSize).ToListAsync(ct);
        var agg = await db.RatingAggregates.AsNoTracking().FirstOrDefaultAsync(a => a.Scope == "listing" && a.ScopeId == listingId, ct);
        return Results.Json(new { summary = Aggregates.Summary(agg), reviews = rows.Select(r => View(r)), page = p, pageSize = ReviewLimits.PageSize, total });
    }

    private static async Task<IResult> Eligible(Guid? listingId, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        if (listingId is null || listingId == Guid.Empty) return Private(Problems.Validation(new Dictionary<string, string> { ["listingId"] = "Send the listing id." }), ctx);
        var items = await s.EligibleAsync(ctx.Buyer().AccountId, listingId.Value, ct);
        return Private(Results.Json(new { items = items.Select(i => new { kind = i.Kind, id = i.Id, label = i.Label, at = i.At }) }), ctx);
    }

    private static IResult Refuse(ReviewOutcome o, HttpContext ctx) => Private(o switch
    {
        ReviewOutcome.NotFound => Problems.NotFound(),
        ReviewOutcome.NotEligible => Problems.Unprocessable("not_eligible", "You can review what you have received or attended."),
        ReviewOutcome.AlreadyReviewed => Problems.Json(409, "already_reviewed", "You have already reviewed this."),
        ReviewOutcome.Locked => Problems.Unprocessable("review_locked", "This review can't be changed any more."),
        ReviewOutcome.OwnReview => Problems.Unprocessable("own_review", "You can't do that to your own review."),
        _ => Problems.Validation(new Dictionary<string, string> { ["kind"] = "Choose what you are reviewing." }),
    }, ctx);

    private static async Task<IResult> Create(ReviewBody? body, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (body?.Rating is not (>= 1 and <= 5)) errors["rating"] = "Choose 1 to 5 stars.";
        if (ReviewService.TextProblem(body?.Text, out var text) is { } tp) errors["text"] = tp;
        if (body?.TargetId is null || body.TargetId == Guid.Empty) errors["targetId"] = "Choose what you are reviewing.";
        if (errors.Count > 0) return Private(Problems.Validation(errors), ctx);
        var (outcome, review) = await s.CreateAsync(ctx.Buyer().AccountId, body!.Kind, body.TargetId!.Value, body.Rating!.Value, text, ct);
        return outcome == ReviewOutcome.Ok ? Private(Results.Json(View(review!, true), statusCode: 201), ctx) : Refuse(outcome, ctx);
    }

    private static async Task<IResult> Edit(Guid id, ReviewEditBody? body, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (body?.Rating is not (>= 1 and <= 5)) errors["rating"] = "Choose 1 to 5 stars.";
        if (ReviewService.TextProblem(body?.Text, out var text) is { } tp) errors["text"] = tp;
        if (errors.Count > 0) return Private(Problems.Validation(errors), ctx);
        var (outcome, review) = await s.EditAsync(ctx.Buyer().AccountId, id, body!.Rating!.Value, text, ct);
        return outcome == ReviewOutcome.Ok ? Private(Results.Json(View(review!, true)), ctx) : Refuse(outcome, ctx);
    }

    private static async Task<IResult> Vote(Guid id, bool on, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        var o = await s.VoteAsync(ctx.Buyer().AccountId, id, on, ct);
        return o == ReviewOutcome.Ok ? Private(Results.NoContent(), ctx) : Refuse(o, ctx);
    }

    private static async Task<IResult> Report(Guid id, ReviewReportBody? body, bool reply, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        if (body?.Reason is not { } reason || !ReviewService.ReportReasons.Contains(reason)) return Private(Problems.Validation(new Dictionary<string, string> { ["reason"] = "Choose a reason." }), ctx);
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > 1000 }) return Private(Problems.Validation(new Dictionary<string, string> { ["note"] = "Use at most 1000 characters." }), ctx);
        var o = await s.ReportAsync(ctx.Buyer().AccountId, id, reason, note, ct, reply);
        return o == ReviewOutcome.Ok ? Private(Results.NoContent(), ctx) : Refuse(o, ctx);
    }

    private static async Task<IResult> Mine(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var id = ctx.Buyer().AccountId;
        var rows = await db.Reviews.AsNoTracking().Where(r => r.AccountId == id).OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Take(200).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => rows.Select(r => r.ListingId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new { l.Title, l.Slug }, ct);
        return Private(Results.Json(new { reviews = rows.Select(r => new { review = View(r, true), listing = titles.GetValueOrDefault(r.ListingId) }) }), ctx);
    }
}
