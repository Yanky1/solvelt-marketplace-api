using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Auth;

/// <summary>Random opaque tokens. Only the SHA-256 is ever stored, so a database leak does not yield a usable session or link.</summary>
public static class Tokens
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Emailed one-time links: <c>&lt;id&gt;.&lt;base64url HMAC-SHA256(Auth:EmailLinkKey, "link|purpose|id")&gt;</c>. The database stores only the id, so nothing in it
/// (rows, outbox payloads, backups) can be turned into a working link without the key.
/// </summary>
public sealed class LinkTokens(IOptions<AuthOptions> options)
{
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.EmailLinkKey ?? throw new InvalidOperationException("Auth:EmailLinkKey is not configured."));

    public string Make(string purpose, Guid id) => $"{id:N}.{Mac(purpose, id)}";

    /// <summary>The id when the token has a valid MAC for this purpose, otherwise null. The MAC is compared in constant time.</summary>
    public Guid? Parse(string purpose, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 32 + 1 + 43 || token[32] != '.') return null;
        if (!Guid.TryParseExact(token.AsSpan(0, 32), "N", out var id)) return null;
        var expected = Encoding.ASCII.GetBytes(Mac(purpose, id));
        var given = Encoding.ASCII.GetBytes(token[33..]);
        return CryptographicOperations.FixedTimeEquals(expected, given) ? id : null;
    }

    private string Mac(string purpose, Guid id)
        => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"link|{purpose}|{id:N}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Password hashing with ASP.NET Core Identity's V3 hasher (PBKDF2, HMAC-SHA512, random salt, self-describing format).</summary>
public class PasswordService(IOptions<AuthOptions> options)
{
    private readonly PasswordHasher<BuyerAccount> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = Math.Max(1000, options.Value.PasswordIterations) }));
    private static readonly BuyerAccount Nobody = new();
    // Made once, here, so that every BurnTime is exactly one verification (a lazily made one would make the first unknown address slower than the rest).
    private readonly string _dummy = new PasswordHasher<BuyerAccount>(Options.Create(new PasswordHasherOptions { IterationCount = Math.Max(1000, options.Value.PasswordIterations) })).HashPassword(Nobody, "dummy-password-for-timing");

    public virtual string Hash(string password) => _hasher.HashPassword(Nobody, password);

    /// <summary>Verifies and tells whether the stored hash should be upgraded (more iterations since).</summary>
    public virtual (bool Ok, bool Rehash) Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return (false, false);
        var r = _hasher.VerifyHashedPassword(Nobody, hash, password);
        return (r != PasswordVerificationResult.Failed, r == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Spends the same time as a real check, so an unknown email is not faster than a wrong password.</summary>
    public virtual void BurnTime(string password) => _hasher.VerifyHashedPassword(Nobody, _dummy, password);
}

/// <summary>The buyer as the portal and sellers see them: a relay address and "Jane B.". Never the real email or full name.</summary>
public static class RelayIdentity
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string NewRelayEmail(string relayDomain)
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder("relay-", 6 + 16);
        foreach (var b in bytes) sb.Append(Alphabet[b & 31]);
        return sb.Append('@').Append(relayDomain).ToString();
    }

    /// <summary>What a deleted account's relay address becomes: unique (the id is in it), unroutable (<c>.invalid</c>) and never issued again.</summary>
    public static string Tombstone(Guid accountId) => $"deleted-{accountId:N}@relay.invalid";

    /// <summary>"Jane Brown" becomes "Jane B."; a single name stays as it is.</summary>
    public static string PublicName(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "Buyer";
        if (parts.Length == 1) return parts[0];
        var last = parts[^1];
        return $"{parts[0]} {char.ToUpperInvariant(last[0])}.";
    }
}

public static class AuthValidation
{
    public static string? EmailProblem(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "Enter your email address.";
        var e = email.Trim();
        if (e.Length > 254) return "That email address is too long.";
        var at = e.IndexOf('@');
        if (at < 1 || at != e.LastIndexOf('@') || e.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || e[(at + 1)..] is not { Length: > 2 } domain || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
            return "Enter a valid email address.";
        return null;
    }

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    public static string? PasswordProblem(string? password, string? email, int minLength)
    {
        if (string.IsNullOrEmpty(password)) return "Enter a password.";
        if (password.Length < minLength) return $"Use at least {minLength} characters.";
        if (password.Length > 128) return "Use at most 128 characters.";
        if (email is not null && string.Equals(password, email.Trim(), StringComparison.OrdinalIgnoreCase)) return "The password must not be your email address.";
        return null;
    }

    public static string? DisplayNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter your name.";
        if (name.Trim().Length > 60) return "Use at most 60 characters.";
        if (HasForbiddenCharacters(name)) return "That name has characters that are not allowed.";
        return null;
    }

    /// <summary>Control, format (zero-width, bidi overrides), surrogate-half and private-use characters: text that looks like something it is not, or breaks a mail or a label (L3). Used for every free-text field a buyer types.</summary>
    public static bool HasForbiddenCharacters(string s)
        => s.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse);
}
