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
public sealed class DemoVerifier(MarketplaceDb db, TimeProvider time, PasswordService passwords, SignInGate gate, GateKeys gateKeys, GuestClaims guestClaims, ILogger<DemoVerifier> log)
{
    public async Task<(DemoVerifyStatus Status, int? RetryAfterSeconds)> VerifyAsync(string email, string password, string clientIp, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        var addressKey = gateKeys.Address(normal);
        var ipKey = gateKeys.Ip(clientIp);
        var claim = await gate.ClaimAsync(addressKey, ipKey, ct);
        if (!claim.Allowed) return (DemoVerifyStatus.TooManyAttempts, claim.RetryAfterSeconds);
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.DeletedAt == null, ct);
        if (account is null) { passwords.BurnTime(password); return (DemoVerifyStatus.InvalidCredentials, null); }
        if (!passwords.Verify(account.PasswordHash, password).Ok) return (DemoVerifyStatus.InvalidCredentials, null);
        await gate.SucceededAsync(addressKey, ipKey, ct);
        if (account.EmailVerifiedAt is null)
        {
            var now = time.GetUtcNow().ToUniversalTime();
            await db.BuyerAccounts.Where(a => a.Id == account.Id && a.EmailVerifiedAt == null && a.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
            await guestClaims.ClaimAsync(account.Id, normal, ct);   // as a real verification does: guest orders placed with the address become this account's
            log.LogInformation("Demo verify: account {AccountId} marked verified at {At}", account.Id, now);
        }
        return (DemoVerifyStatus.Verified, null);
    }
}
