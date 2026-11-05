using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Staff;

/// <summary>Answers 404 to anything outside <c>Staff:AllowedIps</c> when the list is set: the staff area does not exist for those addresses.</summary>
public sealed class StaffNetworkFilter(IOptions<StaffOptions> options, ClientIpResolver ips) : IEndpointFilter
{
    private IpAllowList? _list;
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var list = _list ??= IpAllowList.From(options.Value.AllowedIps);
        if (!list.Allows(ips.ResolveAddress(context.HttpContext))) return Problems.NotFound();
        return await next(context);
    }
}

/// <summary>Requires <c>Authorization: Bearer stf_…</c>; otherwise 401. A buyer's token never passes (another table, another prefix).</summary>
public sealed class RequireStaffFilter(StaffService staff) : IEndpointFilter
{
    public const string Key = "solvelt.staff";
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var who = await staff.AuthenticateAsync(token, context.HttpContext.RequestAborted);
        if (who is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        context.HttpContext.Items[Key] = who;
        return await next(context);
    }
}

/// <summary>403 unless the staff member's role holds the permission (the matrix in docs/STAFF.md).</summary>
public sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => context.HttpContext.Staff().Can(permission) ? await next(context) : Problems.Json(403, "forbidden", "Your role cannot do this.");
}

public static class StaffEndpoints
{
    public sealed record EnrolOptionsBody(string? Token);
    public sealed record EnrolBody(string? Token, Guid? ChallengeId, string? Name, JsonElement? Credential);
    public sealed record AssertionBody(Guid? ChallengeId, JsonElement? Credential);
    public sealed record PasskeyAddBody(Guid? ChallengeId, string? Name, JsonElement? Credential);
    public sealed record CreateAccountBody(string? Email, string? DisplayName, string? Role, string? Reason);
    public sealed record ReasonBody(string? Reason);
    public sealed record EnrolmentBody(string? Reason, bool RevokePasskeys);
    public sealed record RoleBody(string? Role, string? Reason);

    public static StaffPrincipal Staff(this HttpContext ctx) => (StaffPrincipal)ctx.Items[RequireStaffFilter.Key]!;

    /// <summary>The group every staff endpoint hangs off: network allow-list first, then the staff rate limit.</summary>
    public static RouteGroupBuilder StaffGroup(this IEndpointRouteBuilder app)
        => app.MapGroup("/api/v1/staff").AddEndpointFilter<StaffNetworkFilter>().RequireRateLimiting(RateLimitPolicies.Staff);

    public static IEndpointRouteBuilder MapStaff(this IEndpointRouteBuilder app)
    {
        var g = app.StaffGroup();
        var auth = g.MapGroup("/auth").RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/enrol/options", EnrolOptions);
        auth.MapPost("/enrol", Enrol);
        auth.MapPost("/passkey/options", SignInOptions);
        auth.MapPost("/passkey/sign-in", SignIn);
        auth.MapPost("/reauthenticate/options", ReauthOptions).AddEndpointFilter<RequireStaffFilter>();
        auth.MapPost("/reauthenticate", Reauth).AddEndpointFilter<RequireStaffFilter>();
        auth.MapPost("/sign-out", SignOut).AddEndpointFilter<RequireStaffFilter>();

        var me = g.MapGroup("/me").AddEndpointFilter<RequireStaffFilter>();
        me.MapGet("", Me);
        me.MapGet("/passkeys", ListPasskeys);
        me.MapPost("/passkeys/options", AddPasskeyOptions);
        me.MapPost("/passkeys", AddPasskey);
        me.MapDelete("/passkeys/{id:guid}", RemovePasskey);

        var accounts = g.MapGroup("/accounts").AddEndpointFilter<RequireStaffFilter>().AddEndpointFilter(new RequirePermissionFilter(StaffPermissions.StaffManage));
        accounts.MapGet("", ListAccounts);
        accounts.MapPost("", CreateAccount);
        accounts.MapPost("/{id:guid}/enrolment", Reissue);
        accounts.MapPost("/{id:guid}/role", SetRole);
        accounts.MapPost("/{id:guid}/disable", (Guid id, ReasonBody? b, HttpContext c, StaffService s, IOptions<StaffOptions> o, CancellationToken ct) => SetDisabled(id, b, true, c, s, o, ct));
        accounts.MapPost("/{id:guid}/enable", (Guid id, ReasonBody? b, HttpContext c, StaffService s, IOptions<StaffOptions> o, CancellationToken ct) => SetDisabled(id, b, false, c, s, o, ct));
        accounts.MapPost("/{id:guid}/sign-out", SignOutEverywhere);

        g.MapGet("/audit", ListAudit).AddEndpointFilter<RequireStaffFilter>().AddEndpointFilter(new RequirePermissionFilter(StaffPermissions.AuditView));
        return app;
    }

    // ---------- helpers shared by every staff action ----------

    /// <summary>The two checks every sensitive action starts with: a recent passkey check and a reason. Null when both are fine.</summary>
    public static IResult? Guard(HttpContext ctx, TimeProvider time, IOptions<StaffOptions> options, string? reason, bool stepUp = true)
    {
        if (stepUp && !ctx.Staff().Recent(time.GetUtcNow().ToUniversalTime(), options.Value.StepUpMinutes))
            return Problems.Json(403, "reauthentication_required", "Confirm with your passkey again first.");
        return StaffReason.Problem(reason) is { } p ? Problems.Validation(new Dictionary<string, string> { ["reason"] = p }) : null;
    }

    private static IResult PasskeyProblem(StaffPasskeyOutcome o) => o switch
    {
        StaffPasskeyOutcome.InvalidToken => Problems.Json(400, "invalid_token", "This enrolment link is invalid, used or has expired."),
        StaffPasskeyOutcome.InvalidChallenge => Problems.Json(400, "invalid_challenge", "Start again: the passkey request is missing, used or has expired."),
        StaffPasskeyOutcome.InvalidCredential => Problems.Json(400, "invalid_credential", "The passkey could not be verified."),
        StaffPasskeyOutcome.AlreadyRegistered => Problems.Json(409, "already_registered", "This passkey is already registered."),
        StaffPasskeyOutcome.TooMany => Problems.Json(409, "too_many_passkeys", "The number of passkeys is at its limit. Remove one first."),
        StaffPasskeyOutcome.ReauthenticationRequired => Problems.Json(403, "reauthentication_required", "Confirm with your passkey again first."),
        StaffPasskeyOutcome.LastPasskey => Problems.Json(409, "last_passkey", "This is your only passkey. Add another first, or ask an admin for a new enrolment link."),
        StaffPasskeyOutcome.InvalidName => Problems.Validation(new Dictionary<string, string> { ["name"] = "Give the passkey a name of up to 60 characters." }),
        _ => Problems.NotFound(),
    };

    private static object Principal(StaffAccount a) => new { id = a.Id, email = a.Email, displayName = a.DisplayName, role = a.Role, permissions = StaffPermissions.For(a.Role) };

    // ---------- authentication ----------

    private static async Task<IResult> EnrolOptions(EnrolOptionsBody? body, StaffPasskeyService passkeys, CancellationToken ct)
    {
        var (outcome, challengeId, options) = await passkeys.BeginEnrolmentAsync(body?.Token, ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.Json(new { challengeId, options = JsonDocument.Parse(options!).RootElement }) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> Enrol(EnrolBody? body, StaffPasskeyService passkeys, CancellationToken ct)
    {
        if (body?.ChallengeId is not { } challengeId) return PasskeyProblem(StaffPasskeyOutcome.InvalidChallenge);
        var (outcome, session) = await passkeys.CompleteEnrolmentAsync(body.Token, challengeId, body.Name, body.Credential, ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.Json(new { token = session!.Token, staff = Principal(session.Account) }, statusCode: 201) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> SignInOptions(StaffPasskeyService passkeys, CancellationToken ct)
    {
        var (challengeId, options) = await passkeys.BeginSignInAsync(ct);
        return Results.Json(new { challengeId, options = JsonDocument.Parse(options).RootElement });
    }

    private static async Task<IResult> SignIn(AssertionBody? body, StaffPasskeyService passkeys, CancellationToken ct)
    {
        var r = body?.ChallengeId is { } id ? await passkeys.SignInAsync(id, body.Credential, ct) : null;
        return r is null ? Problems.Json(401, "invalid_credentials", "The passkey was not accepted.") : Results.Json(new { token = r.Token, staff = Principal(r.Account) });
    }

    private static async Task<IResult> ReauthOptions(HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct)
    {
        var (outcome, challengeId, options) = await passkeys.BeginReauthenticationAsync(ctx.Staff(), ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.Json(new { challengeId, options = JsonDocument.Parse(options!).RootElement }) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> Reauth(AssertionBody? body, HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct)
    {
        if (body?.ChallengeId is not { } id) return PasskeyProblem(StaffPasskeyOutcome.InvalidChallenge);
        var outcome = await passkeys.ReauthenticateAsync(ctx.Staff(), id, body.Credential, ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.NoContent() : PasskeyProblem(outcome);
    }

    private static async Task<IResult> SignOut(HttpContext ctx, StaffService staff, CancellationToken ct)
    {
        await staff.SignOutAsync(ctx.Staff(), ct);
        return Results.NoContent();
    }

    // ---------- me ----------

    private static IResult Me(HttpContext ctx, TimeProvider time, IOptions<StaffOptions> o)
    {
        var s = ctx.Staff();
        var until = s.AuthenticatedAt.AddMinutes(Math.Max(1, o.Value.StepUpMinutes));
        return Results.Json(new { id = s.StaffId, email = s.Email, displayName = s.DisplayName, role = s.Role, permissions = StaffPermissions.For(s.Role), stepUpUntil = until > time.GetUtcNow() ? until : (DateTimeOffset?)null });
    }

    private static async Task<IResult> ListPasskeys(HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct) => Results.Json(await passkeys.ListAsync(ctx.Staff().StaffId, ct));

    private static async Task<IResult> AddPasskeyOptions(HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct)
    {
        var (outcome, challengeId, options) = await passkeys.BeginAddAsync(ctx.Staff(), ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.Json(new { challengeId, options = JsonDocument.Parse(options!).RootElement }) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> AddPasskey(PasskeyAddBody? body, HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct)
    {
        if (body?.ChallengeId is not { } id) return PasskeyProblem(StaffPasskeyOutcome.InvalidChallenge);
        var (outcome, p) = await passkeys.CompleteAddAsync(ctx.Staff(), id, body.Name, body.Credential, ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.Json(p, statusCode: 201) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> RemovePasskey(Guid id, HttpContext ctx, StaffPasskeyService passkeys, CancellationToken ct)
    {
        var outcome = await passkeys.RemoveAsync(ctx.Staff(), id, ct);
        return outcome == StaffPasskeyOutcome.Ok ? Results.NoContent() : PasskeyProblem(outcome);
    }

    // ---------- accounts (admin) ----------

    private static IResult Outcome(StaffOutcome o) => o switch
    {
        StaffOutcome.Ok => Results.NoContent(),
        StaffOutcome.NotFound => Problems.NotFound(),
        StaffOutcome.Conflict => Problems.Json(409, "email_in_use", "A staff account with that address exists."),
        StaffOutcome.LastAdmin => Problems.Json(409, "last_admin", "There must always be one active admin."),
        StaffOutcome.InvalidRole => Problems.Validation(new Dictionary<string, string> { ["role"] = "Use support, finance or admin." }),
        StaffOutcome.InvalidEmail => Problems.Validation(new Dictionary<string, string> { ["email"] = "Enter a valid email address." }),
        StaffOutcome.InvalidName => Problems.Validation(new Dictionary<string, string> { ["displayName"] = "Enter a name of up to 60 characters." }),
        _ => Problems.NotFound(),
    };

    private static async Task<IResult> ListAccounts(StaffService staff, CancellationToken ct) => Results.Json(await staff.ListAsync(ct));

    private static async Task<IResult> CreateAccount(CreateAccountBody? body, HttpContext ctx, StaffService staff, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var r = await staff.CreateAsync(ctx.Staff(), body!.Email, body.DisplayName, body.Role, StaffReason.Clean(body.Reason), ct);
        return r.Outcome == StaffOutcome.Ok ? Results.Json(new { account = r.Account, enrolmentToken = r.EnrolmentToken, expiresAt = r.ExpiresAt }, statusCode: 201) : Outcome(r.Outcome);
    }

    private static async Task<IResult> Reissue(Guid id, EnrolmentBody? body, HttpContext ctx, StaffService staff, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        var r = await staff.ReissueEnrolmentAsync(ctx.Staff(), id, body!.RevokePasskeys, StaffReason.Clean(body.Reason), ct);
        return r.Outcome == StaffOutcome.Ok ? Results.Json(new { enrolmentToken = r.EnrolmentToken, expiresAt = r.ExpiresAt }) : Outcome(r.Outcome);
    }

    private static async Task<IResult> SetRole(Guid id, RoleBody? body, HttpContext ctx, StaffService staff, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return Outcome(await staff.SetRoleAsync(ctx.Staff(), id, body!.Role, StaffReason.Clean(body.Reason), ct));
    }

    private static async Task<IResult> SetDisabled(Guid id, ReasonBody? body, bool disabled, HttpContext ctx, StaffService staff, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (Guard(ctx, ctx.RequestServices.GetRequiredService<TimeProvider>(), o, body?.Reason) is { } refused) return refused;
        return Outcome(await staff.SetDisabledAsync(ctx.Staff(), id, disabled, StaffReason.Clean(body!.Reason), ct));
    }

    private static async Task<IResult> SignOutEverywhere(Guid id, ReasonBody? body, HttpContext ctx, StaffService staff, TimeProvider time, IOptions<StaffOptions> o, CancellationToken ct)
    {
        if (Guard(ctx, time, o, body?.Reason) is { } refused) return refused;
        return Outcome(await staff.SignOutEverywhereAsync(ctx.Staff(), id, StaffReason.Clean(body!.Reason), ct));
    }

    // ---------- audit (admin) ----------

    private static async Task<IResult> ListAudit(MarketplaceDb db, string? action, Guid? actor, string? targetType, string? target, int? limit, string? before, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var q = db.StaffAudit.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action == action);
        if (actor is { } id) q = q.Where(a => a.ActorId == id);
        if (!string.IsNullOrEmpty(targetType)) q = q.Where(a => a.TargetType == targetType);
        if (!string.IsNullOrEmpty(target)) q = q.Where(a => a.TargetId == target);
        if (!string.IsNullOrEmpty(before))
        {
            if (!StaffCursor.TryDecode(before, out var at, out var beforeId)) return Problems.BadRequest("invalid_cursor", "The cursor is not valid.");
            q = q.Where(a => a.At < at || (a.At == at && a.Id.CompareTo(beforeId) < 0));
        }
        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return Results.Json(new
        {
            entries = rows.Select(a => new { id = a.Id, at = a.At, actorId = a.ActorId, actorRole = a.ActorRole, action = a.Action, targetType = a.TargetType, targetId = a.TargetId, reason = a.Reason, detail = a.Detail }),
            next = more ? StaffCursor.Encode(rows[^1].At, rows[^1].Id) : null,
        });
    }
}

/// <summary>Keyset cursor on (time, id), the same shape the outbox list uses.</summary>
public static class StaffCursor
{
    public static string Encode(DateTimeOffset at, Guid id) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{at.UtcTicks}|{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string cursor, out DateTimeOffset at, out Guid id)
    {
        at = default; id = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id)) return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Cursor for lists keyed by (time, text id), such as Stripe event ids.</summary>
    public static string EncodeText(DateTimeOffset at, string id) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{at.UtcTicks}|{id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeText(string cursor, out DateTimeOffset at, out string id)
    {
        at = default; id = "";
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var bar = text.IndexOf('|');
            if (bar < 1 || !long.TryParse(text[..bar], out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero); id = text[(bar + 1)..];
            return id.Length is > 0 and <= 100;
        }
        catch (FormatException) { return false; }
    }
}
