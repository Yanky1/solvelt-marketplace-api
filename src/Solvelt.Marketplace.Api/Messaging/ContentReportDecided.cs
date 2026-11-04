using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// The ONE handler of <c>content.report_decided</c> (the registry allows one per event type). The portal sends it for reviews, listings and messages (CATALOG.md §6); this class switches on
/// <c>kind</c>. <c>message</c> is handled here (M5); <c>review</c> (and <c>listing</c>, which the portal acts on itself) are the reviews workstream's: it EXTENDS the switch below with a
/// <see cref="ReviewDecision"/> delegate instead of registering a second handler (decision M5-13). Matching: the report id must be the one WE made for that message
/// (<c>MR-&lt;target&gt;</c>) and the message must be one we reported, never the target id alone.
/// </summary>
public sealed class ContentReportDecidedHandler : IProjectionHandler
{
    public const string Type = "content.report_decided";
    public IReadOnlyCollection<string> EventTypes { get; } = [Type];

    /// <summary>The seam for M6: set (or subclass) to handle <c>kind: "review"</c>. Null means reviews are not handled yet and the event is skipped.</summary>
    public Func<ProjectionContext, CancellationToken, Task<HandlerOutcome>>? ReviewDecision { get; set; }

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var d = c.Event.Data;
        switch (FulfilmentProjector.Str(d, "kind"))
        {
            case "message": return await MessageAsync(c, ct);
            case "review" when ReviewDecision is not null: return await ReviewDecision(c, ct);
            case "review": return HandlerOutcome.Skipped("review decisions are handled by the reviews workstream");
            default: return HandlerOutcome.Skipped("not a message report");
        }
    }

    private static async Task<HandlerOutcome> MessageAsync(ProjectionContext c, CancellationToken ct)
    {
        var d = c.Event.Data;
        var reportId = FulfilmentProjector.Str(d, "reportId");
        var target = FulfilmentProjector.Str(d, "targetId");
        var decision = FulfilmentProjector.Str(d, "decision");
        if (string.IsNullOrWhiteSpace(target) || reportId != "MR-" + target) return HandlerOutcome.Skipped("not a report we made");
        var m = await c.Db.ThreadMessages.Include(x => x.Attachments).FirstOrDefaultAsync(x => (x.MessageKey == target || x.PortalMessageId == target) && x.ReportedAt != null, ct);
        if (m is null) return HandlerOutcome.Skipped("unknown reported message");
        if (decision is "hidden" or "removed")
        {
            var now = c.Time.GetUtcNow().ToUniversalTime();
            m.HiddenAt ??= now;
            m.ModerationNote = FulfilmentProjector.Cap(FulfilmentProjector.Str(d, "reasonShownToUser"), 300) ?? m.ModerationNote;
            var ids = m.Attachments.Select(a => a.Id).ToList();
            await c.Db.MessageAttachmentData.Where(x => ids.Contains(x.AttachmentId)).ExecuteDeleteAsync(ct);   // the files go with it
            foreach (var a in m.Attachments) a.Status = AttachmentStatuses.Rejected;
        }
        return HandlerOutcome.Applied();   // dismissed: the message stays
    }
}
