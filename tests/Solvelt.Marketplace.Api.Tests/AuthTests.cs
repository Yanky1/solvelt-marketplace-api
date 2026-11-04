using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Signing_up_again_before_verifying_replaces_the_password_and_invalidates_the_old_link()
    {
        using var k = new AuthKit();
        await k.SignUp(Email, "attackers password", "Mallory");
        var first = TokenOf(await k.Mail(Email), 0);
        await k.SignUp(Email, "the owners password", "Jane Brown");
        var mails = await k.Mail(Email);
        Assert.Equal(2, mails.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = first })).StatusCode);   // older link is dead
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = TokenOf(mails, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "attackers password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "the owners password" })).StatusCode);
        Assert.Equal("Jane Brown", (await k.Account(Email)).DisplayName);
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
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email)).StatusCode);
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
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token })).StatusCode);
        Assert.NotNull((await k.Account(Email)).EmailVerifiedAt);
        var again = await k.Post("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_token", (await Json(again)).GetProperty("code").GetString());

        await k.SignUp("late@example.com");
        var late = TokenOf(await k.Mail("late@example.com"), 0);
        k.F.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = late })).StatusCode);
        Assert.Null((await k.Account("late@example.com")).EmailVerifiedAt);
    }

    [Fact]
    public async Task A_verification_link_just_inside_its_24_hours_still_works()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = TokenOf(await k.Mail(Email), 0);
        k.F.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task Unknown_verification_tokens_are_invalid(string? token)
    {
        using var k = new AuthKit();
        var r = await k.Post("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_token", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_clicks_on_one_verification_link_consume_it_once()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = TokenOf(await k.Mail(Email), 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => k.Post("/api/v1/auth/verify-email", new { token }))));
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
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = old })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = TokenOf(mails, 1) })).StatusCode);
        // verified now: nothing more is sent
        await k.Post("/api/v1/auth/resend-verification", new { email = Email });
        Assert.Equal(2, (await k.Mail(Email)).Count);
    }

    [Fact]
    public async Task Resend_verification_sends_at_most_three_an_hour()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);                                   // 1 (sign-up has its own budget)
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/resend-verification", new { email = Email })).StatusCode);
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
    public async Task Five_wrong_passwords_lock_the_account_for_15_minutes_even_for_the_right_password()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" })).StatusCode);
        var locked = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("rate_limited", (await Json(locked)).GetProperty("code").GetString());
        var retry = int.Parse(locked.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 890, 900);
        k.F.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        Assert.Equal(0, (await k.Account(Email)).FailedSignIns);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_and_a_success_resets_the_count()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        for (var i = 0; i < 4; i++) await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromMinutes(16));   // a new per-address attempt window; the failure count must not have carried over
        for (var i = 0; i < 4; i++) await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
    }

    [Fact]
    public async Task An_unknown_address_is_limited_like_a_known_one_so_a_lock_proves_nothing()
    {
        using var k = new AuthKit();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) statuses.Add((await k.Post("/api/v1/auth/sign-in", new { email = "ghost@example.com", password = "wrong password!!" })).StatusCode);
        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
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
    public async Task Forgot_password_mails_only_a_verified_account_and_always_answers_204()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        foreach (var e in new[] { Email, "unverified@example.com", "nobody@example.com", "not an email" })
            Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/forgot-password", new { email = e })).StatusCode);
        Assert.Contains((await k.Mail(Email)), m => m.Subject == "Reset your password");
        Assert.DoesNotContain((await k.Mail("unverified@example.com")), m => m.Subject == "Reset your password");
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
    public async Task A_weak_new_password_is_refused_without_burning_the_link_and_a_reset_clears_a_lockout()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        for (var i = 0; i < 5; i++) await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
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
        await k.Post("/api/v1/auth/verify-email", new { token = verify });
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var reset = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = reset })).StatusCode);
    }

    // ---------- storage and logs ----------

    [Fact]
    public async Task Emailed_tokens_are_stored_only_as_hashes()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        var row = await k.Db(db => db.EmailTokens.AsNoTracking().SingleAsync());
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(Tokens.Hash(token), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
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
}
