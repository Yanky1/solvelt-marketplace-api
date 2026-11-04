using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Auth;

public sealed record BuyerPrincipal(Guid AccountId, Guid SessionId, string DisplayName, string Email, DateTimeOffset AuthenticatedAt);
public enum SignInStatus { Ok, InvalidCredentials, EmailNotVerified, TooManyAttempts }
public sealed record SignInResult(SignInStatus Status, string? Token = null, BuyerAccount? Account = null, int? RetryAfterSeconds = null);

/// <summary>
/// Buyer authentication. Every answer that could reveal whether an address has an account is the same either way (sign-up, resend, forgot): the
/// difference is only in what we email. Unknown and known accounts share rate limits and the same time cost.
/// </summary>
public sealed class AuthService(MarketplaceDb db, TimeProvider time, IOptions<AuthOptions> authOptions, IOptions<MarketplaceOptions> marketplace,
    OutboxWriter outbox, AttemptLimiter limiter, PasswordService passwords, LinkTokens links)
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

        var hash = passwords.Hash(password);
        BuyerAccount account;
        if (existing is not null)
        {
            // An unverified address: the latest sign-up wins (name and password are replaced), so someone who registered an address they do not
            // own cannot leave their own password on the real owner's account. See decision M1-52.
            account = existing;
            account.PasswordHash = hash; account.DisplayName = name; account.UpdatedAt = now;
        }
        else
        {
            account = new BuyerAccount
            {
                Id = Guid.NewGuid(), Email = normal, DisplayName = name, PasswordHash = hash, CreatedAt = now, UpdatedAt = now,
                RelayEmail = RelayIdentity.NewRelayEmail(marketplace.Value.RelayDomain ?? DevDefaults.RelayDomain),
            };
            db.BuyerAccounts.Add(account);
        }
        await QueueVerificationAsync(account, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (existing is null)
        {
            // Two sign-ups for one new address at the same moment: the other one won. Nothing to do; the caller sees the same answer.
            db.ChangeTracker.Clear();
        }
    }

    // ---------- email verification ----------

    public async Task<bool> VerifyEmailAsync(string token, CancellationToken ct)
    {
        var now = Now;
        var accountId = await ConsumeTokenAsync(EmailTokenPurpose.Verify, token, ct);
        if (accountId is null) return false;
        await db.BuyerAccounts.Where(a => a.Id == accountId && a.EmailVerifiedAt == null && a.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
        return true;
    }

    /// <summary>Always silent. A fresh link replaces older ones.</summary>
    public async Task ResendVerificationAsync(string email, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        if (!limiter.TryAcquire("resend-email", normal, 3, Hour)) return;
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.EmailVerifiedAt == null && a.DeletedAt == null, ct);
        if (account is null) return;
        await QueueVerificationAsync(account, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---------- sign-in / sessions ----------

    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        var window = TimeSpan.FromMinutes(Math.Max(1, O.LockoutMinutes));
        // Unknown and known addresses are limited alike (hashed in the limiter), so a locked answer proves nothing about the address.
        if (!limiter.TryAcquire("signin-email", normal, Math.Max(3, O.LockoutThreshold * 2), window))
            return new SignInResult(SignInStatus.TooManyAttempts, RetryAfterSeconds: (int)window.TotalSeconds);

        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.DeletedAt == null, ct);
        var now = Now;
        if (account is null) { passwords.BurnTime(password); return new SignInResult(SignInStatus.InvalidCredentials); }
        if (account.LockedUntil is { } until && until > now)
        {
            passwords.BurnTime(password);
            return new SignInResult(SignInStatus.TooManyAttempts, RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)));
        }

        var (ok, rehash) = passwords.Verify(account.PasswordHash, password);
        if (!ok)
        {
            // Atomic count, then lock when the threshold is reached.
            await db.BuyerAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedSignIns, a => a.FailedSignIns + 1), ct);
            var lockUntil = now.AddMinutes(Math.Max(1, O.LockoutMinutes));
            await db.BuyerAccounts.Where(a => a.Id == account.Id && a.FailedSignIns >= O.LockoutThreshold)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LockedUntil, (DateTimeOffset?)lockUntil).SetProperty(a => a.FailedSignIns, 0), ct);
            return new SignInResult(SignInStatus.InvalidCredentials);
        }
        // The password was right: only now is it safe to say the address is not verified.
        if (account.EmailVerifiedAt is null) return new SignInResult(SignInStatus.EmailNotVerified);

        var newHash = rehash ? passwords.Hash(password) : null;
        await db.BuyerAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.FailedSignIns, 0).SetProperty(a => a.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(a => a.PasswordHash, a => newHash ?? a.PasswordHash), ct);
        var token = await CreateSessionAsync(account.Id, ct);
        return new SignInResult(SignInStatus.Ok, token, account);
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

    /// <summary>Always silent. Only a verified account gets a link (an unverified one uses "resend verification").</summary>
    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var normal = AuthValidation.NormaliseEmail(email);
        if (!limiter.TryAcquire("forgot-email", normal, 3, Hour)) return;
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Email == normal && a.EmailVerifiedAt != null && a.DeletedAt == null, ct);
        if (account is null) return;
        await InvalidateTokensAsync(account.Id, EmailTokenPurpose.Reset, ct);
        var now = Now;
        var id = Guid.NewGuid();
        db.EmailTokens.Add(new EmailToken { Id = id, AccountId = account.Id, Purpose = EmailTokenPurpose.Reset, CreatedAt = now, ExpiresAt = now.AddMinutes(Math.Max(5, O.ResetTokenMinutes)) });
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.Reset, account.Id, id));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets the password and signs the account out everywhere. Returns false for an unknown, used or expired token.</summary>
    public async Task<bool> ResetPasswordAsync(string token, string newPassword, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var accountId = await ConsumeTokenAsync(EmailTokenPurpose.Reset, token, ct);
        if (accountId is null) return false;
        var hash = passwords.Hash(newPassword);
        var now = Now;
        // The mailbox is proven by the link, so the address counts as verified too. Lockout is cleared.
        var changed = await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.UpdatedAt, now).SetProperty(a => a.FailedSignIns, 0).SetProperty(a => a.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(a => a.EmailVerifiedAt, a => a.EmailVerifiedAt ?? now), ct);
        if (changed == 0) return false;
        await db.BuyerSessions.Where(s => s.AccountId == accountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        await tx.CommitAsync(ct);
        return true;
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

    /// <summary>A fresh link invalidates the older ones.</summary>
    private Task InvalidateTokensAsync(Guid accountId, string purpose, CancellationToken ct)
    {
        var now = Now;
        return db.EmailTokens.Where(t => t.AccountId == accountId && t.Purpose == purpose && t.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, (DateTimeOffset?)now), ct);
    }

    /// <summary>Marks the token used in one atomic UPDATE (two clicks consume it once) and returns its account, or null.</summary>
    private async Task<Guid?> ConsumeTokenAsync(string purpose, string token, CancellationToken ct)
    {
        if (links.Parse(purpose, token) is not { } tokenId) return null;
        var now = Now;
        var row = await db.EmailTokens.AsNoTracking().Where(t => t.Id == tokenId && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now).Select(t => new { t.Id, t.AccountId }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var claimed = await db.EmailTokens.Where(t => t.Id == row.Id && t.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, (DateTimeOffset?)now), ct);
        return claimed == 1 ? row.AccountId : null;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
