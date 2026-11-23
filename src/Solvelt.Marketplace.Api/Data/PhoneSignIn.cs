using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// One SMS code sent for the WEB (decision W3-02): a sign-in by phone, or a step-up of a session already signed in. It holds NO phone number: only the account the number belonged to when the code
/// was sent (null when no account could sign in with it, so a code is still sent and nothing tells the two apart), and an HMAC of the code under its own key (<c>Auth:PhoneCodeKey</c>), so a copy
/// of the table alone gives no working code. Single use, 10 minutes, 5 tries (the same limits as the phone line's <c>PhoneCodeService</c>). Deleted an hour after it ends (PII table).
/// </summary>
public sealed class PhoneSignInChallenge
{
    public Guid Id { get; set; }
    /// <summary><see cref="PhoneSignInPurposes.SignIn"/>, <see cref="PhoneSignInPurposes.StepUp"/> or <see cref="PhoneSignInPurposes.DeleteAccount"/>: a code for one purpose never works for another.</summary>
    public string Purpose { get; set; } = PhoneSignInPurposes.SignIn;
    public Guid? AccountId { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public static class PhoneSignInPurposes
{
    public const string SignIn = "sign-in", StepUp = "step-up";
    /// <summary>Deleting an account that has no password (W3 follow-up): a code texted for exactly this, so a sign-in or step-up code can never delete an account.</summary>
    public const string DeleteAccount = "delete-account";
}

internal sealed class PhoneSignInChallengeConfiguration : IEntityTypeConfiguration<PhoneSignInChallenge>
{
    public void Configure(EntityTypeBuilder<PhoneSignInChallenge> b)
    {
        b.ToTable("phone_sign_in_challenges");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Purpose).HasMaxLength(20).IsRequired();
        b.Property(x => x.CodeHash).HasMaxLength(32).IsRequired();
        b.HasIndex(x => x.ExpiresAt);
    }
}
