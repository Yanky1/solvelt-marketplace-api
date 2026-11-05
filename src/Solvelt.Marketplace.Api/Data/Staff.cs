using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class StaffRoleNames
{
    public const string Support = "support", Finance = "finance", Admin = "admin";
    public static readonly IReadOnlyList<string> All = [Support, Finance, Admin];
}

/// <summary>
/// A member of the marketplace staff (decision M7-01). A SEPARATE account type from <see cref="BuyerAccount"/>: its own tables, its own session tokens, no password (passkeys only).
/// The email and name are the staff member's own, not a buyer's: see the PII table.
/// </summary>
public sealed class StaffAccount
{
    public Guid Id { get; set; }
    /// <summary>Trimmed, NFC and lower-cased; unique.</summary>
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>One of <see cref="StaffRoleNames"/>.</summary>
    public string Role { get; set; } = StaffRoleNames.Support;
    /// <summary>When the first passkey was registered. An account that never enrolled cannot sign in.</summary>
    public DateTimeOffset? EnrolledAt { get; set; }
    /// <summary>Set by an admin: sign-in and every session stop at once.</summary>
    public DateTimeOffset? DisabledAt { get; set; }
    /// <summary>A session counts only while its epoch equals this; bumped by disable, role change, passkey revocation and sign-out-everywhere.</summary>
    public long SessionEpoch { get; set; }
    /// <summary>The staff member who made the account; null for the bootstrap account (made from configuration).</summary>
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
}

/// <summary>A passkey of a staff member. Only the public key is stored.</summary>
public sealed class StaffPasskey
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public long SignCount { get; set; }
    public Guid AaGuid { get; set; }
    public string Name { get; set; } = "";
    public bool BackedUp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>An opaque staff session; only the SHA-256 of the token is stored. Short idle and absolute lifetimes, revocable, bound to the account's epoch.</summary>
public sealed class StaffSession
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    /// <summary>When the staff member last proved themselves with a passkey in this session; sensitive actions need it within <c>Staff:StepUpMinutes</c>.</summary>
    public DateTimeOffset AuthenticatedAt { get; set; }
    public long Epoch { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A one-time link that lets a staff member register their first passkey (or a replacement after a lost one). Only the hash of the secret is stored.</summary>
public sealed class StaffEnrolment
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    /// <summary>True for the one made from the bootstrap token in configuration.</summary>
    public bool Bootstrap { get; set; }
}

/// <summary>
/// One staff action: who, what, why and when. Append only: the application has no update or delete path for it except the retention job. It holds ids (the staff member, the buyer
/// account, the order), never a buyer's name, address or email. <see cref="Reason"/> is typed by staff and is refused when it looks like an email address or a long number.
/// </summary>
public sealed class StaffAuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>Null for the system (bootstrap, retention).</summary>
    public Guid? ActorId { get; set; }
    public string ActorRole { get; set; } = "";
    public Guid? SessionId { get; set; }
    /// <summary>Dotted verb: <c>staff.sign_in</c>, <c>buyer.lock</c>, <c>refund.retry</c>.</summary>
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Small structured facts (old and new role, counts): never personal data.</summary>
    public string? Detail { get; set; }
}

internal sealed class StaffAccountConfiguration : IEntityTypeConfiguration<StaffAccount>
{
    public void Configure(EntityTypeBuilder<StaffAccount> b)
    {
        b.ToTable("staff_accounts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Email).HasMaxLength(254).IsRequired();
        b.Property(a => a.DisplayName).HasMaxLength(60).IsRequired();
        b.Property(a => a.Role).HasMaxLength(20).IsRequired();
        b.HasIndex(a => a.Email).IsUnique();
    }
}

internal sealed class StaffPasskeyConfiguration : IEntityTypeConfiguration<StaffPasskey>
{
    public void Configure(EntityTypeBuilder<StaffPasskey> b)
    {
        b.ToTable("staff_passkeys");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.CredentialId).HasMaxLength(1024).IsRequired();
        b.Property(p => p.PublicKey).HasMaxLength(2048).IsRequired();
        b.Property(p => p.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(p => p.CredentialId).IsUnique();
        b.HasIndex(p => p.StaffId);
    }
}

internal sealed class StaffSessionConfiguration : IEntityTypeConfiguration<StaffSession>
{
    public void Configure(EntityTypeBuilder<StaffSession> b)
    {
        b.ToTable("staff_sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(s => s.TokenHash).IsUnique();
        b.HasIndex(s => s.StaffId);
    }
}

internal sealed class StaffEnrolmentConfiguration : IEntityTypeConfiguration<StaffEnrolment>
{
    public void Configure(EntityTypeBuilder<StaffEnrolment> b)
    {
        b.ToTable("staff_enrolments");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(e => e.TokenHash).IsUnique();
        b.HasIndex(e => e.StaffId);
    }
}

internal sealed class StaffAuditEntryConfiguration : IEntityTypeConfiguration<StaffAuditEntry>
{
    public void Configure(EntityTypeBuilder<StaffAuditEntry> b)
    {
        b.ToTable("staff_audit");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.ActorRole).HasMaxLength(20).IsRequired();
        b.Property(e => e.Action).HasMaxLength(60).IsRequired();
        b.Property(e => e.TargetType).HasMaxLength(40);
        b.Property(e => e.TargetId).HasMaxLength(80);
        b.Property(e => e.Reason).HasMaxLength(300).IsRequired();
        b.Property(e => e.Detail).HasMaxLength(1000);
        b.HasIndex(e => e.At);
        b.HasIndex(e => e.ActorId);
        b.HasIndex(e => new { e.TargetType, e.TargetId });
    }
}
