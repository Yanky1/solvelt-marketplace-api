using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// A passkey (WebAuthn credential) of a buyer: a sign-in method and a second way to prove who they are for sensitive changes. Only the PUBLIC key is stored. Erased with the account.
/// The WebAuthn user handle of a buyer is their account id as 16 bytes (an opaque random id, no personal data).
/// </summary>
public sealed class BuyerPasskey
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>The authenticator's credential id; unique across all buyers.</summary>
    public byte[] CredentialId { get; set; } = [];
    /// <summary>COSE public key.</summary>
    public byte[] PublicKey { get; set; } = [];
    /// <summary>The authenticator's signature counter as last seen (0 for authenticators that do not count). A lower or equal value later means a cloned authenticator and is refused.</summary>
    public long SignCount { get; set; }
    public Guid AaGuid { get; set; }
    /// <summary>The buyer's label ("iPhone", "Work laptop").</summary>
    public string Name { get; set; } = "";
    public bool BackedUp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public static class ChallengePurpose
{
    public const string Register = "register", SignIn = "signin", Reauthenticate = "reauth";
}

/// <summary>
/// A WebAuthn challenge, kept on the server: single use (consumed by one atomic UPDATE), short lived, and bound to what it was made for: a registration or a step-up is bound to the
/// account AND the session that asked, a sign-in to nothing (it is anonymous) but can only be used for signing in. The row holds the options that were sent, which is what the
/// verification needs.
/// </summary>
public sealed class WebAuthnChallenge
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = "";
    public Guid? AccountId { get; set; }
    public Guid? SessionId { get; set; }
    public string Options { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

internal sealed class BuyerPasskeyConfiguration : IEntityTypeConfiguration<BuyerPasskey>
{
    public void Configure(EntityTypeBuilder<BuyerPasskey> b)
    {
        b.ToTable("buyer_passkeys");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.CredentialId).HasMaxLength(1024).IsRequired();
        b.Property(p => p.PublicKey).HasMaxLength(2048).IsRequired();
        b.Property(p => p.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(p => p.CredentialId).IsUnique();
        b.HasIndex(p => p.AccountId);
    }
}

internal sealed class WebAuthnChallengeConfiguration : IEntityTypeConfiguration<WebAuthnChallenge>
{
    public void Configure(EntityTypeBuilder<WebAuthnChallenge> b)
    {
        b.ToTable("webauthn_challenges");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Purpose).HasMaxLength(20).IsRequired();
        b.Property(c => c.Options).HasMaxLength(8192).IsRequired();
        b.HasIndex(c => c.ExpiresAt);
        b.HasIndex(c => c.AccountId);
    }
}
