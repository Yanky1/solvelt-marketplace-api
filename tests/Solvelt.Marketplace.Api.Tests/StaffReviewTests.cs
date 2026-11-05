using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The independent security review of slice 1 (89df5f7..7e641ed): MED-1..3 and the LOWs fixed with slice 2 (decision M7-15). Each test fails without its fix.</summary>
public class StaffReviewTests
{
    private static readonly HttpMethod Post = HttpMethod.Post, Delete = HttpMethod.Delete;
    private static string R => StaffKit.Reason;
    private const string Accounts = "/api/v1/staff/accounts";

    /// <summary>Makes the audit write of one test host fail on demand, the way a full disk or a dropped connection would.</summary>
    private sealed class AuditFault : SaveChangesInterceptor
    {
        public volatile bool On;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        {
            if (On && e.Context!.ChangeTracker.Entries<StaffAuditEntry>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("audit write failed");
            return base.SavingChangesAsync(e, r, ct);
        }
    }

    private static StaffKit WithFault(AuditFault fault) => new(services: s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(fault)));

    // ---------- MED-1: an allow-list that is set but blank never means "everyone" ----------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_allow_list_entry_is_refused_at_start(string blank)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Staff:AllowedIps:0"] = blank }).Build();
        Assert.Contains(StaffConfig.Problems(cfg), p => p.Contains("blank entry"));
        var mixed = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Staff:AllowedIps:0"] = "10.0.0.0/8", ["Staff:AllowedIps:1"] = blank }).Build();
        Assert.Contains(StaffConfig.Problems(mixed), p => p.Contains("blank entry"));
        Assert.Throws<InvalidOperationException>(() => IpAllowList.From([blank]));
    }

    [Fact]
    public void A_blank_allow_list_fails_closed_in_the_running_host_too_it_never_lets_everyone_in()
    {
        // (the start-up validator refuses it in a real start; the middleware itself refuses to be built from a blank entry, so no request is ever answered as "allowed")
        var ex = Assert.ThrowsAny<Exception>(() => { using var s = new StaffKit(new() { ["Staff:AllowedIps:0"] = " " }); });
        Assert.Contains("blank Staff:AllowedIps", ex.ToString());
    }

    // ---------- MED-2: the audit row and the action commit together ----------

    [Fact]
    public async Task A_failing_audit_write_rolls_the_action_back_sign_in_step_up_and_sign_out()
    {
        var fault = new AuditFault();
        using var s = WithFault(fault);
        var (token, authn, id) = await s.Admin();
        var sessionsBefore = await s.Db(db => db.StaffSessions.CountAsync(x => x.StaffId == id));
        var signedInAt = await s.Db(db => db.StaffAccounts.Where(a => a.Id == id).Select(a => a.LastSignInAt).SingleAsync());

        // sign in: no session, no new sign-in time
        s.F.Clock.Advance(TimeSpan.FromMinutes(1));
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.SignInResponse(authn)).StatusCode);
        fault.On = false;
        Assert.Equal(sessionsBefore, await s.Db(db => db.StaffSessions.CountAsync(x => x.StaffId == id)));
        Assert.Equal(signedInAt, await s.Db(db => db.StaffAccounts.Where(a => a.Id == id).Select(a => a.LastSignInAt).SingleAsync()));

        // step up: the "recent" time does not move
        s.F.Clock.Advance(TimeSpan.FromMinutes(6));
        var authBefore = await s.Db(db => db.StaffSessions.Where(x => x.StaffId == id).Select(x => x.AuthenticatedAt).SingleAsync());
        var o = await StaffKit.Json(await s.Post("/api/v1/staff/auth/reauthenticate/options", null, token));
        fault.On = true;
        var r = await s.Post("/api/v1/staff/auth/reauthenticate", new { challengeId = o.GetProperty("challengeId").GetGuid(), credential = authn.Assert(o.GetProperty("options")) }, token);
        fault.On = false;
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal(authBefore, await s.Db(db => db.StaffSessions.Where(x => x.StaffId == id).Select(x => x.AuthenticatedAt).SingleAsync()));

        // sign out: the session lives on
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post("/api/v1/staff/auth/sign-out", null, token)).StatusCode);
        fault.On = false;
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/me", token)).StatusCode);
    }

    [Fact]
    public async Task A_failing_audit_write_rolls_back_creating_a_member_a_role_change_and_a_disable()
    {
        var fault = new AuditFault();
        using var s = WithFault(fault);
        var (admin, _, _) = await s.Admin();
        var (_, _, memberId) = await s.Member(admin, "m@example.com", "support");
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post(Accounts, new { email = "ghost@example.com", displayName = "Ghost", role = "support", reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{Accounts}/{memberId}/role", new { role = "finance", reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{Accounts}/{memberId}/disable", new { reason = R }, admin)).StatusCode);
        fault.On = false;
        Assert.Equal(0, await s.Db(db => db.StaffAccounts.CountAsync(a => a.Email == "ghost@example.com")));
        var m = await s.Db(db => db.StaffAccounts.AsNoTracking().SingleAsync(a => a.Id == memberId));
        Assert.Equal(("support", (DateTimeOffset?)null), (m.Role, m.DisabledAt));
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/me", admin)).StatusCode);   // the admin's own epoch and session were not touched either
    }

    [Fact]
    public async Task A_failing_audit_write_rolls_back_a_buyer_lock_and_a_reveal_returns_nothing()
    {
        var fault = new AuditFault();
        using var s = WithFault(fault);
        var (admin, _, _) = await s.Admin();
        var buyerToken = await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"/api/v1/staff/buyers/{buyer.Id}/lock", new { reason = R }, admin)).StatusCode);
        var reveal = await s.Post($"/api/v1/staff/buyers/{buyer.Id}/reveal", new { fields = new[] { "email" }, reason = R }, admin);
        Assert.Equal(HttpStatusCode.InternalServerError, reveal.StatusCode);
        Assert.DoesNotContain("jane@example.com", await reveal.Content.ReadAsStringAsync());
        fault.On = false;
        Assert.Null((await s.K.Account2(buyer.Id)).StaffLockedAt);
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/account/me", buyerToken)).StatusCode);   // the buyer's sessions survived
    }

    [Fact]
    public async Task A_member_signing_out_is_audited()
    {
        using var s = new StaffKit();
        var (token, _, id) = await s.Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post("/api/v1/staff/auth/sign-out", null, token)).StatusCode);
        Assert.Contains(await s.Audit(), a => a.Action == "staff.sign_out" && a.ActorId == id);
    }

    // ---------- MED-3: deny by default at the route level ----------

    [Fact]
    public void Every_staff_route_declares_how_it_is_protected_and_names_a_real_permission()
    {
        using var s = new StaffKit();
        var routes = s.F.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/staff") == true).ToList();
        Assert.True(routes.Count >= 25, $"found {routes.Count} staff routes");
        foreach (var e in routes)
        {
            Assert.True(StaffRouteExtensions.IsDeclared(e.Metadata), $"{e.RoutePattern.RawText} declares no access rule");
            if (e.Metadata.GetMetadata<StaffPermissionMetadata>() is { } p) Assert.True(StaffPermissions.Matrix.ContainsKey(p.Permission), $"{e.RoutePattern.RawText}: unknown permission {p.Permission}");
        }
        // the routes that show or change buyer data and money all name a permission (not merely "signed in")
        var missing = new[] { "/api/v1/staff/buyers/{id:guid}/reveal", "/api/v1/staff/buyers/{id:guid}/lock", "/api/v1/staff/accounts", "/api/v1/staff/audit" }
            .Where(path => !routes.Any(e => e.RoutePattern.RawText!.TrimEnd('/') == path && e.Metadata.GetMetadata<StaffPermissionMetadata>() is not null)).ToList();
        Assert.True(missing.Count == 0, "no permission on: " + string.Join(", ", missing) + " | routes: " + string.Join(", ", routes.Select(r => r.RoutePattern.RawText)));
    }

    [Fact]
    public async Task A_route_without_an_access_rule_answers_403_and_a_malformed_rule_does_too()
    {
        static async Task<object?> Run(EndpointMetadataCollection md)
        {
            var ctx = new DefaultHttpContext();
            ctx.SetEndpoint(new Endpoint(null, md, "test"));
            return await new StaffDenyByDefaultFilter().InvokeAsync(EndpointFilterInvocationContext.Create(ctx), _ => ValueTask.FromResult<object?>("reached"));
        }
        Assert.NotEqual("reached", await Run(new EndpointMetadataCollection()));                                                       // nothing declared
        Assert.NotEqual("reached", await Run(new EndpointMetadataCollection(StaffAuthMetadata.Instance)));                           // signed in, but neither self nor a permission
        Assert.NotEqual("reached", await Run(new EndpointMetadataCollection(StaffSelfMetadata.Instance)));                           // no staff session required
        Assert.NotEqual("reached", await Run(new EndpointMetadataCollection(StaffAuthMetadata.Instance, StaffSelfMetadata.Instance, new StaffPermissionMetadata("audit.view"))));   // ambiguous
        Assert.Equal("reached", await Run(new EndpointMetadataCollection(StaffPublicMetadata.Instance)));
        Assert.Equal("reached", await Run(new EndpointMetadataCollection(StaffAuthMetadata.Instance, StaffSelfMetadata.Instance)));
        Assert.Equal("reached", await Run(new EndpointMetadataCollection(StaffAuthMetadata.Instance, new StaffPermissionMetadata("audit.view"))));
    }

    // ---------- LOW 4: the allow-list runs before routing and body limits ----------

    [Fact]
    public async Task An_outside_address_gets_404_for_a_wrong_method_or_an_oversized_body_too()
    {
        using var s = new StaffKit(new() { ["Staff:AllowedIps:0"] = "10.1.2.0/24" });
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(Delete, "/api/v1/staff/me")).StatusCode);              // 405 without the middleware
        var big = new HttpRequestMessage(Post, "/api/v1/staff/auth/passkey/options") { Content = new StringContent(new string('x', 100_000)) };
        Assert.Equal(HttpStatusCode.NotFound, (await s.K.Http.SendAsync(big)).StatusCode);                          // 413 without it
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get("/api/v1/staff/nothing-here", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.K.PostFrom("10.1.2.5", "/api/v1/staff/auth/passkey/options", null)).StatusCode);
    }

    // ---------- LOW 5 and 6: the bootstrap secret works once ever, and makes one admin ----------

    [Fact]
    public async Task A_used_bootstrap_secret_is_never_reopened_even_after_the_first_admins_keys_were_revoked()
    {
        using var s = new StaffKit();
        var (a1, _, id1) = await s.Admin();
        Assert.Equal(StaffService.BootstrapAdminId, id1);
        var (a2, _, _) = await s.Member(a1, "second@example.com", "admin");
        // the second admin revokes the first one's keys: that account is "not enrolled" again
        Assert.Equal(HttpStatusCode.OK, (await s.Post($"{Accounts}/{id1}/enrolment", new { reason = R, revokePasskeys = true }, a2)).StatusCode);
        Assert.False((await s.Db(db => db.StaffAccounts.AsNoTracking().SingleAsync(a => a.Id == id1))).EnrolledAt.HasValue);
        // a restart with the token still configured
        using (var scope = s.F.Services.CreateScope()) Assert.Equal("used", await scope.ServiceProvider.GetRequiredService<StaffService>().BootstrapAsync(default));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
    }

    [Fact]
    public void A_repetitive_bootstrap_token_is_refused()
    {
        static List<string> P(string token) => StaffConfig.Problems(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Staff:BootstrapToken"] = token, ["Staff:BootstrapEmail"] = "a@example.com" }).Build()).ToList();
        Assert.Contains(P(new string('a', 40)), p => p.Contains("repetitive"));
        Assert.Contains(P("abababababababababababababababababababab"), p => p.Contains("repetitive"));
        Assert.Empty(P("Zk3pQ9vLx7Rt2WmN8bYc4HdF6JgS1aEu0oXi5"));
    }

    // ---------- LOW 7: a new link for a working account removes the old keys ----------

    [Fact]
    public async Task A_new_enrolment_link_for_an_account_that_works_must_also_remove_its_passkeys()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (member, key, id) = await s.Member(admin, "victim@example.com", "admin");
        var r = await s.Post($"{Accounts}/{id}/enrolment", new { reason = R, revokePasskeys = false }, admin);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("revoke_required", (await StaffKit.Json(r)).GetProperty("code").GetString());
        // nothing changed: the member's key and session still work
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/me", member)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.SignInResponse(key)).StatusCode);
        Assert.Equal(0, await s.Db(db => db.StaffEnrolments.CountAsync(e => e.StaffId == id && e.UsedAt == null)));
        // with the revoke it is the visible "lost device" case
        Assert.Equal(HttpStatusCode.OK, (await s.Post($"{Accounts}/{id}/enrolment", new { reason = R, revokePasskeys = true }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/staff/me", member)).StatusCode);
    }

    // ---------- LOW 8: the reason filter ----------

    [Theory]
    [InlineData("caller (415) 555-0132 asked")]
    [InlineData("caller 415.555.0132 asked")]
    [InlineData("mail jane(at)example.com please")]
    [InlineData("mail jane [at] example [dot] com please")]
    [InlineData("card 4242-4242-4242-4242")]
    public void A_reason_that_spells_a_phone_number_or_an_email_another_way_is_refused(string reason) => Assert.NotNull(StaffReason.Problem(reason));

    [Theory]
    [InlineData("ticket 4412: caller asked about order MP-1001")]
    [InlineData("chargeback case 88 (urgent)")]
    public void A_reason_with_a_ticket_and_an_order_number_is_fine(string reason) => Assert.Null(StaffReason.Problem(reason));

    // ---------- LOW 10: enrolment attempts that fail are counted ----------

    [Fact]
    public async Task Refused_enrolment_attempts_are_counted_on_health()
    {
        using var s = new StaffKit();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = "ste_not-a-real-link-" + i })).StatusCode);
        var health = await StaffKit.Json(await s.Get("/health", null));
        Assert.Equal(3, health.GetProperty("staff").GetProperty("enrolRefused").GetInt32());
    }

    // ---------- the surviving mutants: the epoch alone and the revocation alone each end a session ----------

    [Fact]
    public async Task A_staff_session_dies_from_the_epoch_alone_and_from_the_revocation_alone()
    {
        using var s = new StaffKit();
        var (_, authn, id) = await s.Admin();
        var t1 = await s.SignIn(authn);
        // a session made at the same moment as a bump (an older epoch, never revoked) does not authenticate
        await s.Db(async db => { await db.StaffAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.SessionEpoch, a => a.SessionEpoch + 1)); return 0; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/staff/me", t1)).StatusCode);
        var t2 = await s.SignIn(authn);
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/me", t2)).StatusCode);
        // revoked, same epoch
        await s.Db(async db => { await db.StaffSessions.Where(x => x.StaffId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAt, s.F.Clock.GetUtcNow())); return 0; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/staff/me", t2)).StatusCode);
    }
}
