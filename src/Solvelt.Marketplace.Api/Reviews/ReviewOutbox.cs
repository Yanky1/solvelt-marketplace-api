using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Messaging;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Reviews;

/// <summary>
/// Sends a review to the portal so the seller sees it (C17: <c>POST /reviews</c>, idempotent on the review id; a newer <c>editedAt</c> updates). The review is read at SEND time, so a late message
/// carries the latest text; a review that is no longer published is sent as a state change instead, a deleted one is nothing to send. Limits are checked before sending (5000 / 60).
/// </summary>
public sealed class ReviewUpsertOutboxHandler(MarketplaceDb db, ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [ReviewService.UpsertType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        ReviewService.UpsertPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<ReviewService.UpsertPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var r = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.ReviewId, ct);
        if (r is null) return OutboxResult.Delivered();
        if (r.Status != ReviewStatus.Published) return OutboxResult.Delivered();   // staff already know (they decided it); the state message covers our own changes
        if (r.Text.Length > 5000 || r.DisplayName.Length > 60) return OutboxResult.Dead("over_limit");
        try
        {
            var res = await portal.UpsertReviewAsync(new ReviewUpsertBody(r.Id.ToString(), r.ListingId, r.SellerId, r.Rating, r.Text, r.DisplayName, r.CreatedAt, r.EditedAt), ct);
            if (res.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
            return PortalOutcome.From(res);
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}

public sealed class ReviewStateOutboxHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [ReviewService.StateType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        ReviewService.StatePayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<ReviewService.StatePayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || p.State is not ("visible" or "hidden" or "removed")) return OutboxResult.Dead("invalid_payload");
        try
        {
            var res = await portal.SetReviewStateAsync(p.ReviewId.ToString(), new ReviewStateBody(p.State, p.Reason), ct);
            if (res.Status == 404) return OutboxResult.Delivered();   // the portal never had it (its creation failed or was refused): nothing to change
            if (res.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
            return PortalOutcome.From(res);
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}

/// <summary>A buyer's report of a review, sent to the portal's moderation queue (C2, <c>kind: review</c> or <c>review_reply</c>, idempotent on the report id).</summary>
public sealed class ReviewReportOutboxHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [ReviewService.ReportType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        ReviewService.ReportPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<ReviewService.ReportPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        try
        {
            var res = await portal.ReportContentAsync(new ContentReportBody(p.ReportId, p.Kind, p.TargetId, p.Reason, p.Note, p.ReporterRef), ct);
            if (res.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
            if (res.Status == 409 && res.Error?.Code == "already_resolved") return OutboxResult.Delivered();
            return PortalOutcome.From(res);
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}
