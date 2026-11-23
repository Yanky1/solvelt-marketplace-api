using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// W3 item 2 (decision W3-02): an account made by PHONE (no email, no password, a proven number: V-09) gets a web path. It signs in with an SMS code (the phone line's limits and caps), then adds a
/// verified email (V-13b's pending address and link) and a password or a passkey. Forgot-password stays silent and leaks nothing for such an account.
/// </summary>
public sealed class PhoneWebSignInTests : IDisposable
{
    private readonly AuthKit _k = new();
    private const string Number = "+17185550142";
    public void Dispose() => _k.Dispose();

    /// <summary>The texts sent so far, after the sends in flight have finished (they run after the answer: W3-02).</summary>
    private InMemorySmsSender Sms
    {
        get
        {
            _k.F.Services.GetRequiredService<Solvelt.Marketplace.Api.Auth.PhoneSmsDispatcher>().DrainAsync().GetAwaiter().GetResult();
            return (InMemorySmsSender)_k.F.Services.GetRequiredService<ISmsSender>();
        }
    }
    private string LastCode(string to = Number) => Regex.Match(Sms.Sent.Last(s => s.To == to).Body, @"\b(\d{6})\b").Groups[1].Value;
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("code").GetString()!;

    private async Task<Guid> PhoneAccount(string number = Number, string name = "Dana Levi")
    {
        using var scope = _k.F.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().CreateAsync(name, number, default))!.Value;
    }

    private async Task<Guid> Start(string phone = "(718) 555-0142")
    {
        var r = await _k.Post("/api/v1/auth/phone/start", new { phone });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Json(r)).GetProperty("challengeId").GetGuid();
    }

    private Task<HttpResponseMessage> Verify(Guid id, string code) => _k.Post("/api/v1/auth/phone/verify", new { challengeId = id, code });

    private async Task<string> SignedInByPhone()
    {
        var id = await Start();
        var r = await Verify(id, LastCode());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task An_account_made_by_phone_signs_in_on_the_web_with_an_sms_code_and_its_session_works()
    {
        var id = await PhoneAccount();
        var challenge = await Start("718 555 0142");
        Assert.Contains("sign in on the Solvelt website", Sms.Sent.Single().Body);
        var r = await Verify(challenge, LastCode());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.Equal((id, "Dana Levi"), (body.GetProperty("user").GetProperty("id").GetGuid(), body.GetProperty("user").GetProperty("displayName").GetString()));
        var token = body.GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await _k.Get("/api/v1/account/me", token)).StatusCode);       // the session works although the account has no email (W3-02)
        var methods = await Json(await _k.Get("/api/v1/account/sign-in-methods", token));
        Assert.Equal(JsonValueKind.Null, methods.GetProperty("email").ValueKind);
        Assert.False(methods.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("0142", methods.GetProperty("phoneLast4").GetString());                          // never the whole number
        Assert.Equal(0, methods.GetProperty("passkeys").GetInt32());
        Assert.DoesNotContain(await _k.Db(db => db.PhoneSignInChallenges.AsNoTracking().ToListAsync()), c => c.UsedAt == null);   // consumed
    }

    [Fact]
    public async Task Only_a_number_an_eligible_account_holds_is_texted_and_the_answer_is_identical_either_way()
    {
        await PhoneAccount();
        var known = await _k.Post("/api/v1/auth/phone/start", new { phone = Number });
        var unknown = await _k.Post("/api/v1/auth/phone/start", new { phone = "+17185550999" });
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (known.StatusCode, unknown.StatusCode));
        var (k, u) = (await Json(known), await Json(unknown));
        Assert.Equal(k.EnumerateObject().Select(p => p.Name), u.EnumerateObject().Select(p => p.Name));              // the same shape ...
        Assert.Equal(k.GetProperty("expiresInSeconds").GetInt32(), u.GetProperty("expiresInSeconds").GetInt32());   // ... and the same values but the random id
        Assert.Equal(known.Headers.CacheControl?.ToString(), unknown.Headers.CacheControl?.ToString());
        Assert.Equal(new[] { Number }, Sms.Sent.Select(m => m.To));                                                // only the account's number was texted (no SMS pumping)
        // both wrote the same kind of row: an unknown number's has no account and no number, and no code that anyone holds
        var rows = await _k.Db(db => db.PhoneSignInChallenges.AsNoTracking().ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((32, 0, (DateTimeOffset?)null), (r.CodeHash.Length, r.Attempts, r.UsedAt)));
        Assert.Single(rows, r => r.AccountId == null);
        // guessing on the unknown number's challenge gets the same "wrong code" as on a real one
        Assert.Equal("invalid_code", await Code(await Verify(u.GetProperty("challengeId").GetGuid(), "123456")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Post("/api/v1/auth/phone/start", new { phone = "12" })).StatusCode);
    }

    [Fact]
    public async Task An_ineligible_account_is_not_texted_either_and_the_answer_does_not_change()
    {
        var id = await PhoneAccount();
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, "x")));   // signs in with its password
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/auth/phone/start", new { phone = Number })).StatusCode);
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, (string?)null).SetProperty(a => a.StaffLockedAt, _k.F.Clock.GetUtcNow())));
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/auth/phone/start", new { phone = Number })).StatusCode);
        Assert.Empty(Sms.Sent);
    }

    [Fact]
    public async Task Five_tries_lock_a_code_even_for_the_right_digits_and_a_code_ends_after_ten_minutes()
    {
        await PhoneAccount();
        var id = await Start();
        var right = LastCode();
        var wrong = right == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) Assert.Equal("invalid_code", await Code(await Verify(id, wrong)));
        Assert.Equal("code_expired", await Code(await Verify(id, wrong)));                            // the fifth wrong one locks it
        Assert.Equal("code_expired", await Code(await Verify(id, right)));                            // even for the right code afterwards

        var later = await Start();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal("code_expired", await Code(await Verify(later, LastCode())));
    }

    [Fact]
    public async Task Parallel_right_answers_make_exactly_one_session_and_parallel_guesses_never_exceed_five_tries()
    {
        await PhoneAccount();
        var id = await Start();
        var code = LastCode();
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Verify(id, code)));
        Assert.Equal(1, all.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await _k.Db(db => db.BuyerSessions.CountAsync()));

        var other = await Start();
        var guesses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Verify(other, (100000 + i).ToString())));
        Assert.Equal(5, await _k.Db(db => db.PhoneSignInChallenges.Where(c => c.Id == other).Select(c => c.Attempts).FirstAsync()));
        Assert.True(guesses.Count(g => g.StatusCode == HttpStatusCode.Unauthorized) <= 5);
    }

    [Fact]
    public async Task A_lock_a_password_or_a_deletion_after_the_code_was_sent_still_stops_the_sign_in()
    {
        var id = await PhoneAccount();
        async Task NoAccountAfter(Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<BuyerAccount>> change, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<BuyerAccount>> undo)
        {
            var challenge = await Start();
            var code = LastCode();
            await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(change));
            Assert.Equal("no_account", await Code(await Verify(challenge, code)));
            await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(undo));
        }
        await NoAccountAfter(s => s.SetProperty(a => a.PasswordHash, "x"), s => s.SetProperty(a => a.PasswordHash, (string?)null));
        await NoAccountAfter(s => s.SetProperty(a => a.StaffLockedAt, _k.F.Clock.GetUtcNow()), s => s.SetProperty(a => a.StaffLockedAt, (DateTimeOffset?)null));
        await NoAccountAfter(s => s.SetProperty(a => a.DeletedAt, _k.F.Clock.GetUtcNow()), s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)null));
        Assert.Equal(0, await _k.Db(db => db.BuyerSessions.CountAsync()));
    }

    [Fact]
    public async Task The_phone_lines_sms_caps_apply_per_number_and_are_counted_for_unknown_numbers_too()
    {
        await PhoneAccount();
        for (var i = 0; i < 3; i++) await Start();
        var fourth = await _k.Post("/api/v1/auth/phone/start", new { phone = Number });
        Assert.Equal((HttpStatusCode.TooManyRequests, "rate_limited"), (fourth.StatusCode, await Code(fourth)));
        Assert.Equal(3, Sms.Sent.Count);

        // an unknown number is never texted, but its starts count the same, so the cap answers alike for both
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/auth/phone/start", new { phone = "+17185550143" })).StatusCode);
        var unknownFourth = await _k.Post("/api/v1/auth/phone/start", new { phone = "+17185550143" });
        Assert.Equal((HttpStatusCode.TooManyRequests, "rate_limited"), (unknownFourth.StatusCode, await Code(unknownFourth)));
        Assert.Equal(fourth.Headers.RetryAfter?.ToString(), unknownFourth.Headers.RetryAfter?.ToString());
        Assert.Equal(3, Sms.Sent.Count);
    }

    [Fact]
    public async Task Ten_starts_per_client_address_per_hour_whatever_the_numbers()
    {
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Accepted, (await _k.PostFrom("203.0.113.9", "/api/v1/auth/phone/start", new { phone = $"+1718555{1000 + i}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _k.PostFrom("203.0.113.9", "/api/v1/auth/phone/start", new { phone = "+17185552000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _k.PostFrom("203.0.113.10", "/api/v1/auth/phone/start", new { phone = "+17185552000" })).StatusCode);
    }

    [Fact]
    public async Task A_failed_send_leaves_no_working_code()
    {
        await PhoneAccount();
        Sms.Fail = true;
        var r = await _k.Post("/api/v1/auth/phone/start", new { phone = Number });
        _ = Sms;   // let the send finish
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);   // the same answer as for a number without an account: a 503 only an account can produce would tell
        Assert.All(await _k.Db(db => db.PhoneSignInChallenges.AsNoTracking().ToListAsync()), c => Assert.NotNull(c.UsedAt));   // and no code works
    }

    [Fact]
    public async Task A_step_up_code_goes_to_the_accounts_own_number_and_codes_never_cross_purposes()
    {
        await PhoneAccount();
        var token = await SignedInByPhone();
        var signInCode = await Start();                                                                // a sign-in code ...
        var asStepUp = await _k.Post("/api/v1/auth/reauthenticate/phone", new { challengeId = signInCode, code = LastCode() }, token);
        Assert.Equal("code_expired", await Code(asStepUp));                                            // ... is no step-up code

        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        var start = await _k.Post("/api/v1/auth/reauthenticate/phone/start", new { phone = "+17185550999" }, token);   // a number in the body is ignored
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Equal(Number, Sms.Sent.Last().To);
        var stepUp = (await Json(start)).GetProperty("challengeId").GetGuid();
        var code = LastCode();
        Assert.Equal("code_expired", await Code(await Verify(stepUp, code)));                         // a step-up code is no sign-in code
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/auth/reauthenticate/phone", new { challengeId = stepUp, code }, token)).StatusCode);
        var session = await _k.Db(db => db.BuyerSessions.AsNoTracking().Where(s => s.RevokedAt == null).SingleAsync());
        Assert.Equal(_k.F.Clock.GetUtcNow(), session.AuthenticatedAt);
    }

    [Fact]
    public async Task A_step_up_code_works_only_for_the_session_it_was_sent_for()
    {
        await PhoneAccount();
        await PhoneAccount("+17185550143", "Other Person");
        var mine = await SignedInByPhone();
        var otherChallenge = await Start("+17185550143");
        var r = await Verify(otherChallenge, LastCode("+17185550143"));
        var theirs = (await Json(r)).GetProperty("token").GetString()!;
        var start = await _k.Post("/api/v1/auth/reauthenticate/phone/start", null, theirs);
        var id = (await Json(start)).GetProperty("challengeId").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Post("/api/v1/auth/reauthenticate/phone", new { challengeId = id, code = LastCode("+17185550143") }, mine)).StatusCode);
    }

    [Fact]
    public async Task An_account_with_a_password_steps_up_with_the_password_not_by_sms()
    {
        var token = await _k.VerifiedSession("pw@example.com");
        var r = await _k.Post("/api/v1/auth/reauthenticate/phone/start", null, token);
        Assert.Equal((HttpStatusCode.Conflict, "no_phone"), (r.StatusCode, await Code(r)));
        Assert.Empty(Sms.Sent);
    }

    [Fact]
    public async Task The_whole_web_path_add_a_verified_email_then_a_password_then_sign_in_with_them_and_no_longer_by_sms()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        // without an email there is no password to set
        var early = await _k.Post("/api/v1/account/password/set", new { newPassword = "a long new passphrase" }, token);
        Assert.Equal((HttpStatusCode.Conflict, "email_required"), (early.StatusCode, await Code(early)));

        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/account/email", new { email = "Dana@Example.com" }, token)).StatusCode);
        Assert.Equal(("dana@example.com", (string?)null), ((await _k.Account2(id)).PendingEmail, (await _k.Account2(id)).Email));   // pending, not the account's email yet
        var mail = (await _k.Mail("dana@example.com")).Single();
        Assert.Contains("on the Solvelt website", mail.TextBody);
        var link = AuthKit.TokenIn(mail, PhoneEmailEndpoints.Path);
        var confirm = await _k.Http.PostAsync(PhoneEmailEndpoints.Path, new FormUrlEncodedContent([new("token", link)]));
        Assert.Contains("is confirmed", await confirm.Content.ReadAsStringAsync());
        var verified = await _k.Account2(id);
        Assert.Equal(("dana@example.com", true), (verified.Email, verified.EmailVerifiedAt is not null));
        Assert.Equal(HttpStatusCode.OK, (await _k.Get("/api/v1/account/me", token)).StatusCode);       // the session goes on (the email is verified now)

        var set = await _k.Post("/api/v1/account/password/set", new { newPassword = "a long new passphrase" }, token);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var fresh = (await Json(set)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get("/api/v1/account/me", token)).StatusCode);   // every other session ended (M1-87)
        Assert.Equal(HttpStatusCode.OK, (await _k.Get("/api/v1/account/me", fresh)).StatusCode);
        Assert.Contains((await _k.Mail("dana@example.com")), m => m.Subject == "Your password was changed");
        Assert.Equal(HttpStatusCode.Conflict, (await _k.Post("/api/v1/account/password/set", new { newPassword = "another long passphrase" }, fresh)).StatusCode);   // once only

        Assert.Equal(HttpStatusCode.OK, (await _k.Post("/api/v1/auth/sign-in", new { email = "dana@example.com", password = "a long new passphrase" })).StatusCode);
        var before = Sms.Sent.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/auth/phone/start", new { phone = Number })).StatusCode);
        Assert.Equal(before, Sms.Sent.Count);                                                          // a text message is no longer a way in: nothing is sent
    }

    [Fact]
    public async Task Adding_an_email_or_a_password_needs_a_recent_sign_in_and_an_account_with_an_email_cannot_add_another()
    {
        await PhoneAccount();
        var token = await SignedInByPhone();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        var late = await _k.Post("/api/v1/account/email", new { email = "dana@example.com" }, token);
        Assert.Equal((HttpStatusCode.Forbidden, "reauthentication_required"), (late.StatusCode, await Code(late)));
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.Post("/api/v1/account/password/set", new { newPassword = "a long new passphrase" }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _k.Post("/api/v1/account/email", new { email = "not an email" }, token)).StatusCode);

        var withEmail = await _k.VerifiedSession("has@example.com");
        var r = await _k.Post("/api/v1/account/email", new { email = "second@example.com" }, withEmail);
        Assert.Equal((HttpStatusCode.Conflict, "email_already_set"), (r.StatusCode, await Code(r)));
    }

    [Fact]
    public async Task An_address_another_account_holds_is_never_attached_and_the_answer_is_the_same()
    {
        await _k.VerifiedSession("taken@example.com");
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/account/email", new { email = "taken@example.com" }, token)).StatusCode);
        Assert.Null((await _k.Account2(id)).PendingEmail);
        Assert.Contains(await _k.Mail("taken@example.com"), m => m.Subject == "You already have a Solvelt account");
    }

    [Fact]
    public async Task Forgot_password_is_silent_for_an_account_that_never_had_one_and_leaks_nothing()
    {
        var id = await PhoneAccount();
        // the account has no email: any address answers 204 and nothing is mailed, exactly as for an unknown address
        var unknown = await _k.Post("/api/v1/auth/forgot-password", new { email = "nobody@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        Assert.Empty(await _k.Mail("nobody@example.com"));
        Assert.Equal(0, await _k.Db(db => db.EmailTokens.CountAsync(t => t.AccountId == id)));

        // with a VERIFIED email but no password yet: the reset link arrives and sets the first password (it proves the mailbox, as for every account)
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Email, "dana@example.com").SetProperty(a => a.EmailVerifiedAt, _k.F.Clock.GetUtcNow())));
        var known = await _k.Post("/api/v1/auth/forgot-password", new { email = "dana@example.com" });
        Assert.Equal(unknown.StatusCode, known.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());
        var reset = AuthKit.TokenIn((await _k.Mail("dana@example.com")).Single(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/auth/reset-password", new { token = reset, password = "a long new passphrase" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _k.Post("/api/v1/auth/sign-in", new { email = "dana@example.com", password = "a long new passphrase" })).StatusCode);
    }

    [Fact]
    public async Task A_pending_email_does_not_open_sessions_or_resets_and_an_unverified_email_account_still_has_no_session()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        await _k.Post("/api/v1/account/email", new { email = "dana@example.com" }, token);
        await _k.Post("/api/v1/auth/forgot-password", new { email = "dana@example.com" });
        Assert.DoesNotContain(await _k.Mail("dana@example.com"), m => m.Subject == "Reset your password");   // only the confirm link: a pending address is not the account's
        // an account whose email is NOT verified (signed up, never confirmed) still gets no session, even with a proven phone
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Email, "dana@example.com").SetProperty(a => a.PendingEmail, (string?)null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task Codes_are_purged_an_hour_after_they_end_and_erased_with_the_account()
    {
        var id = await PhoneAccount();
        await Start();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(75));
        using (var scope = _k.F.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Auth.AuthMaintenance>().PurgeAsync();
        Assert.Equal(0, await _k.Db(db => db.PhoneSignInChallenges.CountAsync()));
        await Start();
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, _k.F.Clock.GetUtcNow())));   // the job erases a deleted account
        using (var scope = _k.F.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Account.AccountService>().RedactAsync(id, scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountEraser>(), default));
        Assert.Equal(0, await _k.Db(db => db.PhoneSignInChallenges.CountAsync(c => c.AccountId == id)));
    }
}

/// <summary>The SMS is sent after the answer (orchestrator review of f4363ad): a start never waits for the provider, so a real account answers as fast as an unknown number.</summary>
public sealed class PhoneSmsAfterTheAnswerTests : IDisposable
{
    private sealed class BlockingSms : ISmsSender
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<bool> SendAsync(string toE164, string body, CancellationToken ct) { Interlocked.Increment(ref Calls); await Release.Task.WaitAsync(ct); return true; }
    }
    private readonly BlockingSms _sms = new();
    private readonly AuthKit _k;
    public PhoneSmsAfterTheAnswerTests() => _k = new AuthKit(services: s => s.AddSingleton<ISmsSender>(_sms));
    public void Dispose() { _sms.Release.TrySetResult(); _k.Dispose(); }

    [Fact]
    public async Task A_start_answers_while_the_provider_is_still_sending_and_the_code_works_once_sent()
    {
        using (var scope = _k.F.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().CreateAsync("Dana Levi", "+17185550142", default);
        var start = _k.Post("/api/v1/auth/phone/start", new { phone = "+17185550142" });
        var done = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(start, done);                                       // answered although the sender is still blocked
        Assert.Equal(HttpStatusCode.Accepted, (await start).StatusCode);
        for (var i = 0; i < 50 && Volatile.Read(ref _sms.Calls) == 0; i++) await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref _sms.Calls));                 // the send did start, after the answer
        _sms.Release.SetResult();
        await _k.F.Services.GetRequiredService<Solvelt.Marketplace.Api.Auth.PhoneSmsDispatcher>().DrainAsync();
        Assert.All(await _k.Db(db => db.PhoneSignInChallenges.AsNoTracking().ToListAsync()), c => Assert.Null(c.UsedAt));   // delivered: the code is live
    }
}

public sealed class PhoneCodeKeyStartupTests
{
    private static IReadOnlyList<string> Problems(Dictionary<string, string?> extra)
    {
        var baseline = new Dictionary<string, string?>
        {
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40),
            ["Auth:EmailLinkKey"] = new string('d', 40), ["Auth:GuestLinkKey"] = new string('e', 40), ["Checkout:QuoteKey"] = new string('f', 40), ["Notifications:UnsubscribeKey"] = new string('g', 40),
        };
        foreach (var kv in extra) baseline[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(baseline).Build();
        return StartupValidator.Validate(config, new FakeEnv("Production"));
    }

    [Fact]
    public void The_web_phone_code_key_is_optional_but_when_set_it_is_long_its_own_and_never_a_dev_default()
    {
        Assert.DoesNotContain(Problems([]), p => p.Contains("Auth:PhoneCodeKey"));
        Assert.Contains(Problems(new() { ["Auth:PhoneCodeKey"] = "short" }), p => p.Contains("Auth:PhoneCodeKey must be at least 32"));
        Assert.Contains(Problems(new() { ["Auth:PhoneCodeKey"] = DevDefaults.PhoneCodeKey }), p => p.Contains("Auth:PhoneCodeKey: Development default"));
        Assert.Contains(Problems(new() { ["Auth:PhoneCodeKey"] = new string('d', 40) }), p => p.Contains("Auth:PhoneCodeKey") && p.Contains("must differ"));
        Assert.Contains(Problems(new() { ["Auth:PhoneCodeKey"] = new string('v', 40), ["Voice:PhoneCodeKey"] = new string('v', 40) }), p => p == "Auth:PhoneCodeKey and Voice:PhoneCodeKey must differ; one key per purpose.");
        Assert.Contains(Problems(new() { ["Auth:PhoneCodeKey"] = new string('s', 40), ["SignalWire:ApiToken"] = new string('s', 40) }), p => p.Contains("Auth:PhoneCodeKey and SignalWire:ApiToken must differ"));
        Assert.DoesNotContain(Problems(new() { ["Auth:PhoneCodeKey"] = new string('z', 40) }), p => p.Contains("Auth:PhoneCodeKey"));
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
