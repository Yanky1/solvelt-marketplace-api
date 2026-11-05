using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Staff accounts, passkey-only sign-in, roles and the audit log (decisions M7-01..M7-05).</summary>
public class StaffAccountTests
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete;
    private const string Me = "/api/v1/staff/me", Accounts = "/api/v1/staff/accounts", AuditPath = "/api/v1/staff/audit";
    private static string R => StaffKit.Reason;

    // ---------- the first account ----------

    [Fact]
    public async Task Without_a_bootstrap_token_there_is_no_staff_account_and_no_link_works()
    {
        using var s = new StaffKit(bootstrap: false);
        Assert.Equal(0, await s.Db(db => db.StaffAccounts.CountAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = "" })).StatusCode);
    }

    [Fact]
    public async Task The_bootstrap_token_makes_the_first_admin_once_and_has_no_password()
    {
        using var s = new StaffKit();
        var acct = await s.Db(db => db.StaffAccounts.AsNoTracking().SingleAsync());
        Assert.Equal((StaffKit.AdminEmail, StaffRoleNames.Admin, (DateTimeOffset?)null), (acct.Email, acct.Role, acct.EnrolledAt));
        // not enrolled: nothing signs in yet, and a wrong token opens nothing
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken + "x" })).StatusCode);
        var (token, _, id) = await s.Admin();
        Assert.Equal(acct.Id, id);
        var me = await StaffKit.Json(await s.Get(Me, token));
        Assert.Equal((StaffRoleNames.Admin, StaffKit.AdminEmail), (me.GetProperty("role").GetString(), me.GetProperty("email").GetString()));
        Assert.Contains("staff.manage", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        // the secret works once
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
        Assert.Contains(await s.Audit(), a => a.Action == "staff.bootstrap" && a.ActorId == null && a.TargetId == id.ToString("D"));
        Assert.Contains(await s.Audit(), a => a.Action == "staff.enrol" && a.TargetId == id.ToString("D"));
    }

    [Fact]
    public async Task A_restart_before_enrolment_opens_a_new_window_and_after_it_the_token_does_nothing()
    {
        using var s = new StaffKit();
        s.F.Clock.Advance(TimeSpan.FromHours(25));   // the first window is over
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
        using (var scope = s.F.Services.CreateScope()) Assert.Equal("open", await scope.ServiceProvider.GetRequiredService<StaffService>().BootstrapAsync(default));
        var (_, authn, _) = await s.Admin();
        using (var scope = s.F.Services.CreateScope()) Assert.Equal("used", await scope.ServiceProvider.GetRequiredService<StaffService>().BootstrapAsync(default));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
        Assert.Equal(1, await s.Db(db => db.StaffAccounts.CountAsync()));
        Assert.NotNull(authn);
    }

    [Fact]
    public async Task An_attestation_that_does_not_verify_does_not_burn_the_link()
    {
        using var s = new StaffKit();
        var bad = new VirtualAuthenticator { RpId = "evil.example" };
        Assert.Equal(HttpStatusCode.BadRequest, (await s.TryEnrol(StaffKit.BootstrapToken, bad)).StatusCode);
        var (token, _, _) = await s.Admin();   // the same link still works
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, token)).StatusCode);
    }

    [Fact]
    public async Task Two_parallel_enrolments_with_one_link_make_one_passkey()
    {
        using var s = new StaffKit();
        var a = new VirtualAuthenticator(); var b = new VirtualAuthenticator();
        var results = await Task.WhenAll(s.TryEnrol(StaffKit.BootstrapToken, a), s.TryEnrol(StaffKit.BootstrapToken, b));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await s.Db(db => db.StaffPasskeys.CountAsync()));
    }

    // ---------- sign-in ----------

    [Fact]
    public async Task A_passkey_signs_in_and_a_stranger_does_not()
    {
        using var s = new StaffKit();
        var (_, authn, id) = await s.Admin();
        var token = await s.SignIn(authn);
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, token)).StatusCode);
        Assert.Contains(await s.Audit(), a => a.Action == "staff.sign_in" && a.TargetId == id.ToString("D"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SignInResponse(new VirtualAuthenticator())).StatusCode);
        // the right credential but a lower counter (a clone) is refused too
        authn.Counter = 0;
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SignInResponse(authn)).StatusCode);
    }

    [Fact]
    public async Task A_buyers_passkey_and_challenge_cannot_sign_in_staff_and_the_reverse()
    {
        using var s = new StaffKit();
        var (_, staffKey, _) = await s.Admin();
        // a buyer with a passkey
        var buyerToken = await s.K.VerifiedSession("jane@example.com");
        var buyerKey = new VirtualAuthenticator();
        var o = await StaffKit.Json(await s.K.Send(Post, "/api/v1/account/passkeys/options", null, buyerToken));
        var reg = await s.K.Send(Post, "/api/v1/account/passkeys", new { challengeId = o.GetProperty("challengeId").GetGuid(), name = "Phone", credential = buyerKey.Register(o.GetProperty("options")) }, buyerToken);
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        // buyer passkey against the staff sign-in
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SignInResponse(buyerKey)).StatusCode);
        // staff passkey against the buyer sign-in
        var bo = await StaffKit.Json(await s.K.Post("/api/v1/auth/passkey/options", null));
        var r = await s.K.Post("/api/v1/auth/passkey/sign-in", new { challengeId = bo.GetProperty("challengeId").GetGuid(), credential = staffKey.Assert(bo.GetProperty("options")) });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        // a buyer sign-in challenge cannot be spent on the staff endpoint
        var bo2 = await StaffKit.Json(await s.K.Post("/api/v1/auth/passkey/options", null));
        var r2 = await s.Post("/api/v1/staff/auth/passkey/sign-in", new { challengeId = bo2.GetProperty("challengeId").GetGuid(), credential = staffKey.Assert(bo2.GetProperty("options")) });
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
    }

    [Fact]
    public async Task A_staff_token_is_not_a_buyer_session_and_a_buyer_token_is_not_a_staff_session()
    {
        using var s = new StaffKit();
        var (staffToken, _, _) = await s.Admin();
        var buyerToken = await s.K.VerifiedSession("jane@example.com");
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, staffToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/account/me", staffToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, buyerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, buyerToken.Replace(buyerToken[..4], "stf_"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, null)).StatusCode);
    }

    [Fact]
    public async Task Staff_responses_are_never_cached_or_indexed()
    {
        using var s = new StaffKit();
        var (token, _, _) = await s.Admin();
        foreach (var t in new string?[] { token, null })
        {
            var r = await s.Get(Me, t);
            Assert.True(r.Headers.CacheControl is { NoStore: true, Private: true });
            Assert.Contains("noindex", r.Headers.GetValues("X-Robots-Tag").Single());
        }
    }

    [Fact]
    public async Task Sessions_end_when_idle_or_after_their_absolute_lifetime_and_on_sign_out()
    {
        using var s = new StaffKit();
        var (_, authn, _) = await s.Admin();
        var t1 = await s.SignIn(authn);
        s.F.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, t1)).StatusCode);   // idle
        var t2 = await s.SignIn(authn);
        for (var i = 0; i < 20; i++) { s.F.Clock.Advance(TimeSpan.FromMinutes(25)); if (i < 19) Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, t2)).StatusCode); }   // kept alive, but 8 hours is the end
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, t2)).StatusCode);
        var t3 = await s.SignIn(authn);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post("/api/v1/staff/auth/sign-out", null, t3)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, t3)).StatusCode);
    }

    // ---------- roles ----------

    [Fact]
    public async Task The_permission_matrix_is_enforced_and_matches_the_document()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        foreach (var token in new[] { support, finance })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Get(Accounts, token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Get(AuditPath, token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Post(Accounts, new { email = "x@example.com", displayName = "X", role = "admin", reason = R }, token)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Accounts, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get(AuditPath, admin)).StatusCode);
        // what /me says is the matrix
        var me = await StaffKit.Json(await s.Get(Me, finance));
        Assert.Equal(StaffPermissions.For("finance"), me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList());

        // the document says the same
        var doc = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "STAFF.md")).Where(l => l.StartsWith("| `")).Select(l => l.Split('|', StringSplitOptions.TrimEntries)).ToList();
        Assert.Equal(StaffPermissions.Matrix.Count, doc.Count);
        foreach (var row in doc)
        {
            var permission = row[1].Trim('`');
            var allowed = new[] { (StaffRoleNames.Support, row[2]), (StaffRoleNames.Finance, row[3]), (StaffRoleNames.Admin, row[4]) }.Where(x => x.Item2 == "yes").Select(x => x.Item1).ToList();
            Assert.True(StaffPermissions.Matrix.ContainsKey(permission), permission);
            Assert.Equal(StaffPermissions.Matrix[permission].Order(), allowed.Order());
        }
        // an unknown permission or role is never allowed
        Assert.False(StaffPermissions.Allowed("admin", "nothing.here"));
        Assert.False(StaffPermissions.Allowed("root", StaffPermissions.AuditView));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d!.FullName;
    }

    // ---------- step-up and reasons ----------

    [Fact]
    public async Task Account_management_needs_a_recent_passkey_check()
    {
        using var s = new StaffKit();
        var (token, authn, _) = await s.Admin();
        s.F.Clock.Advance(TimeSpan.FromMinutes(6));   // the session is alive (idle 30) but the check is old (5)
        var body = new { email = "late@example.com", displayName = "Late", role = "support", reason = R };
        var refused = await s.Post(Accounts, body, token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("reauthentication_required", (await StaffKit.Json(refused)).GetProperty("code").GetString());
        Assert.Equal(0, await s.Db(db => db.StaffAccounts.CountAsync(a => a.Email == "late@example.com")));
        await s.StepUp(token, authn);
        Assert.Equal(HttpStatusCode.Created, (await s.Post(Accounts, body, token)).StatusCode);
        Assert.Contains(await s.Audit(), a => a.Action == "staff.step_up");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("buyer is jane@example.com")]
    [InlineData("phone 415 555 0132 asked")]
    [InlineData("card 4242 4242 4242 4242")]
    public async Task A_reason_is_required_and_may_not_hold_an_email_or_a_long_number(string? reason)
    {
        using var s = new StaffKit();
        var (token, _, _) = await s.Admin();
        var r = await s.Post(Accounts, new { email = "new@example.com", displayName = "New", role = "support", reason }, token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, await s.Db(db => db.StaffAccounts.CountAsync(a => a.Email == "new@example.com")));
        Assert.DoesNotContain(await s.Audit(), a => a.Action == "staff.create");
    }

    [Fact]
    public async Task Every_account_action_is_audited_with_who_what_why_and_when_and_no_email()
    {
        using var s = new StaffKit();
        var (admin, _, adminId) = await s.Admin();
        var before = s.F.Clock.GetUtcNow();
        s.F.Clock.Advance(TimeSpan.FromSeconds(30));
        var create = await StaffKit.Json(await s.Post(Accounts, new { email = "audited@example.com", displayName = "Audited", role = "finance", reason = "ticket 77: new finance hire" }, admin));
        var id = create.GetProperty("account").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/role", new { role = "support", reason = "ticket 78: moved teams" }, admin)).StatusCode);
        var entries = await s.Audit();
        var created = entries.Single(a => a.Action == "staff.create");
        Assert.Equal((adminId, StaffRoleNames.Admin, "staff", id.ToString("D"), "ticket 77: new finance hire"), (created.ActorId, created.ActorRole, created.TargetType, created.TargetId, created.Reason));
        Assert.True(created.At >= before + TimeSpan.FromSeconds(30));
        var role = entries.Single(a => a.Action == "staff.role");
        Assert.Contains("finance", role.Detail); Assert.Contains("support", role.Detail);
        // no row holds an email address
        Assert.DoesNotContain(entries, a => (a.Detail + a.Reason + a.TargetId).Contains('@'));
        // the log is readable by an admin, newest first, and pages
        var page = await StaffKit.Json(await s.Get(AuditPath + "?limit=2", admin));
        Assert.Equal(2, page.GetProperty("entries").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        var second = await StaffKit.Json(await s.Get(AuditPath + "?limit=50&before=" + page.GetProperty("next").GetString(), admin));
        Assert.DoesNotContain(second.GetProperty("entries").EnumerateArray(), e => page.GetProperty("entries").EnumerateArray().Any(p => p.GetProperty("id").GetGuid() == e.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Get(AuditPath + "?before=garbage", admin)).StatusCode);
        var filtered = await StaffKit.Json(await s.Get(AuditPath + "?action=staff.role", admin));
        Assert.Single(filtered.GetProperty("entries").EnumerateArray());
    }

    // ---------- account lifecycle ----------

    [Fact]
    public async Task A_new_member_enrols_once_and_a_duplicate_address_is_a_conflict()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var r = await s.Post(Accounts, new { email = "Dup@Example.com", displayName = "Dup", role = "support", reason = R }, admin);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var link = (await StaffKit.Json(r)).GetProperty("enrolmentToken").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post(Accounts, new { email = "dup@example.com", displayName = "Dup", role = "admin", reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post(Accounts, new { email = "x@example.com", displayName = "X", role = "root", reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post(Accounts, new { email = "not-an-email", displayName = "X", role = "support", reason = R }, admin)).StatusCode);
        var (token, _, _) = await s.Enrol(link);
        Assert.Equal("support", (await StaffKit.Json(await s.Get(Me, token))).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = link })).StatusCode);   // used
        // an expired link is dead too
        var r2 = await StaffKit.Json(await s.Post(Accounts, new { email = "slow@example.com", displayName = "Slow", role = "support", reason = R }, admin));
        s.F.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = r2.GetProperty("enrolmentToken").GetString() })).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_member_ends_their_sessions_at_once_and_blocks_sign_in_until_enabled()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (member, authn, id) = await s.Member(admin, "m@example.com", "support");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/disable", new { reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, member)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SignInResponse(authn)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/enable", new { reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, member)).StatusCode);   // the old session stays dead
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, await s.SignIn(authn))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"{Accounts}/{Guid.NewGuid()}/disable", new { reason = R }, admin)).StatusCode);
    }

    [Fact]
    public async Task A_role_change_ends_the_members_sessions()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (member, _, id) = await s.Member(admin, "m@example.com", "support");
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, member)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/role", new { role = "finance", reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, member)).StatusCode);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_disabled_or_demoted_even_by_two_admins_at_once()
    {
        using var s = new StaffKit();
        var (a1, _, id1) = await s.Admin();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"{Accounts}/{id1}/disable", new { reason = R }, a1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"{Accounts}/{id1}/role", new { role = "support", reason = R }, a1)).StatusCode);
        var (a2, _, id2) = await s.Member(a1, "second@example.com", "admin");
        // each admin disables the other at the same moment: exactly one wins
        var rs = await Task.WhenAll(s.Post($"{Accounts}/{id2}/disable", new { reason = R }, a1), s.Post($"{Accounts}/{id1}/disable", new { reason = R }, a2));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, await s.Db(db => db.StaffAccounts.CountAsync(a => a.Role == "admin" && a.DisabledAt == null)));
    }

    [Fact]
    public async Task A_lost_passkey_is_replaced_through_a_new_link_and_revoking_ends_the_old_key_and_sessions()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (member, oldKey, id) = await s.Member(admin, "lost@example.com", "support");
        var r = await s.Post($"{Accounts}/{id}/enrolment", new { reason = R, revokePasskeys = true }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var link = (await StaffKit.Json(r)).GetProperty("enrolmentToken").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, member)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SignInResponse(oldKey)).StatusCode);
        var (fresh, newKey, _) = await s.Enrol(link);
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, await s.SignIn(newKey))).StatusCode);
        Assert.Equal(1, await s.Db(db => db.StaffPasskeys.CountAsync(p => p.StaffId == id)));
        // an earlier link dies when a newer one is made (for a member who never enrolled: an enrolled one must revoke, see the review tests)
        var fresh2 = await StaffKit.Json(await s.Post(Accounts, new { email = "never@example.com", displayName = "Never", role = "support", reason = R }, admin));
        var neverId = fresh2.GetProperty("account").GetProperty("id").GetGuid();
        var first = fresh2.GetProperty("enrolmentToken").GetString()!;
        await s.Post($"{Accounts}/{neverId}/enrolment", new { reason = R, revokePasskeys = false }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = first })).StatusCode);
        // the only admin cannot revoke their own keys (they would be locked out)
        var admins = await s.Db(db => db.StaffAccounts.Where(a => a.Role == "admin").Select(a => a.Id).ToListAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"{Accounts}/{admins.Single()}/enrolment", new { reason = R, revokePasskeys = true }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, admin)).StatusCode);
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_session_of_a_member()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var (member, authn, id) = await s.Member(admin, "m@example.com", "finance");
        var second = await s.SignIn(authn);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/sign-out", new { reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, member)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(Me, second)).StatusCode);
    }

    // ---------- own passkeys ----------

    [Fact]
    public async Task A_member_adds_and_removes_passkeys_with_a_recent_check_and_keeps_the_last_one()
    {
        using var s = new StaffKit();
        var (token, first, _) = await s.Admin();
        var key2 = new VirtualAuthenticator();
        var o = await StaffKit.Json(await s.Post("/api/v1/staff/me/passkeys/options", null, token));
        var add = await s.Post("/api/v1/staff/me/passkeys", new { challengeId = o.GetProperty("challengeId").GetGuid(), name = "Phone", credential = key2.Register(o.GetProperty("options")) }, token);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var id2 = (await StaffKit.Json(add)).GetProperty("id").GetGuid();
        Assert.Equal(2, (await StaffKit.Json(await s.Get("/api/v1/staff/me/passkeys", token))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, await s.SignIn(key2))).StatusCode);
        s.F.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(Delete, $"/api/v1/staff/me/passkeys/{id2}", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Post("/api/v1/staff/me/passkeys/options", null, token)).StatusCode);
        await s.StepUp(token, first);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Send(Delete, $"/api/v1/staff/me/passkeys/{id2}", null, token)).StatusCode);
        var only = (await StaffKit.Json(await s.Get("/api/v1/staff/me/passkeys", token))).EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Send(Delete, $"/api/v1/staff/me/passkeys/{only}", null, token)).StatusCode);
    }

    // ---------- the network and the settings ----------

    [Fact]
    public async Task The_ip_allow_list_hides_the_staff_area_from_everyone_else()
    {
        using var s = new StaffKit(new() { ["Staff:AllowedIps:0"] = "10.1.2.0/24", ["Staff:AllowedIps:1"] = "2001:db8::7" });
        // the test client's own address is not on the list
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post("/api/v1/staff/auth/passkey/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post("/api/v1/staff/auth/enrol/options", new { token = StaffKit.BootstrapToken })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.K.PostFrom("10.1.3.1", "/api/v1/staff/auth/passkey/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.K.PostFrom("10.1.2.200", "/api/v1/staff/auth/passkey/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.K.PostFrom("2001:db8::7", "/api/v1/staff/auth/passkey/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.K.PostFrom("2001:db8::8", "/api/v1/staff/auth/passkey/options", null)).StatusCode);
        // a forged forwarding header does not count: it is not signed with the web key
        var forged = new HttpRequestMessage(Post, "/api/v1/staff/auth/passkey/options") { Content = System.Net.Http.Json.JsonContent.Create(new { }) };
        forged.Headers.Add("X-Solvelt-Client-Ip", "10.1.2.9"); forged.Headers.Add("X-Solvelt-Client-Timestamp", s.F.Clock.GetUtcNow().ToUnixTimeSeconds().ToString()); forged.Headers.Add("X-Solvelt-Client-Signature", "v1=deadbeef");
        Assert.Equal(HttpStatusCode.NotFound, (await s.K.Http.SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task Without_an_allow_list_every_address_may_reach_the_sign_in()
    {
        using var s = new StaffKit();
        Assert.Equal(HttpStatusCode.OK, (await s.Post("/api/v1/staff/auth/passkey/options", null)).StatusCode);
    }

    [Fact]
    public void The_settings_are_checked_at_start()
    {
        static List<string> Problems(params (string, string)[] kv) => StaffConfig.Problems(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build()).ToList();
        Assert.Empty(Problems());
        Assert.Contains(Problems(("Staff:BootstrapToken", "short"), ("Staff:BootstrapEmail", "a@example.com")), p => p.Contains("32 characters"));
        Assert.Contains(Problems(("Staff:BootstrapToken", new string('t', 40))), p => p.Contains("BootstrapEmail"));
        Assert.Contains(Problems(("Staff:AllowedIps:0", "not-an-ip")), p => p.Contains("not-an-ip"));
        Assert.Empty(Problems(("Staff:AllowedIps:0", "10.0.0.0/8"), ("Staff:AllowedIps:1", "::1")));
    }

    // ---------- retention ----------

    [Fact]
    public async Task Retention_removes_old_audit_rows_ended_sessions_and_used_links_and_keeps_the_rest()
    {
        using var s = new StaffKit();
        var (token, authn, _) = await s.Admin();
        var old = s.F.Clock.GetUtcNow();
        s.F.Clock.Advance(TimeSpan.FromDays(731));
        var t2 = await s.SignIn(authn);   // a fresh audit row and session
        using (var scope = s.F.Services.CreateScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<StaffMaintenance>().PurgeAsync();
            Assert.True(r.Audit >= 2 && r.Sessions >= 1 && r.Enrolments >= 1);
        }
        var left = await s.Audit();
        Assert.DoesNotContain(left, a => a.At <= old);
        Assert.Contains(left, a => a.Action == "staff.sign_in");
        Assert.Equal(HttpStatusCode.OK, (await s.Get(Me, t2)).StatusCode);
        Assert.NotNull(token);
    }
}
