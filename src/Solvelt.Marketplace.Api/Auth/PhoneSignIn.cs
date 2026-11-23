using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Auth;

public enum PhoneStartStatus { Sent, Invalid, RateLimited, SendFailed, Unavailable, NoPhone }
public enum PhoneVerifyStatus { Ok, Wrong, Expired, NoAccount, Unavailable }
public sealed record PhoneStartResult(PhoneStartStatus Status, Guid? ChallengeId = null);
public sealed record PhoneVerifyResult(PhoneVerifyStatus Status, string? Token = null, Guid? AccountId = null, string? DisplayName = null);

/// <summary>
/// The web path for an account made by phone (decision W3-02; V-05 and V-09 follow-ups): such an account has no email and no password, its proven number is its only credential, so the web signs it in
/// with an SMS code sent to that number. The rules are the phone line's (<see cref="PhoneCodeService"/>, decision V-03), not a second set:
/// <list type="bullet">
/// <item>a code is texted ONLY to a number an eligible account holds (no SMS pumping, no cost, no texts nobody asked for), but the answer, the caps counted, the row written and the work done are
/// the same for any valid number, so nothing tells who is a customer: only the provider call is skipped;</item>
/// <item>six digits, 10 minutes, 5 tries (the fifth wrong one locks the code, even for the right digits afterwards), single use, consumed atomically (two parallel right answers make one session);</item>
/// <item>the SMS caps are the phone line's own buckets (<c>Voice:MaxSmsPerNumberPerHour</c> per number, <c>Voice:MaxSmsPerHour</c> in total), shared, so the web adds no extra texts per number;
/// sent through the same <see cref="ISmsSender"/> (SignalWire);</item>
/// <item>the code is an HMAC under its OWN key (<c>Auth:PhoneCodeKey</c>): never the phone line's key (one key per purpose). Without that key the feature is off (503).</item>
/// </list>
/// Who can sign in this way: an account whose number is PROVEN (<c>PhoneVerifiedE164</c>, V-05) and which has NO password, not deleted, not staff-locked, not demo-verified. An account with a password signs
/// in with it (and recovers it by email): a text message is not made a second way into it.
/// </summary>
public sealed class PhoneSignInService(MarketplaceDb db, PhoneSmsDispatcher dispatcher, AttemptLimiter limiter, AuthService auth, IOptions<AuthOptions> authOptions, IOptions<VoiceOptions> voice, TimeProvider time)
{
    public const int Digits = PhoneCodeService.Digits, MaxAttempts = PhoneCodeService.MaxAttempts;
    public static readonly TimeSpan Lifetime = PhoneCodeService.Lifetime;
    /// <summary>Codes one client address may ask for per hour (on top of the per-number and global caps).</summary>
    public const int MaxStartsPerIpPerHour = 10;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public bool Enabled => !string.IsNullOrWhiteSpace(authOptions.Value.PhoneCodeKey);

    internal byte[] Hash(Guid challengeId, string purpose, string code)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(authOptions.Value.PhoneCodeKey!));
        return h.ComputeHash(Encoding.UTF8.GetBytes($"web-phone-code-v1|{purpose}|{challengeId:N}|{code}"));
    }

    /// <summary>The account that may sign in on the web with this proven number, or null.</summary>
    private Task<Guid?> EligibleAsync(string e164, CancellationToken ct)
        => db.BuyerAccounts.AsNoTracking()
            .Where(a => a.PhoneVerifiedE164 == e164 && a.PasswordHash == null && a.DeletedAt == null && a.StaffLockedAt == null && a.DemoVerifiedAt == null)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    /// <summary>Sign-in: texts a code to the number given when an eligible account holds it. The answer is the same whether or not one does.</summary>
    public async Task<PhoneStartResult> StartSignInAsync(string? rawPhone, string clientIp, CancellationToken ct)
    {
        if (!Enabled) return new(PhoneStartStatus.Unavailable);
        if (PhoneNumbers.ToE164(rawPhone) is not { } e164) return new(PhoneStartStatus.Invalid);
        if (!limiter.TryAcquire("web-phone-ip", clientIp, MaxStartsPerIpPerHour, Hour)) return new(PhoneStartStatus.RateLimited);
        return await SendAsync(e164, PhoneSignInPurposes.SignIn, await EligibleAsync(e164, ct), ct);
    }

    /// <summary>Step-up of a session (decision O-11) for an account that has no password: a code to the account's own proven number. The body names no number, so nothing else can be texted.</summary>
    public Task<PhoneStartResult> StartStepUpAsync(BuyerPrincipal who, string clientIp, CancellationToken ct) => StartForOwnAccountAsync(who, clientIp, PhoneSignInPurposes.StepUp, ct);

    /// <summary>Deleting an account that has no password (W3 follow-up): a code for that purpose only, to the account's own proven number, with the same caps and the same send after the answer.</summary>
    public Task<PhoneStartResult> StartDeleteAsync(BuyerPrincipal who, string clientIp, CancellationToken ct) => StartForOwnAccountAsync(who, clientIp, PhoneSignInPurposes.DeleteAccount, ct);

    private async Task<PhoneStartResult> StartForOwnAccountAsync(BuyerPrincipal who, string clientIp, string purpose, CancellationToken ct)
    {
        if (!Enabled) return new(PhoneStartStatus.Unavailable);
        // only an account without a password: one with a password steps up with it (a text message is not a second way past the password)
        var e164 = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null && a.PasswordHash == null).Select(a => a.PhoneVerifiedE164).FirstOrDefaultAsync(ct);
        if (e164 is null) return new(PhoneStartStatus.NoPhone);
        if (!limiter.TryAcquire("web-phone-ip", clientIp, MaxStartsPerIpPerHour, Hour)) return new(PhoneStartStatus.RateLimited);
        return await SendAsync(e164, purpose, who.AccountId, ct);
    }

    private async Task<PhoneStartResult> SendAsync(string e164, string purpose, Guid? accountId, CancellationToken ct)
    {
        var v = voice.Value;
        // the phone line's own buckets: one cap per number for every way a code reaches it
        if (!limiter.TryAcquire("voice-sms-number", e164, Math.Max(1, v.MaxSmsPerNumberPerHour), Hour)) return new(PhoneStartStatus.RateLimited);
        if (!limiter.TryAcquire("voice-sms-all", "all", Math.Max(1, v.MaxSmsPerHour), Hour)) return new(PhoneStartStatus.RateLimited);
        var now = time.GetUtcNow().ToUniversalTime();
        var id = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.PhoneSignInChallenges.Add(new PhoneSignInChallenge { Id = id, Purpose = purpose, AccountId = accountId, CodeHash = Hash(id, purpose, code), CreatedAt = now, ExpiresAt = now + Lifetime });
        await db.SaveChangesAsync(ct);
        var body = purpose switch
        {
            PhoneSignInPurposes.SignIn => $"Solvelt code: {code}. Use it to sign in on the Solvelt website. It expires in 10 minutes. Never give it to anyone.",
            PhoneSignInPurposes.DeleteAccount => $"Solvelt code: {code}. Use it ONLY to DELETE your Solvelt account on the website. If you did not ask to delete it, do not use it and ignore this message. It expires in 10 minutes.",
            _ => $"Solvelt code: {code}. Use it to confirm a change to your account on the Solvelt website. It expires in 10 minutes. Never give it to anyone.",
        };
        // A sign-in code is texted ONLY to a number an eligible account holds (orchestrator review of W3-02): texting any number the internet types in invites SMS pumping (toll fraud), costs money
        // and texts people who never asked. Everything else is identical for both: the caps above are counted, the code is drawn and hashed, the row is written (a null account id), and the answer
        // is the same 202. Only the send is skipped, so the answer and its timing apart from the provider call say nothing.
        // The send itself never runs in the request (orchestrator review of f4363ad): waiting for the provider would make a real account answer measurably slower than an unknown number.
        // It is handed to the dispatcher, which sends after the answer and records a failure on the row (marked used: no code works; the buyer asks for a new one).
        if (accountId is not null) dispatcher.Enqueue(id, e164, body);
        return new(PhoneStartStatus.Sent, id);
    }

    /// <summary>
    /// Checks a code. A try is counted FIRST, in one UPDATE that refuses an ended, used or locked code (so parallel guesses cannot exceed five), then the digits are compared in constant time, then a right code
    /// is consumed in a second UPDATE that only one request can win. Sign-in makes a session; a step-up stamps the caller's session (only for the account the code was sent for).
    /// </summary>
    public async Task<PhoneVerifyResult> VerifyAsync(Guid challengeId, string? typed, string purpose, BuyerPrincipal? who, CancellationToken ct)
    {
        if (!Enabled) return new(PhoneVerifyStatus.Unavailable);
        var now = time.GetUtcNow().ToUniversalTime();
        var digits = new string((typed ?? "").Where(char.IsAsciiDigit).ToArray());
        var counted = await db.PhoneSignInChallenges.Where(c => c.Id == challengeId && c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > now && c.Attempts < MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct);
        if (counted != 1) return new(PhoneVerifyStatus.Expired);
        var row = await db.PhoneSignInChallenges.AsNoTracking().Where(c => c.Id == challengeId).Select(c => new { c.CodeHash, c.AccountId, c.Attempts }).FirstAsync(ct);
        var ok = digits.Length == Digits && CryptographicOperations.FixedTimeEquals(Hash(challengeId, purpose, digits), row.CodeHash);
        if (!ok) return new(row.Attempts >= MaxAttempts ? PhoneVerifyStatus.Expired : PhoneVerifyStatus.Wrong);
        if (purpose != PhoneSignInPurposes.SignIn && (who is null || row.AccountId != who.AccountId)) return new(PhoneVerifyStatus.Wrong);   // a step-up or delete code is for the session's own account only
        var consumed = await db.PhoneSignInChallenges.Where(c => c.Id == challengeId && c.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTimeOffset?)now), ct);
        if (consumed != 1) return new(PhoneVerifyStatus.Expired);
        if (row.AccountId is not { } accountId) return new(PhoneVerifyStatus.NoAccount);   // the number was proven, but no account signs in with it: only its holder learns that

        if (purpose == PhoneSignInPurposes.DeleteAccount) return new(PhoneVerifyStatus.Ok, AccountId: accountId);   // consumed; the caller deletes in its own transaction (AccountService)
        if (purpose == PhoneSignInPurposes.StepUp)
        {
            await db.BuyerSessions.Where(s => s.Id == who!.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthenticatedAt, now), ct);
            return new(PhoneVerifyStatus.Ok, AccountId: accountId);
        }
        // the account is checked again NOW: a password set, a lock or a deletion since the code was sent ends this way in
        var account = await db.BuyerAccounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.PhoneVerifiedE164 != null && a.PasswordHash == null && a.DeletedAt == null && a.StaffLockedAt == null && a.DemoVerifiedAt == null)
            .Select(a => new { a.Id, a.DisplayName, a.SessionEpoch }).FirstOrDefaultAsync(ct);
        if (account is null) return new(PhoneVerifyStatus.NoAccount);
        var token = await auth.CreateSessionAsync(account.Id, ct, account.SessionEpoch);
        return new(PhoneVerifyStatus.Ok, token, account.Id, account.DisplayName);
    }
}

/// <summary>
/// Sends the web's SMS codes AFTER the request has answered (W3-02), so a start does the same work and takes the same time whether or not a text goes out. Each send runs on its own scope; a
/// provider that refuses, fails or takes longer than <see cref="SendTimeout"/> leaves the challenge marked used (no code works, nothing half-live). The code lives only in this closure's memory
/// for the few seconds of the send, never in a queue table. Per instance and in memory: a process that stops in that window loses the text, and the buyer asks for a new code.
/// </summary>
public sealed class PhoneSmsDispatcher(IServiceScopeFactory scopes, TimeProvider time, ILogger<PhoneSmsDispatcher> log)
{
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _pending = new();

    public void Enqueue(Guid challengeId, string e164, string body)
    {
        var task = Task.Run(async () =>
        {
            var sent = false;
            try
            {
                using var scope = scopes.CreateScope();
                using var timeout = new CancellationTokenSource(SendTimeout);
                sent = await scope.ServiceProvider.GetRequiredService<ISmsSender>().SendAsync(e164, body, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException) { sent = false; }
            if (sent) return;
            log.LogWarning("A web phone code could not be sent.");
            try
            {
                using var scope = scopes.CreateScope();
                var now = time.GetUtcNow().ToUniversalTime();
                await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PhoneSignInChallenges.Where(c => c.Id == challengeId && c.UsedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTimeOffset?)now));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning("A failed web phone code could not be marked used ({Type}).", ex.GetType().Name); }   // it still expires in 10 minutes
        });
        _pending[challengeId] = task;
        _ = task.ContinueWith(_ => _pending.TryRemove(challengeId, out var _), TaskScheduler.Default);
    }

    /// <summary>Waits for the sends in flight (tests, and a graceful stop).</summary>
    public Task DrainAsync() => Task.WhenAll(_pending.Values);
}
