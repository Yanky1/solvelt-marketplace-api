using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>A running API with a bootstrap admin configured, plus the calls every staff test makes (software authenticators stand in for the browser).</summary>
public sealed class StaffKit : IDisposable
{
    public const string BootstrapToken = "bootstrap-token-for-tests-0123456789abcdef";
    public const string AdminEmail = "ops-admin@example.com";
    public AuthKit K { get; }
    public TestFactory F => K.F;

    public StaffKit(Dictionary<string, string?>? config = null, bool bootstrap = true, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var c = new Dictionary<string, string?> { ["RateLimits:StaffPerMinute"] = "100000", ["RateLimits:AuthPerMinute"] = "100000" };
        if (bootstrap) { c["Staff:BootstrapToken"] = BootstrapToken; c["Staff:BootstrapEmail"] = AdminEmail; }
        if (config != null) foreach (var kv in config) c[kv.Key] = kv.Value;
        K = new AuthKit(c, services);
    }

    public void Dispose() => K.Dispose();

    public static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    public Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null, string? token = null) => K.Send(m, path, body, token);
    public Task<HttpResponseMessage> Post(string path, object? body, string? token = null) => K.Post(path, body, token);
    public Task<HttpResponseMessage> Get(string path, string? token) => K.Get(path, token);

    /// <summary>Enrols a passkey with an enrolment link (the bootstrap token, or one an admin made) and returns the signed-in token.</summary>
    public async Task<(string Token, VirtualAuthenticator Authn, Guid StaffId)> Enrol(string enrolmentToken, VirtualAuthenticator? authn = null, string name = "Laptop")
    {
        authn ??= new VirtualAuthenticator();
        var o = await Post("/api/v1/staff/auth/enrol/options", new { token = enrolmentToken });
        Assert.Equal(HttpStatusCode.OK, o.StatusCode);
        var oj = await Json(o);
        var r = await Post("/api/v1/staff/auth/enrol", new { token = enrolmentToken, challengeId = oj.GetProperty("challengeId").GetGuid(), name, credential = authn.Register(oj.GetProperty("options")) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        return (j.GetProperty("token").GetString()!, authn, j.GetProperty("staff").GetProperty("id").GetGuid());
    }

    public async Task<HttpResponseMessage> TryEnrol(string enrolmentToken, VirtualAuthenticator authn)
    {
        var o = await Post("/api/v1/staff/auth/enrol/options", new { token = enrolmentToken });
        if (o.StatusCode != HttpStatusCode.OK) return o;
        var oj = await Json(o);
        return await Post("/api/v1/staff/auth/enrol", new { token = enrolmentToken, challengeId = oj.GetProperty("challengeId").GetGuid(), name = "Key", credential = authn.Register(oj.GetProperty("options")) });
    }

    public async Task<(string Token, VirtualAuthenticator Authn, Guid StaffId)> Admin() => await Enrol(BootstrapToken);

    public async Task<HttpResponseMessage> SignInResponse(VirtualAuthenticator authn)
    {
        var o = await Post("/api/v1/staff/auth/passkey/options", null);
        Assert.Equal(HttpStatusCode.OK, o.StatusCode);
        var oj = await Json(o);
        return await Post("/api/v1/staff/auth/passkey/sign-in", new { challengeId = oj.GetProperty("challengeId").GetGuid(), credential = authn.Assert(oj.GetProperty("options")) });
    }

    public async Task<string> SignIn(VirtualAuthenticator authn)
    {
        var r = await SignInResponse(authn);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("token").GetString()!;
    }

    /// <summary>The passkey check that makes a session "recent" again.</summary>
    public async Task StepUp(string token, VirtualAuthenticator authn)
    {
        var o = await Post("/api/v1/staff/auth/reauthenticate/options", null, token);
        Assert.Equal(HttpStatusCode.OK, o.StatusCode);
        var oj = await Json(o);
        var r = await Post("/api/v1/staff/auth/reauthenticate", new { challengeId = oj.GetProperty("challengeId").GetGuid(), credential = authn.Assert(oj.GetProperty("options")) }, token);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    public const string Reason = "ticket 4412: routine check";

    /// <summary>Creates a staff member as <paramref name="adminToken"/> and enrols them; returns their token and authenticator.</summary>
    public async Task<(string Token, VirtualAuthenticator Authn, Guid StaffId)> Member(string adminToken, string email, string role, string name = "Sam Staff")
    {
        var r = await Post("/api/v1/staff/accounts", new { email, displayName = name, role, reason = Reason }, adminToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        return await Enrol(j.GetProperty("enrolmentToken").GetString()!);
    }

    public async Task<T> Db<T>(Func<MarketplaceDb, Task<T>> f)
    {
        using var scope = F.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<MarketplaceDb>());
    }

    public Task<List<StaffAuditEntry>> Audit() => Db(db => db.StaffAudit.AsNoTracking().OrderBy(a => a.At).ToListAsync());
}
