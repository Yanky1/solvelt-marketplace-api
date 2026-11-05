using System.Security.Cryptography;
using System.Text;
using Solvelt.Marketplace.Api.Text;
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

    /// <summary>Hashes the password in Unicode normal form C (M1-81): a password typed with a combining accent and the same one typed with the precomposed letter are one password.</summary>
    public virtual string Hash(string password) => _hasher.HashPassword(Nobody, UnicodeNormalizer.Nfc(password));

    /// <summary>
    /// Verifies and tells whether the stored hash should be upgraded (more iterations since, or made from the text exactly as typed). A hash made before decision M1-81 was made from
    /// the password as typed, so when the normal form does not match and differs from what was typed, the text as typed is tried too; a match that way asks for a rehash,
    /// which stores the normal form.
    /// </summary>
    public virtual (bool Ok, bool Rehash) Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return (false, false);
        var normal = UnicodeNormalizer.Nfc(password);
        var r = _hasher.VerifyHashedPassword(Nobody, hash, normal);
        if (r != PasswordVerificationResult.Failed) return (true, r == PasswordVerificationResult.SuccessRehashNeeded);
        if (!string.Equals(normal, password, StringComparison.Ordinal) && _hasher.VerifyHashedPassword(Nobody, hash, password) != PasswordVerificationResult.Failed) return (true, true);
        return (false, false);
    }

    /// <summary>Spends the same time as a real check, so an unknown email is not faster than a wrong password.</summary>
    public virtual void BurnTime(string password)
    {
        var normal = UnicodeNormalizer.Nfc(password);
        _hasher.VerifyHashedPassword(Nobody, _dummy, normal);
        if (!string.Equals(normal, password, StringComparison.Ordinal)) _hasher.VerifyHashedPassword(Nobody, _dummy, password);   // as many checks as Verify makes for the same input
    }
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
        var e = UnicodeNormalizer.Nfc(email.Trim());   // the length that will be stored: some characters grow when normalised
        if (e.Length > 254) return "That email address is too long.";
        var at = e.IndexOf('@');
        if (at < 1 || at != e.LastIndexOf('@') || e.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || e[(at + 1)..] is not { Length: > 2 } domain || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
            return "Enter a valid email address.";
        return null;
    }

    /// <summary>Trimmed, in Unicode normal form C and lower-cased (M1-81): the same address typed with a combining accent or with the precomposed letter is one address.</summary>
    public static string NormaliseEmail(string email) => UnicodeNormalizer.Nfc(UnicodeNormalizer.Nfc(email.Trim()).ToLowerInvariant());

    /// <summary>Free text a buyer typed (a name, an address line): trimmed and in normal form C. Validate the length of THIS, not of the raw text, and store THIS.</summary>
    public static string NormaliseText(string? text) => UnicodeNormalizer.Nfc((text ?? "").Trim());

    public static string? PasswordProblem(string? password, string? email, int minLength)
    {
        if (string.IsNullOrEmpty(password)) return "Enter a password.";
        var normal = UnicodeNormalizer.Nfc(password);   // what is hashed (M1-81), so what is counted
        if (normal.Length < minLength) return $"Use at least {minLength} characters.";
        if (normal.Length > 128) return "Use at most 128 characters.";
        if (email is not null && string.Equals(normal, NormaliseText(email), StringComparison.OrdinalIgnoreCase)) return "The password must not be your email address.";
        return null;
    }

    public static string? DisplayNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter your name.";
        var normal = NormaliseText(name);
        if (normal.Length > 60) return "Use at most 60 characters.";
        if (HasForbiddenCharacters(normal)) return "That name has characters that are not allowed.";
        return null;
    }

    /// <summary>
    /// Control, format (zero-width, bidi overrides), private-use characters and lone surrogate halves: text that looks like something it is not, or that cannot be encoded
    /// (L3). Works on whole characters, so a valid pair (emoji, CJK Extension B) is judged by what it is: letters, emoji and symbols outside the basic plane are accepted.
    /// Used for every free-text field a buyer types.
    /// </summary>
    public static bool HasForbiddenCharacters(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var category = System.Text.Rune.GetUnicodeCategory(new System.Text.Rune(c, s[i + 1]));
                if (category is System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control) return true;
                i++;
                continue;
            }
            if (char.IsSurrogate(c)) return true;   // a half with no partner
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse) return true;
        }
        return false;
    }
}
