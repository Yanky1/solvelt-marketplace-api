using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Staff access to the owner's test-call diagnostics (decision V-39): one call's turns, or a CSV of every kept turn. Only for staff with <c>voice.view</c>, never on the public API, and every read is audited with the
/// reason the member gave. POST with a body, not a query string, so the reason is not in a URL log (the same rule as the buyer lookups). The diagnostics feature being off does not hide old rows: they stay readable
/// until the retention deletes them.
/// </summary>
public static class VoiceDiagnosticsEndpoints
{
    public sealed record ExportBody(string? Reason, int? Hours);

    public static IEndpointRouteBuilder MapStaffVoiceDiagnostics(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup().MapGroup("/voice/diagnostics");
        g.MapPost("/calls/{callId:guid}", Call).StaffCan(StaffPermissions.VoiceView);
        g.MapPost("/export", Export).StaffCan(StaffPermissions.VoiceView);
        return app;
    }

    private static async Task<IResult> Call(Guid callId, StaffEndpoints.ReasonBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var turns = await db.VoiceDiagnosticTurns.AsNoTracking().Where(t => t.CallId == callId).OrderBy(t => t.Turn).ToListAsync(ct);
        StaffAudit.Add(db, time, ctx.Staff(), "voice.diagnostics_view", "voice_call", callId.ToString("D"), StaffReason.Clean(body!.Reason), new { turns = turns.Count });
        await db.SaveChangesAsync(ct);
        return Results.Json(new { callId, turns = turns.Select(Shape) });
    }

    private static async Task<IResult> Export(ExportBody? body, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var hours = Math.Clamp(body!.Hours ?? DiagnosticsOptions.MaxRetentionHours, 1, DiagnosticsOptions.MaxRetentionHours);
        var since = time.GetUtcNow().AddHours(-hours);
        var rows = await db.VoiceDiagnosticTurns.AsNoTracking().Where(t => t.At >= since).OrderBy(t => t.At).ThenBy(t => t.Turn).Take(20000).ToListAsync(ct);
        StaffAudit.Add(db, time, ctx.Staff(), "voice.diagnostics_export", "voice_diagnostics", null, StaffReason.Clean(body.Reason), new { hours, rows = rows.Count });
        await db.SaveChangesAsync(ct);
        var sb = new StringBuilder("call_id,turn,at,option,recogniser,text,engines,language,confidence,speech_end_to_text_ms,speech_end_to_first_audio_ms,endpointing_reason\r\n");
        foreach (var t in rows)
            sb.Append(string.Join(',', Csv(t.CallId.ToString("D")), t.Turn, Csv(t.At.ToString("O")), Csv(t.Option), Csv(t.Recogniser), Csv(t.Text), Csv(t.Engines), Csv(t.Language),
                t.Confidence?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), t.SpeechEndToTextMs, t.SpeechEndToFirstAudioMs, Csv(t.EndpointingReason))).Append("\r\n");
        return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
    }

    private static object Shape(VoiceDiagnosticTurn t) => new { t.Turn, t.At, t.Option, t.Recogniser, t.Text, engines = EnginesOf(t.Engines), t.Language, t.Confidence, t.SpeechEndToTextMs, t.SpeechEndToFirstAudioMs, t.EndpointingReason };

    /// <summary>The stored engines cell as JSON; a cell that does not parse (it should never happen) is returned as text, never as an error.</summary>
    private static object? EnginesOf(string? engines)
    {
        if (engines is null) return null;
        try { return System.Text.Json.JsonDocument.Parse(engines).RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return engines; }
    }

    /// <summary>A CSV cell: quoted, quotes doubled, and a leading = + - @ tab or CR made harmless (a spreadsheet would run it as a formula).</summary>
    internal static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
