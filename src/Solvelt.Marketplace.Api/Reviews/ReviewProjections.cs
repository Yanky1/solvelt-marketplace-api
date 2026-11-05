using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Reviews;

/// <summary>
/// The portal's moderation decision on a REVIEW or on a seller's REPLY (<c>content.report_decided</c>, CATALOG.md §6; wired into M5's handler through <see cref="ContentReportDecidedHandler.ReviewDecision"/>).
/// Matching (review hotspot): the report id must be one WE made (<see cref="ReviewReport.ReportId"/>) and the target must be that report's own review (or its reply id), never the target id alone.
/// The status of a review follows ALL its decided reports: removed beats hidden beats published; a later decision of the same report (staff changed their mind: <c>dismissed</c>) restores a hidden review;
/// removal is final (the text is gone). A change of status moves the rating figures by the same atomic increments.
/// </summary>
public static class ReviewModeration
{
    public static async Task<HandlerOutcome> ApplyAsync(ProjectionContext c, CancellationToken ct)
    {
        var d = c.Event.Data;
        var reportId = FulfilmentProjector.Str(d, "reportId");
        var target = FulfilmentProjector.Str(d, "targetId");
        var decision = FulfilmentProjector.Str(d, "decision");
        var kind = FulfilmentProjector.Str(d, "kind");
        if (string.IsNullOrWhiteSpace(reportId) || string.IsNullOrWhiteSpace(target) || decision is not ("dismissed" or "hidden" or "removed")) return HandlerOutcome.Skipped("not a review decision we understand");
        var report = await c.Db.ReviewReports.FirstOrDefaultAsync(x => x.ReportId == reportId && x.Kind == kind, ct);
        if (report is null) return HandlerOutcome.Skipped("not a report we made");
        var review = await c.Db.Reviews.FirstOrDefaultAsync(x => x.Id == report.ReviewId, ct);
        if (review is null) return HandlerOutcome.Skipped("unknown reported review");
        if (report.TargetId is null || !string.Equals(report.TargetId, target, StringComparison.Ordinal)) return HandlerOutcome.Skipped("target does not match the report");   // the target WE reported, whether or not the reply is still there
        if (!await c.AdvanceAsync("review-report", reportId, "decision", ct: ct)) return HandlerOutcome.Skipped("older than the applied decision");
        var now = c.Time.GetUtcNow().ToUniversalTime();
        report.Decision = decision; report.DecidedAt = now;
        await c.Db.SaveChangesAsync(ct);
        var note = FulfilmentProjector.Cap(FulfilmentProjector.Str(d, "reasonShownToUser"), 300);

        if (kind == "review_reply")
        {
            // only the reply that was reported goes: a newer reply of the seller (another id) is not what staff decided on
            if (decision is "hidden" or "removed" && review.ReplyId == target) { review.ReplyText = null; review.ReplyId = null; review.ReplyAt = null; await c.Db.SaveChangesAsync(ct); }
            return HandlerOutcome.Applied();
        }

        // The status and the rating that decide the figures are read under the review's row lock: an edit (or a second decision) that ran a moment ago has changed them since `review` was loaded.
        await ReviewService.LockAsync(c.Db, review.Id, null, ct);
        await c.Db.Entry(review).ReloadAsync(ct);
        var decisions = await c.Db.ReviewReports.AsNoTracking().Where(x => x.ReviewId == review.Id && x.Kind == "review" && x.Decision != null).Select(x => x.Decision!).ToListAsync(ct);
        var next = review.Status == ReviewStatus.Removed || decisions.Contains("removed") ? ReviewStatus.Removed : decisions.Contains("hidden") ? ReviewStatus.Hidden : ReviewStatus.Published;
        if (next != review.Status)
        {
            var wasPublished = review.Status == ReviewStatus.Published;
            review.Status = next; review.StatusAt = now;
            if (note is not null) review.ModerationNote = note;
            if (next == ReviewStatus.Removed) { review.Text = ""; review.ReplyText = null; review.ReplyId = null; review.ReplyAt = null; }
            await c.Db.SaveChangesAsync(ct);
            if (next == ReviewStatus.Removed)
            {
                await c.Db.ReviewPhotoData.Where(d => c.Db.ReviewPhotos.Any(p => p.Id == d.PhotoId && p.ReviewId == review.Id)).ExecuteDeleteAsync(ct);   // removal is final: the pictures go too (a hidden review keeps them, unserved)
                await c.Db.ReviewPhotos.Where(p => p.ReviewId == review.Id).ExecuteDeleteAsync(ct);
            }
            if (wasPublished && next != ReviewStatus.Published) await Aggregates.ApplyAsync(c.Db, review, -1, ct);
            else if (!wasPublished && next == ReviewStatus.Published)
            {
                await Aggregates.ApplyAsync(c.Db, review, +1, ct);
                // Restored: the portal may never have been told (a review hidden before its first send is not sent) and the seller must see it; sending again is safe (idempotent on the review id, the newest edit wins).
                var stamp = "r" + now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await new OutboxWriter(c.Db, c.Time).TryEnqueueAsync(ReviewService.UpsertType, new ReviewService.UpsertPayload(review.Id, stamp), $"review-upsert:{review.Id:N}:{stamp}", ct: ct);
            }
        }
        return HandlerOutcome.Applied();
    }
}

/// <summary>
/// The seller's reply to a review, relayed by the portal (C17: <c>review.reply_posted</c>, re-sent on every edit with a stable reply id; <c>review.reply_removed</c>). One reply per review, kept by the
/// portal's event order (an older event never overwrites a newer one); none on a review that is not published. Matching is by OUR review id.
/// </summary>
public sealed class ReviewReplyHandler(ILogger<ReviewReplyHandler> log) : IProjectionHandler
{
    public const int MaxReplyLength = 2000;
    public IReadOnlyCollection<string> EventTypes { get; } = ["review.reply_posted", "review.reply_removed"];

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var d = c.Event.Data;
        if (!Guid.TryParse(FulfilmentProjector.Str(d, "reviewId"), out var reviewId)) return HandlerOutcome.Skipped("no review id");
        var replyId = FulfilmentProjector.Str(d, "replyId");
        if (string.IsNullOrWhiteSpace(replyId) || replyId.Length > 100) return HandlerOutcome.Skipped("no reply id");
        var review = await c.Db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null) return HandlerOutcome.Skipped("unknown review");
        // C17.1: the portal numbers the events of one review in the scope "review:{reviewId}"; the greater sequence wins, without one the arrival order does.
        // A sequence in ANOTHER scope should never happen (the portal names the review's own scope): it is not used, and it is worth a person's look.
        if (c.Event is { Sequence: not null, SequenceScope: { } foreign } && !string.Equals(foreign, $"review:{reviewId}", StringComparison.Ordinal))
            log.LogWarning("Portal event {EventId} of type {Type} carries the sequence scope {Scope} instead of the review's own; its sequence is ignored and arrival order applies", c.Event.Id, c.Event.Type, foreign.Length <= 100 ? foreign : foreign[..100]);
        if (!await c.AdvanceBySequenceAsync("review", reviewId.ToString(), "reply", $"review:{reviewId}", ct)) return HandlerOutcome.Skipped("older than the applied reply state");
        if (c.Event.Type == "review.reply_removed")
        {
            if (review.ReplyId == replyId) { review.ReplyText = null; review.ReplyId = null; review.ReplyAt = null; }
            return HandlerOutcome.Applied();
        }
        // A reply that comes while the review is HIDDEN is kept (it is not shown: the public list holds published reviews only), because the order watermark has advanced and the portal will not send it again; when staff
        // restore the review the reply is there. A removed review is final and keeps nothing (M6 review L4).
        if (review.Status == ReviewStatus.Removed) return HandlerOutcome.Skipped("no reply on a removed review");
        var text = FulfilmentProjector.Cap(FulfilmentProjector.Str(d, "text"), MaxReplyLength);
        if (text is null) return HandlerOutcome.Skipped("empty reply");
        review.ReplyText = text; review.ReplyId = replyId;
        review.ReplyAt = FulfilmentProjector.Time(d, "editedAt") ?? FulfilmentProjector.Time(d, "repliedAt") ?? c.Time.GetUtcNow().ToUniversalTime();
        return HandlerOutcome.Applied();
    }
}

/// <summary>
/// Deleting an account deletes its reviews (text and name are the buyer's own words), the votes it cast and the reports it made, at once and safely repeatable: the rating figures are taken back by the
/// same atomic decrements, the portal is told to remove each review (C17 state <c>removed</c>, which also removes the seller's reply there), and the helpful counts of other reviews go down by the votes removed.
/// </summary>
public sealed class ReviewsAccountEraser(MarketplaceDb db, TimeProvider time) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var outbox = new OutboxWriter(db, time);
        var mine = await db.Reviews.AsNoTracking().Where(r => r.AccountId == accountId).ToListAsync(ct);
        foreach (var r in mine)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await ReviewService.LockAsync(db, r.Id, null, ct)) { await tx.RollbackAsync(ct); continue; }
            var cur = await db.Reviews.AsNoTracking().FirstAsync(x => x.Id == r.Id, ct);   // the status and rating NOW, not those listed before the lock
            if (cur.Status == ReviewStatus.Published) await Aggregates.ApplyAsync(db, cur, -1, ct);
            await outbox.TryEnqueueAsync(ReviewService.StateType, new ReviewService.StatePayload(r.Id, "removed", "account_deleted"), $"review-state:{r.Id:N}:removed", ct: ct);
            await db.ReviewPhotoData.Where(d => db.ReviewPhotos.Any(p => p.Id == d.PhotoId && p.ReviewId == r.Id)).ExecuteDeleteAsync(ct);
            await db.ReviewPhotos.Where(p => p.ReviewId == r.Id).ExecuteDeleteAsync(ct);
            await db.ReviewVotes.Where(v => v.ReviewId == r.Id).ExecuteDeleteAsync(ct);
            await db.ReviewReports.Where(x => x.ReviewId == r.Id).ExecuteDeleteAsync(ct);
            await db.Reviews.Where(x => x.Id == r.Id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        var votes = await db.ReviewVotes.AsNoTracking().Where(v => v.AccountId == accountId).Select(v => v.ReviewId).ToListAsync(ct);
        foreach (var reviewId in votes)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.ReviewVotes.Where(v => v.ReviewId == reviewId && v.AccountId == accountId).ExecuteDeleteAsync(ct) > 0)
                await db.Reviews.Where(x => x.Id == reviewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.HelpfulCount, x => x.HelpfulCount - 1), ct);
            await tx.CommitAsync(ct);
        }
        // The reports this account made are NOT deleted: the portal's staff may still decide on them, and the decision is matched on our report id. They are anonymised instead: the note (the buyer's words) goes,
        // the account id is replaced by a fresh one that belongs to nobody (a fixed empty id would collide on the one-report-per-buyer index when two reporters are erased), and the export finds nothing by the old id.
        foreach (var report in await db.ReviewReports.Where(x => x.AccountId == accountId).ToListAsync(ct))
        {
            report.AccountId = Guid.NewGuid();
            report.Note = null;
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>"Download my data": the buyer's reviews as they are kept (rating, text, status, the note staff wrote, dates), the helpful votes they cast and the reports they made.</summary>
public sealed class ReviewsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "reviews";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.AccountId == accountId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => db.Reviews.Any(r => r.ListingId == l.Id && r.AccountId == accountId)).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        var photos = await db.ReviewPhotos.AsNoTracking().Where(p => db.Reviews.Any(r => r.Id == p.ReviewId && r.AccountId == accountId)).OrderBy(p => p.CreatedAt).Select(p => new { p.ReviewId, p.ContentType, p.Width, p.Height, bytes = p.Size, p.CreatedAt }).ToListAsync(ct);   // the files' details, not their bytes
        var votes = await db.ReviewVotes.AsNoTracking().Where(v => v.AccountId == accountId).OrderBy(v => v.CreatedAt).Select(v => new { v.ReviewId, v.CreatedAt }).ToListAsync(ct);
        var reports = await db.ReviewReports.AsNoTracking().Where(r => r.AccountId == accountId).OrderBy(r => r.CreatedAt).Select(r => new { r.ReviewId, r.Kind, r.Reason, r.Note, r.CreatedAt, r.Decision, r.DecidedAt }).ToListAsync(ct);
        return new
        {
            reviews = reviews.Select(r => new { item = titles.GetValueOrDefault(r.ListingId), r.Rating, r.Text, shownAs = r.DisplayName, r.Status, r.CreatedAt, r.EditedAt, r.HelpfulCount, moderationNote = r.ModerationNote, sellerReply = r.ReplyText }),
            photos,
            helpfulVotes = votes,
            reportsMade = reports,
        };
    }
}
