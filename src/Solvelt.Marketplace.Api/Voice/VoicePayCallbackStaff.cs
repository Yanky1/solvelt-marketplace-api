using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Staff view of the pay callbacks (decision V-60): who is waiting for a call, in what state, with each callback's own trail of changes. Read only, the number MASKED (last four digits), no reveal. Only for staff
/// with <c>voice.view</c>; POST with a body so the reason is not in a URL log; every look is audited with the reason.
/// </summary>
public static class VoicePayCallbackStaff
{
    public sealed record ListBody(string? Reason, string? Which);
    public const string ListAction = "voice.pay_callbacks_view";

    public static IEndpointRouteBuilder MapStaffVoicePayCallbacks(this IEndpointRouteBuilder app)
    {
        app.StaffGroup().MapGroup("/voice/pay-callbacks").MapPost("", List).StaffCan(StaffPermissions.VoiceView);
        return app;
    }

    private static async Task<IResult> List(ListBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var which = string.Equals(body!.Which, "recent", StringComparison.OrdinalIgnoreCase) ? "recent" : "active";   // active (the default) or the last week
        var since = time.GetUtcNow().AddDays(-7);
        var q = db.VoicePayCallbacks.AsNoTracking().Where(c => which == "recent" ? c.CreatedAt >= since : (c.Status == PayCallbackStatus.Scheduled || c.Status == PayCallbackStatus.Dialing || c.Status == PayCallbackStatus.Answered || c.Status == PayCallbackStatus.Paying));
        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var events = (await db.VoicePayCallbackEvents.AsNoTracking().Where(e => ids.Contains(e.CallbackId)).OrderBy(e => e.At).ToListAsync(ct)).ToLookup(e => e.CallbackId);
        StaffAudit.Add(db, time, ctx.Staff(), ListAction, "voice_pay_callbacks", null, StaffReason.Clean(body.Reason), new { which, rows = rows.Count });
        await db.SaveChangesAsync(ct);
        return Results.Json(new
        {
            which,
            callbacks = rows.Select(c => new
            {
                c.Id, c.OrderNumber, c.Status, phone = PhoneMask.Mask(c.Phone), c.CreatedAt, c.DueAt, c.ExpiresAt, c.EndedAt,
                events = events[c.Id].Select(e => new { e.At, e.Status, e.Detail }),
            }),
        });
    }
}
