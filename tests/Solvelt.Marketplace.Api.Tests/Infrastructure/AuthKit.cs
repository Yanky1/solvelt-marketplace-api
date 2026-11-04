using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>Helpers for tests that drive the auth endpoints and read the emails the outbox would send.</summary>
public sealed class AuthKit : IDisposable
{
    public const string Password = "correct horse battery";
    public TestFactory F { get; }
    public HttpClient Http { get; }

    public AuthKit(Dictionary<string, string?>? config = null)
    {
        var c = new Dictionary<string, string?> { ["Auth:PasswordIterations"] = "1000", ["RateLimits:AuthPerMinute"] = "10000", ["RateLimits:PublicPerMinute"] = "10000" };
        if (config != null) foreach (var kv in config) c[kv.Key] = kv.Value;
        F = new TestFactory(c);
        Http = F.CreateClient();
    }

    public void Dispose() => F.Dispose();

    public Task<HttpResponseMessage> Post(string path, object? body, string? token = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Http.SendAsync(r);
    }

    public Task<HttpResponseMessage> Get(string path, string? token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Http.SendAsync(r);
    }

    public Task<HttpResponseMessage> SignUp(string email, string? password = null, string name = "Jane Brown")
        => Post("/api/v1/auth/sign-up", new { email, password = password ?? Password, displayName = name });

    public async Task<int> Dispatch()
    {
        using var scope = F.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
    }

    public DevEmailSink Sink => (DevEmailSink)F.Services.GetRequiredService<IEmailSender>();

    /// <summary>Delivers queued mail and returns what was sent to <paramref name="to"/>.</summary>
    public async Task<List<EmailMessage>> Mail(string to)
    {
        while (await Dispatch() > 0) { }
        return Sink.Sent.Where(m => m.To == to).ToList();
    }

    public static string TokenIn(EmailMessage m, string page)
    {
        var match = Regex.Match(m.TextBody, Regex.Escape(page) + @"\?token=([A-Za-z0-9_.-]+)");
        Assert.True(match.Success, $"no {page} link in: {m.TextBody}");
        return match.Groups[1].Value;
    }

    /// <summary>Signs up, verifies through the emailed link and signs in. Returns the session token.</summary>
    public async Task<string> VerifiedSession(string email, string? password = null, string name = "Jane Brown")
    {
        Assert.Equal(HttpStatusCode.NoContent, (await SignUp(email, password, name)).StatusCode);
        var mail = (await Mail(email.Trim().ToLowerInvariant())).Last(m => m.Subject.StartsWith("Confirm"));
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/v1/auth/verify-email", new { token = TokenIn(mail, "/verify-email") })).StatusCode);
        var r = await Post("/api/v1/auth/sign-in", new { email, password = password ?? Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<SignInResponse>())!.Token;
    }

    public sealed record SignInResponse(string Token, SignInUser User);
    public sealed record SignInUser(Guid Id, string DisplayName);

    public async Task<T> Db<T>(Func<MarketplaceDb, Task<T>> f)
    {
        using var scope = F.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<MarketplaceDb>());
    }

    public Task<BuyerAccount> Account(string email) => Db(db => db.BuyerAccounts.AsNoTracking().FirstAsync(a => a.Email == email));
}
