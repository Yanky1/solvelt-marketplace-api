using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Payments;

/// <summary>
/// Verifies a Stripe webhook: header <c>Stripe-Signature: t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;")&gt;</c> (several <c>v1</c> values while a secret rotates).
/// The check runs on the raw bytes, in constant time, with a 5 minute window, before anything is parsed. Uses <c>Stripe:WebhookSecret</c> only.
/// </summary>
public static class StripeSignature
{
    public const int ToleranceSeconds = 300;

    public static bool Verify(string? header, ReadOnlySpan<byte> rawBody, string secret, DateTimeOffset now, int toleranceSeconds = ToleranceSeconds)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret) || header.Length > 1000) return false;
        long? t = null;
        var candidates = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var (k, v) = (part[..eq], part[(eq + 1)..]);
            if (k == "t" && long.TryParse(v, out var ts) && ts > 0) t = ts;
            else if (k == "v1") candidates.Add(v);
        }
        if (t is not { } stamp || candidates.Count == 0 || Math.Abs(now.ToUnixTimeSeconds() - stamp) > toleranceSeconds) return false;
        var signed = new byte[Encoding.ASCII.GetByteCount(stamp.ToString()) + 1 + rawBody.Length];
        var n = Encoding.ASCII.GetBytes(stamp.ToString(), signed);
        signed[n] = (byte)'.';
        rawBody.CopyTo(signed.AsSpan(n + 1));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        var ok = false;
        foreach (var c in candidates)
        {
            byte[] given;
            try { given = Convert.FromHexString(c); } catch (FormatException) { continue; }
            ok |= CryptographicOperations.FixedTimeEquals(expected, given);   // no early exit: constant work per candidate
        }
        return ok;
    }

    /// <summary>The header a sender would produce (used by tests and the dev tools).</summary>
    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexStringLower(mac)}";
    }
}
