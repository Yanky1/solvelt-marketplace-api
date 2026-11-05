using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Messaging;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Reviews;

/// <summary>What a buyer can review: one entry per delivered order line or completed booking they have not reviewed yet.</summary>
public sealed record Reviewable(string Kind, Guid Id, string Label, DateTimeOffset At);

public enum ReviewOutcome { Ok, NotFound, NotEligible, AlreadyReviewed, Invalid, Locked, OwnReview, TooMany, BadFile }

/// <summary>
/// Reviews (M6). Decisions M6-04..: eligibility is read from OUR projections (a delivered seller order of this buyer, a completed booking of this buyer; demo rows never), one review per line/booking
/// (unique index), the text is checked for contact details and profanity, every figure that is shown is kept by atomic increments in the transaction of the change.
/// </summary>
public sealed class ReviewService(MarketplaceDb db, TimeProvider time)
{
    public const string UpsertType = "review.upsert", StateType = "review.state", ReportType = "review.report";
    public sealed record UpsertPayload(Guid ReviewId, string Stamp);
    public sealed record StatePayload(Guid ReviewId, string State, string? Reason);
    public sealed record ReportPayload(Guid ReviewId, string ReportId, string Kind, string TargetId, string Reason, string? Note, string ReporterRef);
    public static readonly string[] ReportReasons = ["spam", "personal_information", "offensive", "fake", "off_topic", "other"];
    private static readonly string[] Profanity = ["fuck", "shit", "bitch", "asshole", "bastard", "cunt", "dick", "slut", "whore"];

    // ---------------------------------------------------------------- eligibility
    public async Task<List<Reviewable>> EligibleAsync(Guid accountId, Guid listingId, CancellationToken ct)
    {
        var lines = await (from l in db.OrderLines.AsNoTracking()
                           join o in db.Orders.AsNoTracking() on l.OrderId equals o.Id
                           join s in db.SellerOrderStates.AsNoTracking() on l.GroupId equals s.GroupId
                           where o.AccountId == accountId && !o.IsDemo && l.ListingId == listingId && s.Status == FulfilmentStatus.Delivered && l.RefundedQuantity < l.Quantity
                                 && !db.Reviews.Any(r => r.AccountId == accountId && r.OrderLineId == l.Id)
                           orderby s.DeliveredAt descending
                           select new { l.Id, l.Title, l.VariantName, o.Number, At = s.DeliveredAt ?? s.UpdatedAt }).ToListAsync(ct);
        var bookings = await db.Bookings.AsNoTracking().Where(b => b.AccountId == accountId && !b.IsDemo && b.ListingId == listingId && b.Status == BookingStatus.Completed
                && !db.Reviews.Any(r => r.AccountId == accountId && r.BookingId == b.Id))
            .OrderByDescending(b => b.EndAt).Select(b => new { b.Id, b.Number, At = b.EndAt ?? b.StartAt }).ToListAsync(ct);
        return lines.Select(l => new Reviewable("order_line", l.Id, $"{l.Title}{(l.VariantName.Length > 0 ? ", " + l.VariantName : "")} (order {l.Number})", l.At))
            .Concat(bookings.Select(b => new Reviewable("booking", b.Id, $"Booking {b.Number}", b.At ?? DateTimeOffset.MinValue))).ToList();
    }

    // ---------------------------------------------------------------- text checks
    public static string? TextProblem(string? raw, out string text)
    {
        text = (raw ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Trim();
        if (text.Length < ReviewLimits.MinTextLength) return "Write a few words about it.";
        if (new StringInfo(text).LengthInTextElements > ReviewLimits.MaxTextLength) return $"Use at most {ReviewLimits.MaxTextLength} characters.";
        if (text.EnumerateRunes().Any(r => System.Text.Rune.IsControl(r) && r.Value != '\n') || text.Contains((char)0x2028) || text.Contains((char)0x2029)) return "The text has characters we can't use.";
        if (ContactDetails.Find(text).Count > 0) return "Please leave out phone numbers, email addresses and links: reviews are about the product or service.";
        var lower = text.ToLowerInvariant();
        if (Profanity.Any(w => System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{w}\w*\b"))) return "Please keep the review polite.";
        return null;
    }

    public static string ShortName(string displayName)
    {
        var parts = displayName.Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length == 0 ? "A customer" : parts.Length == 1 ? parts[0] : $"{parts[0]} {new StringInfo(parts[^1]).SubstringByTextElements(0, 1)}.";
        return name.Length <= ReviewLimits.MaxNameLength ? name : name[..ReviewLimits.MaxNameLength];
    }

    // ---------------------------------------------------------------- create and edit
    public async Task<(ReviewOutcome Outcome, Review? Review)> CreateAsync(Guid accountId, string? kind, Guid targetId, int rating, string text, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        Guid listingId, sellerId = Guid.Empty;
        Guid? lineId = null, bookingId = null;
        if (kind == "order_line")
        {
            var l = await (from x in db.OrderLines.AsNoTracking()
                           join o in db.Orders.AsNoTracking() on x.OrderId equals o.Id
                           join s in db.SellerOrderStates.AsNoTracking() on x.GroupId equals s.GroupId
                           where x.Id == targetId && o.AccountId == accountId
                           select new { x.ListingId, o.IsDemo, s.Status, x.RefundedQuantity, x.Quantity }).FirstOrDefaultAsync(ct);
            if (l is null) return (ReviewOutcome.NotFound, null);   // another buyer's line is a 404
            if (l.IsDemo || l.Status != FulfilmentStatus.Delivered || l.RefundedQuantity >= l.Quantity) return (ReviewOutcome.NotEligible, null);
            listingId = l.ListingId; lineId = targetId;
        }
        else if (kind == "booking")
        {
            var b = await db.Bookings.AsNoTracking().Where(x => x.Id == targetId && x.AccountId == accountId).Select(x => new { x.ListingId, x.IsDemo, x.Status }).FirstOrDefaultAsync(ct);
            if (b is null) return (ReviewOutcome.NotFound, null);
            if (b.IsDemo || b.Status != BookingStatus.Completed) return (ReviewOutcome.NotEligible, null);
            listingId = b.ListingId; bookingId = targetId;
        }
        else return (ReviewOutcome.Invalid, null);
        var listing = await db.CatalogListings.AsNoTracking().Where(x => x.Id == listingId).Select(x => new { x.SellerId, x.BrandId, x.IsDemoListing }).FirstOrDefaultAsync(ct);
        if (listing is null || listing.IsDemoListing) return (ReviewOutcome.NotEligible, null);
        sellerId = listing.SellerId;
        var name = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null).Select(a => a.DisplayName).FirstOrDefaultAsync(ct);
        if (name is null) return (ReviewOutcome.NotFound, null);
        var review = new Review
        {
            Id = Guid.NewGuid(), AccountId = accountId, ListingId = listingId, SellerId = sellerId, BrandId = listing.BrandId, OrderLineId = lineId, BookingId = bookingId,
            Rating = rating, Text = text, DisplayName = ShortName(name), Status = ReviewStatus.Published, CreatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Reviews.Add(review);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); await tx.RollbackAsync(ct); return (ReviewOutcome.AlreadyReviewed, null); }
        await Aggregates.ApplyAsync(db, review, +1, ct);
        await new OutboxWriter(db, time).TryEnqueueAsync(UpsertType, new UpsertPayload(review.Id, "c"), $"review-upsert:{review.Id:N}:c", ct: ct);
        await tx.CommitAsync(ct);
        return (ReviewOutcome.Ok, review);
    }

    public async Task<(ReviewOutcome Outcome, Review? Review)> EditAsync(Guid accountId, Guid reviewId, int rating, string text, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var r = await db.Reviews.FirstOrDefaultAsync(x => x.Id == reviewId && x.AccountId == accountId, ct);
        if (r is null) return (ReviewOutcome.NotFound, null);
        if (r.Status != ReviewStatus.Published || now - r.CreatedAt > TimeSpan.FromDays(ReviewLimits.EditDays)) return (ReviewOutcome.Locked, null);
        var oldRating = r.Rating;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        r.Rating = rating; r.Text = text; r.EditedAt = now;
        await db.SaveChangesAsync(ct);
        if (oldRating != rating)
        {
            await Aggregates.ApplyAsync(db, new Review { ListingId = r.ListingId, SellerId = r.SellerId, BrandId = r.BrandId, Rating = oldRating }, -1, ct);
            await Aggregates.ApplyAsync(db, r, +1, ct);
        }
        var stamp = now.UtcTicks.ToString(CultureInfo.InvariantCulture);
        await new OutboxWriter(db, time).TryEnqueueAsync(UpsertType, new UpsertPayload(r.Id, stamp), $"review-upsert:{r.Id:N}:{stamp}", ct: ct);
        await tx.CommitAsync(ct);
        return (ReviewOutcome.Ok, r);
    }

    // ---------------------------------------------------------------- photos
    /// <summary>
    /// Adds a photo to the buyer's own published review inside its edit window. The type is decided from the BYTES, the picture is decoded and re-encoded fresh (no metadata survives) and cut to
    /// <see cref="AttachmentSanitizer.PhotoLongSide"/> pixels; at most <see cref="ReviewLimits.MaxPhotos"/> per review: the count is taken under a row lock on the review, so parallel uploads fill exactly the free places and cannot pass the cap.
    /// </summary>
    public async Task<(ReviewOutcome Outcome, ReviewPhoto? Photo)> AddPhotoAsync(Guid accountId, Guid reviewId, byte[] input, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var r = await db.Reviews.AsNoTracking().Where(x => x.Id == reviewId && x.AccountId == accountId).Select(x => new { x.Status, x.CreatedAt }).FirstOrDefaultAsync(ct);
        if (r is null) return (ReviewOutcome.NotFound, null);
        if (r.Status != ReviewStatus.Published || now - r.CreatedAt > TimeSpan.FromDays(ReviewLimits.EditDays)) return (ReviewOutcome.Locked, null);
        if (await db.ReviewPhotos.CountAsync(p => p.ReviewId == reviewId, ct) >= ReviewLimits.MaxPhotos) return (ReviewOutcome.TooMany, null);
        var clean = AttachmentSanitizer.ProcessPhoto(input);
        if (clean.Bytes is null || clean.ContentType is null) return (ReviewOutcome.BadFile, null);
        var photo = new ReviewPhoto { Id = Guid.NewGuid(), ReviewId = reviewId, ContentType = clean.ContentType, Width = clean.Width, Height = clean.Height, Size = clean.Bytes.Length, SortOrder = 0, CreatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The review row is the lock: a no-op UPDATE takes the row lock (PostgreSQL) and queues parallel uploads for the same review, so the count below is exact (SQLite serialises writers anyway).
        // Counting after an unlocked insert made every racer see more than the cap and remove its own photo, so none was accepted.
        if (await db.Reviews.Where(x => x.Id == reviewId && x.AccountId == accountId).ExecuteUpdateAsync(s => s.SetProperty(x => x.HelpfulCount, x => x.HelpfulCount), ct) == 0) return (ReviewOutcome.NotFound, null);
        if (await db.ReviewPhotos.CountAsync(p => p.ReviewId == reviewId, ct) >= ReviewLimits.MaxPhotos) return (ReviewOutcome.TooMany, null);
        db.ReviewPhotos.Add(photo);
        db.ReviewPhotoData.Add(new ReviewPhotoData { PhotoId = photo.Id, Bytes = clean.Bytes });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ReviewOutcome.Ok, photo);
    }

    public async Task<ReviewOutcome> DeletePhotoAsync(Guid accountId, Guid reviewId, Guid photoId, CancellationToken ct)
    {
        if (!await db.Reviews.AnyAsync(x => x.Id == reviewId && x.AccountId == accountId, ct)) return ReviewOutcome.NotFound;
        await db.ReviewPhotoData.Where(d => d.PhotoId == photoId && db.ReviewPhotos.Any(p => p.Id == photoId && p.ReviewId == reviewId)).ExecuteDeleteAsync(ct);
        await db.ReviewPhotos.Where(p => p.Id == photoId && p.ReviewId == reviewId).ExecuteDeleteAsync(ct);
        return ReviewOutcome.Ok;
    }

    // ---------------------------------------------------------------- votes
    /// <summary>One helpful vote per buyer, never on their own review. The vote row and the counter change together; a parallel duplicate is a no-op (unique key).</summary>
    public async Task<ReviewOutcome> VoteAsync(Guid accountId, Guid reviewId, bool on, CancellationToken ct)
    {
        var r = await db.Reviews.AsNoTracking().Where(x => x.Id == reviewId && x.Status == ReviewStatus.Published).Select(x => new { x.AccountId }).FirstOrDefaultAsync(ct);
        if (r is null) return ReviewOutcome.NotFound;
        if (r.AccountId == accountId) return ReviewOutcome.OwnReview;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (on)
        {
            var inserted = await InsertVoteIgnoringDuplicateAsync(reviewId, accountId, ct);
            if (inserted) await db.Reviews.Where(x => x.Id == reviewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.HelpfulCount, x => x.HelpfulCount + 1), ct);
        }
        else if (await db.ReviewVotes.Where(v => v.ReviewId == reviewId && v.AccountId == accountId).ExecuteDeleteAsync(ct) > 0)
            await db.Reviews.Where(x => x.Id == reviewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.HelpfulCount, x => x.HelpfulCount - 1), ct);
        await tx.CommitAsync(ct);
        return ReviewOutcome.Ok;
    }

    private async Task<bool> InsertVoteIgnoringDuplicateAsync(Guid reviewId, Guid accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        object At = db.Database.IsSqlite() ? new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter().ConvertToProvider(now)! : now;
        var sql = db.Database.IsSqlite()
            ? "INSERT OR IGNORE INTO \"review_votes\" (\"ReviewId\", \"AccountId\", \"CreatedAt\") VALUES ({0}, {1}, {2})"
            : "INSERT INTO \"review_votes\" (\"ReviewId\", \"AccountId\", \"CreatedAt\") VALUES ({0}, {1}, {2}) ON CONFLICT (\"ReviewId\", \"AccountId\") DO NOTHING";
        return await db.Database.ExecuteSqlRawAsync(sql, [reviewId, accountId, At], ct) == 1;
    }

    // ---------------------------------------------------------------- reports
    /// <summary>A buyer reports a published review of someone else: one report per buyer, sent to the portal's moderation queue through the outbox (C2, <c>kind: review</c>).</summary>
    public async Task<ReviewOutcome> ReportAsync(Guid accountId, Guid reviewId, string reason, string? note, CancellationToken ct, bool reply = false)
    {
        var r = await db.Reviews.AsNoTracking().Where(x => x.Id == reviewId && x.Status == ReviewStatus.Published && (!reply || x.ReplyId != null)).Select(x => new { x.AccountId, x.ReplyId }).FirstOrDefaultAsync(ct);
        if (r is null) return ReviewOutcome.NotFound;
        if (r.AccountId == accountId) return ReviewOutcome.OwnReview;
        var now = time.GetUtcNow().ToUniversalTime();
        var report = new ReviewReport { Id = Guid.NewGuid(), ReviewId = reviewId, AccountId = accountId, Kind = reply ? "review_reply" : "review", TargetId = reply ? r.ReplyId : reviewId.ToString(), ReportId = $"RV-{reviewId:N}-{Guid.NewGuid():N}"[..60], Reason = reason, Note = note, CreatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ReviewReports.Add(report);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); await tx.RollbackAsync(ct); return ReviewOutcome.Ok; }   // already reported: the same answer
        await new OutboxWriter(db, time).TryEnqueueAsync(ReportType, new ReportPayload(reviewId, report.ReportId, report.Kind, reply ? r.ReplyId! : reviewId.ToString(), reason, note, ReporterRef(accountId)), $"review-report:{report.ReportId}", ct: ct);
        await tx.CommitAsync(ct);
        return ReviewOutcome.Ok;
    }

    /// <summary>The portal's opaque reference for a reporter: a hash of the account id, never a name or an address.</summary>
    public static string ReporterRef(Guid accountId) => "buyer-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("reporter|" + accountId.ToString("N"))))[..24];
}

/// <summary>Rating counts per listing, seller and brand, changed by atomic increments (no read-modify-write) inside the transaction of the review change.</summary>
public static class Aggregates
{
    public static async Task ApplyAsync(MarketplaceDb db, Review r, int sign, CancellationToken ct)
    {
        foreach (var (scope, id) in new[] { ("listing", r.ListingId), ("seller", r.SellerId), ("brand", r.BrandId) })
        {
            var sql = db.Database.IsSqlite()
                ? "INSERT OR IGNORE INTO \"rating_aggregates\" (\"Scope\", \"ScopeId\", \"Count\", \"Sum\", \"Stars1\", \"Stars2\", \"Stars3\", \"Stars4\", \"Stars5\") VALUES ({0}, {1}, 0, 0, 0, 0, 0, 0, 0)"
                : "INSERT INTO \"rating_aggregates\" (\"Scope\", \"ScopeId\", \"Count\", \"Sum\", \"Stars1\", \"Stars2\", \"Stars3\", \"Stars4\", \"Stars5\") VALUES ({0}, {1}, 0, 0, 0, 0, 0, 0, 0) ON CONFLICT (\"Scope\", \"ScopeId\") DO NOTHING";
            await db.Database.ExecuteSqlRawAsync(sql, [scope, id], ct);
            var rating = r.Rating;
            await db.RatingAggregates.Where(a => a.Scope == scope && a.ScopeId == id).ExecuteUpdateAsync(s =>
            {
                s.SetProperty(a => a.Count, a => a.Count + sign).SetProperty(a => a.Sum, a => a.Sum + sign * rating);
                switch (rating)
                {
                    case 1: s.SetProperty(a => a.Stars1, a => a.Stars1 + sign); break;
                    case 2: s.SetProperty(a => a.Stars2, a => a.Stars2 + sign); break;
                    case 3: s.SetProperty(a => a.Stars3, a => a.Stars3 + sign); break;
                    case 4: s.SetProperty(a => a.Stars4, a => a.Stars4 + sign); break;
                    default: s.SetProperty(a => a.Stars5, a => a.Stars5 + sign); break;
                }
            }, ct);
        }
    }

    public static object Summary(RatingAggregate? a) => new
    {
        count = a?.Count ?? 0,
        average = a is null || a.Count == 0 ? (double?)null : Math.Round((double)a.Sum / a.Count, 1),
        distribution = new[] { a?.Stars1 ?? 0, a?.Stars2 ?? 0, a?.Stars3 ?? 0, a?.Stars4 ?? 0, a?.Stars5 ?? 0 },
    };
}
