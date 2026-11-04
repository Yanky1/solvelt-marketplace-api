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
    public sealed record ChangePasswordBody(string? CurrentPassword, string? NewPassword);
    public sealed record DeleteBody(string? Password);

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

    private static IResult Map(AccountResult r, HttpContext ctx, string passwordField)
    {
        switch (r.Outcome)
        {
            case AccountOutcome.Ok: return Results.NoContent();
            case AccountOutcome.ReauthenticationRequired: return Problems.Json(403, "reauthentication_required", "Confirm your password again first.");
            case AccountOutcome.WeakPassword: return Problems.Validation(new Dictionary<string, string> { [passwordField] = r.Problem ?? "Choose a stronger password." });
            case AccountOutcome.InvalidCredentials: return Problems.Json(401, "invalid_credentials", "The password is not correct.");
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
        return Map(await a.ChangePasswordAsync(ctx.Buyer(), body.CurrentPassword, body.NewPassword!, ips.Resolve(ctx), ct), ctx, "newPassword");
    }

    /// <summary>Deletes the account (soft delete now, erasure by the redaction job). Needs a recent sign-in and the password.</summary>
    private static async Task<IResult> Delete(DeleteBody? body, HttpContext ctx, AccountService a, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.Password)) return Problems.Validation(new Dictionary<string, string> { ["password"] = "Enter your password." });
        if (body.Password.Length > 128) return Problems.Json(401, "invalid_credentials", "The password is not correct.");
        return Map(await a.DeleteAccountAsync(ctx.Buyer(), body.Password, ips.Resolve(ctx), ct), ctx, "password");
    }
}
