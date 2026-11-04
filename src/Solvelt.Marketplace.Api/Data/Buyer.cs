using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>A buyer. The real email lives only here; the portal only ever sees <see cref="RelayEmail"/> (CONTRACT.md §8).</summary>
public sealed class BuyerAccount
{
    public Guid Id { get; set; }
    /// <summary>Trimmed and lower-cased; unique. Null after account deletion (redaction).</summary>
    public string? Email { get; set; }
    public string DisplayName { get; set; } = "";
    public string? PasswordHash { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    /// <summary><c>relay-&lt;random&gt;@&lt;relay domain&gt;</c>, generated once, unique, the only address the portal and sellers see.</summary>
    public string RelayEmail { get; set; } = "";
    public int FailedSignIns { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>An opaque session. Only the SHA-256 of the token is stored. Idle (sliding) and absolute expiry; revocable.</summary>
public sealed class BuyerSession
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class EmailTokenPurpose
{
    public const string Verify = "verify", Reset = "reset";
}

/// <summary>A one-time emailed token (verify address, reset password). Only its SHA-256 is stored.</summary>
public sealed class EmailToken
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Purpose { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

internal sealed class BuyerAccountConfiguration : IEntityTypeConfiguration<BuyerAccount>
{
    public void Configure(EntityTypeBuilder<BuyerAccount> b)
    {
        b.ToTable("buyer_accounts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Email).HasMaxLength(254);
        b.Property(a => a.DisplayName).HasMaxLength(60).IsRequired();
        b.Property(a => a.PasswordHash).HasMaxLength(400);
        b.Property(a => a.RelayEmail).HasMaxLength(120).IsRequired();
        b.HasIndex(a => a.Email).IsUnique();
        b.HasIndex(a => a.RelayEmail).IsUnique();
    }
}

internal sealed class BuyerSessionConfiguration : IEntityTypeConfiguration<BuyerSession>
{
    public void Configure(EntityTypeBuilder<BuyerSession> b)
    {
        b.ToTable("buyer_sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(s => s.TokenHash).IsUnique();
        b.HasIndex(s => s.AccountId);
        b.HasIndex(s => s.AbsoluteExpiresAt);
    }
}

internal sealed class EmailTokenConfiguration : IEntityTypeConfiguration<EmailToken>
{
    public void Configure(EntityTypeBuilder<EmailToken> b)
    {
        b.ToTable("email_tokens");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Purpose).HasMaxLength(20).IsRequired();
        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => new { t.AccountId, t.Purpose });
    }
}
