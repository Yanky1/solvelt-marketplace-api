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
        g.MapPost("", Create).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapPatch("/{id:guid}", Edit).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapPut("/{id:guid}/helpful", (Guid id, HttpContext c, ReviewService s, CancellationToken ct) => Vote(id, true, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapDelete("/{id:guid}/helpful", (Guid id, HttpContext c, ReviewService s, CancellationToken ct) => Vote(id, false, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapPost("/{id:guid}/report", (Guid id, ReviewReportBody? b, HttpContext c, ReviewService s, CancellationToken ct) => Report(id, b, false, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapPost("/{id:guid}/reply/report", (Guid id, ReviewReportBody? b, HttpContext c, ReviewService s, CancellationToken ct) => Report(id, b, true, c, s, ct)).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapPost("/{id:guid}/photos", AddPhoto).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapDelete("/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, HttpContext c, ReviewService s, CancellationToken ct) => { var o = await s.DeletePhotoAsync(c.Buyer().AccountId, id, photoId, ct); return o == ReviewOutcome.Ok ? Private(Results.NoContent(), c) : Refuse(o, c); }).AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter<ReviewWriteRateLimitFilter>();
        g.MapGet("/photos/{photoId:guid}", Photo);
        app.MapGet("/api/v1/account/reviews", Mine).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    private static object View(Review r, bool mine = false, IReadOnlyList<ReviewPhoto>? photos = null) => new
    {
        photos = (photos ?? []).Select(p => new { id = p.Id, width = p.Width, height = p.Height }),
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
        var ids = rows.Select(r => r.Id).ToList();
        var photos = (await db.ReviewPhotos.AsNoTracking().Where(x => ids.Contains(x.ReviewId)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).ToLookup(x => x.ReviewId);
        return Results.Json(new { summary = Aggregates.Summary(agg), reviews = rows.Select(r => View(r, false, photos[r.Id].ToList())), page = p, pageSize = ReviewLimits.PageSize, total });
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
        ReviewOutcome.TooMany => Problems.Unprocessable("too_many_photos", $"A review can have up to {ReviewLimits.MaxPhotos} photos."),
        ReviewOutcome.BadFile => Problems.Unprocessable("bad_photo", "Use a JPEG, PNG or WebP picture of up to 5 MB."),
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
        var rids = rows.Select(r => r.Id).ToList();
        var mp = (await db.ReviewPhotos.AsNoTracking().Where(x => rids.Contains(x.ReviewId)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).ToLookup(x => x.ReviewId);
        return Private(Results.Json(new { reviews = rows.Select(r => new { review = View(r, true, mp[r.Id].ToList()), listing = titles.GetValueOrDefault(r.ListingId) }) }), ctx);
    }

    private const int MaxUploadBytes = 6 * 1024 * 1024;

    /// <summary><c>POST /{id}/photos</c>, multipart with ONE file part. The declared type and the file name are ignored: the bytes decide (see <see cref="ReviewService.AddPhotoAsync"/>).</summary>
    private static async Task<IResult> AddPhoto(Guid id, HttpContext ctx, ReviewService s, CancellationToken ct)
    {
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = MaxUploadBytes;
        if (!ctx.Request.HasFormContentType) return Private(Problems.Unprocessable("bad_photo", "Send the picture as a file."), ctx);
        byte[] bytes;
        try
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1 || form.Files[0].Length is 0 or > MaxUploadBytes) return Private(Problems.Unprocessable("bad_photo", "Send one picture of up to 5 MB."), ctx);
            await using var stream = form.Files[0].OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or Microsoft.AspNetCore.Http.BadHttpRequestException or IOException) { return Private(Problems.Unprocessable("bad_photo", "Send one picture of up to 5 MB."), ctx); }
        var (outcome, photo) = await s.AddPhotoAsync(ctx.Buyer().AccountId, id, bytes, ct);
        return outcome == ReviewOutcome.Ok ? Private(Results.Json(new { id = photo!.Id, width = photo.Width, height = photo.Height }, statusCode: 201), ctx) : Refuse(outcome, ctx);
    }

    /// <summary>The stored (re-encoded) picture of a PUBLISHED review: public, immutable, never sniffed; a photo of a hidden or removed review is 404.</summary>
    private static async Task<IResult> Photo(Guid photoId, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var p = await (from x in db.ReviewPhotos.AsNoTracking() join r in db.Reviews.AsNoTracking() on x.ReviewId equals r.Id
                       where x.Id == photoId && r.Status == ReviewStatus.Published select new { x.ContentType }).FirstOrDefaultAsync(ct);
        if (p is null) return Problems.NotFound();
        var bytes = await db.ReviewPhotoData.AsNoTracking().Where(d => d.PhotoId == photoId).Select(d => d.Bytes).FirstOrDefaultAsync(ct);
        if (bytes is null) return Problems.NotFound();
        // The picture itself never changes (the id is unique per upload), but whether it may be shown does: a hidden or removed review's photos must stop being served soon, so caches keep it for five minutes at most
        // and must ask again; the ETag makes the asking cheap (a 304), and the visibility check above runs BEFORE any 304, so a hidden review's photo is a 404 even for a cache that holds it.
        ctx.Response.Headers.CacheControl = "public, max-age=300, must-revalidate";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        return Results.File(bytes, p.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{photoId:N}\""));
    }
}
