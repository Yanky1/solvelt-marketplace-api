using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Messaging;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Reviews;

public sealed record QuestionBody(Guid? ListingId, string? Text);

/// <summary>
/// Product questions (M6, portal C1, CATALOG.md §5). A signed-in buyer asks about a visible listing (1-1000 characters, no contact details); the question is stored and sent to the seller's Q&amp;A inbox through the
/// outbox with OUR question id as the idempotency key (so a retry or a parallel delivery is one question). Throttled per buyer (per listing and per day) before anything is stored. See DECISIONS M6-06.
/// </summary>
public static class QuestionEndpoints
{
    public const string SendType = "question.send";
    public const int MaxTextLength = 1000, PerListingPerDay = 3, PerDay = 10;
    public sealed record SendPayload(string QuestionId);

    public static IEndpointRouteBuilder MapQuestions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/questions", Ask).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        app.MapGet("/api/v1/account/questions", Mine).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    private static IResult Private(IResult r, HttpContext ctx) { ctx.Response.Headers.CacheControl = "private, no-store"; return r; }

    public static string? TextProblem(string? raw, out string text)
    {
        text = (raw ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return "Write your question.";
        if (new StringInfo(text).LengthInTextElements > MaxTextLength) return $"Use at most {MaxTextLength} characters.";
        if (text.EnumerateRunes().Any(r => Rune.IsControl(r) && r.Value != '\n') || text.Contains((char)0x2028) || text.Contains((char)0x2029)) return "The text has characters we can't use.";
        if (ContactDetails.Find(text).Count > 0) return "Please leave out phone numbers, email addresses and links: sellers answer questions here.";
        return null;
    }

    private static async Task<IResult> Ask(QuestionBody? body, HttpContext ctx, MarketplaceDb db, OutboxWriter outbox, TimeProvider time, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (body?.ListingId is null || body.ListingId == Guid.Empty) errors["listingId"] = "Choose the item.";
        if (TextProblem(body?.Text, out var text) is { } tp) errors["text"] = tp;
        if (errors.Count > 0) return Private(Problems.Validation(errors), ctx);
        var accountId = ctx.Buyer().AccountId;
        var listingId = body!.ListingId!.Value;
        if (!await CatalogEndpoints.Visible(db).AnyAsync(l => l.Id == listingId && !l.IsDemoListing, ct)) return Private(Problems.NotFound("That item is not available."), ctx);   // a demo listing never reaches the portal
        var now = time.GetUtcNow().ToUniversalTime();
        var since = now.AddDays(-1);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The throttle counts and then inserts, so parallel requests of one buyer would all count the same few and all insert. The buyer's account row is the lock (a no-op UPDATE: PostgreSQL queues the account's other
        // writers behind it, SQLite serialises writers anyway): the count below is taken under it and the insert commits before the next request counts (M6 review L3).
        if (await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(a => a.SetProperty(x => x.DisplayName, x => x.DisplayName), ct) == 0) return Private(Problems.NotFound(), ctx);
        if (await db.ProductQuestions.CountAsync(q => q.AccountId == accountId && q.CreatedAt > since, ct) >= PerDay
            || await db.ProductQuestions.CountAsync(q => q.AccountId == accountId && q.ListingId == listingId && q.CreatedAt > since, ct) >= PerListingPerDay)
            return Private(Problems.Json(429, "too_many_questions", "You have asked a lot of questions today. Please try again tomorrow."), ctx);
        var name = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null).Select(a => a.DisplayName).FirstOrDefaultAsync(ct);
        if (name is null) return Private(Problems.NotFound(), ctx);
        var q = new ProductQuestion { Id = "Q-" + Guid.NewGuid().ToString("N"), AccountId = accountId, ListingId = listingId, Text = text, DisplayName = ReviewService.ShortName(name), Status = QuestionStatus.Sending, CreatedAt = now };
        db.ProductQuestions.Add(q);
        outbox.Enqueue(SendType, new SendPayload(q.Id), "question-send:" + q.Id);
        await db.SaveChangesAsync(ct);   // the question and its message commit together
        await tx.CommitAsync(ct);
        return Private(Results.Json(new { id = q.Id, status = q.Status, createdAt = q.CreatedAt }, statusCode: 202), ctx);
    }

    private static async Task<IResult> Mine(HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        var id = ctx.Buyer().AccountId;
        var rows = await db.ProductQuestions.AsNoTracking().Where(q => q.AccountId == id).OrderByDescending(q => q.CreatedAt).ThenBy(q => q.Id).Take(100).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => rows.Select(r => r.ListingId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new { l.Title, l.Slug }, ct);
        return Private(Results.Json(new { questions = rows.Select(q => new { id = q.Id, text = q.Text, status = q.Status, rejectedReason = q.RejectedReason, createdAt = q.CreatedAt, listing = titles.GetValueOrDefault(q.ListingId) }) }), ctx);
    }
}

/// <summary>Sends a stored question to the portal (<c>POST /products/{id}/questions</c>, idempotent on <c>questionId</c>: a repeat is a 200 duplicate). A listing the portal no longer shows (404) or a refused text (422) ends the question as rejected; 429 and outages are retried.</summary>
public sealed class QuestionSendOutboxHandler(MarketplaceDb db, ISignedPortalClient portal, TimeProvider time) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [QuestionEndpoints.SendType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        QuestionEndpoints.SendPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<QuestionEndpoints.SendPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var q = await db.ProductQuestions.FirstOrDefaultAsync(x => x.Id == p.QuestionId, ct);
        if (q is null || q.Status != QuestionStatus.Sending) return OutboxResult.Delivered();   // deleted with the account, or already settled
        try
        {
            var res = await portal.AskQuestionAsync(q.ListingId, new PortalQuestionBody(q.Id, q.Text, q.DisplayName), ct);
            if (res.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
            if (res.Status is 404 or 422 or 409)
            {
                q.Status = QuestionStatus.Rejected; q.RejectedReason = res.Status == 404 ? "listing_unavailable" : "invalid_question";
                await db.SaveChangesAsync(ct);
                return OutboxResult.Delivered();   // permanent for this question: nothing to retry
            }
            var outcome = PortalOutcome.From(res);
            if (outcome.Outcome == OutboxOutcome.Delivered) { q.Status = QuestionStatus.Sent; q.SentAt = time.GetUtcNow().ToUniversalTime(); await db.SaveChangesAsync(ct); }
            return outcome;
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}

public sealed class QuestionsAccountEraser(MarketplaceDb db) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct) => await db.ProductQuestions.Where(q => q.AccountId == accountId).ExecuteDeleteAsync(ct);
}

public sealed class QuestionsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "questions";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.ProductQuestions.AsNoTracking().Where(q => q.AccountId == accountId).OrderBy(q => q.CreatedAt).ThenBy(q => q.Id).ToListAsync(ct);
        var titles = await db.CatalogListings.AsNoTracking().Where(l => db.ProductQuestions.Any(q => q.ListingId == l.Id && q.AccountId == accountId)).ToDictionaryAsync(l => l.Id, l => l.Title, ct);
        return new { questions = rows.Select(q => new { item = titles.GetValueOrDefault(q.ListingId), q.Text, shownAs = q.DisplayName, q.Status, q.CreatedAt, q.SentAt }) };
    }
}
