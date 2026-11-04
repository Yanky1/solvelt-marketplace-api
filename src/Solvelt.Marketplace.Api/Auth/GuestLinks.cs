using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Auth;

/// <summary>
/// Order-access links for guests (checkout without an account): <c>g1.&lt;order id&gt;.&lt;expiry, unix seconds&gt;.&lt;HMAC-SHA256&gt;</c> under <c>Auth:GuestLinkKey</c>,
/// which is not any other key. The link names ONE order and stops working at its expiry; nothing is stored, so there is nothing to leak or to purge. The MAC covers
/// the order id and the expiry, and is compared in constant time. Whoever holds the link can read that order (M5), so it is mailed to the checkout address only.
/// </summary>
public sealed class GuestLinks(IOptions<AuthOptions> options, TimeProvider time)
{
    private const string Prefix = "g1";
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.GuestLinkKey ?? throw new InvalidOperationException("Auth:GuestLinkKey is not configured."));

    /// <summary>A link for <paramref name="orderId"/>, valid for <c>Auth:GuestLinkDays</c> from now (or until <paramref name="expiresAt"/> when given).</summary>
    public (string Token, DateTimeOffset ExpiresAt) Make(Guid orderId, DateTimeOffset? expiresAt = null)
    {
        var expires = (expiresAt ?? time.GetUtcNow().AddDays(Math.Max(1, options.Value.GuestLinkDays))).ToUniversalTime();
        var seconds = expires.ToUnixTimeSeconds();
        return ($"{Prefix}.{orderId:N}.{seconds}.{Mac(orderId, seconds)}", DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    /// <summary>The order id when the link is genuine and has not expired, otherwise null.</summary>
    public Guid? Parse(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 120) return null;
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Prefix || parts[1].Length != 32 || parts[3].Length != 43) return null;
        if (!Guid.TryParseExact(parts[1], "N", out var orderId)) return null;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        var expected = Encoding.ASCII.GetBytes(Mac(orderId, seconds));
        var given = Encoding.ASCII.GetBytes(parts[3]);
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;
        // the MAC is checked first, so a forged expiry is refused as forged; a genuine one is refused when it has passed (at the second it names)
        return time.GetUtcNow().ToUnixTimeSeconds() < seconds ? orderId : null;
    }

    private string Mac(Guid orderId, long expiresUnix)
        => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"guest-order|{orderId:N}|{expiresUnix.ToString(CultureInfo.InvariantCulture)}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Implemented by whoever stores guest orders (M3 and M5). After an address is PROVEN to belong to an account (verified, reset, or a verified sign-in) every claimer
/// attaches the guest orders placed with that address to the account. Only a verified address ever reaches a claimer, so a guest who typed someone else's address at
/// checkout cannot make those orders appear anywhere but with whoever can read that mailbox. Must be idempotent and cheap (one conditional UPDATE); a claimer that fails
/// is logged by type and skipped, it never fails the sign-in.
/// </summary>
public interface IGuestOrderClaimer
{
    /// <summary>Attaches guest orders whose checkout address equals <paramref name="normalisedEmail"/> (trimmed, lower-cased) to the account. Returns how many.</summary>
    Task<int> ClaimAsync(Guid accountId, string normalisedEmail, CancellationToken ct);
}

public sealed class GuestClaims(IEnumerable<IGuestOrderClaimer> claimers, ILogger<GuestClaims> log)
{
    public async Task<int> ClaimAsync(Guid accountId, string normalisedEmail, CancellationToken ct)
    {
        var total = 0;
        foreach (var c in claimers)
        {
            try { total += await c.ClaimAsync(accountId, normalisedEmail, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogWarning("A guest-order claimer failed ({Claimer}, {ExceptionType}); the sign-in goes on", c.GetType().Name, ex.GetType().Name); }
        }
        return total;
    }
}
