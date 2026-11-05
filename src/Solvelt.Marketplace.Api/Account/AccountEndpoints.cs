using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>
/// <c>/api/v1/account/*</c> for the signed-in buyer (<c>Authorization: Bearer</c>). Another buyer's address is <c>404</c>, never <c>403</c>; ids are GUIDs only (anything
/// else matches no route and is <c>404</c>); every answer is <c>private, no-store</c>.
/// </summary>
public static class AccountEndpoints
{
    public sealed record ProfileBody(string? DisplayName, string? Phone);
    public sealed record ChangePasswordBody(string? CurrentPassword, string? NewPassword, bool? RemovePasskeys = null);
    public sealed record DeleteBody(string? Password);
    public sealed record PasskeyRegisterBody(Guid? ChallengeId, string? Name, System.Text.Json.JsonElement? Credential);
    public sealed record PasskeyRenameBody(string? Name);

    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/account").AddEndpointFilter<RequireBuyerFilter>().AddEndpointFilter(NoStore).RequireRateLimiting(RateLimitPolicies.Public);
        g.MapGet("/profile", GetProfile);
        g.MapPut("/profile", PutProfile);
        g.MapGet("/addresses", ListAddresses);
        g.MapPost("/addresses", CreateAddress);
        g.MapGet("/addresses/{id:guid}", GetAddress);
        g.MapPut("/addresses/{id:guid}", PutAddress);
        g.MapDelete("/addresses/{id:guid}", DeleteAddress);
        g.MapPost("/addresses/{id:guid}/default", SetDefault);
        // sensitive: the strict per-client limit, a recent sign-in and the password
        g.MapPost("/password", ChangePassword).RequireRateLimiting(RateLimitPolicies.Auth);
        g.MapPost("/delete", Delete).RequireRateLimiting(RateLimitPolicies.Auth);
        // passkeys (M1-80): adding and removing one needs a recent sign-in like the password change
        g.MapGet("/passkeys", ListPasskeys);
        g.MapPost("/passkeys/options", PasskeyOptions).RequireRateLimiting(RateLimitPolicies.Auth);
        g.MapPost("/passkeys", RegisterPasskey).RequireRateLimiting(RateLimitPolicies.Auth);
        g.MapPatch("/passkeys/{id:guid}", RenamePasskey);
        g.MapDelete("/passkeys/{id:guid}", RemovePasskey).RequireRateLimiting(RateLimitPolicies.Auth);
        g.MapDelete("/passkeys", RemoveAllPasskeys).RequireRateLimiting(RateLimitPolicies.Auth);
        return app;
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        c.HttpContext.Response.Headers.CacheControl = "private, no-store";
        return await next(c);
    }

    private static async Task<IResult> GetProfile(HttpContext ctx, AccountService a, CancellationToken ct)
        => await a.GetProfileAsync(ctx.Buyer().AccountId, ct) is { } p ? Results.Json(p) : Problems.NotFound();

    private static async Task<IResult> PutProfile(ProfileBody? body, HttpContext ctx, AccountService a, CancellationToken ct)
    {
        var errors = AccountValidation.Profile(body?.DisplayName, body?.Phone);
        if (errors.Count > 0) return Problems.Validation(errors);
        return await a.UpdateProfileAsync(ctx.Buyer().AccountId, body!.DisplayName!, body.Phone, ct) is { } p ? Results.Json(p) : Problems.NotFound();
    }

    private static async Task<IResult> ListAddresses(HttpContext ctx, AccountService a, CancellationToken ct)
        => Results.Json(await a.ListAddressesAsync(ctx.Buyer().AccountId, ct));

    private static async Task<IResult> GetAddress(Guid id, HttpContext ctx, AccountService a, CancellationToken ct)
        => await a.GetAddressAsync(ctx.Buyer().AccountId, id, ct) is { } v ? Results.Json(v) : Problems.NotFound();

    private static async Task<IResult> CreateAddress(AddressBody? body, HttpContext ctx, AccountService a, CancellationToken ct)
    {
        var errors = AccountValidation.Address(body);
        if (errors.Count > 0) return Problems.Validation(errors);
        var (address, outcome) = await a.CreateAddressAsync(ctx.Buyer().AccountId, body!, ct);
        return outcome == AccountOutcome.TooMany
            ? Problems.Json(409, "too_many_addresses", $"You can keep up to {AccountValidation.MaxAddresses} addresses. Delete one first.")
            : Results.Json(address, statusCode: 201);
    }

    private static async Task<IResult> PutAddress(Guid id, AddressBody? body, HttpContext ctx, AccountService a, CancellationToken ct)
    {
        var errors = AccountValidation.Address(body);
        if (errors.Count > 0) return Problems.Validation(errors);
        return await a.UpdateAddressAsync(ctx.Buyer().AccountId, id, body!, ct) is { } v ? Results.Json(v) : Problems.NotFound();
    }

    private static async Task<IResult> DeleteAddress(Guid id, HttpContext ctx, AccountService a, CancellationToken ct)
        => await a.DeleteAddressAsync(ctx.Buyer().AccountId, id, ct) ? Results.NoContent() : Problems.NotFound();

    private static async Task<IResult> SetDefault(Guid id, HttpContext ctx, AccountService a, CancellationToken ct)
        => await a.SetDefaultAsync(ctx.Buyer().AccountId, id, ct) ? Results.NoContent() : Problems.NotFound();

    private static async Task<IResult> ListPasskeys(HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
        => Results.Json((await passkeys.ListAsync(ctx.Buyer().AccountId, ct)).Select(p => new { id = p.Id, name = p.Name, createdAt = p.CreatedAt, lastUsedAt = p.LastUsedAt, backedUp = p.BackedUp }));

    private static async Task<IResult> PasskeyOptions(HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
    {
        var (outcome, challengeId, options) = await passkeys.BeginRegistrationAsync(ctx.Buyer(), ct);
        return outcome == PasskeyOutcome.Ok ? Results.Json(new { challengeId, options = System.Text.Json.JsonDocument.Parse(options!).RootElement }) : PasskeyProblem(outcome);
    }

    private static async Task<IResult> RegisterPasskey(PasskeyRegisterBody? body, HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
    {
        if (body?.ChallengeId is not { } challengeId) return Problems.Json(400, "invalid_challenge", "Start again: the passkey request is missing or has expired.");
        var (outcome, passkey) = await passkeys.CompleteRegistrationAsync(ctx.Buyer(), challengeId, body.Name, body.Credential, ct);
        return outcome == PasskeyOutcome.Ok
            ? Results.Json(new { id = passkey!.Id, name = passkey.Name, createdAt = passkey.CreatedAt, lastUsedAt = passkey.LastUsedAt, backedUp = passkey.BackedUp }, statusCode: 201)
            : PasskeyProblem(outcome);
    }

    private static async Task<IResult> RenamePasskey(Guid id, PasskeyRenameBody? body, HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
        => PasskeyOutcomeToResult(await passkeys.RenameAsync(ctx.Buyer().AccountId, id, body?.Name, ct));

    private static async Task<IResult> RemovePasskey(Guid id, HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
        => PasskeyOutcomeToResult(await passkeys.RemoveAsync(ctx.Buyer(), id, ct));

    /// <summary>Removes every passkey of the account (a recent sign-in; refused when the account has no password, so it is never left without a way in). The answer to "I do not recognise one of them".</summary>
    private static async Task<IResult> RemoveAllPasskeys(HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
        => PasskeyOutcomeToResult(await passkeys.RemoveAllAsync(ctx.Buyer(), ct));

    private static IResult PasskeyOutcomeToResult(PasskeyOutcome outcome) => outcome == PasskeyOutcome.Ok ? Results.NoContent() : PasskeyProblem(outcome);

    internal static IResult PasskeyProblem(PasskeyOutcome outcome) => outcome switch
    {
        PasskeyOutcome.ReauthenticationRequired => Problems.Json(403, "reauthentication_required", "Confirm your password again first."),
        PasskeyOutcome.InvalidChallenge => Problems.Json(400, "invalid_challenge", "Start again: the passkey request is missing, used or has expired."),
        PasskeyOutcome.InvalidCredential => Problems.Json(400, "invalid_credential", "The passkey could not be verified."),
        PasskeyOutcome.AlreadyRegistered => Problems.Json(409, "already_registered", "This passkey is already registered."),
        PasskeyOutcome.TooMany => Problems.Json(409, "too_many_passkeys", "You have reached the number of passkeys you can keep. Remove one first."),
        PasskeyOutcome.LastSignInMethod => Problems.Json(409, "last_sign_in_method", "This is your only way to sign in. Add another one first."),
        PasskeyOutcome.InvalidName => Problems.Validation(new Dictionary<string, string> { ["name"] = "Give the passkey a name of up to 60 characters." }),
        PasskeyOutcome.NoPasskeys => Problems.Json(409, "no_passkeys", "There is no passkey on this account."),
        _ => Problems.NotFound(),
    };

    private static IResult Map(AccountResult r, HttpContext ctx, string passwordField)
    {
        switch (r.Outcome)
        {
            case AccountOutcome.Ok: return Results.NoContent();
            case AccountOutcome.ReauthenticationRequired: return Problems.Json(403, "reauthentication_required", "Confirm your password again first.");
            case AccountOutcome.WeakPassword: return Problems.Validation(new Dictionary<string, string> { [passwordField] = r.Problem ?? "Choose a stronger password." });
            case AccountOutcome.InvalidCredentials: return Problems.Json(401, "invalid_credentials", "The password is not correct.");
            case AccountOutcome.Conflict: return Problems.Json(409, "account_changed", "Your account changed while this was running. Sign in again and retry.");
            case AccountOutcome.RateLimited:
                ctx.Response.Headers.RetryAfter = Math.Max(1, r.RetryAfterSeconds).ToString();
                return Problems.Json(429, "rate_limited", "Too many attempts. Try again later.");
            default: return Problems.NotFound();
        }
    }

    /// <summary><c>204</c>; <c>403 reauthentication_required</c> when the last sign-in is older than 10 minutes (the web asks for the password and calls <c>/auth/reauthenticate</c>, then retries); <c>401</c> for a wrong current password.</summary>
    private static async Task<IResult> ChangePassword(ChangePasswordBody? body, HttpContext ctx, AccountService a, ClientIpResolver ips, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(body?.CurrentPassword)) errors["currentPassword"] = "Enter your current password.";
        if (string.IsNullOrEmpty(body?.NewPassword)) errors["newPassword"] = "Enter a new password.";
        if (errors.Count > 0) return Problems.Validation(errors);
        if (body!.CurrentPassword!.Length > 128) return Problems.Json(401, "invalid_credentials", "The password is not correct.");
        return Map(await a.ChangePasswordAsync(ctx.Buyer(), body.CurrentPassword, body.NewPassword!, ips.Resolve(ctx), ct, body.RemovePasskeys == true), ctx, "newPassword");
    }

    /// <summary>Deletes the account (soft delete now, erasure by the redaction job). Needs a recent sign-in and the password.</summary>
    private static async Task<IResult> Delete(DeleteBody? body, HttpContext ctx, AccountService a, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.Password)) return Problems.Validation(new Dictionary<string, string> { ["password"] = "Enter your password." });
        if (body.Password.Length > 128) return Problems.Json(401, "invalid_credentials", "The password is not correct.");
        return Map(await a.DeleteAccountAsync(ctx.Buyer(), body.Password, ips.Resolve(ctx), ct), ctx, "password");
    }
}
