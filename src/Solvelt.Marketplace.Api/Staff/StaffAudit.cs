using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Staff;

/// <summary>The reason every staff action carries (decision M7-03).</summary>
public static partial class StaffReason
{
    [GeneratedRegex(@"\S+@\S+\.\S+")] private static partial Regex Email();
    [GeneratedRegex(@"\d[\d\s().\-]{7,}\d")] private static partial Regex LongNumber();
    [GeneratedRegex(@"\S+\s*[\(\[{<]\s*at\s*[\)\]}>]\s*\S+|\S+\s+(at|@)\s+\S+\s+(dot|\.)\s+\w+", RegexOptions.IgnoreCase)] private static partial Regex SpelledEmail();

    /// <summary>Null when the reason is fine. BEST EFFORT: it catches an email address (also spelled "(at)"), phone and card numbers, not a name: staff are told to write a ticket number. Five to 300 characters; it is stored for years, so it must not hold what the audit log must never hold: an email address or a long number (phone, card).</summary>
    public static string? Problem(string? reason)
    {
        var r = AuthValidation.NormaliseText(reason);
        if (r.Length < 5) return "Say why, in at least 5 characters.";
        if (r.Length > 300) return "Use at most 300 characters.";
        if (AuthValidation.HasForbiddenCharacters(r)) return "That text has characters that are not allowed.";
        if (Email().IsMatch(r) || LongNumber().IsMatch(r) || SpelledEmail().IsMatch(r)) return "Do not write an email address, phone number or card number in the reason: use the ticket or order number.";
        return null;
    }

    public static string Clean(string? reason) => AuthValidation.NormaliseText(reason);
}

/// <summary>Writes the audit row in the caller's unit of work, so an action and its record commit together or not at all.</summary>
public static class StaffAudit
{
    public static StaffAuditEntry Add(MarketplaceDb db, TimeProvider time, StaffPrincipal? who, string action, string? targetType, string? targetId, string reason, object? detail = null, string? role = null)
    {
        var entry = new StaffAuditEntry
        {
            Id = Guid.NewGuid(), At = time.GetUtcNow().ToUniversalTime(), ActorId = who?.StaffId, ActorRole = who?.Role ?? role ?? "system", SessionId = who?.SessionId,
            Action = action, TargetType = targetType, TargetId = targetId, Reason = reason.Length > 300 ? reason[..300] : reason,
            Detail = detail is null ? null : Clip(JsonSerializer.Serialize(detail)),
        };
        db.StaffAudit.Add(entry);
        return entry;
    }

    private static string Clip(string s) => s.Length <= 1000 ? s : s[..1000];
}

/// <summary>The hourly cap on lookups (security review LOW 7): counted from the audit log under the staff row's lock, so parallel requests count each other.</summary>
public static class StaffLookupCap
{
    public static readonly string[] Actions = ["buyer.lookup", "buyer.lookup_miss", "order.lookup", "order.lookup_miss", "buyer.orders"];

    /// <summary>True when the member has already used their hour. Call inside the transaction that will write the audit row.</summary>
    public static async Task<bool> ReachedAsync(MarketplaceDb db, StaffPrincipal who, DateTimeOffset now, int perHour, CancellationToken ct)
    {
        await db.StaffAccounts.Where(a => a.Id == who.StaffId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct);   // the lock
        var since = now.AddHours(-1);
        return await db.StaffAudit.CountAsync(a => a.ActorId == who.StaffId && Actions.Contains(a.Action) && a.At > since, ct) >= Math.Max(1, perHour);
    }
}

/// <summary>Retention of the staff tables (PII table, decision M7-05).</summary>
public sealed class StaffMaintenance(MarketplaceDb db, TimeProvider time, Microsoft.Extensions.Options.IOptions<StaffOptions> options)
{
    public sealed record Result(int Audit, int Sessions, int Enrolments);

    public async Task<Result> PurgeAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var week = now.AddDays(-7);
        var cutoff = now.AddDays(-Math.Max(30, options.Value.AuditRetentionDays));
        var audit = await db.StaffAudit.Where(a => a.At < cutoff).ExecuteDeleteAsync(ct);
        var sessions = await db.StaffSessions.Where(s => s.AbsoluteExpiresAt < week || (s.RevokedAt != null && s.RevokedAt < week)).ExecuteDeleteAsync(ct);
        var enrolments = await db.StaffEnrolments.Where(e => (e.UsedAt != null && e.UsedAt < week) || e.ExpiresAt < week).ExecuteDeleteAsync(ct);
        return new Result(audit, sessions, enrolments);
    }
}
