using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Staff;

public enum StaffPasskeyOutcome { Ok, InvalidToken, InvalidChallenge, InvalidCredential, AlreadyRegistered, TooMany, ReauthenticationRequired, LastPasskey, NotFound, InvalidName }

public sealed record StaffSignIn(string Token, StaffAccount Account);

/// <summary>
/// Staff passkeys (decision M7-01): the only way a staff member proves who they are. The WebAuthn plumbing is the buyer's (Fido2NetLib, the same relying party, user verification required, a
/// discoverable credential); what differs is that a challenge here has its own purposes (a buyer's challenge cannot be spent on a staff sign-in, nor the reverse), the credentials live in
/// <c>staff_passkeys</c>, and there is no password to fall back on.
/// </summary>
public sealed class StaffPasskeyService(MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> options, IOptions<AuthOptions> authOptions, IFido2 fido2, StaffService staff)
{
    public const string Enrol = "staff-enrol", SignInPurpose = "staff-signin", Reauth = "staff-reauth", AddKey = "staff-addkey";
    private StaffOptions O => options.Value;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    // ---------- challenges ----------

    private async Task<Guid> StoreChallengeAsync(string purpose, Guid? staffId, Guid? bindTo, string optionsJson, CancellationToken ct)
    {
        var now = Now;
        var id = Guid.NewGuid();
        db.WebAuthnChallenges.Add(new WebAuthnChallenge { Id = id, Purpose = purpose, AccountId = staffId, SessionId = bindTo, Options = optionsJson, CreatedAt = now, ExpiresAt = now.AddMinutes(Math.Clamp(authOptions.Value.PasskeyChallengeMinutes, 1, 15)) });
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>One atomic UPDATE is the claim (right purpose, unused, not expired, made for this staff member and this session or enrolment).</summary>
    private async Task<string?> ConsumeChallengeAsync(Guid id, string purpose, Guid? staffId, Guid? bindTo, CancellationToken ct)
    {
        var now = Now;
        var rows = await db.WebAuthnChallenges.Where(c => c.Id == id && c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > now && c.AccountId == staffId && c.SessionId == bindTo)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTimeOffset?)now), ct);
        if (rows != 1) return null;
        return await db.WebAuthnChallenges.AsNoTracking().Where(c => c.Id == id).Select(c => c.Options).FirstAsync(ct);
    }

    private static T? Parse<T>(JsonElement? credential) where T : class
    {
        if (credential is not { ValueKind: JsonValueKind.Object } c) return null;
        try { return JsonSerializer.Deserialize<T>(c.GetRawText()); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private Task<string> OptionsFor(StaffAccount a, List<byte[]> existing)
        => Task.FromResult(fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = a.Id.ToByteArray(), Name = a.Email, DisplayName = a.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        }).ToJson());

    private async Task<RegisteredPublicKeyCredential?> VerifyAttestationAsync(string stored, AuthenticatorAttestationRawResponse response, CancellationToken ct)
    {
        try
        {
            return await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(stored),
                IsCredentialIdUniqueToUserCallback = async (p, token) => !await db.StaffPasskeys.AnyAsync(c => c.CredentialId == p.CredentialId, token) && !await db.BuyerPasskeys.AnyAsync(c => c.CredentialId == p.CredentialId, token),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }   // whatever the library throws about hostile input: not accepted
    }

    // ---------- enrolment (first passkey, or a replacement after a lost one) ----------

    private async Task<(StaffEnrolment Enrolment, StaffAccount Account)?> FindEnrolmentAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var hash = Tokens.Hash(token);
        var now = Now;
        var row = await (from e in db.StaffEnrolments.AsNoTracking()
                         join a in db.StaffAccounts.AsNoTracking() on e.StaffId equals a.Id
                         where e.TokenHash == hash && e.UsedAt == null && e.ExpiresAt > now && a.DisabledAt == null
                         select new { e, a }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.e, row.a);
    }

    public async Task<(StaffPasskeyOutcome Outcome, Guid ChallengeId, string? Options)> BeginEnrolmentAsync(string? token, CancellationToken ct)
    {
        if (await FindEnrolmentAsync(token, ct) is not { } found) return (StaffPasskeyOutcome.InvalidToken, default, null);
        var existing = await db.StaffPasskeys.AsNoTracking().Where(p => p.StaffId == found.Account.Id).Select(p => p.CredentialId).ToListAsync(ct);
        if (existing.Count >= Math.Max(1, O.MaxPasskeys)) return (StaffPasskeyOutcome.TooMany, default, null);
        var json = await OptionsFor(found.Account, existing);
        return (StaffPasskeyOutcome.Ok, await StoreChallengeAsync(Enrol, found.Account.Id, found.Enrolment.Id, json, ct), json);
    }

    /// <summary>
    /// Registers the passkey and uses the enrolment up in ONE transaction: the claim of the enrolment is a conditional UPDATE, so two parallel completions with one link make one passkey.
    /// A passkey that does not verify burns the challenge but not the link.
    /// </summary>
    public async Task<(StaffPasskeyOutcome Outcome, StaffSignIn? Session)> CompleteEnrolmentAsync(string? token, Guid challengeId, string? name, JsonElement? credential, CancellationToken ct)
    {
        if (PasskeyService.NameProblem(name ?? "Passkey") is not null) return (StaffPasskeyOutcome.InvalidName, null);
        if (await FindEnrolmentAsync(token, ct) is not { } found) return (StaffPasskeyOutcome.InvalidToken, null);
        var stored = await ConsumeChallengeAsync(challengeId, Enrol, found.Account.Id, found.Enrolment.Id, ct);
        if (stored is null) return (StaffPasskeyOutcome.InvalidChallenge, null);
        var response = Parse<AuthenticatorAttestationRawResponse>(credential);
        if (response is null) return (StaffPasskeyOutcome.InvalidCredential, null);
        var registered = await VerifyAttestationAsync(stored, response, ct);
        if (registered is null) return (StaffPasskeyOutcome.InvalidCredential, null);
        var now = Now;
        var label = string.IsNullOrWhiteSpace(name) ? "Passkey" : AuthValidation.NormaliseText(name);
        string sessionToken;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimed = await db.StaffEnrolments.Where(e => e.Id == found.Enrolment.Id && e.UsedAt == null && e.ExpiresAt > now).ExecuteUpdateAsync(s => s.SetProperty(e => e.UsedAt, (DateTimeOffset?)now), ct);
            if (claimed != 1) return (StaffPasskeyOutcome.InvalidToken, null);
            // the account row is the lock for the cap, and its current state (not the one read before) decides
            if (await db.StaffAccounts.Where(a => a.Id == found.Account.Id && a.DisabledAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, now), ct) != 1) return (StaffPasskeyOutcome.InvalidToken, null);
            if (await db.StaffPasskeys.CountAsync(p => p.StaffId == found.Account.Id, ct) >= Math.Max(1, O.MaxPasskeys)) return (StaffPasskeyOutcome.TooMany, null);
            db.StaffPasskeys.Add(new StaffPasskey { Id = Guid.NewGuid(), StaffId = found.Account.Id, CredentialId = registered.Id, PublicKey = registered.PublicKey, SignCount = registered.SignCount, AaGuid = registered.AaGuid, Name = label, BackedUp = registered.IsBackedUp, CreatedAt = now });
            await db.StaffAccounts.Where(a => a.Id == found.Account.Id && a.EnrolledAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.EnrolledAt, (DateTimeOffset?)now), ct);
            await db.StaffAccounts.Where(a => a.Id == found.Account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSignInAt, (DateTimeOffset?)now), ct);
            var fresh = await db.StaffAccounts.AsNoTracking().FirstAsync(a => a.Id == found.Account.Id, ct);
            StaffAudit.Add(db, time, null, "staff.enrol", "staff", fresh.Id.ToString("D"), found.Enrolment.Bootstrap ? "first passkey from the bootstrap token" : "passkey registered from an enrolment link", new { bootstrap = found.Enrolment.Bootstrap }, fresh.Role);
            await db.SaveChangesAsync(ct);
            sessionToken = await staff.CreateSessionAsync(fresh.Id, fresh.SessionEpoch, ct);
            await tx.CommitAsync(ct);
            return (StaffPasskeyOutcome.Ok, new StaffSignIn(sessionToken, fresh));
        }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return (StaffPasskeyOutcome.AlreadyRegistered, null); }
    }

    // ---------- sign-in ----------

    public async Task<(Guid ChallengeId, string Options)> BeginSignInAsync(CancellationToken ct)
    {
        var json = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = [], UserVerification = UserVerificationRequirement.Required }).ToJson();
        return (await StoreChallengeAsync(SignInPurpose, null, null, json, ct), json);
    }

    /// <summary>Every failure is the same answer (and nothing says whether the credential or the account was the problem).</summary>
    public async Task<StaffSignIn?> SignInAsync(Guid challengeId, JsonElement? credential, CancellationToken ct)
    {
        var stored = await ConsumeChallengeAsync(challengeId, SignInPurpose, null, null, ct);
        var response = Parse<AuthenticatorAssertionRawResponse>(credential);
        if (stored is null || response?.RawId is not { Length: > 0 } rawId) return null;
        var passkey = await db.StaffPasskeys.AsNoTracking().FirstOrDefaultAsync(p => p.CredentialId == rawId, ct);
        if (passkey is null) return null;
        if (response.Response.UserHandle is not { Length: > 0 } handle || !handle.AsSpan().SequenceEqual(passkey.StaffId.ToByteArray())) return null;
        var account = await db.StaffAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == passkey.StaffId && a.DisabledAt == null && a.EnrolledAt != null, ct);
        if (account is null) return null;
        if (!await VerifyAssertionAsync(passkey, response, AssertionOptions.FromJson(stored), ct)) return null;
        var now = Now;
        await db.StaffAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSignInAt, (DateTimeOffset?)now), ct);
        var token = await staff.CreateSessionAsync(account.Id, account.SessionEpoch, ct);
        StaffAudit.Add(db, time, null, "staff.sign_in", "staff", account.Id.ToString("D"), "passkey sign-in", role: account.Role);
        await db.SaveChangesAsync(ct);
        return new StaffSignIn(token, account);
    }

    // ---------- step-up ----------

    public async Task<(StaffPasskeyOutcome Outcome, Guid ChallengeId, string? Options)> BeginReauthenticationAsync(StaffPrincipal who, CancellationToken ct)
    {
        var ids = await db.StaffPasskeys.AsNoTracking().Where(p => p.StaffId == who.StaffId).Select(p => p.CredentialId).ToListAsync(ct);
        if (ids.Count == 0) return (StaffPasskeyOutcome.NotFound, default, null);
        var json = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = ids.Select(i => new PublicKeyCredentialDescriptor(i)).ToList(), UserVerification = UserVerificationRequirement.Required }).ToJson();
        return (StaffPasskeyOutcome.Ok, await StoreChallengeAsync(Reauth, who.StaffId, who.SessionId, json, ct), json);
    }

    /// <summary>Stamps <c>authenticated_at</c> on THIS session when one of the member's own passkeys signs the fresh challenge.</summary>
    public async Task<StaffPasskeyOutcome> ReauthenticateAsync(StaffPrincipal who, Guid challengeId, JsonElement? credential, CancellationToken ct)
    {
        var stored = await ConsumeChallengeAsync(challengeId, Reauth, who.StaffId, who.SessionId, ct);
        if (stored is null) return StaffPasskeyOutcome.InvalidChallenge;
        var response = Parse<AuthenticatorAssertionRawResponse>(credential);
        if (response?.RawId is not { Length: > 0 } rawId) return StaffPasskeyOutcome.InvalidCredential;
        var passkey = await db.StaffPasskeys.AsNoTracking().FirstOrDefaultAsync(p => p.CredentialId == rawId && p.StaffId == who.StaffId, ct);
        if (passkey is null || !await VerifyAssertionAsync(passkey, response, AssertionOptions.FromJson(stored), ct)) return StaffPasskeyOutcome.InvalidCredential;
        var now = Now;
        await db.StaffSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthenticatedAt, now), ct);
        StaffAudit.Add(db, time, who, "staff.step_up", "staff", who.StaffId.ToString("D"), "passkey re-check");
        await db.SaveChangesAsync(ct);
        return StaffPasskeyOutcome.Ok;
    }

    private async Task<bool> VerifyAssertionAsync(StaffPasskey passkey, AuthenticatorAssertionRawResponse response, AssertionOptions options, CancellationToken ct)
    {
        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(p.UserHandle is not { Length: > 0 } || p.UserHandle.AsSpan().SequenceEqual(passkey.StaffId.ToByteArray())),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        var now = Now;
        var seen = passkey.SignCount;
        var moved = await db.StaffPasskeys.Where(p => p.Id == passkey.Id && p.SignCount == seen)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SignCount, (long)result.SignCount).SetProperty(p => p.LastUsedAt, (DateTimeOffset?)now).SetProperty(p => p.BackedUp, result.IsBackedUp), ct);
        return moved == 1;
    }

    // ---------- own passkeys ----------

    public sealed record PasskeyView(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool BackedUp);

    public async Task<List<PasskeyView>> ListAsync(Guid staffId, CancellationToken ct)
        => await db.StaffPasskeys.AsNoTracking().Where(p => p.StaffId == staffId).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Select(p => new PasskeyView(p.Id, p.Name, p.CreatedAt, p.LastUsedAt, p.BackedUp)).ToListAsync(ct);

    public async Task<(StaffPasskeyOutcome Outcome, Guid ChallengeId, string? Options)> BeginAddAsync(StaffPrincipal who, CancellationToken ct)
    {
        if (!who.Recent(Now, O.StepUpMinutes)) return (StaffPasskeyOutcome.ReauthenticationRequired, default, null);
        var account = await db.StaffAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == who.StaffId, ct);
        if (account is null) return (StaffPasskeyOutcome.NotFound, default, null);
        var existing = await db.StaffPasskeys.AsNoTracking().Where(p => p.StaffId == who.StaffId).Select(p => p.CredentialId).ToListAsync(ct);
        if (existing.Count >= Math.Max(1, O.MaxPasskeys)) return (StaffPasskeyOutcome.TooMany, default, null);
        var json = await OptionsFor(account, existing);
        return (StaffPasskeyOutcome.Ok, await StoreChallengeAsync(AddKey, who.StaffId, who.SessionId, json, ct), json);
    }

    public async Task<(StaffPasskeyOutcome Outcome, PasskeyView? Passkey)> CompleteAddAsync(StaffPrincipal who, Guid challengeId, string? name, JsonElement? credential, CancellationToken ct)
    {
        if (PasskeyService.NameProblem(name ?? "Passkey") is not null) return (StaffPasskeyOutcome.InvalidName, null);
        if (!who.Recent(Now, O.StepUpMinutes)) return (StaffPasskeyOutcome.ReauthenticationRequired, null);
        var stored = await ConsumeChallengeAsync(challengeId, AddKey, who.StaffId, who.SessionId, ct);
        if (stored is null) return (StaffPasskeyOutcome.InvalidChallenge, null);
        var response = Parse<AuthenticatorAttestationRawResponse>(credential);
        if (response is null) return (StaffPasskeyOutcome.InvalidCredential, null);
        var registered = await VerifyAttestationAsync(stored, response, ct);
        if (registered is null) return (StaffPasskeyOutcome.InvalidCredential, null);
        var now = Now;
        var row = new StaffPasskey { Id = Guid.NewGuid(), StaffId = who.StaffId, CredentialId = registered.Id, PublicKey = registered.PublicKey, SignCount = registered.SignCount, AaGuid = registered.AaGuid, Name = string.IsNullOrWhiteSpace(name) ? "Passkey" : AuthValidation.NormaliseText(name), BackedUp = registered.IsBackedUp, CreatedAt = now };
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.StaffAccounts.Where(a => a.Id == who.StaffId && a.DisabledAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, now), ct) != 1) return (StaffPasskeyOutcome.InvalidChallenge, null);
            if (await db.StaffPasskeys.CountAsync(p => p.StaffId == who.StaffId, ct) >= Math.Max(1, O.MaxPasskeys)) return (StaffPasskeyOutcome.TooMany, null);
            db.StaffPasskeys.Add(row);
            StaffAudit.Add(db, time, who, "staff.passkey_add", "staff", who.StaffId.ToString("D"), "passkey added");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return (StaffPasskeyOutcome.AlreadyRegistered, null); }
        return (StaffPasskeyOutcome.Ok, new PasskeyView(row.Id, row.Name, row.CreatedAt, null, row.BackedUp));
    }

    /// <summary>Needs a recent passkey check. The last passkey is never removed (there is no password to fall back on): an admin issues a new enrolment link for that.</summary>
    public async Task<StaffPasskeyOutcome> RemoveAsync(StaffPrincipal who, Guid id, CancellationToken ct)
    {
        if (!who.Recent(Now, O.StepUpMinutes)) return StaffPasskeyOutcome.ReauthenticationRequired;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.StaffAccounts.Where(a => a.Id == who.StaffId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct) != 1) return StaffPasskeyOutcome.NotFound;
        if (!await db.StaffPasskeys.AnyAsync(p => p.Id == id && p.StaffId == who.StaffId, ct)) return StaffPasskeyOutcome.NotFound;
        if (await db.StaffPasskeys.CountAsync(p => p.StaffId == who.StaffId, ct) <= 1) return StaffPasskeyOutcome.LastPasskey;
        await db.StaffPasskeys.Where(p => p.Id == id && p.StaffId == who.StaffId).ExecuteDeleteAsync(ct);
        StaffAudit.Add(db, time, who, "staff.passkey_remove", "staff", who.StaffId.ToString("D"), "passkey removed");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StaffPasskeyOutcome.Ok;
    }
}
