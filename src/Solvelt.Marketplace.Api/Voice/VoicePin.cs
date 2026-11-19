using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

public enum PinResult { Ok, Wrong, Locked, NoPin }

/// <summary>
/// The phone PIN (decision V-18): a numeric PIN typed on the KEYPAD (never spoken, never sent to the model, never logged), a second factor for signing in by phone AFTER the SMS code. It is hashed with the existing password hasher
/// over a value peppered with a server key (so a database copy alone cannot be brute-forced offline: six digits are only a million guesses), locks for 30 minutes after the fifth wrong try (the count is on the account, so it survives hang-ups
/// and new calls), is rate limited per number, and is used by the phone line only: the web sign-in never looks at it.
/// </summary>
public sealed class PhonePinService(MarketplaceDb db, PasswordService passwords, IOptions<VoiceOptions> options, TimeProvider time, AttemptLimiter limiter)
{
    public const int MinDigits = 6, MaxDigits = 12, MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(30);

    /// <summary>Why a PIN is refused, or null. The reason is generic enough to say aloud: it never repeats the digits.</summary>
    public static string? Problem(string? digits, string? phoneE164)
    {
        if (digits is null || digits.Length < MinDigits || digits.Length > MaxDigits || !digits.All(char.IsAsciiDigit)) return $"A PIN is {MinDigits} to {MaxDigits} digits.";
        if (digits.Distinct().Count() == 1) return "That PIN is too easy to guess.";
        var steps = digits.Zip(digits.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (steps.Count == 1 && steps[0] is 1 or -1) return "That PIN is too easy to guess.";                                    // 123456, 654321
        if (digits.Length % 2 == 0 && Enumerable.Range(0, digits.Length / 2).All(i => digits[2 * i] == digits[0] && digits[2 * i + 1] == digits[1])) return "That PIN is too easy to guess.";   // 121212
        var phone = new string((phoneE164 ?? "").Where(char.IsAsciiDigit).ToArray());
        if (phone.Length > 0 && phone.Contains(digits)) return "That PIN is part of your phone number.";
        return null;
    }

    private string Pepper(Guid accountId, string digits)
    {
        var secret = options.Value.PhoneCodeKey ?? throw new InvalidOperationException("Voice:PhoneCodeKey is not set.");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, salt: null, info: Encoding.ASCII.GetBytes("Solvelt.PhonePin.pepper.v1"));
        try { return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{accountId:N}|{digits}"))); } finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>The hash to store (or hold for the confirmation step): the digits themselves are not kept.</summary>
    public string Prepare(Guid accountId, string digits) => passwords.Hash(Pepper(accountId, digits));

    public bool Matches(string hash, Guid accountId, string digits) => passwords.Verify(hash, Pepper(accountId, digits)).Ok;

    public async Task<bool> HasPinAsync(Guid accountId, CancellationToken ct) => await db.BuyerAccounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.PinHash != null && a.DeletedAt == null, ct);

    public async Task<PinResult> VerifyAsync(Guid accountId, string? typed, string? phone, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var a = await db.BuyerAccounts.FirstOrDefaultAsync(x => x.Id == accountId && x.DeletedAt == null, ct);
        if (a?.PinHash is null) return PinResult.NoPin;
        if (a.PinLockedUntil is { } until && until > now) return PinResult.Locked;
        if (!limiter.TryAcquire("voice-pin-number", phone ?? "unknown", 20, TimeSpan.FromHours(1))) return PinResult.Locked;   // a per-number ceiling on top of the account's own lock
        var digits = new string((typed ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length >= MinDigits && digits.Length <= MaxDigits && Matches(a.PinHash, accountId, digits))
        {
            if (a.PinFailures != 0 || a.PinLockedUntil is not null) { a.PinFailures = 0; a.PinLockedUntil = null; await db.SaveChangesAsync(ct); }
            return PinResult.Ok;
        }
        a.PinFailures++;
        var locked = a.PinFailures >= MaxFailures;
        if (locked) { a.PinLockedUntil = now + LockFor; a.PinFailures = 0; }
        await db.SaveChangesAsync(ct);
        return locked ? PinResult.Locked : PinResult.Wrong;
    }

    public async Task SaveAsync(Guid accountId, string hash, CancellationToken ct)
        => await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.PinHash, hash).SetProperty(a => a.PinFailures, 0).SetProperty(a => a.PinLockedUntil, (DateTimeOffset?)null), ct);

    public async Task ClearAsync(Guid accountId, CancellationToken ct)
        => await db.BuyerAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PinHash, (string?)null).SetProperty(a => a.PinFailures, 0).SetProperty(a => a.PinLockedUntil, (DateTimeOffset?)null), ct);
}
