using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

public sealed record SignatureResult(bool Ok, int Status, string Code, string Message)
{
    public static readonly SignatureResult Valid = new(true, 200, "ok", "");
}

/// <summary>
/// CONTRACT.md §1.2 / docs/ORDERS.md §3.4: <c>X-Solvelt-Signature: v1=hex HMAC-SHA256(Portal:WebhookSecret, "&lt;X-Solvelt-Timestamp&gt;.&lt;raw body&gt;")</c>.
/// The MAC is computed over the raw bytes, never over a re-serialised body. Note the signature does NOT cover the
/// <c>X-Solvelt-Event-Id</c> / <c>X-Solvelt-Event-Type</c> headers; the endpoint therefore keys everything on the signed body.
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "X-Solvelt-Timestamp", SignatureHeader = "X-Solvelt-Signature";
    public const string EventIdHeader = "X-Solvelt-Event-Id", EventTypeHeader = "X-Solvelt-Event-Type";
    public const int WindowSeconds = 300;
    public const int MaxBodyBytes = 256 * 1024;

    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        h.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        h.AppendData(body);
        return "v1=" + Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary>Checks the timestamp window, then the signature in constant time. Nothing is parsed before this succeeds.</summary>
    public static SignatureResult Verify(string secret, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return new(false, 401, "unsigned", "Missing signature headers.");
        if (!long.TryParse(timestamp, out var unix) || unix < 0 || unix > 4_102_444_800L) return new(false, 401, "bad_timestamp", "Invalid timestamp.");
        if (Math.Abs(now.ToUnixTimeSeconds() - unix) > WindowSeconds) return new(false, 401, "stale", "The request timestamp is outside the allowed window.");
        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        var given = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given) ? SignatureResult.Valid : new(false, 401, "bad_signature", "Signature mismatch.");
    }
}
