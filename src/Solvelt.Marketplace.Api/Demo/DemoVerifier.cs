using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Demo;

public enum DemoVerifyStatus { Verified, InvalidCredentials, TooManyAttempts }

/// <summary>
/// Test-only "Verify this account" (M9). An account that has not verified its email cannot sign in (that is the point of verification), so there is no session to take the
/// account from: the owner proves it is theirs with the address and password, through the SAME per-address delay and per-IP budget as sign-in, so this cannot be used to guess a
/// password. It answers the same for an unknown address and a wrong password. Only reachable while <c>Demo:Enabled</c> is on.
/// </summary>
public sealed class DemoVerifier(MarketplaceDb db, TimeProvider time, PasswordService passwords, SignInGate gate, GateKeys gateKeys, ILogger<DemoVerifier> log)
{
    public async Task<(DemoVerifyStatus Status, int? RetryAfterSeconds)> VerifyAsync(string email, string password, string clientIp, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        var addressKey = gateKeys.Address(normal);
        var ipKey = gateKeys.Ip(clientIp);
        var claim = await gate.ClaimAsync(addressKey, ipKey, ct);
        if (!claim.Allowed) { log.LogWarning("Demo verify throttled for client {ClientHash}", ClientHash(clientIp)); return (DemoVerifyStatus.TooManyAttempts, claim.RetryAfterSeconds); }
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.DeletedAt == null, ct);
        if (account is null) { passwords.BurnTime(password); return Failed(clientIp); }
        if (!passwords.Verify(account.PasswordHash, password).Ok) return Failed(clientIp);
        await gate.SucceededAsync(addressKey, ipKey, ct);
        if (account.EmailVerifiedAt is null)
        {
            var now = time.GetUtcNow().ToUniversalTime();
            // Demo verify skips the mailbox proof, so it must never hand over anything the mailbox owner could have: it does NOT claim guest orders (a real verification does), and it
            // refuses an address that still has an unclaimed guest order (security review H1: an attacker could sign up as the victim's address and take the order). The check is part
            // of the one conditional update, so an order placed between a check and the write cannot slip through. The answer is the generic refusal either way: nothing here says
            // that guest orders exist for an address. Only EmailVerifiedAt (and UpdatedAt) change.
            var changed = await db.BuyerAccounts.Where(a => a.Id == account.Id && a.EmailVerifiedAt == null && a.DeletedAt == null
                    && !db.Orders.Any(o => o.AccountId == null && o.GuestEmail == normal))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
            if (changed == 0)
            {
                var stillOpen = await db.BuyerAccounts.AnyAsync(a => a.Id == account.Id && a.EmailVerifiedAt == null && a.DeletedAt == null, ct);
                if (stillOpen) { log.LogWarning("Demo verify refused for account {AccountId}: its address has unclaimed guest orders (client {ClientHash})", account.Id, ClientHash(clientIp)); return (DemoVerifyStatus.InvalidCredentials, null); }
            }
            else log.LogInformation("Demo verify: account {AccountId} marked verified at {At}", account.Id, now);
        }
        return (DemoVerifyStatus.Verified, null);
    }

    /// <summary>A failed verify is audited too (security review L5): which client, never which address (the answer stays the same for known and unknown addresses, and so does the log).</summary>
    private (DemoVerifyStatus, int?) Failed(string clientIp)
    {
        log.LogWarning("Demo verify failed for client {ClientHash}", ClientHash(clientIp));
        return (DemoVerifyStatus.InvalidCredentials, null);
    }

    /// <summary>12 hex characters of the SHA-256 of the client address: enough to tell one client from another in the log, not enough to read the address back.</summary>
    public static string ClientHash(string clientIp) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("demo-verify|" + clientIp)))[..12];
}
