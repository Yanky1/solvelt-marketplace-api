using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Buyer lookup (masked), break-glass reveal, lock/unlock and sign-out everywhere (decisions M7-07..M7-09).</summary>
public class StaffBuyerTests
{
    private const string Email = "jane.doe@example.com", Phone = "+1 415 555 0132", Street = "742 Evergreen Terrace";
    private static string R => StaffKit.Reason;
    private static readonly HttpMethod Post = HttpMethod.Post;

    private sealed record World(StaffKit S, string Admin, VirtualAuthenticator AdminKey, string Support, string Finance, string BuyerToken, Guid BuyerId) : IAsyncDisposable { public ValueTask DisposeAsync() { S.Dispose(); return ValueTask.CompletedTask; } }

    private static async Task<World> Setup(Dictionary<string, string?>? config = null)
    {
        var s = new StaffKit(config);
        var (admin, adminKey, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        var buyerToken = await s.K.VerifiedSession(Email, name: "Jane Brown");
        var buyer = await s.K.Account(Email);
        await s.Db(async db =>
        {
            await db.BuyerAccounts.Where(a => a.Id == buyer.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.Phone, Phone));
            db.BuyerAddresses.Add(new BuyerAddress { Id = Guid.NewGuid(), AccountId = buyer.Id, FullName = "Jane Brown", Line1 = Street, City = "Springfield", PostalCode = "62704", Country = "US", IsDefault = true, CreatedAt = s.F.Clock.GetUtcNow(), UpdatedAt = s.F.Clock.GetUtcNow() });
            db.Orders.Add(new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-1001", AccountId = buyer.Id, CreatedAt = s.F.Clock.GetUtcNow(), UpdatedAt = s.F.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
            return 0;
        });
        return new World(s, admin, adminKey, support, finance, buyerToken, buyer.Id);
    }

    private static string Q(string key, string value, string reason = "ticket 9: caller asked") => $"/api/v1/staff/buyers?{key}={Uri.EscapeDataString(value)}&reason={Uri.EscapeDataString(reason)}";

    // ---------- lookup ----------

    [Fact]
    public async Task Lookup_finds_one_buyer_by_email_order_or_id_and_shows_nothing_unmasked()
    {
        await using var w = await Setup();
        foreach (var url in new[] { Q("email", "  Jane.Doe@Example.com "), Q("order", "MP-1001"), Q("id", w.BuyerId.ToString()) })
        {
            var r = await w.S.Get(url, w.Support);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("jane.doe@example.com", body); Assert.DoesNotContain("Brown", body); Assert.DoesNotContain(Phone, body); Assert.DoesNotContain(Street, body);
            var j = JsonDocument.Parse(body).RootElement;
            Assert.Equal((w.BuyerId, "j***@e***.com", "Jane B."), (j.GetProperty("id").GetGuid(), j.GetProperty("email").GetString(), j.GetProperty("name").GetString()));
            Assert.Equal((1, 1), (j.GetProperty("addresses").GetInt32(), j.GetProperty("orders").GetInt32()));
        }
        Assert.Equal(HttpStatusCode.OK, (await w.S.Get($"/api/v1/staff/buyers/{w.BuyerId}?reason=ticket%209%3A%20again", w.Finance)).StatusCode);   // finance may look, masked
    }

    [Fact]
    public async Task A_lookup_needs_a_reason_and_exactly_one_key_and_is_audited_by_id_not_by_what_was_typed()
    {
        await using var w = await Setup();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Get($"/api/v1/staff/buyers?email={Email}", w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Get(Q("email", Email, "jane.doe@example.com asked"), w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.S.Get($"/api/v1/staff/buyers?email={Email}&order=MP-1001&reason=ticket%209%3A%20x", w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Get(Q("email", "nobody@example.com"), w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.S.Get(Q("email", Email), w.Support)).StatusCode);
        var audit = await w.S.Audit();
        Assert.Single(audit, a => a.Action == "buyer.lookup" && a.TargetId == w.BuyerId.ToString("D") && a.Reason == "ticket 9: caller asked");
        Assert.Single(audit, a => a.Action == "buyer.lookup_miss" && a.TargetId == null);
        Assert.DoesNotContain(audit, a => (a.Reason + a.Detail + a.TargetId).Contains("nobody") || (a.Reason + a.Detail + a.TargetId).Contains("jane.doe"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get(Q("email", Email), w.BuyerToken)).StatusCode);   // a buyer is not staff
    }

    [Fact]
    public async Task Demo_and_deleted_accounts_are_labelled()
    {
        await using var w = await Setup();
        await w.S.Db(async db => { await db.BuyerAccounts.Where(a => a.Id == w.BuyerId).ExecuteUpdateAsync(x => x.SetProperty(a => a.DemoVerifiedAt, w.S.F.Clock.GetUtcNow())); return 0; });
        var j = await StaffKit.Json(await w.S.Get(Q("id", w.BuyerId.ToString()), w.Support));
        Assert.True(j.GetProperty("demo").GetBoolean());
        await w.S.Db(async db => { await db.BuyerAccounts.Where(a => a.Id == w.BuyerId).ExecuteUpdateAsync(x => x.SetProperty(a => a.DeletedAt, w.S.F.Clock.GetUtcNow())); return 0; });
        var d = await StaffKit.Json(await w.S.Get(Q("id", w.BuyerId.ToString()), w.Support));
        Assert.True(d.GetProperty("deleted").GetBoolean());
    }

    // ---------- break-glass reveal ----------

    [Fact]
    public async Task Reveal_needs_the_permission_a_recent_passkey_check_and_a_reason_and_records_fields_not_values()
    {
        await using var w = await Setup();
        var url = $"/api/v1/staff/buyers/{w.BuyerId}/reveal";
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post(url, new { fields = new[] { "email" }, reason = R }, w.Finance)).StatusCode);   // finance may not
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Post(url, new { fields = new[] { "email" }, reason = "no" }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Post(url, new { fields = new[] { "password" }, reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.S.Post(url, new { fields = Array.Empty<string>(), reason = R }, w.Support)).StatusCode);
        var ok = await StaffKit.Json(await w.S.Post(url, new { fields = new[] { "email", "phone", "addresses", "name" }, reason = R }, w.Support));
        Assert.Equal((Email, Phone, "Jane Brown"), (ok.GetProperty("email").GetString(), ok.GetProperty("phone").GetString(), ok.GetProperty("name").GetString()));
        Assert.Equal(Street, ok.GetProperty("addresses")[0].GetProperty("line1").GetString());
        // only what was asked for
        var one = await StaffKit.Json(await w.S.Post(url, new { fields = new[] { "email" }, reason = R }, w.Support));
        Assert.Equal(JsonValueKind.Null, one.GetProperty("phone").ValueKind);
        var audit = (await w.S.Audit()).Where(a => a.Action == "buyer.reveal").ToList();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => { Assert.Equal(w.BuyerId.ToString("D"), a.TargetId); Assert.Equal(R, a.Reason); Assert.DoesNotContain("jane", (a.Detail ?? "").ToLowerInvariant()); Assert.DoesNotContain(Phone, a.Detail ?? ""); Assert.DoesNotContain(Street, a.Detail ?? ""); });
        Assert.Contains("email", audit[0].Detail); Assert.Contains("addresses", audit[0].Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Post($"/api/v1/staff/buyers/{Guid.NewGuid()}/reveal", new { fields = new[] { "email" }, reason = R }, w.Admin)).StatusCode);
        // an old passkey check is refused and reveals nothing
        w.S.F.Clock.Advance(TimeSpan.FromMinutes(6));
        var late = await w.S.Post(url, new { fields = new[] { "email" }, reason = R }, w.Support);
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.DoesNotContain("example.com", await late.Content.ReadAsStringAsync());
        Assert.Equal(2, (await w.S.Audit()).Count(a => a.Action == "buyer.reveal"));
    }

    [Fact]
    public async Task Reveals_are_capped_per_staff_member_per_hour_even_in_parallel()
    {
        await using var w = await Setup(new() { ["Staff:RevealsPerHour"] = "2" });
        var url = $"/api/v1/staff/buyers/{w.BuyerId}/reveal";
        var body = new { fields = new[] { "email" }, reason = R };
        Assert.Equal(HttpStatusCode.OK, (await w.S.Post(url, body, w.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.S.Post(url, body, w.Admin)).StatusCode);
        var third = await w.S.Post(url, body, w.Admin);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("reveal_limit", (await StaffKit.Json(third)).GetProperty("code").GetString());
        w.S.F.Clock.Advance(TimeSpan.FromMinutes(61));   // the session is idle-expired by now: sign in again
        var fresh = await w.S.SignIn(w.AdminKey);
        Assert.Equal(HttpStatusCode.OK, (await w.S.Post(url, body, fresh)).StatusCode);
        // parallel: the cap holds (one allowed left)
        await using var p = await Setup(new() { ["Staff:RevealsPerHour"] = "1" });
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => p.S.Post($"/api/v1/staff/buyers/{p.BuyerId}/reveal", body, p.Admin)));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, (await p.S.Audit()).Count(a => a.Action == "buyer.reveal"));
    }

    // ---------- lock, unlock, sign-out ----------

    [Fact]
    public async Task Locking_ends_sessions_and_stops_every_sign_in_and_unlocking_does_not_revive_them()
    {
        await using var w = await Setup();
        var buyerKey = new VirtualAuthenticator();
        var o = await StaffKit.Json(await w.S.K.Send(Post, "/api/v1/account/passkeys/options", null, w.BuyerToken));
        Assert.Equal(HttpStatusCode.Created, (await w.S.K.Send(Post, "/api/v1/account/passkeys", new { challengeId = o.GetProperty("challengeId").GetGuid(), name = "Phone", credential = buyerKey.Register(o.GetProperty("options")) }, w.BuyerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.S.Get("/api/v1/account/me", w.BuyerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/lock", new { reason = R }, w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/lock", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get("/api/v1/account/me", w.BuyerToken)).StatusCode);
        var pw = await w.S.K.Post("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.Forbidden, pw.StatusCode);
        Assert.Equal("account_locked", (await StaffKit.Json(pw)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.K.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password here" })).StatusCode);   // a wrong password learns nothing
        var bo = await StaffKit.Json(await w.S.K.Post("/api/v1/auth/passkey/options", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.K.Post("/api/v1/auth/passkey/sign-in", new { challengeId = bo.GetProperty("challengeId").GetGuid(), credential = buyerKey.Assert(bo.GetProperty("options")) })).StatusCode);
        var view = await StaffKit.Json(await w.S.Get(Q("id", w.BuyerId.ToString()), w.Support));
        Assert.True(view.GetProperty("locked").GetBoolean());
        Assert.Equal(0, view.GetProperty("activeSessions").GetInt32());
        // unlock: sign-in works again, the old session does not
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/unlock", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get("/api/v1/account/me", w.BuyerToken)).StatusCode);
        var again = await w.S.K.Post("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var audit = await w.S.Audit();
        Assert.Single(audit, a => a.Action == "buyer.lock" && a.TargetId == w.BuyerId.ToString("D") && a.Reason == R);
        Assert.Single(audit, a => a.Action == "buyer.unlock");
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Post($"/api/v1/staff/buyers/{Guid.NewGuid()}/lock", new { reason = R }, w.Support)).StatusCode);
    }

    [Fact]
    public async Task Locking_twice_or_in_parallel_is_one_change_and_one_audit_row()
    {
        await using var w = await Setup();
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/lock", new { reason = R }, w.Support)));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(1, (await w.S.Audit()).Count(a => a.Action == "buyer.lock"));
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/lock", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(1, (await w.S.Audit()).Count(a => a.Action == "buyer.lock"));
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_buyer_session_and_leaves_sign_in_open()
    {
        await using var w = await Setup();
        var second = (await (await w.S.K.Post("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password })).Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        Assert.Equal(HttpStatusCode.Forbidden, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/sign-out", new { reason = R }, w.Finance)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.S.Post($"/api/v1/staff/buyers/{w.BuyerId}/sign-out", new { reason = R }, w.Support)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get("/api/v1/account/me", w.BuyerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.S.Get("/api/v1/account/me", second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.S.K.Post("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password })).StatusCode);
        var entry = (await w.S.Audit()).Single(a => a.Action == "buyer.sign_out_all");
        Assert.Contains("\"sessions\":2", entry.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await w.S.Post($"/api/v1/staff/buyers/{Guid.NewGuid()}/sign-out", new { reason = R }, w.Support)).StatusCode);
    }

    [Fact]
    public void Masking_hides_the_local_part_and_the_domain_name()
    {
        Assert.Equal("j***@e***.com", Masking.Email("jane.doe@example.com"));
        Assert.Equal("a***@b***.uk", Masking.Email("a@b.co.uk"));   // only the last label of the domain is kept
        Assert.Equal("***", Masking.Email("weird"));
        Assert.Null(Masking.Email(null));
    }
}
