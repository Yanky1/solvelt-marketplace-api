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
    /// <summary>
    /// Set when the test-only "Verify this account" (M9) marked the address verified WITHOUT a mailbox proof. While it is set no guest order is ever attached to the account (the
    /// address was never proven, so an order placed later by whoever owns that mailbox must not move to this account); a real proof (the verification link) or a password reset
    /// through the mailbox clears it. Null for every ordinary account.
    /// </summary>
    public DateTimeOffset? DemoVerifiedAt { get; set; }
    /// <summary>Optional contact number (profile). Personal data: erased with the account.</summary>
    public string? Phone { get; set; }
    /// <summary><c>relay-&lt;random&gt;@&lt;relay domain&gt;</c>, generated once, unique, the only address the portal and sellers see.</summary>
    public string RelayEmail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>
    /// Bumped by a password reset and by sign-out-all. A session counts only while its <see cref="BuyerSession.Epoch"/> equals this, so a session created at the same
    /// moment as the bump (a rotation racing a reset under READ COMMITTED) is dead on arrival instead of surviving it (review of 44b5d27, M3).
    /// </summary>
    public long SessionEpoch { get; set; }
    /// <summary>When the account was soft-deleted: sign-in and every session stop at once; the personal data goes in the redaction job (<see cref="RedactedAt"/>).</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>When the personal data of a deleted account was erased (email, name, phone, password hash, addresses, links). The row stays as an opaque id for orders.</summary>
    public DateTimeOffset? RedactedAt { get; set; }
    /// <summary>
    /// Set by staff (M7-07): sign-in, passkey sign-in and every session stop at once; unlocking clears it (old sessions stay dead). Holds no reason: the reason lives in the staff audit log.
    /// </summary>
    public DateTimeOffset? StaffLockedAt { get; set; }
}

/// <summary>A saved address book entry (US and international). Exactly one per buyer is the default, enforced by a partial unique index.</summary>
public sealed class BuyerAddress : Solvelt.Marketplace.Api.Demo.IDemoMarked
{
    /// <summary>Created by "Fill with demo data" (M9): removed by "Remove demo data", never sent to the portal, a payment provider or a mailbox.</summary>
    public bool IsDemo { get; set; }
    /// <summary>Which press of the button created it.</summary>
    public Guid? DemoBatchId { get; set; }
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string FullName { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string? Region { get; set; }
    public string PostalCode { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string Country { get; set; } = "";
    public string? Phone { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class BuyerAddressConfiguration : IEntityTypeConfiguration<BuyerAddress>
{
    public void Configure(EntityTypeBuilder<BuyerAddress> b)
    {
        b.ToTable("buyer_addresses");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.FullName).HasMaxLength(100).IsRequired();
        b.Property(a => a.Line1).HasMaxLength(200).IsRequired();
        b.Property(a => a.Line2).HasMaxLength(200);
        b.Property(a => a.City).HasMaxLength(100).IsRequired();
        b.Property(a => a.Region).HasMaxLength(100);
        b.Property(a => a.PostalCode).HasMaxLength(20).IsRequired();
        b.Property(a => a.Country).HasMaxLength(2).IsRequired();
        b.Property(a => a.Phone).HasMaxLength(30);
        b.HasIndex(a => new { a.AccountId, a.CreatedAt });
        // The partial unique index "one default per buyer" is added in MarketplaceDb.OnModelCreating: its filter is provider specific.
    }
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
    /// <summary>The account's <see cref="BuyerAccount.SessionEpoch"/> when this session was made; a rotation copies it from the session it replaces.</summary>
    public long Epoch { get; set; }
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
        b.Property(a => a.Phone).HasMaxLength(30);
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
