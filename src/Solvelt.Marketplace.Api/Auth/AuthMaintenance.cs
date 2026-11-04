using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Auth;

/// <summary>Retention for the auth tables (PII table): old sessions and links, placeholder rows, and the sign-in gate's memory.</summary>
public sealed class AuthMaintenance(MarketplaceDb db, TimeProvider time, Outbox.OutboxWriter outbox)
{
    public sealed record Result(int Sessions, int Tokens, int Decoys, int GateRows, int Redactions = 0);

    public async Task<Result> PurgeAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var week = now.AddDays(-7);
        var hour = now.AddHours(-1);
        // a week after they ended, expired and revoked sessions and used or expired links are gone
        var sessions = await db.BuyerSessions.Where(s => s.AbsoluteExpiresAt < week || (s.RevokedAt != null && s.RevokedAt < week)).ExecuteDeleteAsync(ct);
        var tokens = await db.EmailTokens.Where(t => t.Purpose != EmailTokenPurpose.Decoy && ((t.UsedAt != null && t.UsedAt < week) || t.ExpiresAt < week)).ExecuteDeleteAsync(ct);
        // placeholder rows (decision M1-65) live for an hour
        var decoys = await db.EmailTokens.Where(t => t.Purpose == EmailTokenPurpose.Decoy && t.CreatedAt < hour).ExecuteDeleteAsync(ct);
        decoys += await db.OutboxMessages.Where(m => m.Type == AuthMailHandler.DecoyType && m.CreatedAt < hour).ExecuteDeleteAsync(ct);
        // the sign-in gate forgets an address after two quiet days and a client after its hour is over (the budget window is an hour)
        var gate = await db.SignInAttempts.Where(a => a.UpdatedAt < now.AddHours(-48)).ExecuteDeleteAsync(ct);
        gate += await db.SignInIpBudgets.Where(b => b.WindowStart < now.AddHours(-2)).ExecuteDeleteAsync(ct);
        // a deleted account whose redaction has not happened within the hour (its message was lost or keeps failing) is queued again, at most once an hour
        var stuck = await db.BuyerAccounts.AsNoTracking().Where(a => a.DeletedAt != null && a.DeletedAt < hour && a.RedactedAt == null).Select(a => a.Id).Take(200).ToListAsync(ct);
        var queued = 0;
        foreach (var id in stuck)
            if (await outbox.TryEnqueueAsync(Account.AccountRedactionHandler.Type, new Account.AccountRedactionPayload(id), $"redact-sweep:{id:N}:{now.ToUnixTimeSeconds() / 3600}", ct: ct)) queued++;
        return new Result(sessions, tokens, decoys, gate, queued);
    }
}
