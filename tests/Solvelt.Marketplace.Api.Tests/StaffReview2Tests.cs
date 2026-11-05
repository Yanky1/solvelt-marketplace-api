using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The review of slice 2 and M7-15 (e0b4b4a..f57a6eb), decision M7-17. Each test fails without its fix.</summary>
public class StaffReview2Tests
{
    private static string R => StaffKit.Reason;
    private const string Accounts = "/api/v1/staff/accounts", BuyerPath = "/api/v1/staff/buyers";
    private static StaffKit WithFault(AuditFault fault, Dictionary<string, string?>? config = null) => new(config, services: s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(fault)));

    // ---------- MED-1 (residual): a scalar allow-list never means "everyone" ----------

    [Theory]
    [InlineData("203.0.113.0/24")]
    [InlineData("10.0.0.0/8")]
    [InlineData("")]
    public void A_single_value_allow_list_is_refused_not_read_as_no_list(string scalar)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Staff:AllowedIps"] = scalar }).Build();
        Assert.Contains(StaffConfig.Problems(cfg), p => p.Contains("must be a list") && p.Contains("Staff__AllowedIps__0"));
        Assert.Throws<InvalidOperationException>(() => IpAllowList.FromConfig(cfg));   // and the middleware itself fails closed
        Assert.Empty(StaffConfig.Problems(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Staff:AllowedIps:0"] = scalar.Length == 0 ? "10.0.0.1" : scalar }).Build()));
    }

    [Fact]
    public void A_scalar_allow_list_in_the_running_host_fails_closed()
    {
        var ex = Assert.ThrowsAny<Exception>(() => { using var s = new StaffKit(new() { ["Staff:AllowedIps"] = "203.0.113.0/24" }); });
        Assert.Contains("must be a list", ex.ToString());
    }

    // ---------- LOW 1: the deny-by-default filter is on every staff route ----------

    [Fact]
    public void Every_staff_route_carries_the_deny_by_default_filter_marker()
    {
        using var s = new StaffKit();
        var routes = s.F.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/staff") == true).ToList();
        Assert.True(routes.Count >= 25);
        Assert.All(routes, e => Assert.NotNull(e.Metadata.GetMetadata<StaffDenyByDefaultMetadata>()));
    }

    // ---------- LOW 2 and 3: the epoch bump and the lock check each end a buyer session ----------

    private static async Task<string> PlantSession(StaffKit s, Guid accountId, long epoch, string name)
    {
        var token = "planted-" + name + "-" + Guid.NewGuid().ToString("N");
        var now = s.F.Clock.GetUtcNow();
        await s.Db(async db =>
        {
            db.BuyerSessions.Add(new BuyerSession { Id = Guid.NewGuid(), AccountId = accountId, TokenHash = Tokens.Hash(token), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now, Epoch = epoch, ExpiresAt = now.AddDays(1), AbsoluteExpiresAt = now.AddDays(2) });
            await db.SaveChangesAsync();
            return 0;
        });
        return token;
    }

    [Fact]
    public async Task Sign_out_everywhere_moves_the_epoch_so_a_session_made_at_the_same_moment_is_dead()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        var buyerToken = await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{BuyerPath}/{buyer.Id}/sign-out", new { reason = R }, admin)).StatusCode);
        var after = await s.K.Account2(buyer.Id);
        Assert.Equal(buyer.SessionEpoch + 1, after.SessionEpoch);
        // a rotation that raced it committed a session with the OLD epoch: it never authenticates; one with the new epoch does
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/account/me", await PlantSession(s, buyer.Id, buyer.SessionEpoch, "old"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/account/me", await PlantSession(s, buyer.Id, after.SessionEpoch, "new"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/account/me", buyerToken)).StatusCode);
    }

    [Fact]
    public async Task A_locked_account_authenticates_no_session_not_even_one_of_the_current_epoch()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{BuyerPath}/{buyer.Id}/lock", new { reason = R }, admin)).StatusCode);
        var locked = await s.K.Account2(buyer.Id);
        Assert.Equal(buyer.SessionEpoch + 1, locked.SessionEpoch);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/account/me", await PlantSession(s, buyer.Id, buyer.SessionEpoch, "old"))).StatusCode);          // the epoch moved
        // a passkey removal that raced the lock rotated a session at the CURRENT epoch: the lock itself refuses it
        var fresh = await PlantSession(s, buyer.Id, locked.SessionEpoch, "current");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/account/me", fresh)).StatusCode);
        // and it is the lock that did it: once unlocked the very same session works
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{BuyerPath}/{buyer.Id}/unlock", new { reason = R }, admin)).StatusCode);
        var unlocked = await s.K.Account2(buyer.Id);
        var after = await PlantSession(s, buyer.Id, unlocked.SessionEpoch, "after");
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/account/me", after)).StatusCode);
    }

    // ---------- LOW 4: more audit-fault tests ----------

    [Fact]
    public async Task A_failing_audit_write_leaves_buyer_sign_out_and_unlock_undone()
    {
        var fault = new AuditFault();
        using var s = WithFault(fault);
        var (admin, _, _) = await s.Admin();
        var buyerToken = await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{BuyerPath}/{buyer.Id}/sign-out", new { reason = R }, admin)).StatusCode);
        fault.On = false;
        Assert.Equal(buyer.SessionEpoch, (await s.K.Account2(buyer.Id)).SessionEpoch);                         // the epoch did not move
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/account/me", buyerToken)).StatusCode);           // and the sessions live
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{BuyerPath}/{buyer.Id}/lock", new { reason = R }, admin)).StatusCode);
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{BuyerPath}/{buyer.Id}/unlock", new { reason = R }, admin)).StatusCode);
        fault.On = false;
        Assert.NotNull((await s.K.Account2(buyer.Id)).StaffLockedAt);                                          // still locked
    }

    [Fact]
    public async Task A_failing_audit_write_leaves_staff_sign_out_everywhere_enable_and_a_new_link_undone()
    {
        var fault = new AuditFault();
        using var s = WithFault(fault);
        var (admin, _, _) = await s.Admin();
        var (member, key, id) = await s.Member(admin, "m@example.com", "support");
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{Accounts}/{id}/sign-out", new { reason = R }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{Accounts}/{id}/enrolment", new { reason = R, revokePasskeys = true }, admin)).StatusCode);
        fault.On = false;
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/me", member)).StatusCode);                  // the member's session lives
        Assert.Equal(HttpStatusCode.OK, (await s.SignInResponse(key)).StatusCode);                              // so does their key
        Assert.Equal(0, await s.Db(db => db.StaffEnrolments.CountAsync(e => e.StaffId == id && e.UsedAt == null)));   // and no link was made
        Assert.Equal(1, await s.Db(db => db.StaffPasskeys.CountAsync(p => p.StaffId == id)));
        // enable
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{Accounts}/{id}/disable", new { reason = R }, admin)).StatusCode);
        fault.On = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Post($"{Accounts}/{id}/enable", new { reason = R }, admin)).StatusCode);
        fault.On = false;
        Assert.NotNull((await s.Db(db => db.StaffAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).DisabledAt);   // still disabled
    }

    // ---------- LOW 5: the lock is cleared with the personal data ----------

    [Fact]
    public async Task Erasing_an_account_clears_the_staff_lock()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"{BuyerPath}/{buyer.Id}/lock", new { reason = R }, admin)).StatusCode);
        await s.Db(async db => { await db.BuyerAccounts.Where(a => a.Id == buyer.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.DeletedAt, s.F.Clock.GetUtcNow())); return 0; });
        using (var scope = s.F.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AccountService>().RedactAsync(buyer.Id, scope.ServiceProvider.GetServices<IAccountEraser>(), default));
        var gone = await s.K.Account2(buyer.Id);
        Assert.Null(gone.StaffLockedAt);
        Assert.NotNull(gone.RedactedAt);
    }

    // ---------- LOW 6: a null reveal field is a 422, not a 500 ----------

    [Fact]
    public async Task A_null_in_the_reveal_field_list_is_a_validation_error()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        var r = await s.Post($"{BuyerPath}/{buyer.Id}/reveal", new { fields = new string?[] { null }, reason = R }, admin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post($"{BuyerPath}/{buyer.Id}/reveal", new { fields = new string?[] { "email", null }, reason = R }, admin)).StatusCode);
        Assert.DoesNotContain(await s.Audit(), a => a.Action == "buyer.reveal");
    }

    // ---------- LOW 7: lookups are POSTs with a body, and capped per hour ----------

    [Fact]
    public async Task The_old_query_string_lookups_are_gone_and_nothing_typed_reaches_a_url()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        foreach (var url in new[] { $"{BuyerPath}?email=jane%40example.com&reason=ticket%209", $"{BuyerPath}/{Guid.NewGuid()}?reason=ticket%209", "/api/v1/staff/orders?number=MP-1&reason=ticket%209", $"{BuyerPath}/{Guid.NewGuid()}/orders?reason=ticket%209" })
            Assert.NotEqual(HttpStatusCode.OK, (await s.Get(url, admin)).StatusCode);   // there is no such GET any more
    }

    [Fact]
    public async Task Lookups_are_capped_per_staff_member_per_hour_hits_misses_and_orders_alike_even_in_parallel()
    {
        using var s = new StaffKit(new() { ["Staff:LookupsPerHour"] = "3" });
        var (admin, adminKey, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        Task<HttpResponseMessage> Hit() => s.Post($"{BuyerPath}/lookup", new { email = "jane@example.com", reason = R }, admin);
        Assert.Equal(HttpStatusCode.OK, (await Hit()).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"{BuyerPath}/lookup", new { email = "nobody@example.com", reason = R }, admin)).StatusCode);   // a miss counts
        Assert.Equal(HttpStatusCode.OK, (await s.Post($"{BuyerPath}/{buyer.Id}/orders", new { reason = R }, admin)).StatusCode);                          // so does a buyer's order list
        var fourth = await Hit();
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal("lookup_limit", (await StaffKit.Json(fourth)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await s.Post("/api/v1/staff/orders/lookup", new { number = "MP-1", reason = R }, admin)).StatusCode);   // the order lookup shares the cap
        Assert.Equal(3, (await s.Audit()).Count(a => StaffLookupCap.Actions.Contains(a.Action)));
        // an hour later the member can look again (their session is idle-expired by then: sign in again)
        s.F.Clock.Advance(TimeSpan.FromMinutes(61));
        var later = await s.SignIn(adminKey);
        Assert.Equal(HttpStatusCode.OK, (await s.Post($"{BuyerPath}/lookup", new { email = "jane@example.com", reason = R }, later)).StatusCode);
    }

    [Fact]
    public async Task Parallel_lookups_cannot_exceed_the_cap()
    {
        using var s = new StaffKit(new() { ["Staff:LookupsPerHour"] = "2" });
        var (admin, _, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Post($"{BuyerPath}/lookup", new { email = "jane@example.com", reason = R }, admin)));
        Assert.Equal(2, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2, (await s.Audit()).Count(a => a.Action == "buyer.lookup"));
    }

    // ---------- LOW 8: the relay address is masked ----------

    [Fact]
    public async Task The_masked_buyer_view_does_not_show_the_relay_address()
    {
        using var s = new StaffKit();
        var (admin, _, _) = await s.Admin();
        await s.K.VerifiedSession("jane@example.com");
        var buyer = await s.K.Account("jane@example.com");
        var r = await s.Post($"{BuyerPath}/lookup", new { email = "jane@example.com", reason = R }, admin);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(buyer.RelayEmail, body);
        Assert.DoesNotContain("relay-", body);
        var shown = JsonDocument.Parse(body).RootElement.GetProperty("relayEmail").GetString()!;
        Assert.StartsWith("r***@", shown);
    }
}
