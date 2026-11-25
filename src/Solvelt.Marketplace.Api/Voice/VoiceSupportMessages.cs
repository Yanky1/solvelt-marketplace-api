using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Leaves a text message for staff when no person can be reached by phone (decision V-55). Offered by the model when <c>transfer_to_person</c> says nobody is available; the SERVER refuses while a person IS reachable
/// (so the tool cannot be used to skip the transfer), for a caller nobody has verified (staff could not answer an unknown caller), over 500 characters, for text that looks like a card number, and after three messages in one
/// call. The message is PERSONAL DATA: PII registry module "voice-support-messages", an eraser, an export section, a shorter retention, staff access audited. Nothing of the text is logged.
/// </summary>
public sealed class LeaveSupportMessageTool(MarketplaceDb db, IOptions<VoiceOptions> options, TimeProvider time) : IVoiceTool
{
    public const int MaxChars = 500, MaxPerCall = 3;
    public string Name => "leave_support_message";
    public JsonObject Definition => ToolKit.Def(Name, "Take a short text message for our team when no person can be reached (use it after transfer_to_person says nobody is available, and only when the caller wants to leave one). Ask what they want to say, keep it short, and call this with their words. Never take a card number or a PIN.",
        new JsonObject { ["message"] = ToolKit.Prop("string", "What the caller wants the team to know and what they need, in their own words, at most 500 characters.") }, "message");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct) => RunCoreAsync(ctx, args, ct);

    private async Task<VoiceToolResult> RunCoreAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (VoiceTransfer.IsAvailable(options.Value, time.GetUtcNow())) return ToolKit.Error("person_available", "A person can be reached now: offer to connect them with transfer_to_person instead.");
        var account = OrderAccess.IsAccount(s);
        var guest = OrderAccess.IsGuest(s);
        if (!account && !guest) return ToolKit.Error("verify_first", "The team can only answer someone who is verified: say it is a quick check, then verify by text code (identify_caller). If they would rather not, they can order or write on the web site.");
        var text = Clean(ToolKit.Str(args, "message"));
        if (text is null) return ToolKit.Error("empty", "Ask what they would like to tell the team.");
        if (text.Length > MaxChars) return ToolKit.Error("too_long", $"Ask them to say it shorter: at most about {MaxChars} characters, a few sentences.");
        if (LooksLikeCardOrPin(text)) return ToolKit.Error("not_this", "Do not take card numbers or PINs: tell the caller never to say them, and ask them to leave the message without.");
        if (s.CountSupportMessage() > MaxPerCall) return ToolKit.Error("too_many", "Enough messages were left in this call: say the team will read them, and offer the web site for more.");
        db.VoiceSupportMessages.Add(new VoiceSupportMessage { Id = Guid.NewGuid(), AccountId = account ? s.AccountId : null, Phone = s.VerifiedPhone, Text = text, CreatedAt = time.GetUtcNow().ToUniversalTime() });
        await db.SaveChangesAsync(ct);
        return new("""{"status":"saved","instruction":"Tell the caller briefly that the team has the message and will get back to them. Do not promise a time."}""");
    }

    /// <summary>Plain text only: control characters and runs of spaces go, the ends are trimmed. Null when nothing is left.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var plain = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        plain = string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return plain.Length == 0 ? null : plain;
    }

    /// <summary>Twelve digits or more (spaces and dashes aside) in a row look like a card number; a message must never carry one.</summary>
    public static bool LooksLikeCardOrPin(string text)
    {
        var run = 0;
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { if (++run >= 12) return true; }
            else if (c is not (' ' or '-')) run = 0;
        }
        return false;
    }
}

/// <summary>Deletes the messages an account's holder left (module "voice-support-messages"). A message left by a caller with only a verified number has no account: the retention deletes it. Idempotent; runs inside M1's erasure transaction.</summary>
public sealed class VoiceSupportMessageEraser(MarketplaceDb db) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct) => await db.VoiceSupportMessages.Where(m => m.AccountId == accountId).ExecuteDeleteAsync(ct);
}

/// <summary>The messages the buyer left by phone, in their data export (section "support-messages").</summary>
public sealed class VoiceSupportMessageExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "support-messages";
    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
        => (await db.VoiceSupportMessages.AsNoTracking().Where(m => m.AccountId == accountId).OrderBy(m => m.CreatedAt).ToListAsync(ct)).Select(m => new { m.Text, m.CreatedAt, m.Status });
}

/// <summary>
/// Staff access to the messages callers left (decision V-55): <c>buyers.view</c> to list (the number masked) and mark one handled, <c>buyers.reveal</c> for the full number. POST with a body, a reason on every action, every action
/// audited, and the listing counts against the hourly lookup cap like the buyer lookups.
/// </summary>
public static class VoiceSupportMessageEndpoints
{
    public sealed record ListBody(string? Reason, string? Status);
    public const string ListAction = "voice.support_messages_list", RevealAction = "voice.support_message_reveal", HandledAction = "voice.support_message_handled";

    public static IEndpointRouteBuilder MapStaffVoiceSupportMessages(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup().MapGroup("/voice/support-messages");
        g.MapPost("", List).StaffCan(StaffPermissions.BuyersView);
        g.MapPost("/{id:guid}/handled", Handled).StaffCan(StaffPermissions.BuyersView);
        g.MapPost("/{id:guid}/reveal", Reveal).StaffCan(StaffPermissions.BuyersReveal);
        return app;
    }

    /// <summary>"+15551234567" is shown as "***4567".</summary>
    public static string? MaskPhone(string? phone) => string.IsNullOrEmpty(phone) ? null : phone.Length <= 4 ? "***" : "***" + phone[^4..];

    private static async Task<IResult> List(ListBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var status = body!.Status is null or "new" ? "new" : body.Status == "handled" ? "handled" : body.Status == "all" ? "all" : null;
        if (status is null) return Problems.BadRequest("status_invalid", "status must be new, handled or all.");
        var who = ctx.Staff();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        if (await StaffLookupCap.ReachedAsync(db, who, now, o.Value.LookupsPerHour, ct)) return Problems.Json(429, "lookup_limit", "Too many lookups this hour.");
        var q = db.VoiceSupportMessages.AsNoTracking().AsQueryable();
        if (status != "all") q = q.Where(m => m.Status == status);
        var rows = (await q.ToListAsync(ct)).OrderByDescending(m => m.CreatedAt).Take(50).ToList();   // ordered in memory: SQLite cannot order a DateTimeOffset in SQL
        StaffAudit.Add(db, time, who, ListAction, "voice_support_messages", null, StaffReason.Clean(body.Reason), new { status, count = rows.Count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Json(new { messages = rows.Select(m => new { id = m.Id, createdAt = m.CreatedAt, status = m.Status, accountId = m.AccountId, phone = MaskPhone(m.Phone), text = m.Text, handledAt = m.HandledAt }) });
    }

    private static async Task<IResult> Handled(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var who = ctx.Staff();
        var now = time.GetUtcNow().ToUniversalTime();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.VoiceSupportMessages.Where(m => m.Id == id && m.Status == "new").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "handled").SetProperty(m => m.HandledAt, now).SetProperty(m => m.HandledBy, who.StaffId), ct);
        if (changed == 0) return await db.VoiceSupportMessages.AnyAsync(m => m.Id == id, ct) ? Problems.Json(409, "already_handled", "Already handled.") : Problems.NotFound();
        StaffAudit.Add(db, time, who, HandledAction, "voice_support_message", id.ToString("D"), StaffReason.Clean(body!.Reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Reveal(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var m = await db.VoiceSupportMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Problems.NotFound();
        StaffAudit.Add(db, time, ctx.Staff(), RevealAction, "voice_support_message", id.ToString("D"), StaffReason.Clean(body!.Reason), new { field = "phone" });   // saved in the same transaction as the read
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Json(new { id = m.Id, phone = m.Phone });
    }
}
