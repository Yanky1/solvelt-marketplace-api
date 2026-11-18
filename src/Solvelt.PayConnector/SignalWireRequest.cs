using System.Security.Cryptography;
using System.Text;

namespace Solvelt.PayConnector;

/// <summary>
/// Checks that a connector request really comes from our SignalWire project (https://signalwire.com/docs/compatibility-api/cxml/reference/voice/pay.md, "Securing your payment connector").
/// Connector requests do not come from a published IP range, so there is NO IP allowlist here: the protection is the signature AND the Basic credentials of the URL.
/// <c>X-SignalWire-Signature</c> is the lowercase hex HMAC-SHA1 of the connector URL (credentials removed, scheme lowercased, dot segments resolved, "/" when there is no path) followed by the raw JSON body,
/// keyed with the project's Signing Key. Both comparisons are constant-time, and a failure of either is the same answer.
/// </summary>
public static class SignalWireRequest
{
    public static string NormalisedUrl(string publicUrl)
    {
        var u = new Uri(publicUrl);   // Uri lowercases the scheme and host and resolves dot segments; an empty path becomes "/"
        return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    }

    public static string Sign(string signingKey, string publicUrl, ReadOnlySpan<byte> rawBody)
    {
        var url = Encoding.UTF8.GetBytes(NormalisedUrl(publicUrl));
        var message = new byte[url.Length + rawBody.Length];
        url.CopyTo(message, 0); rawBody.CopyTo(message.AsSpan(url.Length));
        try { return Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingKey), message)); }
        finally { CryptographicOperations.ZeroMemory(message); }   // the raw body holds the card number
    }

    public static bool SignatureOk(string? signingKey, string publicUrl, ReadOnlySpan<byte> rawBody, string? header)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || string.IsNullOrWhiteSpace(header)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(signingKey, publicUrl, rawBody)), Encoding.ASCII.GetBytes(header.Trim().ToLowerInvariant()));
    }

    public static bool BasicOk(string? user, string? password, string? authorization)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string given;
        try { given = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(user + ":" + password)));
    }
}
