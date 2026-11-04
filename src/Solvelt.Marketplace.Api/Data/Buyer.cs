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
    /// <summary>When the buyer last proved who they are (password or passkey) in this session. Sensitive changes need it within 10 minutes (decision O-11).</summary>
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class EmailTokenPurpose
{
    public const string Verify = "verify", Reset = "reset";
    /// <summary>A placeholder row written for an address that has no account, so that "forgot password" does the same writes for every address (decision M1-65). Always already used and expired; purged after an hour.</summary>
    public const string Decoy = "decoy";
}

/// <summary>
/// The sign-in gate's memory for ONE address, known or not (decision M1-63): how many attempts were claimed and when the next one is allowed. The key is a
/// keyed hash of the normalised address, so the table holds no address. Success or a password reset deletes the row.
/// </summary>
public sealed class SignInAttempt
{
    public string Key { get; set; } = "";
    public int Failures { get; set; }
    public DateTimeOffset NextAllowedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The sign-in gate's per-client-IP failure budget for the current hour, stored in the database so every instance shares it. The key is a keyed hash of the IP.</summary>
public sealed class SignInIpBudget
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset WindowStart { get; set; }
}

internal sealed class SignInAttemptConfiguration : IEntityTypeConfiguration<SignInAttempt>
{
    public void Configure(EntityTypeBuilder<SignInAttempt> b)
    {
        b.ToTable("signin_attempts");
        b.HasKey(a => a.Key);
        b.Property(a => a.Key).HasMaxLength(64).ValueGeneratedNever();
        b.HasIndex(a => a.UpdatedAt);
    }
}

internal sealed class SignInIpBudgetConfiguration : IEntityTypeConfiguration<SignInIpBudget>
{
    public void Configure(EntityTypeBuilder<SignInIpBudget> b)
    {
        b.ToTable("signin_ip_budgets");
        b.HasKey(a => a.Key);
        b.Property(a => a.Key).HasMaxLength(64).ValueGeneratedNever();
        b.HasIndex(a => a.WindowStart);
    }
}

/// <summary>
/// A one-time emailed link (verify address, reset password). The row holds NO secret: the link carries <c>&lt;id&gt;.&lt;HMAC(Auth:EmailLinkKey, purpose|id)&gt;</c>,
/// computed when the mail is sent and checked when the link is used, so a copy of the database (or an outbox payload) cannot produce a working link.
/// </summary>
public sealed class EmailToken
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Purpose { get; set; } = "";
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
        b.HasIndex(t => new { t.AccountId, t.Purpose });
    }
}
