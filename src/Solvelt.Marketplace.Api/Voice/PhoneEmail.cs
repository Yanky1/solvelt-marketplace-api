using System.Net;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The confirm page for an email address given by phone (decision V-13b). The mailed link opens it with GET, which only SHOWS a button (a mail scanner that opens links cannot confirm anything);
/// the POST consumes the single-use link and makes the pending address the account's verified email. No password is involved (a phone-made account has none). The page says nothing about any account.
/// </summary>
public static class PhoneEmailEndpoints
{
    public const string Path = "/api/v1/auth/phone-email";

    public static IEndpointRouteBuilder MapPhoneEmail(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (string? token) => Page(token is { Length: > 0 and <= 400 } t
            ? $"<p>Press the button to confirm that this email address is yours and may be added to a Solvelt account.</p><form method=\"post\" action=\"{Path}\"><input type=\"hidden\" name=\"token\" value=\"{WebUtility.HtmlEncode(t)}\"><button type=\"submit\">Confirm this email address</button></form>"
            : "<p>This link is not valid.</p>")).RequireRateLimiting(RateLimitPolicies.Auth);
        app.MapPost(Path, async (HttpRequest req, AuthService auth, CancellationToken ct) =>
        {
            var token = req.HasFormContentType ? (await req.ReadFormAsync(ct)).TryGetValue("token", out var v) ? v.ToString() : "" : "";
            if (token.Length is 0 or > 400) return Page("<p>This link is not valid, was already used, or has expired.</p>");
            return await auth.ConfirmPhoneEmailAsync(token, ct)
                ? Page("<p>Thank you. The email address is confirmed and now belongs to the account. You can close this page.</p>")
                : Page("<p>This link is not valid, was already used, or has expired.</p>");
        }).RequireRateLimiting(RateLimitPolicies.Auth);
        return app;
    }

    private static IResult Page(string body) => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\"><title>Solvelt</title></head><body style=\"font-family:sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem\"><h1>Solvelt</h1>" + body + "</body></html>", "text/html; charset=utf-8");
}
