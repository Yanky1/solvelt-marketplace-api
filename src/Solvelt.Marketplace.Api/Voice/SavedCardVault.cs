using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Encrypts a saved card's processor TOKEN at rest (decision V-16): the token is a payment credential for our own Sola account, so the database holds only ciphertext. AES-256-GCM with a key derived (HKDF-SHA256) from
/// <c>Sola:SavedCardKey</c> for this purpose alone, a fresh nonce per value, and the ACCOUNT ID as associated data: a row copied to another account, or edited, does not decrypt. Anything that does not decrypt returns null.
/// </summary>
public sealed class SavedCardVault(IOptions<SolaOptions> options)
{
    private const string Prefix = "v1.";

    private byte[]? Key()
    {
        var k = options.Value.SavedCardKey;
        return string.IsNullOrWhiteSpace(k) || k.Length < 32 ? null : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(k), 32, salt: null, info: Encoding.ASCII.GetBytes("Solvelt.SavedCardToken.v1"));
    }

    public string Protect(string token, Guid accountId)
    {
        var key = Key() ?? throw new InvalidOperationException("Sola:SavedCardKey is not configured.");
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, accountId.ToByteArray());
        CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string stored, Guid accountId)
    {
        var key = Key();
        if (key is null || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var raw = Convert.FromBase64String(stored[Prefix.Length..]);
            if (raw.Length < 12 + 16 + 1) return null;
            var plain = new byte[raw.Length - 28];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain, accountId.ToByteArray());
            try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
