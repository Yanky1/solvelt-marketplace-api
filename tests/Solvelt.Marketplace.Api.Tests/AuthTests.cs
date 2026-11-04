using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public class AuthTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---------- sign-up ----------

    [Fact]
    public async Task Sign_up_creates_an_unverified_account_with_a_hashed_password_a_relay_address_and_one_verification_mail()
    {
        using var k = new AuthKit();
        var r = await k.SignUp("  Jane@Example.COM ", Pw, " Jane Brown ");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        var a = await k.Account(Email);
        Assert.Null(a.EmailVerifiedAt);
        Assert.Equal("Jane Brown", a.DisplayName);
        Assert.NotNull(a.PasswordHash);
        Assert.DoesNotContain(Pw, a.PasswordHash);
        Assert.Matches("^relay-[a-z2-7]{16}@relay\\.solvelt\\.test$", a.RelayEmail);
        var mail = Assert.Single(await k.Mail(Email));
        Assert.Equal("Confirm your email address", mail.Subject);
        Assert.StartsWith("http://localhost:3000/verify-email?token=", Regex.Match(mail.TextBody, "http\\S+").Value);
    }

    [Theory]
    [InlineData("", "correct horse battery", "Jane", "email")]
    [InlineData("not-an-email", "correct horse battery", "Jane", "email")]
    [InlineData("a@b", "correct horse battery", "Jane", "email")]
    [InlineData("a b@example.com", "correct horse battery", "Jane", "email")]
    [InlineData("a@b@example.com", "correct horse battery", "Jane", "email")]
    [InlineData("@example.com", "correct horse battery", "Jane", "email")]
    [InlineData("jane@.example.com", "correct horse battery", "Jane", "email")]
    [InlineData("jane@example..com", "correct horse battery", "Jane", "email")]
    [InlineData("jane@example.com", "123456789", "Jane", "password")]
    [InlineData("jane@example.com", "", "Jane", "password")]
    [InlineData("jane@example.com", "jane@example.com", "Jane", "password")]
    [InlineData("jane@example.com", "correct horse battery", "", "displayName")]
    [InlineData("jane@example.com", "correct horse battery", "   ", "displayName")]
    public async Task Sign_up_rejects_bad_input_with_field_errors(string email, string password, string name, string field)
    {
        using var k = new AuthKit();
        var r = await k.SignUp(email, password, name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("validation_failed", j.GetProperty("code").GetString());
        Assert.True(j.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(await k.Db(db => db.BuyerAccounts.ToListAsync()));
    }

    [Fact]
    public async Task Password_and_name_length_limits_are_exact()
    {
        using var k = new AuthKit();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.SignUp("a1@example.com", new string('x', 9))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp("a2@example.com", new string('x', 10))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp("a3@example.com", new string('x', 128))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.SignUp("a4@example.com", new string('x', 129))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp("a5@example.com", Pw, new string('n', 60))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.SignUp("a6@example.com", Pw, new string('n', 61))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Post("/api/v1/auth/sign-up", new { email = "a7@example.com", password = Pw })).StatusCode);   // name missing
    }

    [Fact]
    public async Task Sign_up_for_a_verified_address_answers_the_same_and_only_warns_the_owner()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        var fresh = await k.SignUp("new@example.com");
        var again = await k.SignUp(Email, "another password!", "Mallory");
        Assert.Equal((fresh.StatusCode, await fresh.Content.ReadAsStringAsync()), (again.StatusCode, await again.Content.ReadAsStringAsync()));
        Assert.Equal(1, await k.Db(db => db.BuyerAccounts.CountAsync(a => a.Email == Email)));
        var a = await k.Account(Email);
        Assert.Equal("Jane Brown", a.DisplayName);                          // untouched
        var subjects = (await k.Mail(Email)).Select(m => m.Subject).ToList();
        Assert.Contains("You already have a Solvelt account", subjects);
        Assert.Single(subjects, s => s.StartsWith("Confirm"));              // only the original verification mail
    }

    private static string TokenOf(List<Solvelt.Marketplace.Api.Outbox.EmailMessage> mails, int i) => AuthKit.TokenIn(mails[i], "/verify-email");

    [Fact]
    public async Task Concurrent_sign_ups_for_one_address_make_one_account_and_all_answer_204()
    {
        using var k = new AuthKit();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => k.SignUp(Email))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(1, await k.Db(db => db.BuyerAccounts.CountAsync()));
    }

    [Fact]
    public async Task Sign_up_mail_is_limited_to_three_an_hour_per_address_without_changing_the_answer()
    {
        using var k = new AuthKit();
        for (var i = 0; i < 6; i++) { Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email)).StatusCode); await k.Mail(Email); }
        Assert.Equal(3, (await k.Mail(Email)).Count);
        k.F.Clock.Advance(TimeSpan.FromMinutes(61));
        await k.SignUp(Email);
        Assert.Equal(4, (await k.Mail(Email)).Count);
    }

    // ---------- verification ----------

    [Fact]
    public async Task A_verification_link_works_once_and_expires_after_24_hours()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = TokenOf(await k.Mail(Email), 0);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);
        Assert.NotNull((await k.Account(Email)).EmailVerifiedAt);
        var again = await k.Post("/api/v1/auth/verify-email", new { token, password = Pw });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_token", (await Json(again)).GetProperty("code").GetString());

        await k.SignUp("late@example.com");
        var late = TokenOf(await k.Mail("late@example.com"), 0);
        k.F.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = late, password = Pw })).StatusCode);
        Assert.Null((await k.Account("late@example.com")).EmailVerifiedAt);
    }

    [Fact]
    public async Task A_verification_link_just_inside_its_24_hours_still_works()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = TokenOf(await k.Mail(Email), 0);
        k.F.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task Unknown_verification_tokens_are_invalid(string? token)
    {
        using var k = new AuthKit();
        var r = await k.Post("/api/v1/auth/verify-email", new { token, password = Pw });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_token", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_clicks_on_one_verification_link_consume_it_once()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = TokenOf(await k.Mail(Email), 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => k.Post("/api/v1/auth/verify-email", new { token, password = Pw }))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Resend_verification_always_answers_204_replaces_the_old_link_and_is_limited_per_address()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var old = TokenOf(await k.Mail(Email), 0);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/resend-verification", new { email = Email })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/resend-verification", new { email = "nobody@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/resend-verification", new { email = "not an email" })).StatusCode);
        Assert.Empty(await k.Mail("nobody@example.com"));
        var mails = await k.Mail(Email);
        Assert.Equal(2, mails.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = old, password = Pw })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = TokenOf(mails, 1), password = Pw })).StatusCode);
        // verified now: nothing more is sent
        await k.Post("/api/v1/auth/resend-verification", new { email = Email });
        Assert.Equal(2, (await k.Mail(Email)).Count);
    }

    [Fact]
    public async Task Resend_verification_sends_at_most_three_an_hour()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);                                   // 1 (sign-up has its own budget)
        await k.Mail(Email);                                     // sent before the first resend replaces its link
        for (var i = 0; i < 6; i++) { Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/resend-verification", new { email = Email })).StatusCode); await k.Mail(Email); }
        Assert.Equal(1 + 3, (await k.Mail(Email)).Count);
    }

    // ---------- sign-in ----------

    [Fact]
    public async Task Sign_in_before_verification_says_so_only_to_someone_who_knows_the_password()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var right = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw });
        Assert.Equal(HttpStatusCode.Forbidden, right.StatusCode);
        Assert.Equal("email_not_verified", (await Json(right)).GetProperty("code").GetString());
        var wrong = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Sign_in_returns_a_token_and_the_public_user_and_the_token_is_stored_only_as_a_hash()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var r = await k.Post("/api/v1/auth/sign-in", new { email = " JANE@example.com ", password = Pw });
        var j = await Json(r);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { "token", "user" }, j.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(new[] { "displayName", "id" }, j.GetProperty("user").EnumerateObject().Select(p => p.Name).Order().ToArray());
        var issued = j.GetProperty("token").GetString()!;
        Assert.True(issued.Length >= 43);
        Assert.NotEqual(token, issued);
        var rows = await k.Db(db => db.BuyerSessions.AsNoTracking().ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, s => s.TokenHash == issued || s.TokenHash == token);
        Assert.Contains(rows, s => s.TokenHash == Tokens.Hash(issued));
        Assert.All(rows, s => Assert.Equal(64, s.TokenHash.Length));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_address_look_identical()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        var wrong = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        var unknown = await k.Post("/api/v1/auth/sign-in", new { email = "ghost@example.com", password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal((wrong.StatusCode, await wrong.Content.ReadAsStringAsync()), (unknown.StatusCode, await unknown.Content.ReadAsStringAsync()));
        Assert.Equal("invalid_credentials", (await Json(wrong)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = new string('x', 129) })).StatusCode);
    }

    [Fact]
    public async Task A_hash_with_fewer_iterations_is_upgraded_at_the_next_sign_in()
    {
        using var k = new AuthKit(new() { ["Auth:PasswordIterations"] = "5000" });
        await k.VerifiedSession(Email);
        var weak = new PasswordService(Options.Create(new AuthOptions { PasswordIterations = 1000 })).Hash(Pw);
        await k.Db(db => db.BuyerAccounts.Where(a => a.Email == Email).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, weak)));
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        var upgraded = (await k.Account(Email)).PasswordHash;
        Assert.NotEqual(weak, upgraded);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);   // and still works
        Assert.Equal(upgraded, (await k.Account(Email)).PasswordHash);                                                          // not rehashed every time
    }

    // ---------- sessions ----------

    [Fact]
    public async Task Me_needs_a_valid_bearer_token_and_returns_the_buyer()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var r = await k.Get("/api/v1/account/me", token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal((Email, "Jane Brown"), (j.GetProperty("email").GetString(), j.GetProperty("displayName").GetString()));
        Assert.Equal((await k.Account(Email)).Id, j.GetProperty("id").GetGuid());
        foreach (var bad in new string?[] { null, "", "nonsense", token + "x", new string('a', 200) })
        {
            var denied = await k.Get("/api/v1/account/me", bad);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("unauthorized", (await Json(denied)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_session_slides_for_7_idle_days_and_ends_after_7_without_use()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        k.F.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);   // slides to day 13
        k.F.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task Activity_cannot_extend_a_session_past_30_days()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        for (var day = 6; day <= 24; day += 6)
        {
            k.F.Clock.Advance(TimeSpan.FromDays(6));
            Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
        }
        k.F.Clock.Advance(TimeSpan.FromDays(5));     // day 29
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
        var row = await k.Db(db => db.BuyerSessions.AsNoTracking().FirstAsync());
        Assert.Equal(row.AbsoluteExpiresAt, row.ExpiresAt);                    // the idle window is cut at the absolute limit
        k.F.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));   // day 30
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task Touching_a_session_is_throttled_to_once_every_five_minutes()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var created = (await k.Db(db => db.BuyerSessions.AsNoTracking().FirstAsync())).LastSeenAt;
        k.F.Clock.Advance(TimeSpan.FromMinutes(4));
        await k.Get("/api/v1/account/me", token);
        Assert.Equal(created, (await k.Db(db => db.BuyerSessions.AsNoTracking().FirstAsync())).LastSeenAt);
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        await k.Get("/api/v1/account/me", token);
        Assert.Equal(k.F.Clock.GetUtcNow(), (await k.Db(db => db.BuyerSessions.AsNoTracking().FirstAsync())).LastSeenAt);
    }

    [Fact]
    public async Task Sign_out_ends_only_this_session_and_sign_out_all_ends_every_session_of_this_buyer_only()
    {
        using var k = new AuthKit();
        var a1 = await k.VerifiedSession(Email);
        var a2 = (await (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        var other = await k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/sign-out", null, a1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", a1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", a2)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-out", null, a1)).StatusCode);   // already gone
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/sign-out-all", null, a2)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", a2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-out", null, null)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_account_has_no_valid_session()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var now = k.F.Clock.GetUtcNow();
        await k.Db(db => db.BuyerAccounts.Where(a => a.Email == Email).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)now)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
    }

    // ---------- password reset ----------

    [Fact]
    public async Task Forgot_password_mails_an_existing_account_verified_or_not_and_always_answers_204()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        foreach (var e in new[] { Email, "unverified@example.com", "nobody@example.com", "not an email" })
            Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/forgot-password", new { email = e })).StatusCode);
        Assert.Contains((await k.Mail(Email)), m => m.Subject == "Reset your password");
        Assert.Contains((await k.Mail("unverified@example.com")), m => m.Subject == "Reset your password");     // decision O-16 c: the way in for the owner of an address someone else registered
        Assert.Empty(await k.Mail("nobody@example.com"));
    }

    [Fact]
    public async Task A_reset_changes_the_password_signs_everyone_out_and_works_once()
    {
        using var k = new AuthKit();
        var session = await k.VerifiedSession(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(m => m.Subject == "Reset your password"), "/reset-password");
        var r = await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "a brand new password" })).StatusCode);
        var again = await k.Post("/api/v1/auth/reset-password", new { token, password = "yet another password" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_token", (await Json(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_reset_link_expires_after_an_hour_and_a_new_request_replaces_the_old_link()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var first = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var second = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/reset-password", new { token = first, password = "a brand new password" })).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/reset-password", new { token = second, password = "a brand new password" })).StatusCode);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_without_burning_the_link_and_a_reset_clears_the_sign_in_delay()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        for (var i = 0; i < 7; i++) await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);   // delayed, even for the right password
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        var weak = await k.Post("/api/v1/auth/reset-password", new { token, password = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.True((await Json(weak)).GetProperty("errors").TryGetProperty("password", out _));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "a brand new password" })).StatusCode);
    }

    [Fact]
    public async Task Reset_with_a_missing_or_unknown_token_is_invalid()
    {
        using var k = new AuthKit();
        foreach (var token in new string?[] { null, "", "garbage" })
            Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" })).StatusCode);
    }

    [Fact]
    public async Task A_verification_token_cannot_be_used_to_reset_a_password_nor_the_other_way_round()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var verify = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/reset-password", new { token = verify, password = "a brand new password" })).StatusCode);
        Assert.Null((await k.Account(Email)).EmailVerifiedAt == null ? null : "verified");
        await k.Post("/api/v1/auth/verify-email", new { token = verify, password = Pw });
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var reset = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = reset, password = Pw })).StatusCode);
    }

    // ---------- storage and logs ----------

    private static LinkTokens Links(string key = "dev-only-email-link-key-not-for-production") => new(Options.Create(new AuthOptions { EmailLinkKey = key }));

    [Fact]
    public async Task Account_mail_in_the_outbox_and_the_database_holds_no_link_secret_and_no_address()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        // queued, not yet sent
        var queued = await k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync());
        Assert.Equal("auth.mail", queued.Type);
        using var doc = JsonDocument.Parse(queued.Payload!);
        Assert.Equal(new[] { "accountId", "kind", "tokenId" }, doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("@", queued.Payload);
        var mail = Assert.Single(await k.Mail(Email));
        var token = AuthKit.TokenIn(mail, "/verify-email");
        var dump = JsonSerializer.Serialize(new
        {
            outbox = await k.Db(db => db.OutboxMessages.AsNoTracking().ToListAsync()),
            tokens = await k.Db(db => db.EmailTokens.AsNoTracking().ToListAsync()),
        });
        Assert.DoesNotContain(token, dump);
        Assert.DoesNotContain(token[33..], dump);          // not even the MAC half
        Assert.DoesNotContain(Email, dump);
        var row = await k.Db(db => db.EmailTokens.AsNoTracking().SingleAsync());
        Assert.Equal($"{row.Id:N}", token[..32]);          // the link names the row; the secret half is only computed
        Assert.Equal(Links().Make("verify", row.Id), token);
    }

    [Fact]
    public async Task Forged_or_foreign_links_are_refused()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var good = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        var id = Guid.Parse(good[..32]);
        var forgeries = new[]
        {
            $"{id:N}.{new string('A', 43)}",                         // right id, wrong MAC
            Links("another-key-another-key-another-key-0000").Make("verify", id),   // MAC from another key
            Links().Make("reset", id),                               // MAC for the other purpose
            good + "x", good[..^1], good[33..], good[..32], good.Replace('.', '-'),
            $"{Guid.NewGuid():N}.{good[33..]}",                      // another id, copied MAC
        };
        foreach (var f in forgeries)
            Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = f, password = Pw })).StatusCode);
        Assert.Null((await k.Account(Email)).EmailVerifiedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = good, password = Pw })).StatusCode);
    }

    [Fact]
    public async Task A_link_that_died_while_its_mail_waited_is_not_sent()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        await k.SignUp(Email, "the owners password");            // replaces the first link before anything was sent
        var mails = await k.Mail(Email);
        Assert.Single(mails);                                     // only the live link is mailed
        Assert.Equal(0, await k.Db(db => db.OutboxMessages.CountAsync(m => m.Status != "delivered")));

        await k.SignUp("late@example.com");
        k.F.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Empty(await k.Mail("late@example.com"));           // expired before it was sent: no mail, and the message is not stuck
        Assert.Equal(0, await k.Db(db => db.OutboxMessages.CountAsync(m => m.Status != "delivered")));
    }

    [Fact]
    public async Task Mail_for_a_deleted_account_is_dropped()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var now = k.F.Clock.GetUtcNow();
        await k.Db(db => db.BuyerAccounts.Where(a => a.Email == Email).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)now)));
        Assert.Empty(await k.Mail(Email));
        Assert.Equal(0, await k.Db(db => db.OutboxMessages.CountAsync(m => m.Status != "delivered")));
    }

    [Fact]
    public async Task Mail_that_cannot_be_sent_waits_in_the_outbox_and_keeps_no_secret_while_it_waits()
    {
        using var broken = new TestFactory(new() { ["Auth:PasswordIterations"] = "1000", ["RateLimits:AuthPerMinute"] = "1000" },
            s => s.AddSingleton<Solvelt.Marketplace.Api.Outbox.IEmailSender, FailingEmailSender>());
        var c = broken.CreateClient();
        await c.PostAsJsonAsync("/api/v1/auth/sign-up", new { email = Email, password = Pw, displayName = "Jane Brown" });
        using (var scope = broken.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Outbox.OutboxDispatcher>().DispatchDueAsync();
        using var check = broken.Services.CreateScope();
        var m = await check.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(("pending", "InvalidOperationException"), (m.Status, m.LastError));   // the type only: the exception text names the recipient
        Assert.DoesNotContain("@", m.Payload);
        using var payload = JsonDocument.Parse(m.Payload!);
        Assert.Equal(new[] { "accountId", "kind", "tokenId" }, payload.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());   // ids only
        Assert.DoesNotContain(broken.Logs.Lines, l => l.Contains(Email));
    }

    [Fact]
    public async Task A_session_records_when_the_buyer_last_signed_in_and_activity_does_not_change_it()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var signedIn = k.F.Clock.GetUtcNow();
        var row = await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync());
        Assert.Equal((signedIn, signedIn), (row.CreatedAt, row.AuthenticatedAt));
        k.F.Clock.Advance(TimeSpan.FromMinutes(20));
        await k.Get("/api/v1/account/me", token);
        var after = await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync());
        Assert.Equal(signedIn, after.AuthenticatedAt);                         // the touch moved LastSeenAt, not the proof of identity
        Assert.NotEqual(signedIn, after.LastSeenAt);
    }

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40),
        ["Auth:EmailLinkKey"] = new string('e', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Auth:GuestLinkKey"] = new string('G', 40), ["Checkout:QuoteKey"] = new string('Q', 40),
        ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> d)
        => StartupValidator.Validate(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env("Production"));

    [Fact]
    public void The_email_link_key_is_required_long_distinct_and_never_a_dev_default_in_production()
    {
        Assert.Empty(Validate(GoodProd()));
        var missing = GoodProd(); missing.Remove("Auth:EmailLinkKey");
        Assert.Contains(Validate(missing), p => p.Contains("Auth:EmailLinkKey") && p.Contains("required"));
        var shortKey = GoodProd(); shortKey["Auth:EmailLinkKey"] = "short";
        Assert.Contains(Validate(shortKey), p => p.Contains("Auth:EmailLinkKey") && p.Contains("32"));
        var dev = GoodProd(); dev["Auth:EmailLinkKey"] = DevDefaults.EmailLinkKey;
        Assert.Contains(Validate(dev), p => p.Contains("Auth:EmailLinkKey") && p.Contains("Development default"));
        var reused = GoodProd(); reused["Auth:EmailLinkKey"] = reused["Web:ForwardingKey"];
        Assert.Contains(Validate(reused), p => p.Contains("must differ"));
    }

    [Fact]
    public void The_unsubscribe_key_is_required_long_distinct_and_never_a_dev_default_in_production()
    {
        var missing = GoodProd(); missing.Remove("Notifications:UnsubscribeKey");
        Assert.Contains(Validate(missing), p => p.Contains("Notifications:UnsubscribeKey") && p.Contains("required"));
        var shortKey = GoodProd(); shortKey["Notifications:UnsubscribeKey"] = "short";
        Assert.Contains(Validate(shortKey), p => p.Contains("Notifications:UnsubscribeKey") && p.Contains("32"));
        var dev = GoodProd(); dev["Notifications:UnsubscribeKey"] = DevDefaults.UnsubscribeKey;
        Assert.Contains(Validate(dev), p => p.Contains("Notifications:UnsubscribeKey") && p.Contains("Development default"));
        foreach (var other in new[] { "Auth:EmailLinkKey", "Web:ForwardingKey", "Portal:InboundSecret", "Portal:WebhookSecret" })
        {
            var reused = GoodProd(); reused["Notifications:UnsubscribeKey"] = reused[other];
            Assert.Contains(Validate(reused), p => p.Contains("Notifications:UnsubscribeKey") && p.Contains("must differ"));
        }
    }

    [Fact]
    public async Task No_password_token_or_email_reaches_the_logs()
    {
        using var k = new AuthKit();
        var session = await k.VerifiedSession(Email, "my very secret password");
        await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        await k.Get("/api/v1/account/me", session);
        var resetToken = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        await k.Post("/api/v1/auth/reset-password", new { token = resetToken, password = "a brand new password" });
        var text = string.Join("\n", k.F.Logs.Lines);
        foreach (var secret in new[] { "my very secret password", "wrong password!!", "a brand new password", session, resetToken, Email, "Jane Brown" })
            Assert.DoesNotContain(secret, text);
    }

    // ---------- relay identity ----------

    [Theory]
    [InlineData("Jane Brown", "Jane B.")]
    [InlineData("  jane   brown ", "jane B.")]
    [InlineData("Jane", "Jane")]
    [InlineData("Mary Ann van der Berg", "Mary B.")]
    [InlineData("", "Buyer")]
    public void The_public_name_is_first_name_and_last_initial(string name, string expected) => Assert.Equal(expected, RelayIdentity.PublicName(name));

    [Fact]
    public void Relay_addresses_are_random_and_on_the_relay_domain()
    {
        var all = Enumerable.Range(0, 200).Select(_ => RelayIdentity.NewRelayEmail("relay.example")).ToList();
        Assert.Equal(200, all.Distinct().Count());
        Assert.All(all, a => Assert.Matches("^relay-[a-z2-7]{16}@relay\\.example$", a));
    }

    [Fact]
    public async Task Every_account_gets_its_own_relay_address_and_it_never_changes()
    {
        using var k = new AuthKit();
        await k.VerifiedSession("a@example.com");
        await k.VerifiedSession("b@example.com");
        var a = await k.Account("a@example.com"); var b = await k.Account("b@example.com");
        Assert.NotEqual(a.RelayEmail, b.RelayEmail);
        await k.Post("/api/v1/auth/sign-in", new { email = "a@example.com", password = Pw });
        Assert.Equal(a.RelayEmail, (await k.Account("a@example.com")).RelayEmail);
    }

    // ---------- rate limits ----------

    [Theory]
    [InlineData("/api/v1/auth/sign-up")]
    [InlineData("/api/v1/auth/sign-in")]
    [InlineData("/api/v1/auth/verify-email")]
    [InlineData("/api/v1/auth/resend-verification")]
    [InlineData("/api/v1/auth/forgot-password")]
    [InlineData("/api/v1/auth/reset-password")]
    public async Task Every_public_auth_endpoint_is_limited_per_client_address(string path)
    {
        using var k = new AuthKit(new() { ["RateLimits:AuthPerMinute"] = "2" });
        var body = new { email = "x@example.com", password = Pw, displayName = "X Y", token = "t" };
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await k.Post(path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, statuses[1]);
    }

    // ---------- gaps found by mutation checks ----------

    [Fact]
    public async Task A_links_secret_half_works_only_with_its_own_id()
    {
        using var k = new AuthKit();
        await k.SignUp("a@example.com");
        await k.SignUp("b@example.com");
        var a = AuthKit.TokenIn((await k.Mail("a@example.com")).Single(), "/verify-email");
        var b = AuthKit.TokenIn((await k.Mail("b@example.com")).Single(), "/verify-email");
        var crossed = b[..32] + "." + a[33..];                       // B's id with A's MAC
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = crossed, password = Pw })).StatusCode);
        Assert.Null((await k.Account("b@example.com")).EmailVerifiedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = b, password = Pw })).StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"nope\",\"accountId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"kind\":\"verify\",\"accountId\":\"00000000-0000-0000-0000-000000000001\"}")]   // no token id
    [InlineData("{\"kind\":\"reset\",\"accountId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("null")]
    public async Task A_malformed_account_mail_work_item_is_dead_lettered_not_retried(string payload)
    {
        using var k = new AuthKit();
        Guid id;
        using (var scope = k.F.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var m = scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Outbox.OutboxWriter>().Enqueue(AuthMailHandler.Type, new { });
            m.Payload = payload;
            id = m.Id;
            await db.SaveChangesAsync();
        }
        // a real account must exist for the "verify/reset without a token id" cases to reach the check
        await k.Db(async db => { db.BuyerAccounts.Add(new BuyerAccount { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Email = "x@example.com", DisplayName = "X", RelayEmail = "relay-x@relay.example", CreatedAt = k.F.Clock.GetUtcNow(), UpdatedAt = k.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
        await k.Dispatch();
        var row = await k.Db(db => db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id));
        Assert.Equal(("dead", "invalid_payload"), (row.Status, row.LastError));
    }

    [Fact]
    public async Task Forgot_password_sends_at_most_three_mails_an_hour_per_address()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        var before = (await k.Mail(Email)).Count;
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/forgot-password", new { email = Email })).StatusCode);
            await k.Mail(Email);
        }
        Assert.Equal(before + 3, (await k.Mail(Email)).Count);
        k.F.Clock.Advance(TimeSpan.FromMinutes(61));
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        Assert.Equal(before + 4, (await k.Mail(Email)).Count);
    }
}
