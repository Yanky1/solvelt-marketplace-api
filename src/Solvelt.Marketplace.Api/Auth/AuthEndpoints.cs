using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Auth;

public static class AuthEndpoints
{
    private const string PrincipalKey = "solvelt.buyer";

    public sealed record SignUpBody(string? Email, string? Password, string? DisplayName);
    public sealed record EmailBody(string? Email);
    public sealed record VerifyBody(string? Token, string? Password);
    public sealed record SignInBody(string? Email, string? Password);
    public sealed record ResetBody(string? Token, string? Password);
    public sealed record PasswordBody(string? Password);
    public sealed record PasskeyAssertionBody(Guid? ChallengeId, System.Text.Json.JsonElement? Credential);

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/sign-up", SignUp).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/resend-verification", ResendVerification).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/verify-email", VerifyEmail).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/sign-in", SignIn).RequireRateLimiting(RateLimitPolicies.Auth);
        // passkeys (M1-80): anonymous sign-in, and a second way to step up
        auth.MapPost("/passkey/options", PasskeySignInOptions).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/passkey/sign-in", PasskeySignIn).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/reauthenticate/passkey/options", PasskeyReauthOptions).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/reauthenticate/passkey", PasskeyReauth).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/forgot-password", ForgotPassword).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/reset-password", ResetPassword).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/reauthenticate", Reauthenticate).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/session/rotate", Rotate).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/sign-out", SignOut).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        auth.MapPost("/sign-out-all", SignOutAll).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        app.MapGet("/api/v1/account/me", Me).AddEndpointFilter<RequireBuyerFilter>().RequireRateLimiting(RateLimitPolicies.Public);
        return app;
    }

    /// <summary>The signed-in buyer, set by <see cref="RequireBuyerFilter"/>.</summary>
    public static BuyerPrincipal Buyer(this HttpContext ctx) => (BuyerPrincipal)ctx.Items[PrincipalKey]!;

    internal static void SetBuyer(HttpContext ctx, BuyerPrincipal p) => ctx.Items[PrincipalKey] = p;

    private static async Task<IResult> SignUp(SignUpBody? body, AuthService auth, IOptions<AuthOptions> o, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (AuthValidation.EmailProblem(body?.Email) is { } e) errors["email"] = e;
        if (AuthValidation.PasswordProblem(body?.Password, body?.Email, o.Value.PasswordMinLength) is { } p) errors["password"] = p;
        if (AuthValidation.DisplayNameProblem(body?.DisplayName) is { } d) errors["displayName"] = d;
        if (errors.Count > 0) return Problems.Validation(errors);
        await auth.SignUpAsync(body!.Email!, body.Password!, body.DisplayName!, ct);
        return Results.NoContent();   // the same for a new, an unverified and an existing address
    }

    private static async Task<IResult> ResendVerification(EmailBody? body, AuthService auth, CancellationToken ct)
    {
        if (AuthValidation.EmailProblem(body?.Email) is null) await auth.ResendVerificationAsync(body!.Email!, ct);
        return Results.NoContent();
    }

    /// <summary>Needs the link and the account's password (decision O-16 b). A wrong password is the same generic 400 as a bad link, and the link is not used up.</summary>
    private static async Task<IResult> VerifyEmail(VerifyBody? body, AuthService auth, HttpContext ctx, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.Token)) return Problems.BadRequest("invalid_token", "This link is invalid or has expired.");
        if (string.IsNullOrEmpty(body.Password)) return Problems.Validation(new Dictionary<string, string> { ["password"] = "Enter the password you chose when you signed up." });
        if (body.Password.Length > 128) return Problems.BadRequest("invalid_token", "This link is invalid or has expired.");
        return await auth.VerifyEmailAsync(body.Token, body.Password, ips.Resolve(ctx), ct) ? Results.NoContent() : Problems.BadRequest("invalid_token", "This link is invalid or has expired.");
    }

    private static async Task<IResult> SignIn(SignInBody? body, AuthService auth, HttpContext ctx, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Email) || string.IsNullOrEmpty(body.Password) || body.Password.Length > 128 || body.Email.Length > 254)
            return Problems.Json(401, "invalid_credentials", "The email or password is not correct.");
        var r = await auth.SignInAsync(body.Email, body.Password, ips.Resolve(ctx), ct);
        switch (r.Status)
        {
            case SignInStatus.Ok:
                return Results.Json(new { token = r.Token, user = new { id = r.Account!.Id, displayName = r.Account.DisplayName } });
            case SignInStatus.EmailNotVerified:
                return Problems.Json(403, "email_not_verified", "Confirm your email address first. You can ask for a new link.");
            case SignInStatus.Locked:
                return Problems.Json(403, "account_locked", "This account is locked. Contact support.");
            case SignInStatus.TooManyAttempts:
                ctx.Response.Headers.RetryAfter = (r.RetryAfterSeconds ?? 60).ToString();
                return Problems.Json(429, "rate_limited", "Too many attempts. Try again later.");
            default:
                return Problems.Json(401, "invalid_credentials", "The email or password is not correct.");
        }
    }

    private static async Task<IResult> PasskeySignInOptions(PasskeyService passkeys, CancellationToken ct)
    {
        var (challengeId, options) = await passkeys.BeginSignInAsync(ct);
        return Results.Json(new { challengeId, options = System.Text.Json.JsonDocument.Parse(options).RootElement });
    }

    private static async Task<IResult> PasskeySignIn(PasskeyAssertionBody? body, PasskeyService passkeys, CancellationToken ct)
    {
        var r = body?.ChallengeId is { } challengeId ? await passkeys.SignInAsync(challengeId, body.Credential, ct) : new SignInResult(SignInStatus.InvalidCredentials);
        return r.Status == SignInStatus.Ok
            ? Results.Json(new { token = r.Token, user = new { id = r.Account!.Id, displayName = r.Account.DisplayName } })
            : Problems.Json(401, "invalid_credentials", "The passkey was not accepted.");
    }

    private static async Task<IResult> PasskeyReauthOptions(HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
    {
        var (outcome, challengeId, options) = await passkeys.BeginReauthenticationAsync(ctx.Buyer(), ct);
        return outcome == PasskeyOutcome.Ok ? Results.Json(new { challengeId, options = System.Text.Json.JsonDocument.Parse(options!).RootElement }) : Account.AccountEndpoints.PasskeyProblem(outcome);
    }

    private static async Task<IResult> PasskeyReauth(PasskeyAssertionBody? body, HttpContext ctx, PasskeyService passkeys, CancellationToken ct)
    {
        if (body?.ChallengeId is not { } challengeId) return Account.AccountEndpoints.PasskeyProblem(PasskeyOutcome.InvalidChallenge);
        var outcome = await passkeys.ReauthenticateAsync(ctx.Buyer(), challengeId, body.Credential, ct);
        return outcome == PasskeyOutcome.Ok ? Results.NoContent() : Account.AccountEndpoints.PasskeyProblem(outcome);
    }

    private static async Task<IResult> ForgotPassword(EmailBody? body, AuthService auth, CancellationToken ct)
    {
        if (AuthValidation.EmailProblem(body?.Email) is null) await auth.ForgotPasswordAsync(body!.Email!, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPassword(ResetBody? body, AuthService auth, IOptions<AuthOptions> o, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.Token)) return Problems.BadRequest("invalid_token", "This link is invalid or has expired.");
        if (AuthValidation.PasswordProblem(body.Password, null, o.Value.PasswordMinLength) is { } p)
            return Problems.Validation(new Dictionary<string, string> { ["password"] = p });
        var r = await auth.ResetPasswordAsync(body.Token, body.Password!, ct);   // also refuses a password equal to the address, without using the link up
        if (r.PasswordProblem is { } problem) return Problems.Validation(new Dictionary<string, string> { ["password"] = problem });
        return r.Ok ? Results.NoContent() : Problems.BadRequest("invalid_token", "This link is invalid or has expired.");
    }

    /// <summary>Step-up: <c>204</c> when the password is right (the session's authenticated_at moves to now), <c>401 invalid_credentials</c> when it is not, <c>429</c> with Retry-After when the gate says wait.</summary>
    private static async Task<IResult> Reauthenticate(PasswordBody? body, AuthService auth, HttpContext ctx, ClientIpResolver ips, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body?.Password)) return Problems.Validation(new Dictionary<string, string> { ["password"] = "Enter your password." });
        if (body.Password.Length > 128) return Problems.Json(401, "invalid_credentials", "The password is not correct.");
        var r = await auth.ReauthenticateAsync(ctx.Buyer(), body.Password, ips.Resolve(ctx), ct);
        switch (r.Status)
        {
            case SignInStatus.Ok: return Results.NoContent();
            case SignInStatus.TooManyAttempts:
                ctx.Response.Headers.RetryAfter = (r.RetryAfterSeconds ?? 60).ToString();
                return Problems.Json(429, "rate_limited", "Too many attempts. Try again later.");
            default: return Problems.Json(401, "invalid_credentials", "The password is not correct.");
        }
    }

    /// <summary><c>200 { token }</c>: a new session token; the old one is revoked.</summary>
    private static async Task<IResult> Rotate(HttpContext ctx, AuthService auth, CancellationToken ct)
    {
        var token = await auth.RotateSessionAsync(ctx.Buyer(), ct);
        return token is null ? Problems.Json(401, "unauthorized", "Sign in to continue.") : Results.Json(new { token });
    }

    private static async Task<IResult> SignOut(HttpContext ctx, AuthService auth, CancellationToken ct)
    {
        await auth.SignOutAsync(ctx.Buyer().SessionId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SignOutAll(HttpContext ctx, AuthService auth, CancellationToken ct)
    {
        await auth.SignOutAllAsync(ctx.Buyer().AccountId, ct);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext ctx)
    {
        var b = ctx.Buyer();
        return Results.Json(new { id = b.AccountId, displayName = b.DisplayName, email = b.Email });
    }
}

/// <summary>Requires <c>Authorization: Bearer &lt;session token&gt;</c>; otherwise <c>401 unauthorized</c>.</summary>
public sealed class RequireBuyerFilter(AuthService auth) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var principal = await auth.AuthenticateAsync(token, context.HttpContext.RequestAborted);
        if (principal is null) return Problems.Json(401, "unauthorized", "Sign in to continue.");
        AuthEndpoints.SetBuyer(context.HttpContext, principal);
        return await next(context);
    }
}
