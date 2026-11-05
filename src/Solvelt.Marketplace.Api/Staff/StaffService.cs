using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Staff;

public enum StaffOutcome { Ok, NotFound, Conflict, LastAdmin, InvalidRole, InvalidEmail, InvalidName, TooMany, RevokeRequired }

/// <summary>
/// Staff sessions and accounts (decisions M7-01..M7-04). Staff tokens are <c>stf_</c> + 256 random bits, kept only as a SHA-256 in <c>staff_sessions</c>: a buyer session token is a different
/// table and a different shape, so neither can ever be accepted for the other.
/// </summary>
public sealed class StaffService(MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> options)
{
    public const string SessionPrefix = "stf_", EnrolmentPrefix = "ste_";
    private StaffOptions O => options.Value;
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    // ---------- sessions ----------

    public async Task<string> CreateSessionAsync(Guid staffId, long epoch, CancellationToken ct)
    {
        var now = Now;
        var token = SessionPrefix + Tokens.New();
        var absolute = now.AddHours(Math.Max(1, O.SessionAbsoluteHours));
        db.StaffSessions.Add(new StaffSession
        {
            Id = Guid.NewGuid(), StaffId = staffId, TokenHash = Tokens.Hash(token), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now, Epoch = epoch,
            ExpiresAt = Earlier(now.AddMinutes(Math.Max(1, O.SessionIdleMinutes)), absolute), AbsoluteExpiresAt = absolute,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>The signed-in staff member for a bearer token, or null. The account must be enrolled and not disabled, and the session's epoch must be the account's.</summary>
    public async Task<StaffPrincipal?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100 || !token.StartsWith(SessionPrefix, StringComparison.Ordinal)) return null;
        var hash = Tokens.Hash(token);
        var now = Now;
        var row = await (from s in db.StaffSessions.AsNoTracking()
                         join a in db.StaffAccounts.AsNoTracking() on s.StaffId equals a.Id
                         where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now && s.Epoch == a.SessionEpoch && a.DisabledAt == null && a.EnrolledAt != null
                         select new { s.Id, s.LastSeenAt, s.AuthenticatedAt, s.AbsoluteExpiresAt, StaffId = a.Id, a.Email, a.DisplayName, a.Role }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        if (now - row.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            var idle = Earlier(now.AddMinutes(Math.Max(1, O.SessionIdleMinutes)), row.AbsoluteExpiresAt);
            await db.StaffSessions.Where(s => s.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.ExpiresAt, idle), ct);
        }
        return new StaffPrincipal(row.StaffId, row.Id, row.Email, row.DisplayName, row.Role, row.AuthenticatedAt);
    }

    /// <summary>Ends THIS session and records it (one transaction, like every staff action).</summary>
    public async Task SignOutAsync(StaffPrincipal who, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.StaffSessions.Where(s => s.Id == who.SessionId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct) == 0) return;
        StaffAudit.Add(db, time, who, "staff.sign_out", "staff", who.StaffId.ToString("D"), "sign-out");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Ends every session of the account (the epoch moves, so one being made right now is dead too). Does not save: the caller's transaction does.</summary>
    public async Task EndAllSessionsAsync(Guid staffId, CancellationToken ct)
    {
        var now = Now;
        await db.StaffAccounts.Where(a => a.Id == staffId).ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, a => a.SessionEpoch + 1), ct);
        await db.StaffSessions.Where(s => s.StaffId == staffId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
    }

    // ---------- accounts ----------

    public sealed record AccountView(Guid Id, string Email, string DisplayName, string Role, bool Enrolled, bool Disabled, int Passkeys, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

    public async Task<List<AccountView>> ListAsync(CancellationToken ct)
    {
        var rows = await db.StaffAccounts.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var counts = (await db.StaffPasskeys.AsNoTracking().GroupBy(p => p.StaffId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        return rows.Select(a => new AccountView(a.Id, a.Email, a.DisplayName, a.Role, a.EnrolledAt != null, a.DisabledAt != null, counts.GetValueOrDefault(a.Id), a.CreatedAt, a.LastSignInAt)).ToList();
    }

    public sealed record Created(StaffOutcome Outcome, AccountView? Account = null, string? EnrolmentToken = null, DateTimeOffset? ExpiresAt = null);

    public async Task<Created> CreateAsync(StaffPrincipal actor, string? email, string? displayName, string? role, string reason, CancellationToken ct)
    {
        if (AuthValidation.EmailProblem(email) is not null) return new(StaffOutcome.InvalidEmail);
        if (AuthValidation.DisplayNameProblem(displayName) is not null) return new(StaffOutcome.InvalidName);
        if (!StaffPermissions.IsRole(role)) return new(StaffOutcome.InvalidRole);
        var now = Now;
        var account = new StaffAccount { Id = Guid.NewGuid(), Email = AuthValidation.NormaliseEmail(email!), DisplayName = AuthValidation.NormaliseText(displayName), Role = role!, CreatedBy = actor.StaffId, CreatedAt = now, UpdatedAt = now };
        var (token, enrolment) = NewEnrolment(account.Id, false, now);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.StaffAccounts.Add(account);
            db.StaffEnrolments.Add(enrolment);
            StaffAudit.Add(db, time, actor, "staff.create", "staff", account.Id.ToString("D"), reason, new { role });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return new(StaffOutcome.Conflict); }
        return new(StaffOutcome.Ok, new AccountView(account.Id, account.Email, account.DisplayName, account.Role, false, false, 0, now, null), token, enrolment.ExpiresAt);
    }

    private (string Token, StaffEnrolment Row) NewEnrolment(Guid staffId, bool bootstrap, DateTimeOffset now, string? fixedToken = null)
    {
        var token = fixedToken ?? EnrolmentPrefix + Tokens.New();
        return (token, new StaffEnrolment { Id = Guid.NewGuid(), StaffId = staffId, TokenHash = Tokens.Hash(token), CreatedAt = now, ExpiresAt = now.AddHours(Math.Max(1, O.EnrolmentHours)), Bootstrap = bootstrap });
    }

    /// <summary>
    /// A new one-time enrolment link for a staff member who lost their passkey (or never enrolled). Earlier unused links die. With <paramref name="revokePasskeys"/> the old passkeys and every
    /// session go too, which is what a LOST device needs.
    /// </summary>
    public async Task<Created> ReissueEnrolmentAsync(StaffPrincipal actor, Guid id, bool revokePasskeys, string reason, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.StaffAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, now), ct) != 1) return new(StaffOutcome.NotFound);
        // A link that ADDS a passkey to an account that already works would let one admin quietly put their own key on another admin's account (security review LOW 7): for an account that
        // has enrolled, the old keys and sessions must go with it (the "lost device" case), which is visible to the member and in the audit log.
        if (!revokePasskeys && await db.StaffAccounts.AnyAsync(a => a.Id == id && a.EnrolledAt != null, ct)) return new(StaffOutcome.RevokeRequired);
        await db.StaffEnrolments.Where(e => e.StaffId == id && e.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.UsedAt, (DateTimeOffset?)now), ct);
        if (revokePasskeys)
        {
            await LockAdminsAsync(id, ct);
            await db.StaffPasskeys.Where(p => p.StaffId == id).ExecuteDeleteAsync(ct);
            await db.StaffAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.EnrolledAt, (DateTimeOffset?)null), ct);
            await EndAllSessionsAsync(id, ct);
        }
        var (token, row) = NewEnrolment(id, false, now);
        db.StaffEnrolments.Add(row);
        StaffAudit.Add(db, time, actor, "staff.enrolment", "staff", id.ToString("D"), reason, new { revokePasskeys });
        await db.SaveChangesAsync(ct);
        // the last active admin must not lock themselves out by revoking their own keys
        if (revokePasskeys && await ActiveAdminsAsync(ct) == 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StaffOutcome.LastAdmin); }
        await tx.CommitAsync(ct);
        return new(StaffOutcome.Ok, null, token, row.ExpiresAt);
    }

    private Task<int> ActiveAdminsAsync(CancellationToken ct) => db.StaffAccounts.CountAsync(a => a.Role == StaffRoleNames.Admin && a.DisabledAt == null && a.EnrolledAt != null, ct);

    /// <summary>Takes a lock on every admin row, so two admins cannot each remove the other in parallel (the second one waits, then counts again). A no-op write.</summary>
    private Task<int> LockAdminsAsync(Guid alsoId, CancellationToken ct)
        => db.StaffAccounts.Where(a => a.Role == StaffRoleNames.Admin || a.Id == alsoId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct);

    public async Task<StaffOutcome> SetRoleAsync(StaffPrincipal actor, Guid id, string? role, string reason, CancellationToken ct)
    {
        if (!StaffPermissions.IsRole(role)) return StaffOutcome.InvalidRole;
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdminsAsync(id, ct);
        var account = await db.StaffAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return StaffOutcome.NotFound;
        if (account.Role == role) return StaffOutcome.Ok;
        var old = account.Role;
        account.Role = role!; account.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (old == StaffRoleNames.Admin && await ActiveAdminsAsync(ct) == 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return StaffOutcome.LastAdmin; }
        await EndAllSessionsAsync(id, ct);   // a changed role starts from a fresh sign-in
        StaffAudit.Add(db, time, actor, "staff.role", "staff", id.ToString("D"), reason, new { from = old, to = role });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StaffOutcome.Ok;
    }

    public async Task<StaffOutcome> SetDisabledAsync(StaffPrincipal actor, Guid id, bool disabled, string reason, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdminsAsync(id, ct);
        var account = await db.StaffAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return StaffOutcome.NotFound;
        if ((account.DisabledAt != null) == disabled) return StaffOutcome.Ok;
        account.DisabledAt = disabled ? now : null; account.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (disabled && await ActiveAdminsAsync(ct) == 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return StaffOutcome.LastAdmin; }
        if (disabled) await EndAllSessionsAsync(id, ct);
        StaffAudit.Add(db, time, actor, disabled ? "staff.disable" : "staff.enable", "staff", id.ToString("D"), reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StaffOutcome.Ok;
    }

    public async Task<StaffOutcome> SignOutEverywhereAsync(StaffPrincipal actor, Guid id, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.StaffAccounts.AnyAsync(a => a.Id == id, ct)) return StaffOutcome.NotFound;
        await EndAllSessionsAsync(id, ct);
        StaffAudit.Add(db, time, actor, "staff.sign_out_all", "staff", id.ToString("D"), reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StaffOutcome.Ok;
    }

    // ---------- bootstrap ----------

    /// <summary>
    /// The first admin (decision M7-04). Runs at start. With <c>Staff:BootstrapToken</c> set and NO staff account in the database it makes the admin named by <c>Staff:BootstrapEmail</c> (not
    /// enrolled, no password: there is none), and, while that admin has no passkey, keeps one enrolment whose secret is the configured token, valid <c>Staff:EnrolmentHours</c> from this start
    /// (restart to open a new window). Once any passkey is registered the token does nothing, whatever the configuration still says. Never a default account or password.
    /// </summary>
    /// <summary>The bootstrap admin has a FIXED id: two instances (or two restarts with different <c>Staff:BootstrapEmail</c> values) that both see "no staff yet" collide on the primary key, so only one admin can ever be made this way.</summary>
    public static readonly Guid BootstrapAdminId = new("00000000-0000-0000-0000-00000000b007");

    public async Task<string> BootstrapAsync(CancellationToken ct)
    {
        var token = O.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(O.BootstrapEmail) || token.Length < 32) return "off";
        var now = Now;
        var admin = await db.StaffAccounts.FirstOrDefaultAsync(a => a.Id == BootstrapAdminId, ct);
        if (admin is null)
        {
            if (await db.StaffAccounts.AnyAsync(ct)) return "off";   // there are staff already (made another way): the bootstrap token is for an empty database only
            admin = new StaffAccount { Id = BootstrapAdminId, Email = AuthValidation.NormaliseEmail(O.BootstrapEmail), DisplayName = AuthValidation.NormaliseText(O.BootstrapName), Role = StaffRoleNames.Admin, CreatedAt = now, UpdatedAt = now };
            db.StaffAccounts.Add(admin);
            StaffAudit.Add(db, time, null, "staff.bootstrap", "staff", admin.Id.ToString("D"), "first admin made from the bootstrap token", role: "system");
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return "raced"; }   // another instance made it: fine
        }
        if (admin.EnrolledAt is not null || await db.StaffPasskeys.AnyAsync(ct)) return "used";
        var hash = Tokens.Hash(token);
        // A bootstrap secret works ONCE EVER (security review LOW 5): a row that was used is never reopened, not even when the admin's keys were revoked later and the token is still configured.
        var row = await db.StaffEnrolments.AsNoTracking().Where(e => e.TokenHash == hash).Select(e => new { e.UsedAt }).FirstOrDefaultAsync(ct);
        if (row?.UsedAt is not null) return "used";
        var expires = now.AddHours(Math.Max(1, O.EnrolmentHours));
        // one row per configured token: a restart before the first use extends its window; a changed token is a new row and the old one dies
        await db.StaffEnrolments.Where(e => e.StaffId == admin.Id && e.UsedAt == null && e.TokenHash != hash).ExecuteUpdateAsync(s => s.SetProperty(e => e.UsedAt, (DateTimeOffset?)now), ct);
        if (row is null)
        {
            try { db.StaffEnrolments.Add(new StaffEnrolment { Id = Guid.NewGuid(), StaffId = admin.Id, TokenHash = hash, CreatedAt = now, ExpiresAt = expires, Bootstrap = true }); await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (AccountService.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); }   // another instance stored it first
        }
        else await db.StaffEnrolments.Where(e => e.TokenHash == hash && e.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ExpiresAt, expires), ct);   // only while UNUSED
        return "open";
    }
}
