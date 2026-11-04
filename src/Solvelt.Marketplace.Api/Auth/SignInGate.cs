using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Auth;

public readonly record struct GateResult(bool Allowed, int RetryAfterSeconds = 0)
{
    public static GateResult Open => new(true);
    public static GateResult Blocked(TimeSpan wait) => new(false, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
}

/// <summary>
/// The one gate every password check passes through (sign-in and verify-email): per-address progressive delay and a per-IP failure budget, both in the
/// database so every instance shares them, both applied to known and unknown addresses alike so a delay proves nothing about an address (decisions M1-63, O-17).
/// An attempt is CLAIMED first, atomically, before any password work: it is counted as a failure up front and refunded when the password turns out right,
/// so parallel attempts cannot all slip through a check that was read before it was written.
/// </summary>
public sealed class SignInGate(MarketplaceDb db, TimeProvider time, IOptions<AuthOptions> options)
{
    private const int Retries = 16;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1), Memory = TimeSpan.FromHours(24);
    private AuthOptions O => options.Value;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    /// <summary>Seconds the next attempt must wait after the n-th failure: none for the first few, then 1, 2, 4 ... capped.</summary>
    public static TimeSpan Delay(int failures, int free, int maxSeconds)
    {
        if (failures <= free) return TimeSpan.Zero;
        var exponent = Math.Min(30, failures - free - 1);
        return TimeSpan.FromSeconds(Math.Min(Math.Max(1, maxSeconds), Math.Pow(2, exponent)));
    }

    /// <summary>Claims one attempt for this address from this client. Allowed means the caller may check the password (and must refund on success).</summary>
    public async Task<GateResult> ClaimAsync(string addressKey, string ipKey, CancellationToken ct)
    {
        var ip = await ClaimIpAsync(ipKey, ct);
        if (!ip.Allowed) return ip;
        var address = await ClaimAddressAsync(addressKey, ct);
        if (!address.Allowed) await RefundIpAsync(ipKey, ct);   // nothing was checked, so it does not cost the client's budget
        return address;
    }

    /// <summary>The password was right: the address starts afresh and the client's budget gets its attempt back.</summary>
    public async Task SucceededAsync(string addressKey, string ipKey, CancellationToken ct)
    {
        await ClearAddressAsync(addressKey, ct);
        await RefundIpAsync(ipKey, ct);
    }

    public Task ClearAddressAsync(string addressKey, CancellationToken ct) => db.SignInAttempts.Where(a => a.Key == addressKey).ExecuteDeleteAsync(ct);

    private Task RefundIpAsync(string ipKey, CancellationToken ct)
        => db.SignInIpBudgets.Where(b => b.Key == ipKey && b.Count > 0).ExecuteUpdateAsync(s => s.SetProperty(b => b.Count, b => b.Count - 1), ct);

    private async Task<GateResult> ClaimAddressAsync(string key, CancellationToken ct)
    {
        for (var i = 0; i < Retries; i++)
        {
            var now = Now;
            var row = await db.SignInAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, ct);
            if (row is null)
            {
                db.SignInAttempts.Add(new SignInAttempt { Key = key, Failures = 1, NextAllowedAt = now + Delay(1, O.SignInFreeFailures, O.SignInMaxDelaySeconds), UpdatedAt = now });
                try { await db.SaveChangesAsync(ct); return GateResult.Open; }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); continue; }   // another attempt created the row first: read it and try again
            }
            if (row.NextAllowedAt > now) return GateResult.Blocked(row.NextAllowedAt - now);
            var seen = row.Failures;
            var failures = (now - row.UpdatedAt > Memory ? 0 : seen) + 1;           // a quiet day wipes the memory
            var next = now + Delay(failures, O.SignInFreeFailures, O.SignInMaxDelaySeconds);
            var claimed = await db.SignInAttempts.Where(a => a.Key == key && a.Failures == seen && a.NextAllowedAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Failures, failures).SetProperty(a => a.NextAllowedAt, next).SetProperty(a => a.UpdatedAt, now), ct);
            if (claimed == 1) return GateResult.Open;
            // somebody else moved the row between our read and our write: read it again
        }
        return GateResult.Blocked(TimeSpan.FromSeconds(1));   // too much contention on one address: wait a moment
    }

    private async Task<GateResult> ClaimIpAsync(string key, CancellationToken ct)
    {
        for (var i = 0; i < Retries; i++)
        {
            var now = Now;
            var row = await db.SignInIpBudgets.AsNoTracking().FirstOrDefaultAsync(b => b.Key == key, ct);
            if (row is null)
            {
                db.SignInIpBudgets.Add(new SignInIpBudget { Key = key, Count = 1, WindowStart = now });
                try { await db.SaveChangesAsync(ct); return GateResult.Open; }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); continue; }
            }
            if (now - row.WindowStart >= Window)
            {
                var start = row.WindowStart;
                var reset = await db.SignInIpBudgets.Where(b => b.Key == key && b.WindowStart == start)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Count, 1).SetProperty(b => b.WindowStart, now), ct);
                if (reset == 1) return GateResult.Open;
                continue;
            }
            if (row.Count >= O.SignInIpFailuresPerHour) return GateResult.Blocked(row.WindowStart + Window - now);
            var seen = row.Count; var windowStart = row.WindowStart;
            var claimed = await db.SignInIpBudgets.Where(b => b.Key == key && b.Count == seen && b.WindowStart == windowStart)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Count, seen + 1), ct);
            if (claimed == 1) return GateResult.Open;
        }
        return GateResult.Blocked(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Keyed hashes for the gate's tables, so they hold neither an address nor an IP. The key is derived from <c>Auth:EmailLinkKey</c> for this purpose only.</summary>
public sealed class GateKeys(IOptions<AuthOptions> options)
{
    private byte[] Key => HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.EmailLinkKey ?? throw new InvalidOperationException("Auth:EmailLinkKey is not configured.")), "subkey|signin-gate"u8);

    public string Address(string normalisedEmail) => Hash("addr|" + normalisedEmail);
    public string Ip(string ip) => Hash("ip|" + ip);

    private string Hash(string value) => Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(value)));
}
