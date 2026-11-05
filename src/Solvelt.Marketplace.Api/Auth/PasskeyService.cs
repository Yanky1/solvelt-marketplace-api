using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Auth;

/// <summary>The relying party the passkeys are bound to, resolved from configuration (development and tests default to localhost).</summary>
public sealed record WebAuthnSettings(string RpId, string RpName, IReadOnlyList<string> Origins)
{
    public static WebAuthnSettings From(AuthOptions o, IHostEnvironment env)
    {
        var devLike = StartupValidator.IsDevLike(env);
        var rpId = string.IsNullOrWhiteSpace(o.WebAuthnRpId) ? (devLike ? DevDefaults.WebAuthnRpId : "") : o.WebAuthnRpId.Trim();
        var origins = o.WebAuthnOrigins.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        if (origins.Count == 0 && devLike) origins.Add(DevDefaults.WebAuthnOrigin);
        return new WebAuthnSettings(rpId, string.IsNullOrWhiteSpace(o.WebAuthnRpName) ? "Solvelt" : o.WebAuthnRpName.Trim(), origins);
    }
}

public sealed record PasskeyView(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool BackedUp);
public enum PasskeyOutcome { Ok, InvalidChallenge, InvalidCredential, AlreadyRegistered, TooMany, ReauthenticationRequired, LastSignInMethod, NotFound, InvalidName, NoPasskeys }

/// <summary>
/// Passkeys (WebAuthn, decision M1-80): register, list, rename, remove, sign in, and step up (a second way to prove who the buyer is, next to the password). The protocol is
/// Fido2NetLib's; this class owns what it must not decide: the challenges (stored on the server, single use, five minutes, bound to the flow and, for registration and step-up,
/// to the account AND the session that asked), the recency rule for adding and removing a passkey (O-11), the counter check against cloned authenticators, the rule that the last
/// way to sign in cannot be removed, and the notices to the owner. A failed sign-in says only that the passkey was not accepted.
/// </summary>
public sealed class PasskeyService(MarketplaceDb db, TimeProvider time, IOptions<AuthOptions> options, IFido2 fido2, AuthService auth, GuestClaims guestClaims, OutboxWriter outbox)
{
    private AuthOptions O => options.Value;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();
    private static bool Recent(BuyerPrincipal who, DateTimeOffset now) => now - who.AuthenticatedAt <= AccountService.RecentSignIn;

    // ---------- challenges ----------

    private async Task<Guid> StoreChallengeAsync(string purpose, Guid? accountId, Guid? sessionId, string optionsJson, CancellationToken ct)
    {
        var now = Now;
        var id = Guid.NewGuid();
        db.WebAuthnChallenges.Add(new WebAuthnChallenge { Id = id, Purpose = purpose, AccountId = accountId, SessionId = sessionId, Options = optionsJson, CreatedAt = now, ExpiresAt = now.AddMinutes(Math.Clamp(O.PasskeyChallengeMinutes, 1, 15)) });
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>One atomic UPDATE is the claim: right purpose, unused, not expired, made for this account and session. Whoever gets the row owns the challenge; a second try gets nothing.</summary>
    private async Task<string?> ConsumeChallengeAsync(Guid id, string purpose, Guid? accountId, Guid? sessionId, CancellationToken ct)
    {
        var now = Now;
        var rows = await db.WebAuthnChallenges.Where(c => c.Id == id && c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > now && c.AccountId == accountId && c.SessionId == sessionId)
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

    // ---------- list, rename, remove ----------

    public async Task<List<PasskeyView>> ListAsync(Guid accountId, CancellationToken ct)
        => await db.BuyerPasskeys.AsNoTracking().Where(p => p.AccountId == accountId).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new PasskeyView(p.Id, p.Name, p.CreatedAt, p.LastUsedAt, p.BackedUp)).ToListAsync(ct);

    public static string? NameProblem(string? name)
        => string.IsNullOrWhiteSpace(name) ? "Give the passkey a name." : AuthValidation.NormaliseText(name).Length > 60 ? "Use at most 60 characters." : AuthValidation.HasForbiddenCharacters(name) ? "That name has characters that are not allowed." : null;

    public async Task<PasskeyOutcome> RenameAsync(Guid accountId, Guid id, string? name, CancellationToken ct)
    {
        if (NameProblem(name) is not null) return PasskeyOutcome.InvalidName;
        var clean = AuthValidation.NormaliseText(name);
        var rows = await db.BuyerPasskeys.Where(p => p.Id == id && p.AccountId == accountId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, clean), ct);
        return rows == 1 ? PasskeyOutcome.Ok : PasskeyOutcome.NotFound;
    }

    /// <summary>Needs a recent sign-in (O-11). The last way to sign in is never removed: an account with no password and one passkey keeps it.</summary>
    public async Task<(PasskeyOutcome Outcome, string? Token)> RemoveAsync(BuyerPrincipal who, Guid id, CancellationToken ct)
    {
        if (!Recent(who, Now)) return (PasskeyOutcome.ReauthenticationRequired, null);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // the account's row lock (a no-op UPDATE) puts two removals of one buyer in a row, so two parallel removals cannot both see "another one is left"
        if (await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct) != 1) return (PasskeyOutcome.NotFound, null);
        if (!await db.BuyerPasskeys.AnyAsync(p => p.Id == id && p.AccountId == who.AccountId, ct)) return (PasskeyOutcome.NotFound, null);
        var hasPassword = await db.BuyerAccounts.AsNoTracking().AnyAsync(a => a.Id == who.AccountId && a.PasswordHash != null, ct);
        var count = await db.BuyerPasskeys.CountAsync(p => p.AccountId == who.AccountId, ct);
        if (!hasPassword && count <= 1) return (PasskeyOutcome.LastSignInMethod, null);
        await db.BuyerPasskeys.Where(p => p.Id == id && p.AccountId == who.AccountId).ExecuteDeleteAsync(ct);
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasskeyRemoved, who.AccountId));
        var token = await auth.RotateSessionsAsync(who.AccountId, ct, carryClocksOf: who);   // every session ends, a fresh one is opened (M1-87)
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (PasskeyOutcome.Ok, token);
    }

    /// <summary>Every passkey of the account, in one transaction under the account's row lock. Needs a recent sign-in and a password (an account whose only way in is its passkeys keeps them: <see cref="PasskeyOutcome.LastSignInMethod"/>).</summary>
    public async Task<(PasskeyOutcome Outcome, string? Token)> RemoveAllAsync(BuyerPrincipal who, CancellationToken ct)
    {
        if (!Recent(who, Now)) return (PasskeyOutcome.ReauthenticationRequired, null);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct) != 1) return (PasskeyOutcome.NotFound, null);
        if (!await db.BuyerPasskeys.AnyAsync(p => p.AccountId == who.AccountId, ct)) return (PasskeyOutcome.NoPasskeys, null);
        if (!await db.BuyerAccounts.AsNoTracking().AnyAsync(a => a.Id == who.AccountId && a.PasswordHash != null, ct)) return (PasskeyOutcome.LastSignInMethod, null);
        await db.BuyerPasskeys.Where(p => p.AccountId == who.AccountId).ExecuteDeleteAsync(ct);
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasskeyRemoved, who.AccountId));
        var token = await auth.RotateSessionsAsync(who.AccountId, ct, carryClocksOf: who);   // every session ends, a fresh one is opened (M1-87)
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (PasskeyOutcome.Ok, token);
    }

    // ---------- registration ----------

    /// <summary>Needs a recent sign-in (O-11): adding a way to sign in is as sensitive as changing the password.</summary>
    public async Task<(PasskeyOutcome Outcome, Guid ChallengeId, string? Options)> BeginRegistrationAsync(BuyerPrincipal who, CancellationToken ct)
    {
        if (!Recent(who, Now)) return (PasskeyOutcome.ReauthenticationRequired, default, null);
        var existing = await db.BuyerPasskeys.AsNoTracking().Where(p => p.AccountId == who.AccountId).Select(p => p.CredentialId).ToListAsync(ct);
        if (existing.Count >= Math.Max(1, O.MaxPasskeys)) return (PasskeyOutcome.TooMany, default, null);
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = who.AccountId.ToByteArray(), Name = who.Email, DisplayName = who.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var json = options.ToJson();
        return (PasskeyOutcome.Ok, await StoreChallengeAsync(ChallengePurpose.Register, who.AccountId, who.SessionId, json, ct), json);
    }

    public async Task<(PasskeyOutcome Outcome, PasskeyView? Passkey)> CompleteRegistrationAsync(BuyerPrincipal who, Guid challengeId, string? name, JsonElement? credential, CancellationToken ct)
    {
        if (NameProblem(name ?? "Passkey") is not null) return (PasskeyOutcome.InvalidName, null);
        // the recency (O-11) holds at completion too, not only when the options were asked for: a challenge lives 5 minutes, so without this the window was up to 15. The challenge is not used up by this refusal.
        if (!Recent(who, Now)) return (PasskeyOutcome.ReauthenticationRequired, null);
        var label = string.IsNullOrWhiteSpace(name) ? "Passkey" : AuthValidation.NormaliseText(name);
        var stored = await ConsumeChallengeAsync(challengeId, ChallengePurpose.Register, who.AccountId, who.SessionId, ct);
        if (stored is null) return (PasskeyOutcome.InvalidChallenge, null);
        var response = Parse<AuthenticatorAttestationRawResponse>(credential);
        if (response is null) return (PasskeyOutcome.InvalidCredential, null);
        if (response.RawId is { Length: > 0 } raw && await db.BuyerPasskeys.AnyAsync(p => p.CredentialId == raw, ct)) return (PasskeyOutcome.AlreadyRegistered, null);
        if (await db.BuyerPasskeys.CountAsync(p => p.AccountId == who.AccountId, ct) >= Math.Max(1, O.MaxPasskeys)) return (PasskeyOutcome.TooMany, null);
        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(stored),
                IsCredentialIdUniqueToUserCallback = async (p, token) => !await db.BuyerPasskeys.AnyAsync(c => c.CredentialId == p.CredentialId, token),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (PasskeyOutcome.InvalidCredential, null); }   // whatever the library throws about hostile input: not accepted
        var now = Now;
        var row = new BuyerPasskey { Id = Guid.NewGuid(), AccountId = who.AccountId, CredentialId = registered.Id, PublicKey = registered.PublicKey, SignCount = registered.SignCount, AaGuid = registered.AaGuid, Name = label, BackedUp = registered.IsBackedUp, CreatedAt = now };
        try
        {
            // the cap is counted under the account's row lock, in the same transaction as the insert, so parallel completions of one buyer cannot all see room (the count above is only a cheap early answer)
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct) != 1) return (PasskeyOutcome.InvalidChallenge, null);
            if (await db.BuyerPasskeys.CountAsync(p => p.AccountId == who.AccountId, ct) >= Math.Max(1, O.MaxPasskeys)) return (PasskeyOutcome.TooMany, null);
            db.BuyerPasskeys.Add(row);
            outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasskeyAdded, who.AccountId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return (PasskeyOutcome.AlreadyRegistered, null); }
        return (PasskeyOutcome.Ok, new PasskeyView(row.Id, row.Name, row.CreatedAt, null, row.BackedUp));
    }

    // ---------- sign-in ----------

    /// <summary>Anonymous: options for a sign-in with a discoverable passkey (no account is named; the authenticator offers its passkeys for this site).</summary>
    public async Task<(Guid ChallengeId, string Options)> BeginSignInAsync(CancellationToken ct)
    {
        var json = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = [], UserVerification = UserVerificationRequirement.Required }).ToJson();
        return (await StoreChallengeAsync(ChallengePurpose.SignIn, null, null, json, ct), json);
    }

    /// <summary>
    /// Verifies the assertion against the stored public key and counter, then opens a session exactly like a password sign-in does (verified, not deleted, the epoch of the row that
    /// was read). Every failure is the same answer.
    /// </summary>
    public async Task<SignInResult> SignInAsync(Guid challengeId, JsonElement? credential, CancellationToken ct)
    {
        var fail = new SignInResult(SignInStatus.InvalidCredentials);
        var stored = await ConsumeChallengeAsync(challengeId, ChallengePurpose.SignIn, null, null, ct);
        var response = Parse<AuthenticatorAssertionRawResponse>(credential);
        if (stored is null || response?.RawId is not { Length: > 0 } rawId) return fail;
        // a discoverable sign-in names no account, so the handle the authenticator returns is what ties the credential to its owner (WebAuthn level 2, 7.2 step 6): it must be there
        var passkey = await db.BuyerPasskeys.AsNoTracking().FirstOrDefaultAsync(p => p.CredentialId == rawId, ct);
        if (passkey is null) return fail;
        if (response.Response.UserHandle is not { Length: > 0 } handle || !handle.AsSpan().SequenceEqual(passkey.AccountId.ToByteArray())) return fail;
        var account = await db.BuyerAccounts.FirstOrDefaultAsync(a => a.Id == passkey.AccountId && a.DeletedAt == null && a.EmailVerifiedAt != null && a.Email != null && a.StaffLockedAt == null, ct);
        if (account is null) return fail;
        if (!await VerifyAssertionAsync(passkey, response, AssertionOptions.FromJson(stored), ct)) return fail;
        var token = await auth.CreateSessionAsync(account.Id, ct, account.SessionEpoch);
        await guestClaims.ClaimAsync(account.Id, account.Email!, ct);
        return new SignInResult(SignInStatus.Ok, token, account);
    }

    // ---------- step-up with a passkey ----------

    public async Task<(PasskeyOutcome Outcome, Guid ChallengeId, string? Options)> BeginReauthenticationAsync(BuyerPrincipal who, CancellationToken ct)
    {
        var ids = await db.BuyerPasskeys.AsNoTracking().Where(p => p.AccountId == who.AccountId).Select(p => p.CredentialId).ToListAsync(ct);
        if (ids.Count == 0) return (PasskeyOutcome.NoPasskeys, default, null);
        var json = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = ids.Select(i => new PublicKeyCredentialDescriptor(i)).ToList(), UserVerification = UserVerificationRequirement.Required }).ToJson();
        return (PasskeyOutcome.Ok, await StoreChallengeAsync(ChallengePurpose.Reauthenticate, who.AccountId, who.SessionId, json, ct), json);
    }

    /// <summary>Like <c>POST /auth/reauthenticate</c> with the password: stamps <c>authenticated_at</c> on THIS session. Only one of the buyer's own passkeys counts.</summary>
    public async Task<PasskeyOutcome> ReauthenticateAsync(BuyerPrincipal who, Guid challengeId, JsonElement? credential, CancellationToken ct)
    {
        var stored = await ConsumeChallengeAsync(challengeId, ChallengePurpose.Reauthenticate, who.AccountId, who.SessionId, ct);
        if (stored is null) return PasskeyOutcome.InvalidChallenge;
        var response = Parse<AuthenticatorAssertionRawResponse>(credential);
        if (response?.RawId is not { Length: > 0 } rawId) return PasskeyOutcome.InvalidCredential;
        var passkey = await db.BuyerPasskeys.AsNoTracking().FirstOrDefaultAsync(p => p.CredentialId == rawId && p.AccountId == who.AccountId, ct);
        if (passkey is null || !await VerifyAssertionAsync(passkey, response, AssertionOptions.FromJson(stored), ct)) return PasskeyOutcome.InvalidCredential;
        var now = Now;
        await db.BuyerSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthenticatedAt, now), ct);
        return PasskeyOutcome.Ok;
    }

    /// <summary>The signature, the origin, the relying party, the user-verification flag and the counter; then the new counter is written only if nobody else moved it (a replay or a clone loses).</summary>
    private async Task<bool> VerifyAssertionAsync(BuyerPasskey passkey, AuthenticatorAssertionRawResponse response, AssertionOptions options, CancellationToken ct)
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
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(p.UserHandle is not { Length: > 0 } || p.UserHandle.AsSpan().SequenceEqual(passkey.AccountId.ToByteArray())),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }   // whatever the library throws about hostile input: not accepted
        var now = Now;
        var seen = passkey.SignCount;
        var moved = await db.BuyerPasskeys.Where(p => p.Id == passkey.Id && p.SignCount == seen)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SignCount, (long)result.SignCount).SetProperty(p => p.LastUsedAt, (DateTimeOffset?)now).SetProperty(p => p.BackedUp, result.IsBackedUp), ct);
        return moved == 1;
    }
}
