using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Auth;

public sealed record BuyerPrincipal(Guid AccountId, Guid SessionId, string DisplayName, string Email, DateTimeOffset AuthenticatedAt);
public sealed record ResetOutcome(bool Ok, string? PasswordProblem = null);
public enum SignInStatus { Ok, InvalidCredentials, EmailNotVerified, TooManyAttempts }
public sealed record SignInResult(SignInStatus Status, string? Token = null, BuyerAccount? Account = null, int? RetryAfterSeconds = null);

/// <summary>
/// Buyer authentication. Every answer that could reveal whether an address has an account is the same either way (sign-up, resend, forgot): the
/// difference is only in what we email. Unknown and known accounts share rate limits and the same time cost.
/// </summary>
public sealed class AuthService(MarketplaceDb db, TimeProvider time, IOptions<AuthOptions> authOptions, IOptions<MarketplaceOptions> marketplace,
    OutboxWriter outbox, AttemptLimiter limiter, PasswordService passwords, LinkTokens links, SignInGate gate, GateKeys gateKeys, GuestClaims guestClaims)
{
    private AuthOptions O => authOptions.Value;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    // ---------- sign-up ----------

    /// <summary>Always completes the same way for the caller. Returns normally for a new, an unverified and a verified address.</summary>
    public async Task SignUpAsync(string email, string password, string displayName, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        var name = displayName.Trim();
        // Mail-bombing guard: past 3 sign-ups an hour for one address nothing more is sent (and the caller still sees the same answer).
        if (!limiter.TryAcquire("signup-email", normal, 3, Hour)) { passwords.BurnTime(password); return; }

        var existing = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal, ct);
        var now = Now;
        if (existing is { EmailVerifiedAt: not null })
        {
            passwords.BurnTime(password);
            outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.Exists, existing.Id), $"signup-exists:{existing.Id:N}:{now.ToUnixTimeSeconds() / 3600}");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (existing is not null)
        {
            // An address that is still waiting for verification: NOTHING about the account changes, neither the password nor the name (decision O-16 a).
            // It only gets a fresh link (older ones die). Whoever registered it first keeps the password they chose, and verifying needs that password (M1-64),
            // so nobody can take over an address by signing up for it twice; the real owner who does not know the password uses "forgot password".
            passwords.BurnTime(password);   // the same time cost as hashing a new password
            await QueueVerificationAsync(existing, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var hash = passwords.Hash(password);
        var account = new BuyerAccount
        {
            Id = Guid.NewGuid(), Email = normal, DisplayName = name, PasswordHash = hash, CreatedAt = now, UpdatedAt = now,
            RelayEmail = RelayIdentity.NewRelayEmail(marketplace.Value.RelayDomain ?? DevDefaults.RelayDomain),
        };
        db.BuyerAccounts.Add(account);
        await QueueVerificationAsync(account, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two sign-ups for one new address at the same moment: the other one won. Nothing to do; the caller sees the same answer.
            db.ChangeTracker.Clear();
        }
    }

    // ---------- email verification ----------

    /// <summary>
    /// Verifies the address. Needs the link AND the account's current password (decision O-16 b): a wrong password is a generic failure and the link is not used up.
    /// The password check goes through the sign-in gate like every other password check.
    /// </summary>
    public async Task<bool> VerifyEmailAsync(string token, string password, string clientIp, CancellationToken ct)
    {
        var now = Now;
        if (links.Parse(EmailTokenPurpose.Verify, token) is not { } tokenId) { passwords.BurnTime(password); return false; }
        var row = await (from t in db.EmailTokens.AsNoTracking()
                         join a in db.BuyerAccounts.AsNoTracking() on t.AccountId equals a.Id
                         where t.Id == tokenId && t.Purpose == EmailTokenPurpose.Verify && t.UsedAt == null && t.ExpiresAt > now && a.DeletedAt == null && a.Email != null
                         select new { a.Id, a.Email, a.PasswordHash }).FirstOrDefaultAsync(ct);
        if (row is null) { passwords.BurnTime(password); return false; }
        var addressKey = gateKeys.Address(row.Email!);
        var ipKey = gateKeys.Ip(clientIp);
        if (!(await gate.ClaimAsync(addressKey, ipKey, ct)).Allowed) return false;   // generic: a delay is not told apart from a wrong password here
        if (!passwords.Verify(row.PasswordHash, password).Ok) return false;
        var accountId = await ConsumeTokenAsync(EmailTokenPurpose.Verify, token, ct);   // the atomic claim: two clicks use it once
        if (accountId is null) return false;
        await db.BuyerAccounts.Where(a => a.Id == accountId && a.EmailVerifiedAt == null && a.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
        await gate.SucceededAsync(addressKey, ipKey, ct);
        await guestClaims.ClaimAsync(row.Id, row.Email!, ct);   // the address is now proven: guest orders placed with it become this account's
        return true;
    }

    /// <summary>Always silent. A fresh link replaces older ones. An address with no waiting account does the same writes as one that has (decision M1-65).</summary>
    public async Task ResendVerificationAsync(string email, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        if (!limiter.TryAcquire("resend-email", normal, 3, Hour)) return;
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.EmailVerifiedAt == null && a.DeletedAt == null, ct);
        await IssueLinkOrDecoyAsync(account, EmailTokenPurpose.Verify, ct);
    }

    // ---------- sign-in / sessions ----------

    /// <summary>
    /// Claims an attempt at the gate FIRST (atomically, known and unknown address alike), then checks the password only if the claim was granted. A right
    /// password clears the address; a wrong one has already been counted.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string email, string password, string clientIp, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        var addressKey = gateKeys.Address(normal);
        var ipKey = gateKeys.Ip(clientIp);
        var claim = await gate.ClaimAsync(addressKey, ipKey, ct);
        if (!claim.Allowed) return new SignInResult(SignInStatus.TooManyAttempts, RetryAfterSeconds: claim.RetryAfterSeconds);

        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.DeletedAt == null, ct);
        if (account is null) { passwords.BurnTime(password); return new SignInResult(SignInStatus.InvalidCredentials); }
        var (ok, rehash) = passwords.Verify(account.PasswordHash, password);
        if (!ok) return new SignInResult(SignInStatus.InvalidCredentials);   // already counted by the claim
        await gate.SucceededAsync(addressKey, ipKey, ct);
        // The password was right: only now is it safe to say the address is not verified.
        if (account.EmailVerifiedAt is null) return new SignInResult(SignInStatus.EmailNotVerified);

        if (rehash)
        {
            var newHash = passwords.Hash(password);
            await db.BuyerAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, newHash), ct);
        }
        var token = await CreateSessionAsync(account.Id, ct);
        await guestClaims.ClaimAsync(account.Id, normal, ct);   // orders placed as a guest since the last sign-in
        return new SignInResult(SignInStatus.Ok, token, account);
    }

    /// <summary>
    /// Step-up (decision O-11): the signed-in buyer proves the password again, which stamps <c>authenticated_at</c> on THIS session. The check passes through the
    /// same gate as sign-in (same per-address delay, same per-IP budget), so a stolen or replayed session cannot be used to guess the password for free.
    /// </summary>
    public async Task<SignInResult> ReauthenticateAsync(BuyerPrincipal who, string password, string clientIp, CancellationToken ct)
    {
        var addressKey = gateKeys.Address(AuthValidation.NormaliseEmail(who.Email));
        var ipKey = gateKeys.Ip(clientIp);
        var claim = await gate.ClaimAsync(addressKey, ipKey, ct);
        if (!claim.Allowed) return new SignInResult(SignInStatus.TooManyAttempts, RetryAfterSeconds: claim.RetryAfterSeconds);
        var hash = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null).Select(a => a.PasswordHash).FirstOrDefaultAsync(ct);
        if (hash is null) { passwords.BurnTime(password); return new SignInResult(SignInStatus.InvalidCredentials); }
        if (!passwords.Verify(hash, password).Ok) return new SignInResult(SignInStatus.InvalidCredentials);
        await gate.SucceededAsync(addressKey, ipKey, ct);
        var now = Now;
        await db.BuyerSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthenticatedAt, now), ct);
        return new SignInResult(SignInStatus.Ok);
    }

    /// <summary>
    /// Swaps the session token for a new one and revokes the old, so the web server can rotate after failures and a replayed old cookie dies. The new session keeps
    /// the old one's absolute expiry and its <c>authenticated_at</c>: rotating neither lengthens a session nor counts as proving the password. The revoke is the
    /// claim (one UPDATE), so two parallel rotations of one token give one new session. Null when the session is already gone.
    /// </summary>
    public async Task<string?> RotateSessionAsync(BuyerPrincipal who, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.BuyerSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        if (claimed != 1) return null;
        var old = await db.BuyerSessions.AsNoTracking().Where(s => s.Id == who.SessionId).Select(s => new { s.AbsoluteExpiresAt, s.AuthenticatedAt }).FirstAsync(ct);
        var token = Tokens.New();
        db.BuyerSessions.Add(new BuyerSession
        {
            Id = Guid.NewGuid(), AccountId = who.AccountId, TokenHash = Tokens.Hash(token), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = old.AuthenticatedAt,
            ExpiresAt = Min(now.AddDays(Math.Max(1, O.SessionIdleDays)), old.AbsoluteExpiresAt), AbsoluteExpiresAt = old.AbsoluteExpiresAt,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return token;
    }

    public async Task<string> CreateSessionAsync(Guid accountId, CancellationToken ct)
    {
        var now = Now;
        var token = Tokens.New();
        var absolute = now.AddDays(Math.Max(1, O.SessionAbsoluteDays));
        db.BuyerSessions.Add(new BuyerSession
        {
            Id = Guid.NewGuid(), AccountId = accountId, TokenHash = Tokens.Hash(token), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now,
            ExpiresAt = Min(now.AddDays(Math.Max(1, O.SessionIdleDays)), absolute), AbsoluteExpiresAt = absolute,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>The signed-in buyer for a bearer token, or null. Slides the idle expiry (at most every 5 minutes, to spare the database).</summary>
    public async Task<BuyerPrincipal?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Tokens.Hash(token);
        var now = Now;
        var row = await (from s in db.BuyerSessions.AsNoTracking()
                         join a in db.BuyerAccounts.AsNoTracking() on s.AccountId equals a.Id
                         where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now
                               && a.DeletedAt == null && a.EmailVerifiedAt != null
                         select new { s.Id, s.LastSeenAt, s.AuthenticatedAt, s.AbsoluteExpiresAt, AccountId = a.Id, a.DisplayName, a.Email }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        if (now - row.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            var idle = Min(now.AddDays(Math.Max(1, O.SessionIdleDays)), row.AbsoluteExpiresAt);
            await db.BuyerSessions.Where(s => s.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.ExpiresAt, idle), ct);
        }
        return new BuyerPrincipal(row.AccountId, row.Id, row.DisplayName, row.Email ?? "", row.AuthenticatedAt);
    }

    public Task SignOutAsync(Guid sessionId, CancellationToken ct)
    {
        var now = Now;
        return db.BuyerSessions.Where(s => s.Id == sessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
    }

    public Task SignOutAllAsync(Guid accountId, CancellationToken ct)
    {
        var now = Now;
        return db.BuyerSessions.Where(s => s.AccountId == accountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
    }

    // ---------- password reset ----------

    /// <summary>
    /// Always silent. Any existing account gets a link, verified or not (decision O-16 c): the owner of an address someone else registered first has no other way in,
    /// and a reset proves the mailbox. An address with no account does the same writes (decision M1-65).
    /// </summary>
    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        if (!limiter.TryAcquire("forgot-email", normal, 3, Hour)) return;
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.DeletedAt == null, ct);
        await IssueLinkOrDecoyAsync(account, EmailTokenPurpose.Reset, ct);
    }

    /// <summary>
    /// Sets the password and signs the account out everywhere, in ONE transaction: if any step fails nothing changed and the link still works. The password
    /// is checked against the address first, without using the link up. Returns the problem to show for a weak password, or not-ok for an unknown, used or expired link.
    /// </summary>
    public async Task<ResetOutcome> ResetPasswordAsync(string token, string newPassword, CancellationToken ct)
    {
        var now = Now;
        if (links.Parse(EmailTokenPurpose.Reset, token) is not { } tokenId) return new ResetOutcome(false);
        var peek = await (from t in db.EmailTokens.AsNoTracking()
                          join a in db.BuyerAccounts.AsNoTracking() on t.AccountId equals a.Id
                          where t.Id == tokenId && t.Purpose == EmailTokenPurpose.Reset && t.UsedAt == null && t.ExpiresAt > now && a.DeletedAt == null && a.Email != null
                          select new { a.Id, a.Email }).FirstOrDefaultAsync(ct);
        if (peek is null) return new ResetOutcome(false);
        if (AuthValidation.PasswordProblem(newPassword, peek.Email, O.PasswordMinLength) is { } problem) return new ResetOutcome(false, problem);   // the link is not used up

        var hash = passwords.Hash(newPassword);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var accountId = await ConsumeTokenAsync(EmailTokenPurpose.Reset, token, ct);
        if (accountId is null) return new ResetOutcome(false);
        // The mailbox is proven by the link, so the address counts as verified too, and the sign-in gate forgets the address (a person who can read the mailbox is not an attacker).
        var changed = await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.UpdatedAt, now)
            .SetProperty(a => a.EmailVerifiedAt, a => a.EmailVerifiedAt ?? now), ct);
        if (changed == 0) return new ResetOutcome(false);
        await db.BuyerSessions.Where(s => s.AccountId == accountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        await gate.ClearAddressAsync(gateKeys.Address(peek.Email!), ct);
        await tx.CommitAsync(ct);
        await guestClaims.ClaimAsync(peek.Id, peek.Email!, ct);   // a reset proves the mailbox too
        return new ResetOutcome(true);
    }

    // ---------- internals ----------


    private async Task QueueVerificationAsync(BuyerAccount account, CancellationToken ct)
    {
        await InvalidateTokensAsync(account.Id, EmailTokenPurpose.Verify, ct);
        var now = Now;
        var id = Guid.NewGuid();
        db.EmailTokens.Add(new EmailToken { Id = id, AccountId = account.Id, Purpose = EmailTokenPurpose.Verify, CreatedAt = now, ExpiresAt = now.AddHours(Math.Max(1, O.VerificationTokenHours)) });
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.Verify, account.Id, id));
    }

    /// <summary>
    /// Issues a link for a real account, or - for an address with no matching account - performs exactly the same database work with placeholder rows: one UPDATE
    /// that matches nothing, one token row (already used and expired, purged within the hour) and one outbox row (already delivered, never dispatched). So the
    /// response time of "forgot password" and "resend verification" does not tell an existing address from an unknown one (decision M1-65).
    /// </summary>
    private async Task IssueLinkOrDecoyAsync(BuyerAccount? account, string purpose, CancellationToken ct)
    {
        var now = Now;
        var id = Guid.NewGuid();
        var kind = purpose == EmailTokenPurpose.Reset ? AuthMailKinds.Reset : AuthMailKinds.Verify;
        var lifetime = purpose == EmailTokenPurpose.Reset ? TimeSpan.FromMinutes(Math.Max(5, O.ResetTokenMinutes)) : TimeSpan.FromHours(Math.Max(1, O.VerificationTokenHours));
        if (account is not null)
        {
            await InvalidateTokensAsync(account.Id, purpose, ct);
            db.EmailTokens.Add(new EmailToken { Id = id, AccountId = account.Id, Purpose = purpose, CreatedAt = now, ExpiresAt = now + lifetime });
            outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(kind, account.Id, id));
        }
        else
        {
            await InvalidateTokensAsync(Guid.NewGuid(), purpose, ct);
            db.EmailTokens.Add(new EmailToken { Id = id, AccountId = Guid.NewGuid(), Purpose = EmailTokenPurpose.Decoy, CreatedAt = now, ExpiresAt = now, UsedAt = now });
            var m = outbox.Enqueue(AuthMailHandler.DecoyType, new AuthMailPayload(kind, Guid.NewGuid(), id));
            m.Status = OutboxStatus.Delivered; m.DeliveredAt = now; m.Attempts = 1; m.Payload = null; m.PayloadPurgedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A fresh link invalidates the older ones.</summary>
    private Task InvalidateTokensAsync(Guid accountId, string purpose, CancellationToken ct)
    {
        var now = Now;
        return db.EmailTokens.Where(t => t.AccountId == accountId && t.Purpose == purpose && t.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, (DateTimeOffset?)now), ct);
    }

    /// <summary>Marks the link used in one atomic UPDATE (two clicks consume it once) and returns its account, or null.</summary>
    private async Task<Guid?> ConsumeTokenAsync(string purpose, string token, CancellationToken ct)
    {
        if (links.Parse(purpose, token) is not { } tokenId) return null;
        var now = Now;
        // One atomic UPDATE is the only guard: unused, right purpose, not expired. Two clicks (or two requests) race on it and exactly one gets a row.
        var claimed = await db.EmailTokens.Where(t => t.Id == tokenId && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, (DateTimeOffset?)now), ct);
        if (claimed != 1) return null;
        return await db.EmailTokens.AsNoTracking().Where(t => t.Id == tokenId).Select(t => (Guid?)t.AccountId).FirstOrDefaultAsync(ct);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
