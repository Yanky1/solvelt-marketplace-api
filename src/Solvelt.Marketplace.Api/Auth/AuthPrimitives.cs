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

/// <summary>Password hashing with ASP.NET Core Identity's V3 hasher (PBKDF2, HMAC-SHA512, random salt, self-describing format).</summary>
public sealed class PasswordService(IOptions<AuthOptions> options)
{
    private readonly PasswordHasher<BuyerAccount> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = Math.Max(1000, options.Value.PasswordIterations) }));
    private static readonly BuyerAccount Nobody = new();
    private string? _dummy;

    public string Hash(string password) => _hasher.HashPassword(Nobody, password);

    /// <summary>Verifies and tells whether the stored hash should be upgraded (more iterations since).</summary>
    public (bool Ok, bool Rehash) Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return (false, false);
        var r = _hasher.VerifyHashedPassword(Nobody, hash, password);
        return (r != PasswordVerificationResult.Failed, r == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Spends the same time as a real check, so an unknown email is not faster than a wrong password.</summary>
    public void BurnTime(string password)
    {
        _dummy ??= Hash("dummy-password-for-timing");
        _hasher.VerifyHashedPassword(Nobody, _dummy, password);
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
        if (name.Any(char.IsControl)) return "That name has characters that are not allowed.";
        return null;
    }
}
