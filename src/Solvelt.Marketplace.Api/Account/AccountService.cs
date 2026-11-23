using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Account;

public sealed record ProfileView(Guid Id, string DisplayName, string Email, string? Phone);
public sealed record AddressView(Guid Id, string FullName, string Line1, string? Line2, string City, string? Region, string PostalCode, string Country, string? Phone, double? Lat, double? Lng, bool IsDefault);
public enum AccountOutcome { Ok, NotFound, TooMany, ReauthenticationRequired, InvalidCredentials, RateLimited, WeakPassword, Conflict, EmailRequired, AlreadySet, InvalidEmail, PasswordRequired, InvalidCode, CodeExpired, Unavailable }
/// <summary>How the signed-in buyer can sign in (W3-02): what the account page offers to add. <see cref="PhoneLast4"/> only, never the whole number.</summary>
public sealed record SignInMethods(string? Email, bool EmailVerified, string? PendingEmail, bool HasPassword, int Passkeys, string? PhoneLast4);
public sealed record AccountResult(AccountOutcome Outcome, string? Problem = null, int RetryAfterSeconds = 0, string? Token = null);

/// <summary>
/// The buyer's own data: profile, address book, password change, account deletion. Another buyer's address is simply not found. The address book keeps exactly one
/// default under parallel requests: the database refuses a second one (partial unique index) and every writer that loses that race starts again from what it now sees.
/// </summary>
public sealed class AccountService(MarketplaceDb db, TimeProvider time, AuthService auth, PasswordService passwords, SignInGate gate, GateKeys gateKeys, OutboxWriter outbox, IOptions<AuthOptions> authOptions, IEnumerable<IAccountDeletionHook> deletionHooks, PhoneSignInService phoneCodes)
{
    /// <summary>How recently the buyer must have proven who they are for a sensitive change (decision O-11).</summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(10);
    private const int Attempts = 12;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    /// <summary>
    /// A conflict worth another try: a unique violation (another request made a default first), a SQLite lock that was not freed in time (SQLITE_BUSY 5, SQLITE_LOCKED 6) or a PostgreSQL serialization failure or
    /// deadlock (40001, 40P01). Anything else is a real error and is not retried. After the attempts the caller gets <see cref="TooBusyException"/>: a 503 with Retry-After, never a 500.
    /// </summary>
    public static bool IsRetryable(Exception ex)
    {
        if (IsUniqueViolation(ex)) return true;
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is Npgsql.PostgresException { SqlState: "40001" or "40P01" }) return true;
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }) return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    /// <summary>A short random pause that grows with the attempt, so requests that collided do not collide again in step.</summary>
    private static Task BackOffAsync(int attempt, CancellationToken ct) => Task.Delay(Random.Shared.Next(2, 12) * (attempt + 1), ct);

    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 }) return true;   // SQLITE_CONSTRAINT_UNIQUE or _PRIMARYKEY: code 19 alone covers every constraint (not null, check, foreign key), which must not be retried
            if (e.InnerException is null) break;
        }
        return false;
    }

    private bool Recent(BuyerPrincipal who) => Now - who.AuthenticatedAt <= RecentSignIn;
    public bool IsRecent(BuyerPrincipal who) => Recent(who);

    // ---------- profile ----------

    public async Task<ProfileView?> GetProfileAsync(Guid accountId, CancellationToken ct)
        => await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null)
            .Select(a => new ProfileView(a.Id, a.DisplayName, a.Email ?? "", a.Phone)).FirstOrDefaultAsync(ct);

    public async Task<ProfileView?> UpdateProfileAsync(Guid accountId, string displayName, string? phone, CancellationToken ct)
    {
        var name = AuthValidation.NormaliseText(displayName);
        var number = string.IsNullOrWhiteSpace(phone) ? null : AuthValidation.NormaliseText(phone);
        var now = Now;
        var e164 = Infrastructure.PhoneNumbers.ToE164(number);
        // a number the buyer changed is a number nobody has proven: the proof stays only while the profile phone still means the same number (decision V-05)
        var rows = await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DisplayName, name).SetProperty(a => a.Phone, number).SetProperty(a => a.UpdatedAt, now)
                .SetProperty(a => a.PhoneVerifiedE164, a => a.PhoneVerifiedE164 == e164 ? a.PhoneVerifiedE164 : null)
                .SetProperty(a => a.PhoneVerifiedAt, a => a.PhoneVerifiedE164 == e164 ? a.PhoneVerifiedAt : null), ct);
        return rows == 1 ? await GetProfileAsync(accountId, ct) : null;
    }

    // ---------- address book ----------

    private static AddressView View(BuyerAddress a) => new(a.Id, a.FullName, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.Phone, a.Lat, a.Lng, a.IsDefault);

    private static void Apply(BuyerAddress a, AddressBody b, DateTimeOffset now)
    {
        a.FullName = AuthValidation.NormaliseText(b.FullName); a.Line1 = AuthValidation.NormaliseText(b.Line1);
        a.Line2 = string.IsNullOrWhiteSpace(b.Line2) ? null : AuthValidation.NormaliseText(b.Line2);
        a.City = AuthValidation.NormaliseText(b.City);
        a.Region = string.IsNullOrWhiteSpace(b.Region) ? null : AuthValidation.NormaliseText(b.Region);
        a.PostalCode = AuthValidation.NormaliseText(b.PostalCode);
        a.Country = b.Country!.Trim().ToUpperInvariant();
        a.Phone = string.IsNullOrWhiteSpace(b.Phone) ? null : AuthValidation.NormaliseText(b.Phone);
        a.Lat = b.Lat; a.Lng = b.Lng;
        a.UpdatedAt = now;
    }

    public async Task<List<AddressView>> ListAddressesAsync(Guid accountId, CancellationToken ct)
        => (await db.BuyerAddresses.AsNoTracking().Where(a => a.AccountId == accountId).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct)).Select(View).ToList();

    public async Task<AddressView?> GetAddressAsync(Guid accountId, Guid id, CancellationToken ct)
        => await db.BuyerAddresses.AsNoTracking().Where(a => a.Id == id && a.AccountId == accountId).Select(a => new AddressView(a.Id, a.FullName, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.Phone, a.Lat, a.Lng, a.IsDefault)).FirstOrDefaultAsync(ct);

    /// <summary>The first address is the default; later ones only when asked. At most <see cref="AccountValidation.MaxAddresses"/> per buyer.</summary>
    public async Task<(AddressView? Address, AccountOutcome Outcome)> CreateAddressAsync(Guid accountId, AddressBody body, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await LockAccountAsync(accountId, ct)) return (null, AccountOutcome.NotFound);
                var count = await db.BuyerAddresses.CountAsync(a => a.AccountId == accountId, ct);
                if (count >= AccountValidation.MaxAddresses) return (null, AccountOutcome.TooMany);
                var now = Now;
                var address = new BuyerAddress { Id = Guid.NewGuid(), AccountId = accountId, CreatedAt = now, IsDefault = body.IsDefault == true || count == 0 };
                Apply(address, body, now);
                if (address.IsDefault) await UnsetDefaultsAsync(accountId, address.Id, ct);
                db.BuyerAddresses.Add(address);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (View(address), AccountOutcome.Ok);
            }
            catch (Exception ex) when (IsRetryable(ex)) { last = ex; await BackOffAsync(attempt, ct); }   // a default made by another request, or a lock conflict: look again
        }
        throw new TooBusyException("The address book is too busy; try again.", last);
    }

    /// <summary>Replaces the address. <c>isDefault: true</c> makes it the default; <c>false</c> on the default address is ignored (there is always one).</summary>
    public async Task<AddressView?> UpdateAddressAsync(Guid accountId, Guid id, AddressBody body, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await LockAccountAsync(accountId, ct)) return null;
                var address = await db.BuyerAddresses.FirstOrDefaultAsync(a => a.Id == id && a.AccountId == accountId, ct);
                if (address is null) return null;
                Apply(address, body, Now);
                if (address.IsDemo) { address.IsDemo = false; address.DemoBatchId = null; }   // the buyer's own words now: "Remove demo data" must not delete their edit (M9 security review M2)
                if (body.IsDefault == true && !address.IsDefault)
                {
                    await UnsetDefaultsAsync(accountId, id, ct);
                    address.IsDefault = true;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return View(address);
            }
            catch (Exception ex) when (IsRetryable(ex)) { last = ex; await BackOffAsync(attempt, ct); }
        }
        throw new TooBusyException("The address book is too busy; try again.", last);
    }

    public async Task<bool> SetDefaultAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await LockAccountAsync(accountId, ct)) return false;
                if (!await db.BuyerAddresses.AnyAsync(a => a.Id == id && a.AccountId == accountId, ct)) return false;
                await UnsetDefaultsAsync(accountId, id, ct);
                var now = Now;
                await db.BuyerAddresses.Where(a => a.Id == id && a.AccountId == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, true).SetProperty(a => a.UpdatedAt, now), ct);
                await tx.CommitAsync(ct);
                return true;
            }
            catch (Exception ex) when (IsRetryable(ex)) { last = ex; await BackOffAsync(attempt, ct); }
        }
        throw new TooBusyException("The address book is too busy; try again.", last);
    }

    /// <summary>Deletes the address; when it was the default the oldest remaining one becomes the default.</summary>
    public async Task<bool> DeleteAddressAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await LockAccountAsync(accountId, ct)) return false;
                var row = await db.BuyerAddresses.AsNoTracking().Where(a => a.Id == id && a.AccountId == accountId).Select(a => new { a.IsDefault }).FirstOrDefaultAsync(ct);
                if (row is null) return false;
                if (await db.BuyerAddresses.Where(a => a.Id == id && a.AccountId == accountId).ExecuteDeleteAsync(ct) != 1) return false;
                if (row.IsDefault)
                {
                    var next = await db.BuyerAddresses.AsNoTracking().Where(a => a.AccountId == accountId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
                    if (next is { } n) await db.BuyerAddresses.Where(a => a.Id == n).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, true), ct);
                }
                await tx.CommitAsync(ct);
                return true;
            }
            catch (Exception ex) when (IsRetryable(ex)) { last = ex; await BackOffAsync(attempt, ct); }
        }
        throw new TooBusyException("The address book is too busy; try again.", last);
    }

    /// <summary>
    /// Takes the account's row lock (a no-op UPDATE of it) as the first statement of every address-book write, so two writes of ONE buyer's address book never run side by
    /// side: no lost default, no count past the cap, no promotion of an address someone else just deleted. Other buyers are not held up. The unique index stays as the net.
    /// </summary>
    private async Task<bool> LockAccountAsync(Guid accountId, CancellationToken ct)
        => await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct) == 1;   // a deleted or missing account takes no writes (L5)

    private Task UnsetDefaultsAsync(Guid accountId, Guid exceptId, CancellationToken ct)
        => db.BuyerAddresses.Where(a => a.AccountId == accountId && a.IsDefault && a.Id != exceptId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false), ct);

    // ---------- password change ----------

    /// <summary>
    /// Needs a sign-in in the last 10 minutes (O-11) AND the current password, checked through the same gate as sign-in. EVERY session ends, this one included, and a fresh session token is returned (M1-87).
    /// <paramref name="removePasskeys"/> also removes every passkey in the same transaction (the account has a password now, so it keeps a way in). A mail tells the owner (ids only in the outbox).
    /// </summary>
    public async Task<AccountResult> ChangePasswordAsync(BuyerPrincipal who, string currentPassword, string newPassword, string clientIp, CancellationToken ct, bool removePasskeys = false)
    {
        if (!Recent(who)) return new AccountResult(AccountOutcome.ReauthenticationRequired);
        if (AuthValidation.PasswordProblem(newPassword, who.Email, authOptions.Value.PasswordMinLength) is { } problem) return new AccountResult(AccountOutcome.WeakPassword, problem);
        var proof = await auth.ReauthenticateAsync(who, currentPassword, clientIp, ct);
        if (proof.Status == SignInStatus.TooManyAttempts) return new AccountResult(AccountOutcome.RateLimited, RetryAfterSeconds: proof.RetryAfterSeconds ?? 60);
        if (proof.Status != SignInStatus.Ok) return new AccountResult(AccountOutcome.InvalidCredentials);

        var hash = passwords.Hash(newPassword);
        var now = Now;
        var checkedHash = proof.CheckedHash; var checkedEpoch = proof.CheckedEpoch;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only if the account is still exactly what the password was checked against (A1): a reset that landed during the slow hash above changed the hash and the epoch,
        // and this change must not overwrite it or bring this session back to life. The epoch bump kills every other session even one being created right now (M1-75).
        var changed = await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null && a.PasswordHash == checkedHash && a.SessionEpoch == checkedEpoch)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.UpdatedAt, now).SetProperty(a => a.SessionEpoch, checkedEpoch + 1), ct);
        if (changed != 1) return new AccountResult(AccountOutcome.Conflict);
        // EVERY session ends, this one too, and the caller gets a fresh token (M1-87): someone who changes the password because they think a cookie was stolen must also kill the thief's copy
        await db.BuyerSessions.Where(s => s.AccountId == who.AccountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        var token = await auth.CreateSessionAsync(who.AccountId, ct, checkedEpoch + 1);
        // passkeys are a way in of their own and survive a password change unless the owner asks otherwise (M1-85): a buyer who fears one was added from a stolen session ticks "remove my passkeys" here
        if (removePasskeys) await db.BuyerPasskeys.Where(p => p.AccountId == who.AccountId).ExecuteDeleteAsync(ct);
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasswordChanged, who.AccountId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AccountResult(AccountOutcome.Ok, Token: token);
    }

    // ---------- the web path for an account made by phone (decision W3-02) ----------

    public async Task<SignInMethods?> GetSignInMethodsAsync(Guid accountId, CancellationToken ct)
    {
        var a = await db.BuyerAccounts.AsNoTracking().Where(x => x.Id == accountId && x.DeletedAt == null)
            .Select(x => new { x.Email, x.EmailVerifiedAt, x.PendingEmail, HasPassword = x.PasswordHash != null, x.PhoneVerifiedE164 }).FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var passkeys = await db.BuyerPasskeys.CountAsync(p => p.AccountId == accountId, ct);
        return new SignInMethods(a.Email, a.EmailVerifiedAt is not null, a.PendingEmail, a.HasPassword, passkeys, a.PhoneVerifiedE164 is { Length: >= 4 } p ? p[^4..] : null);
    }

    /// <summary>
    /// Adds an email to an account that has NONE (an account made by phone). It is the phone line's rule (V-13b), not a second one: the address is saved as PENDING and a link is mailed to it; it becomes the
    /// account's verified email only when the link is confirmed, and an address another account holds is never attached (that owner gets the usual notice; the answer is the same). Needs a recent
    /// sign-in (O-11): a verified email is a way to recover the account, so a borrowed session must not add one.
    /// </summary>
    public async Task<AccountResult> AddEmailAsync(BuyerPrincipal who, string? rawEmail, CancellationToken ct)
    {
        if (!Recent(who)) return new AccountResult(AccountOutcome.ReauthenticationRequired);
        if (await db.BuyerAccounts.AsNoTracking().AnyAsync(a => a.Id == who.AccountId && a.Email != null, ct)) return new AccountResult(AccountOutcome.AlreadySet);
        return await auth.RequestPhoneEmailAsync(who.AccountId, rawEmail ?? "", ct) switch
        {
            PhoneEmailResult.Sent => new AccountResult(AccountOutcome.Ok),
            PhoneEmailResult.RateLimited => new AccountResult(AccountOutcome.RateLimited, RetryAfterSeconds: 3600),
            _ => new AccountResult(AccountOutcome.InvalidEmail),
        };
    }

    /// <summary>
    /// The FIRST password of an account that has none (made by phone). Needs a recent sign-in (O-11; the phone step-up stamps it) and a VERIFIED email: the password signs in with the email, and a
    /// forgotten one is recovered through it. Like a password change, every session ends and a fresh token is returned (M1-87); the account then signs in with its password, and no longer by SMS code.
    /// </summary>
    public async Task<AccountResult> SetFirstPasswordAsync(BuyerPrincipal who, string newPassword, CancellationToken ct)
    {
        if (!Recent(who)) return new AccountResult(AccountOutcome.ReauthenticationRequired);
        var row = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null).Select(a => new { a.Email, a.EmailVerifiedAt, a.PasswordHash, a.SessionEpoch }).FirstOrDefaultAsync(ct);
        if (row is null) return new AccountResult(AccountOutcome.NotFound);
        if (row.PasswordHash is not null) return new AccountResult(AccountOutcome.AlreadySet);
        if (row.Email is null || row.EmailVerifiedAt is null) return new AccountResult(AccountOutcome.EmailRequired);
        if (AuthValidation.PasswordProblem(newPassword, row.Email, authOptions.Value.PasswordMinLength) is { } problem) return new AccountResult(AccountOutcome.WeakPassword, problem);
        var hash = passwords.Hash(newPassword);
        var now = Now;
        var epoch = row.SessionEpoch;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // only while the account still has no password and the same epoch: two parallel requests set it once, and a sign-out-all in between is not undone
        var changed = await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null && a.PasswordHash == null && a.SessionEpoch == epoch)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.UpdatedAt, now).SetProperty(a => a.SessionEpoch, epoch + 1), ct);
        if (changed != 1) return new AccountResult(AccountOutcome.Conflict);
        await db.BuyerSessions.Where(s => s.AccountId == who.AccountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        var token = await auth.CreateSessionAsync(who.AccountId, ct, epoch + 1);
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasswordChanged, who.AccountId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AccountResult(AccountOutcome.Ok, Token: token);
    }

    // ---------- account deletion ----------

    /// <summary>
    /// Needs a recent sign-in and the password. Soft delete: the account cannot sign in and every session ends at once, in one transaction together with the
    /// message that starts the redaction job; the personal data is erased by <see cref="RedactAsync"/> (reliably, through the outbox).
    /// </summary>
    public async Task<AccountResult> DeleteAccountAsync(BuyerPrincipal who, string password, string clientIp, CancellationToken ct)
    {
        if (!Recent(who)) return new AccountResult(AccountOutcome.ReauthenticationRequired);
        var proof = await auth.ReauthenticateAsync(who, password, clientIp, ct);
        if (proof.Status == SignInStatus.TooManyAttempts) return new AccountResult(AccountOutcome.RateLimited, RetryAfterSeconds: proof.RetryAfterSeconds ?? 60);
        if (proof.Status != SignInStatus.Ok) return new AccountResult(AccountOutcome.InvalidCredentials);

        return await DeleteCoreAsync(who, proof.CheckedHash, proof.CheckedEpoch, "password", ct);
    }

    /// <summary>
    /// Deleting an account that has NO password (W3 follow-up): a recent sign-in AND a code texted for exactly this purpose (<see cref="PhoneSignInService.StartDeleteAsync"/>) to the account's own proven
    /// number, typed now. The same semantics as the password path: the delete happens only if the account is still what was checked (still no password, same session epoch).
    /// </summary>
    public async Task<AccountResult> DeleteAccountByPhoneAsync(BuyerPrincipal who, Guid challengeId, string code, CancellationToken ct)
    {
        if (!Recent(who)) return new AccountResult(AccountOutcome.ReauthenticationRequired);
        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null).Select(a => new { a.PasswordHash, a.SessionEpoch }).FirstOrDefaultAsync(ct);
        if (account is null) return new AccountResult(AccountOutcome.NotFound);
        if (account.PasswordHash is not null) return new AccountResult(AccountOutcome.PasswordRequired);   // a text message is never a second way past a password
        var proof = await phoneCodes.VerifyAsync(challengeId, code, PhoneSignInPurposes.DeleteAccount, who, ct);
        switch (proof.Status)
        {
            case PhoneVerifyStatus.Ok: break;
            case PhoneVerifyStatus.Expired: return new AccountResult(AccountOutcome.CodeExpired);
            case PhoneVerifyStatus.Unavailable: return new AccountResult(AccountOutcome.Unavailable);
            default: return new AccountResult(AccountOutcome.InvalidCode);
        }
        return await DeleteCoreAsync(who, null, account.SessionEpoch, "phone_code", ct);
    }

    /// <summary>
    /// The delete itself, for both proofs. Only if the account is still what was checked: <paramref name="checkedHash"/> (null for an account with no password) and the session epoch.
    /// Its audit entry (<c>buyer.self_delete</c>, ids and the method only) commits with it.
    /// </summary>
    private async Task<AccountResult> DeleteCoreAsync(BuyerPrincipal who, string? checkedHash, long checkedEpoch, string method, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only if the account is still what the password was checked against (a reset in between means somebody else holds the account now), and the address is
        // freed at once (L2: a sign-up for it used to be swallowed until the redaction job ran). The rest of the personal data goes in the job.
        // The address is about to be freed, so it is read now and handed to the modules that hold it somewhere else than on the account (a guest order placed with it, M3-35): nothing keeps it afterwards.
        var address = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
        var rows = await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null && a.PasswordHash == checkedHash && a.SessionEpoch == checkedEpoch)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)now).SetProperty(a => a.PasswordHash, (string?)null).SetProperty(a => a.Email, (string?)null).SetProperty(a => a.PendingEmail, (string?)null).SetProperty(a => a.PinHash, (string?)null).SetProperty(a => a.UpdatedAt, now), ct);
        if (rows != 1) return new AccountResult(AccountOutcome.Conflict);
        if (!string.IsNullOrEmpty(address))
        {
            // the gate forgets the address in the same transaction (a row made by an attempt between the password check and here must not outlive the account), then the modules
            // that hold it elsewhere get it (a guest order placed with it, M3-35)
            await gate.ClearAddressAsync(gateKeys.Address(address), ct);
            foreach (var hook in deletionHooks) await hook.OnDeletingAsync(who.AccountId, address, ct);
        }
        await db.SavedCards.Where(c => c.AccountId == who.AccountId).ExecuteDeleteAsync(ct);   // saved cards (V-16) go at once: nothing may charge them after the account is deleted
        await db.BuyerSessions.Where(s => s.AccountId == who.AccountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        await outbox.TryEnqueueAsync(AccountRedactionHandler.Type, new AccountRedactionPayload(who.AccountId), $"redact:{who.AccountId:N}", ct: ct);
        // who deleted it and how, for staff (W3 follow-up): ids and the method only, never the address or the number
        Staff.StaffAudit.Add(db, time, null, "buyer.self_delete", "buyer", who.AccountId.ToString("D"), "Deleted by the buyer", new { method, sessionId = who.SessionId }, role: "buyer");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AccountResult(AccountOutcome.Ok);
    }

    /// <summary>
    /// Erases the personal data of a deleted account in ONE transaction: every registered <see cref="IAccountEraser"/> first (carts, orders... owned by other modules; they
    /// share this DbContext, so their statements join the transaction), then ours: addresses, links, session rows, the sign-in gate's memory of the address, and the
    /// account's own email, name, phone and password hash. If anything throws, nothing is changed and the outbox retries, so a half-done erasure never stays (review of the
    /// account slice). The row stays as an opaque id (orders refer to it). Safe to run again.
    /// </summary>
    public async Task<bool> RedactAsync(Guid accountId, IEnumerable<IAccountEraser> erasers, CancellationToken ct)
    {
        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt != null && a.RedactedAt == null).Select(a => new { a.Id, a.Email }).FirstOrDefaultAsync(ct);
        if (account is null) return false;
        var now = Now;
        var tombstone = RelayIdentity.Tombstone(accountId);   // the relay address stops working and is never issued again (L1)
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var e in erasers) await e.EraseAsync(accountId, ct);
        await db.BuyerAddresses.Where(a => a.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.EmailTokens.Where(t => t.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.SavedCards.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.BuyerSessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.BuyerPasskeys.Where(p => p.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.WebAuthnChallenges.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.PhoneSignInChallenges.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);   // web SMS codes sent for the account (W3-02)
        await db.BuyerAccounts.Where(a => a.Id == accountId && a.RedactedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Email, (string?)null).SetProperty(a => a.PendingEmail, (string?)null).SetProperty(a => a.PinHash, (string?)null).SetProperty(a => a.RelayEmail, tombstone).SetProperty(a => a.DisplayName, "Deleted buyer").SetProperty(a => a.Phone, (string?)null).SetProperty(a => a.PhoneVerifiedE164, (string?)null).SetProperty(a => a.PhoneVerifiedAt, (DateTimeOffset?)null)
            .SetProperty(a => a.PasswordHash, (string?)null).SetProperty(a => a.StaffLockedAt, (DateTimeOffset?)null).SetProperty(a => a.RedactedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        return true;
    }
}

/// <summary>Called inside the delete transaction with the address that is being freed, for data the module keeps under that address rather than under the account (a guest order's email). Idempotent; a throw rolls the delete back.</summary>
public interface IAccountDeletionHook
{
    Task OnDeletingAsync(Guid accountId, string email, CancellationToken ct);
}

/// <summary>Implemented by modules that hold personal data of a buyer (carts, orders, messages). Called when the account is erased, inside the erasure transaction on the shared DbContext (a throw rolls everything back and the job is retried); must be idempotent.</summary>
public interface IAccountEraser
{
    Task EraseAsync(Guid accountId, CancellationToken ct);
}
