using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Portal;

/// <summary>
/// CONTRACT.md §1.1. canonical = timestamp \n nonce \n METHOD \n encoded path+query exactly as sent \n hex sha256(exact body bytes).
/// Signature = "v1=" + lower-case hex HMAC-SHA256(secret, canonical).
/// </summary>
public static class PortalSigning
{
    public const string TimestampHeader = "X-Solvelt-Timestamp";
    public const string NonceHeader = "X-Solvelt-Nonce";
    public const string SignatureHeader = "X-Solvelt-Signature";
    public const int MaxBodyBytes = 256 * 1024;

    public static string BodyHash(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    public static string Canonical(string timestamp, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body)
        => $"{timestamp}\n{nonce}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{BodyHash(body)}";

    public static string Sign(string secret, string canonical)
        => "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));

    public static string NewNonce() => RandomNumberGenerator.GetHexString(32, lowercase: true);
}
