using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Staff;

/// <summary>What staff see by default: enough to recognise a buyer a customer is talking about, never enough to contact or find them.</summary>
public static class Masking
{
    /// <summary><c>jane.doe@example.com</c> becomes <c>j***@e***.com</c>.</summary>
    public static string? Email(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 1 || at == email.Length - 1) return "***";
        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var tld = dot > 0 ? domain[dot..] : "";
        return $"{Char(email)}***@{Char(domain)}***{tld}";
    }

    private static string Char(string s) => System.Text.Rune.GetRuneAt(s, 0).ToString();

    /// <summary>"Jane Brown" becomes "Jane B." (the same name the sellers see).</summary>
    public static string Name(string displayName) => RelayIdentity.PublicName(displayName);
}

public sealed record BuyerView(Guid Id, string? Email, string Name, string RelayEmail, bool EmailVerified, bool Locked, DateTimeOffset? LockedAt, bool Deleted, bool Redacted, DateTimeOffset CreatedAt,
    int Passkeys, int ActiveSessions, int Addresses, int Orders, bool HasPassword, bool Demo);

public enum BuyerOutcome { Ok, NotFound, RevealLimit, InvalidFields, LookupLimit }

/// <summary>Buyer account tools for staff (decisions M7-07..M7-09): masked lookup, break-glass reveal, lock and unlock, sign-out everywhere. Each one audits in its own transaction.</summary>
public sealed class StaffBuyerService(MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> options)
{
    public static readonly string[] RevealFields = ["email", "name", "phone", "addresses"];
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    /// <summary>Finds ONE buyer by account id, exact email or order number. There is no search by part of a name: staff cannot browse buyers.</summary>
    public async Task<Guid?> FindAsync(Guid? id, string? email, string? order, CancellationToken ct)
    {
        if (id is { } g) return await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == g).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(email) && email.Length <= 254)
        {
            var normal = AuthValidation.NormaliseEmail(email);
            return await db.BuyerAccounts.AsNoTracking().Where(a => a.Email == normal && a.DeletedAt == null).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(order) && order.Length <= 40)
        {
            var number = order.Trim();
            return await db.Orders.AsNoTracking().Where(o => o.Number == number && o.AccountId != null).Select(o => o.AccountId).FirstOrDefaultAsync(ct);
        }
        return null;
    }

    public async Task<BuyerView?> ViewAsync(Guid id, CancellationToken ct)
    {
        var now = Now;
        var a = await db.BuyerAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return null;
        var passkeys = await db.BuyerPasskeys.CountAsync(p => p.AccountId == id, ct);
        var sessions = await db.BuyerSessions.CountAsync(s => s.AccountId == id && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now && s.Epoch == a.SessionEpoch, ct);
        var addresses = await db.BuyerAddresses.CountAsync(x => x.AccountId == id, ct);
        var orders = await db.Orders.CountAsync(o => o.AccountId == id, ct);
        var demo = a.DemoVerifiedAt != null || await db.BuyerAddresses.AnyAsync(x => x.AccountId == id && x.IsDemo, ct) || await db.Orders.AnyAsync(o => o.AccountId == id && o.IsDemo, ct);
        return new BuyerView(a.Id, Masking.Email(a.Email), a.RedactedAt is null ? Masking.Name(a.DisplayName) : a.DisplayName, Masking.Email(a.RelayEmail) ?? "", a.EmailVerifiedAt != null, a.StaffLockedAt != null, a.StaffLockedAt,
            a.DeletedAt != null, a.RedactedAt != null, a.CreatedAt, passkeys, sessions, addresses, orders, a.PasswordHash != null, demo);
    }

    public sealed record LookupResult(bool Limited, BuyerView? View);

    /// <summary>
    /// One lookup, as one unit (security review LOW 7): the hourly cap is checked first (under the staff row's lock, from the audit log), then the buyer is found and shown, and the audit row names
    /// the buyer found (an id) or none, never what was typed. A miss counts against the cap like a hit.
    /// </summary>
    public async Task<LookupResult> LookupAsync(StaffPrincipal who, Guid? id, string? email, string? order, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await StaffLookupCap.ReachedAsync(db, who, Now, options.Value.LookupsPerHour, ct)) return new(true, null);
        var found = await FindAsync(id, email, order, ct);
        var view = found is { } f ? await ViewAsync(f, ct) : null;
        StaffAudit.Add(db, time, who, view is null ? "buyer.lookup_miss" : "buyer.lookup", view is null ? null : "buyer", view?.Id.ToString("D"), reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(false, view);
    }

    public sealed record Revealed(string? Email, string? Name, string? Phone, List<RevealedAddress>? Addresses, bool Redacted);
    public sealed record RevealedAddress(string Name, string Line1, string? Line2, string City, string? Region, string PostalCode, string Country, string? Phone);

    /// <summary>
    /// Break-glass (decision M7-08): shows the real values of the chosen fields. The audit row (which fields, why, who) is saved in the SAME transaction that reads them, so a read that
    /// is not recorded cannot happen; an hourly cap per staff member (from the audit log, under the staff row's lock so parallel calls count each other) bounds a stolen session.
    /// </summary>
    public async Task<(BuyerOutcome Outcome, Revealed? Data)> RevealAsync(StaffPrincipal who, Guid id, IReadOnlyList<string?> fields, string reason, CancellationToken ct)
    {
        var chosen = fields.Select(f => f?.Trim().ToLowerInvariant()).Distinct().ToList();   // a null entry in the list is just an invalid field, never an exception
        if (chosen.Count == 0 || chosen.Any(f => f is null || !RevealFields.Contains(f))) return (BuyerOutcome.InvalidFields, null);
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.StaffAccounts.Where(a => a.Id == who.StaffId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, a => a.UpdatedAt), ct);
        var since = now.AddHours(-1);
        if (await db.StaffAudit.CountAsync(a => a.ActorId == who.StaffId && a.Action == "buyer.reveal" && a.At > since, ct) >= Math.Max(1, options.Value.RevealsPerHour)) return (BuyerOutcome.RevealLimit, null);
        var a = await db.BuyerAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return (BuyerOutcome.NotFound, null);
        var redacted = a.RedactedAt != null;
        var data = new Revealed(
            chosen.Contains("email") ? a.Email : null,
            chosen.Contains("name") ? a.DisplayName : null,
            chosen.Contains("phone") ? a.Phone : null,
            chosen.Contains("addresses") ? (await db.BuyerAddresses.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new RevealedAddress(x.FullName, x.Line1, x.Line2, x.City, x.Region, x.PostalCode, x.Country, x.Phone)).ToListAsync(ct)) : null,
            redacted);
        StaffAudit.Add(db, time, who, "buyer.reveal", "buyer", id.ToString("D"), reason, new { fields = chosen });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (BuyerOutcome.Ok, data);
    }

    /// <summary>Locks or unlocks. Locking ends every session at once (the epoch moves) and stops sign-in; unlocking never revives an old session. Idempotent, and audited only when something changed.</summary>
    public async Task<BuyerOutcome> SetLockedAsync(StaffPrincipal who, Guid id, bool locked, string reason, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var a = await db.BuyerAccounts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || a.DeletedAt != null) return BuyerOutcome.NotFound;
        if ((a.StaffLockedAt != null) == locked) return BuyerOutcome.Ok;
        var rows = await db.BuyerAccounts.Where(x => x.Id == id && (x.StaffLockedAt != null) == !locked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StaffLockedAt, locked ? now : (DateTimeOffset?)null).SetProperty(x => x.SessionEpoch, x => x.SessionEpoch + 1).SetProperty(x => x.UpdatedAt, now), ct);
        if (rows != 1) return BuyerOutcome.Ok;   // somebody else did the same thing first
        await db.BuyerSessions.Where(s => s.AccountId == id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        StaffAudit.Add(db, time, who, locked ? "buyer.lock" : "buyer.unlock", "buyer", id.ToString("D"), reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BuyerOutcome.Ok;
    }

    public async Task<BuyerOutcome> SignOutEverywhereAsync(StaffPrincipal who, Guid id, string reason, CancellationToken ct)
    {
        var now = Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.BuyerAccounts.Where(a => a.Id == id && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, a => a.SessionEpoch + 1), ct);
        if (rows != 1) return BuyerOutcome.NotFound;
        var ended = await db.BuyerSessions.Where(s => s.AccountId == id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), ct);
        StaffAudit.Add(db, time, who, "buyer.sign_out_all", "buyer", id.ToString("D"), reason, new { sessions = ended });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BuyerOutcome.Ok;
    }
}

public static class StaffBuyerEndpoints
{
    public sealed record RevealBody(string?[]? Fields, string? Reason);
    public sealed record LookupBody(string? Email, string? Order, Guid? Id, string? Reason);

    public static IEndpointRouteBuilder MapStaffBuyers(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup().MapGroup("/buyers");
        g.MapPost("/lookup", Lookup).StaffCan(StaffPermissions.BuyersView);
        g.MapPost("/{id:guid}/lookup", Detail).StaffCan(StaffPermissions.BuyersView);
        g.MapPost("/{id:guid}/reveal", Reveal).StaffCan(StaffPermissions.BuyersReveal);
        g.MapPost("/{id:guid}/lock", (Guid id, StaffEndpoints.ReasonBody? b, HttpContext c, StaffBuyerService s, TimeProvider t, IOptions<StaffOptions> o, CancellationToken ct) => Lock(id, b, true, c, s, t, o, ct)).StaffCan(StaffPermissions.BuyersLock);
        g.MapPost("/{id:guid}/unlock", (Guid id, StaffEndpoints.ReasonBody? b, HttpContext c, StaffBuyerService s, TimeProvider t, IOptions<StaffOptions> o, CancellationToken ct) => Lock(id, b, false, c, s, t, o, ct)).StaffCan(StaffPermissions.BuyersLock);
        g.MapPost("/{id:guid}/sign-out", SignOutAll).StaffCan(StaffPermissions.BuyersSignOut);
        return app;
    }

    private static object Shape(BuyerView v) => new
    {
        id = v.Id, email = v.Email, name = v.Name, relayEmail = v.RelayEmail, emailVerified = v.EmailVerified, locked = v.Locked, lockedAt = v.LockedAt, deleted = v.Deleted, redacted = v.Redacted,
        createdAt = v.CreatedAt, passkeys = v.Passkeys, activeSessions = v.ActiveSessions, addresses = v.Addresses, orders = v.Orders, hasPassword = v.HasPassword, demo = v.Demo,
    };

    /// <summary>
    /// <c>POST /buyers/lookup { email | order | id, reason }</c>: one buyer or 404. A POST with a body, NOT a query string: the typed address and the reason must not appear in a URL, which proxy,
    /// CDN and BFF access logs keep (security review LOW 7). The reason is required (a lookup is an action) and `429 lookup_limit` follows the hourly cap.
    /// </summary>
    private static async Task<IResult> Lookup(LookupBody? body, HttpContext ctx, StaffBuyerService buyers, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        var given = (body!.Email is not null ? 1 : 0) + (body.Order is not null ? 1 : 0) + (body.Id is not null ? 1 : 0);
        if (given != 1) return Problems.BadRequest("one_key", "Give exactly one of email, order or id.");
        return Shown(await buyers.LookupAsync(ctx.Staff(), body.Id, body.Email, body.Order, StaffReason.Clean(body.Reason), ct));
    }

    private static async Task<IResult> Detail(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffBuyerService buyers, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason, stepUp: false) is { } refused) return refused;
        return Shown(await buyers.LookupAsync(ctx.Staff(), id, null, null, StaffReason.Clean(body!.Reason), ct));
    }

    private static IResult Shown(StaffBuyerService.LookupResult r)
        => r.Limited ? Problems.Json(429, "lookup_limit", "Too many lookups this hour.") : r.View is null ? Problems.NotFound() : Results.Json(Shape(r.View));

    private static async Task<IResult> Reveal(Guid id, RevealBody? body, HttpContext ctx, StaffBuyerService buyers, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var (outcome, data) = await buyers.RevealAsync(ctx.Staff(), id, body?.Fields ?? [], StaffReason.Clean(body!.Reason), ct);
        return outcome switch
        {
            BuyerOutcome.Ok => Results.Json(new { email = data!.Email, name = data.Name, phone = data.Phone, addresses = data.Addresses, redacted = data.Redacted }),
            BuyerOutcome.RevealLimit => Problems.Json(429, "reveal_limit", "Too many reveals this hour."),
            BuyerOutcome.InvalidFields => Problems.Validation(new Dictionary<string, string> { ["fields"] = "Choose from: " + string.Join(", ", StaffBuyerService.RevealFields) }),
            _ => Problems.NotFound(),
        };
    }

    private static async Task<IResult> Lock(Guid id, StaffEndpoints.ReasonBody? body, bool locked, HttpContext ctx, StaffBuyerService buyers, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return await buyers.SetLockedAsync(ctx.Staff(), id, locked, StaffReason.Clean(body!.Reason), ct) == BuyerOutcome.Ok ? Results.NoContent() : Problems.NotFound();
    }

    private static async Task<IResult> SignOutAll(Guid id, StaffEndpoints.ReasonBody? body, HttpContext ctx, StaffBuyerService buyers, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (StaffEndpoints.Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return await buyers.SignOutEverywhereAsync(ctx.Staff(), id, StaffReason.Clean(body!.Reason), ct) == BuyerOutcome.Ok ? Results.NoContent() : Problems.NotFound();
    }
}
