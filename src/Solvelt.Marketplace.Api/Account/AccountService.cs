using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Account;

public sealed record ProfileView(Guid Id, string DisplayName, string Email, string? Phone);
public sealed record AddressView(Guid Id, string FullName, string Line1, string? Line2, string City, string? Region, string PostalCode, string Country, string? Phone, double? Lat, double? Lng, bool IsDefault);
public enum AccountOutcome { Ok, NotFound, TooMany, ReauthenticationRequired, InvalidCredentials, RateLimited, WeakPassword, Conflict }
public sealed record AccountResult(AccountOutcome Outcome, string? Problem = null, int RetryAfterSeconds = 0);

/// <summary>
/// The buyer's own data: profile, address book, password change, account deletion. Another buyer's address is simply not found. The address book keeps exactly one
/// default under parallel requests: the database refuses a second one (partial unique index) and every writer that loses that race starts again from what it now sees.
/// </summary>
public sealed class AccountService(MarketplaceDb db, TimeProvider time, AuthService auth, PasswordService passwords, SignInGate gate, GateKeys gateKeys, OutboxWriter outbox, IOptions<AuthOptions> authOptions, IEnumerable<IAccountDeletionHook> deletionHooks)
{
    /// <summary>How recently the buyer must have proven who they are for a sensitive change (decision O-11).</summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(10);
    private const int Attempts = 8;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }) return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    private bool Recent(BuyerPrincipal who) => Now - who.AuthenticatedAt <= RecentSignIn;

    // ---------- profile ----------

    public async Task<ProfileView?> GetProfileAsync(Guid accountId, CancellationToken ct)
        => await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null)
            .Select(a => new ProfileView(a.Id, a.DisplayName, a.Email ?? "", a.Phone)).FirstOrDefaultAsync(ct);

    public async Task<ProfileView?> UpdateProfileAsync(Guid accountId, string displayName, string? phone, CancellationToken ct)
    {
        var name = displayName.Trim();
        var number = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        var now = Now;
        var rows = await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DisplayName, name).SetProperty(a => a.Phone, number).SetProperty(a => a.UpdatedAt, now), ct);
        return rows == 1 ? await GetProfileAsync(accountId, ct) : null;
    }

    // ---------- address book ----------

    private static AddressView View(BuyerAddress a) => new(a.Id, a.FullName, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.Phone, a.Lat, a.Lng, a.IsDefault);

    private static void Apply(BuyerAddress a, AddressBody b, DateTimeOffset now)
    {
        a.FullName = b.FullName!.Trim(); a.Line1 = b.Line1!.Trim();
        a.Line2 = string.IsNullOrWhiteSpace(b.Line2) ? null : b.Line2.Trim();
        a.City = b.City!.Trim();
        a.Region = string.IsNullOrWhiteSpace(b.Region) ? null : b.Region.Trim();
        a.PostalCode = b.PostalCode!.Trim();
        a.Country = b.Country!.Trim().ToUpperInvariant();
        a.Phone = string.IsNullOrWhiteSpace(b.Phone) ? null : b.Phone.Trim();
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
            catch (Exception ex) when (IsUniqueViolation(ex)) { /* another request made a default first: look again */ }
        }
        throw new InvalidOperationException("The address book is too busy; try again.");
    }

    /// <summary>Replaces the address. <c>isDefault: true</c> makes it the default; <c>false</c> on the default address is ignored (there is always one).</summary>
    public async Task<AddressView?> UpdateAddressAsync(Guid accountId, Guid id, AddressBody body, CancellationToken ct)
    {
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
                if (body.IsDefault == true && !address.IsDefault)
                {
                    await UnsetDefaultsAsync(accountId, id, ct);
                    address.IsDefault = true;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return View(address);
            }
            catch (Exception ex) when (IsUniqueViolation(ex)) { }
        }
        throw new InvalidOperationException("The address book is too busy; try again.");
    }

    public async Task<bool> SetDefaultAsync(Guid accountId, Guid id, CancellationToken ct)
    {
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
            catch (Exception ex) when (IsUniqueViolation(ex)) { }
        }
        throw new InvalidOperationException("The address book is too busy; try again.");
    }

    /// <summary>Deletes the address; when it was the default the oldest remaining one becomes the default.</summary>
    public async Task<bool> DeleteAddressAsync(Guid accountId, Guid id, CancellationToken ct)
    {
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
            catch (Exception ex) when (IsUniqueViolation(ex)) { }
        }
        throw new InvalidOperationException("The address book is too busy; try again.");
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
    /// Needs a sign-in in the last 10 minutes (O-11) AND the current password, checked through the same gate as sign-in. Every OTHER session ends; this one goes on.
    /// A mail tells the owner (ids only in the outbox).
    /// </summary>
    public async Task<AccountResult> ChangePasswordAsync(BuyerPrincipal who, string currentPassword, string newPassword, string clientIp, CancellationToken ct)
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
        // this session joins the new epoch, but only if it was still on the one the check saw
        await db.BuyerSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null && s.Epoch == checkedEpoch).ExecuteUpdateAsync(s => s.SetProperty(x => x.Epoch, checkedEpoch + 1), ct);
        await db.BuyerSessions.Where(s => s.AccountId == who.AccountId && s.Id != who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        outbox.Enqueue(AuthMailHandler.Type, new AuthMailPayload(AuthMailKinds.PasswordChanged, who.AccountId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AccountResult(AccountOutcome.Ok);
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

        var now = Now;
        var checkedHash = proof.CheckedHash; var checkedEpoch = proof.CheckedEpoch;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only if the account is still what the password was checked against (a reset in between means somebody else holds the account now), and the address is
        // freed at once (L2: a sign-up for it used to be swallowed until the redaction job ran). The rest of the personal data goes in the job.
        // The address is about to be freed, so it is read now and handed to the modules that hold it somewhere else than on the account (a guest order placed with it, M3-35): nothing keeps it afterwards.
        var address = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == who.AccountId && a.DeletedAt == null).Select(a => a.Email).FirstOrDefaultAsync(ct);
        var rows = await db.BuyerAccounts.Where(a => a.Id == who.AccountId && a.DeletedAt == null && a.PasswordHash == checkedHash && a.SessionEpoch == checkedEpoch)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)now).SetProperty(a => a.PasswordHash, (string?)null).SetProperty(a => a.Email, (string?)null).SetProperty(a => a.UpdatedAt, now), ct);
        if (rows != 1) return new AccountResult(AccountOutcome.Conflict);
        if (!string.IsNullOrEmpty(address)) foreach (var hook in deletionHooks) await hook.OnDeletingAsync(who.AccountId, address, ct);
        await db.BuyerSessions.Where(s => s.AccountId == who.AccountId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        await outbox.TryEnqueueAsync(AccountRedactionHandler.Type, new AccountRedactionPayload(who.AccountId), $"redact:{who.AccountId:N}", ct: ct);
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
        await db.BuyerSessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(ct);
        if (account.Email is { } email) await gate.ClearAddressAsync(gateKeys.Address(email), ct);
        await db.BuyerAccounts.Where(a => a.Id == accountId && a.RedactedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Email, (string?)null).SetProperty(a => a.RelayEmail, tombstone).SetProperty(a => a.DisplayName, "Deleted buyer").SetProperty(a => a.Phone, (string?)null)
            .SetProperty(a => a.PasswordHash, (string?)null).SetProperty(a => a.RedactedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
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
