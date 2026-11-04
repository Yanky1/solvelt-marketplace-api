using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The orchestrator's security review of the auth core (09ea829): findings F1 to F8, decisions O-16 and O-17.</summary>
public class AuthHardeningTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private const string Wrong = "wrong password!!";
    private const string SignIn = "/api/v1/auth/sign-in";

    private static async Task<(HttpStatusCode Status, string? RetryAfter)> Try(AuthKit k, string email, string password)
    {
        var r = await k.Post(SignIn, new { email, password });
        return (r.StatusCode, r.Headers.TryGetValues("Retry-After", out var v) ? v.Single() : null);
    }

    private static Dictionary<string, string?> NoIpLimit() => new() { ["Auth:SignInIpFailuresPerHour"] = "100000" };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---------- F1, F3: one gate for known and unknown addresses, progressive delay ----------

    [Fact]
    public async Task A_known_and_an_unknown_address_get_the_same_answers_and_the_same_retry_after_over_many_tries()
    {
        using var k = new AuthKit(NoIpLimit());
        await k.VerifiedSession(Email);
        var known = new List<(HttpStatusCode, string?)>();
        var unknown = new List<(HttpStatusCode, string?)>();
        for (var i = 0; i < 10; i++)
        {
            known.Add(await Try(k, Email, Wrong));
            unknown.Add(await Try(k, "ghost@example.com", Wrong));
        }
        Assert.Equal(known, unknown);                                              // nothing tells the two apart
        Assert.Equal(6, known.Count(s => s.Item1 == HttpStatusCode.Unauthorized));  // six tries without a delay
        Assert.All(known.Skip(6), s => Assert.Equal((HttpStatusCode.TooManyRequests, "1"), s));
        k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(await Try(k, Email, Wrong), await Try(k, "ghost@example.com", Wrong));   // allowed again: 401 for both
        Assert.Equal(await Try(k, Email, Wrong), await Try(k, "ghost@example.com", Wrong));   // and now 2 seconds, for both
        Assert.Equal((HttpStatusCode.TooManyRequests, "2"), await Try(k, Email, Wrong));
    }

    [Fact]
    public async Task The_delay_after_a_failure_doubles_from_one_second_and_is_capped_at_fifteen_minutes()
    {
        using var k = new AuthKit(NoIpLimit());
        var expected = new[] { 0, 0, 0, 0, 0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 900, 900, 900 };
        for (var n = 1; n <= expected.Length; n++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Try(k, "ghost@example.com", Wrong)).Status);   // failure number n is checked
            var d = expected[n - 1];
            if (d == 0) continue;
            Assert.Equal((HttpStatusCode.TooManyRequests, d.ToString()), await Try(k, "ghost@example.com", Wrong));
            if (d > 1)
            {
                k.F.Clock.Advance(TimeSpan.FromSeconds(d - 1));
                Assert.Equal((HttpStatusCode.TooManyRequests, "1"), await Try(k, "ghost@example.com", Wrong));   // one second early
            }
            k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(5, 0)] [InlineData(6, 1)] [InlineData(7, 2)] [InlineData(8, 4)] [InlineData(9, 8)] [InlineData(10, 16)]
    [InlineData(11, 32)] [InlineData(12, 64)] [InlineData(13, 128)] [InlineData(14, 256)] [InlineData(15, 512)] [InlineData(16, 900)]
    [InlineData(17, 900)] [InlineData(40, 900)] [InlineData(100000, 900)]
    public void The_delay_table(int failures, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), SignInGate.Delay(failures, free: 5, maxSeconds: 900));

    [Fact]
    public void The_delay_table_respects_its_configuration()
    {
        Assert.Equal(TimeSpan.Zero, SignInGate.Delay(2, free: 2, maxSeconds: 60));
        Assert.Equal(TimeSpan.FromSeconds(1), SignInGate.Delay(3, free: 2, maxSeconds: 60));
        Assert.Equal(TimeSpan.FromSeconds(60), SignInGate.Delay(30, free: 2, maxSeconds: 60));
        Assert.Equal(TimeSpan.FromSeconds(1), SignInGate.Delay(1, free: 0, maxSeconds: 60));
    }

    [Fact]
    public async Task A_quiet_day_wipes_an_addresss_failure_memory()
    {
        using var k = new AuthKit(NoIpLimit());
        for (var i = 0; i < 8; i++) await Try(k, "ghost@example.com", Wrong);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Try(k, "ghost@example.com", Wrong)).Status);
        k.F.Clock.Advance(TimeSpan.FromHours(25));
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Try(k, "ghost@example.com", Wrong)).Status);   // fresh again: six free tries
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Try(k, "ghost@example.com", Wrong)).Status);
    }

    [Fact]
    public async Task A_right_password_clears_the_address_and_so_does_a_password_reset()
    {
        using var k = new AuthKit(NoIpLimit());
        await k.VerifiedSession(Email);
        var key = k.F.Services.GetRequiredService<GateKeys>().Address(Email);
        for (var i = 0; i < 4; i++) await Try(k, Email, Wrong);
        Assert.Equal(4, (await k.Db(db => db.SignInAttempts.AsNoTracking().SingleAsync(a => a.Key == key))).Failures);
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, Pw)).Status);
        Assert.Equal(0, await k.Db(db => db.SignInAttempts.CountAsync(a => a.Key == key)));            // a success forgets the address
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Try(k, Email, Wrong)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Try(k, Email, Pw)).Status);                // delayed, even for the right password

        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" })).StatusCode);
        Assert.Equal(0, await k.Db(db => db.SignInAttempts.CountAsync(a => a.Key == key)));            // a reset forgets it too
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, "a brand new password")).Status);        // no waiting
    }

    [Fact]
    public async Task The_gate_tables_hold_neither_an_address_nor_an_ip()
    {
        using var k = new AuthKit();
        await k.PostFrom("203.0.113.77", SignIn, new { email = "someone.private@example.com", password = Wrong });
        var a = await k.Db(db => db.SignInAttempts.AsNoTracking().SingleAsync());
        var b = await k.Db(db => db.SignInIpBudgets.AsNoTracking().SingleAsync());
        Assert.Matches("^[0-9a-f]{64}$", a.Key);
        Assert.Matches("^[0-9a-f]{64}$", b.Key);
        Assert.NotEqual(k.F.Services.GetRequiredService<GateKeys>().Address("someone.private@example.com"), k.F.Services.GetRequiredService<GateKeys>().Ip("someone.private@example.com"));   // purpose-bound
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(k.F.Services.GetRequiredService<GateKeys>().Ip("fwd:203.0.113.77"), b.Key);
        var dump = JsonSerializer.Serialize(new { a, b });
        Assert.DoesNotContain("someone.private", dump);
        Assert.DoesNotContain("203.0.113.77", dump);
    }

    // ---------- F7: the per-IP failure budget, in the database ----------

    [Fact]
    public async Task One_client_ip_gets_a_budget_of_failures_across_all_addresses_and_it_resets_after_an_hour()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "5", ["Auth:SignInFreeFailures"] = "100" });   // the per-address delay is out of the way
        await k.VerifiedSession(Email);
        const string ipA = "203.0.113.7", ipB = "198.51.100.8";
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await k.PostFrom(ipA, SignIn, new { email = $"ghost{i}@example.com", password = Wrong })).StatusCode);
        var blocked = await k.PostFrom(ipA, SignIn, new { email = Email, password = Pw });                 // even the right password, even another address
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var wait = int.Parse(blocked.Headers.GetValues("Retry-After").Single());
        Assert.InRange(wait, 3590, 3600);
        Assert.Equal(HttpStatusCode.OK, (await k.PostFrom(ipB, SignIn, new { email = Email, password = Pw })).StatusCode);   // another client is untouched
        var key = k.F.Services.GetRequiredService<GateKeys>().Ip("fwd:" + ipA);
        Assert.Equal(5, (await k.Db(db => db.SignInIpBudgets.AsNoTracking().SingleAsync(b => b.Key == key))).Count);        // the budget is in the database: every instance sees it
        k.F.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.OK, (await k.PostFrom(ipA, SignIn, new { email = Email, password = Pw })).StatusCode);
    }

    [Fact]
    public async Task A_right_password_gives_its_attempt_back_and_a_delayed_attempt_costs_nothing()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "5", ["Auth:SignInFreeFailures"] = "100" });
        await k.VerifiedSession(Email);
        const string ip = "203.0.113.7";
        var key = k.F.Services.GetRequiredService<GateKeys>().Ip("fwd:" + ip);
        async Task<int> Count() => (await k.Db(db => db.SignInIpBudgets.AsNoTracking().FirstOrDefaultAsync(b => b.Key == key)))?.Count ?? 0;
        for (var i = 0; i < 3; i++) await k.PostFrom(ip, SignIn, new { email = $"ghost{i}@example.com", password = Wrong });
        Assert.Equal(3, await Count());
        Assert.Equal(HttpStatusCode.OK, (await k.PostFrom(ip, SignIn, new { email = Email, password = Pw })).StatusCode);
        Assert.Equal(3, await Count());                                                                      // the success was refunded
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await k.PostFrom(ip, SignIn, new { email = $"more{i}@example.com", password = Wrong })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.PostFrom(ip, SignIn, new { email = Email, password = Pw })).StatusCode);   // five failures: spent

        using var j = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "5", ["Auth:SignInFreeFailures"] = "0" });   // every failure delays its address
        var k2 = j.F.Services.GetRequiredService<GateKeys>().Ip("fwd:" + ip);
        await j.PostFrom(ip, SignIn, new { email = "same@example.com", password = Wrong });                  // claimed: a 1 s delay now
        var again = await j.PostFrom(ip, SignIn, new { email = "same@example.com", password = Wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal(1, (await j.Db(db => db.SignInIpBudgets.AsNoTracking().SingleAsync(b => b.Key == k2))).Count);   // the delayed attempt checked nothing, so it cost nothing
    }

    // ---------- F4: the attempt is claimed atomically before the password is checked ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parallel_attempts_are_claimed_atomically_so_only_six_passwords_are_checked(bool knownAddress)
    {
        CountingPasswordService? counting = null;
        using var k = new AuthKit(NoIpLimit(), s => s.AddSingleton<PasswordService>(sp => counting = new CountingPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>())));
        await k.VerifiedSession(Email);
        counting!.Reset();
        var address = knownAddress ? Email : "ghost@example.com";
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Try(k, address, Wrong))));
        var checkedCount = results.Count(r => r.Status == HttpStatusCode.Unauthorized);
        Assert.InRange(checkedCount, 1, 6);                                                  // never more than the six free tries
        Assert.Equal(20 - checkedCount, results.Count(r => r.Status == HttpStatusCode.TooManyRequests));
        Assert.Equal(checkedCount, counting.HashWork);                                       // and exactly those did password work
    }

    // ---------- the password work is the same on every path ----------

    [Fact]
    public async Task Every_sign_in_path_does_exactly_one_hash_of_work_whatever_the_address()
    {
        CountingPasswordService? counting = null;
        using var k = new AuthKit(NoIpLimit(), s => s.AddSingleton<PasswordService>(sp => counting = new CountingPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>())));
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        async Task<(int Work, int Hashes)> Work(Func<Task> action) { counting!.Reset(); await action(); return (counting.HashWork, counting.Hashes); }

        Assert.Equal((1, 0), await Work(() => Try(k, "ghost@example.com", Wrong)));          // unknown: a stand-in hash
        Assert.Equal((1, 0), await Work(() => Try(k, Email, Wrong)));                        // known, wrong
        Assert.Equal((1, 0), await Work(() => Try(k, Email, Pw)));                           // known, right (no upgrade needed)
        Assert.Equal((1, 0), await Work(() => Try(k, "unverified@example.com", Pw)));        // known, unverified, right
        Assert.Equal((1, 0), await Work(() => Try(k, "unverified@example.com", Wrong)));
        Assert.Equal(0, counting!.Hashes);
    }

    [Fact]
    public async Task A_delayed_attempt_does_no_password_work_for_a_known_or_an_unknown_address_alike()
    {
        CountingPasswordService? counting = null;
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000", ["Auth:SignInFreeFailures"] = "1" }, s => s.AddSingleton<PasswordService>(sp => counting = new CountingPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>())));
        await k.VerifiedSession(Email);
        foreach (var a in new[] { Email, "ghost@example.com" })
        {
            await Try(k, a, Wrong); await Try(k, a, Wrong);                                  // the second one starts the delay
            counting!.Reset();
            Assert.Equal(HttpStatusCode.TooManyRequests, (await Try(k, a, Wrong)).Status);
            Assert.Equal(0, counting.HashWork);
        }
    }

    [Fact]
    public async Task Sign_up_does_one_hash_of_work_for_a_new_an_unverified_and_a_verified_address()
    {
        CountingPasswordService? counting = null;
        using var k = new AuthKit(NoIpLimit(), s => s.AddSingleton<PasswordService>(sp => counting = new CountingPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>())));
        await k.VerifiedSession(Email);
        counting!.Reset(); await k.SignUp("fresh@example.com");                              Assert.Equal(1, counting.HashWork);
        counting.Reset(); await k.SignUp("fresh@example.com", "another password!!");         Assert.Equal(1, counting.HashWork);   // unverified
        counting.Reset(); await k.SignUp(Email, "another password!!");                       Assert.Equal(1, counting.HashWork);   // verified
    }

    // ---------- F2 (O-16): the pre-verification takeover ----------

    [Fact]
    public async Task Signing_up_again_never_changes_an_unverified_account_so_the_attackers_password_does_not_replace_the_victims()
    {
        using var k = new AuthKit();
        await k.SignUp(Email, "victim password 1", "Victim V");
        await k.Mail(Email);
        var before = await k.Account(Email);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email, "attacker password 2", "Mallory")).StatusCode);
        var after = await k.Account(Email);
        Assert.Equal((before.PasswordHash, before.DisplayName, before.RelayEmail, before.UpdatedAt), (after.PasswordHash, after.DisplayName, after.RelayEmail, after.UpdatedAt));
        var mails = (await k.Mail(Email)).Where(m => m.Subject.StartsWith("Confirm")).ToList();
        Assert.Equal(2, mails.Count);                                                        // it only re-sent verification
        var oldLink = AuthKit.TokenIn(mails[0], "/verify-email");
        var newLink = AuthKit.TokenIn(mails[1], "/verify-email");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token = oldLink, password = "victim password 1" })).StatusCode);   // the older link died
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = newLink, password = "victim password 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Try(k, Email, "attacker password 2")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, "victim password 1")).Status);
    }

    [Fact]
    public async Task When_the_attacker_registered_first_the_owner_gets_in_through_forgot_password_and_the_attacker_does_not()
    {
        using var k = new AuthKit();
        await k.SignUp(Email, "attacker password 2", "Mallory");
        await k.Mail(Email);
        await k.SignUp(Email, "victim password 1", "Victim V");                              // the owner signs up for their own address: nothing changes
        var link = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject.StartsWith("Confirm")), "/verify-email");
        var wrong = await k.Post("/api/v1/auth/verify-email", new { token = link, password = "victim password 1" });   // the owner's password is not on the account
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_token", (await Json(wrong)).GetProperty("code").GetString());
        Assert.Null((await k.Account(Email)).EmailVerifiedAt);
        Assert.Null((await k.Db(db => db.EmailTokens.AsNoTracking().SingleAsync(t => t.Purpose == "verify" && t.UsedAt == null))).UsedAt);   // the link was not used up

        await k.Post("/api/v1/auth/forgot-password", new { email = Email });                 // an unverified account gets a reset link
        var reset = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token = reset, password = "victim password 1" })).StatusCode);
        Assert.NotNull((await k.Account(Email)).EmailVerifiedAt);                            // the reset proved the mailbox
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, "victim password 1")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Try(k, Email, "attacker password 2")).Status);
    }

    [Fact]
    public async Task Verifying_needs_the_accounts_password_and_every_failure_looks_the_same()
    {
        using var k = new AuthKit();
        await k.SignUp(Email);
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        var wrong = await k.Post("/api/v1/auth/verify-email", new { token, password = Wrong });
        var garbage = await k.Post("/api/v1/auth/verify-email", new { token = "garbage", password = Pw });
        Assert.Equal((HttpStatusCode.BadRequest, await garbage.Content.ReadAsStringAsync()), (wrong.StatusCode, await wrong.Content.ReadAsStringAsync()));   // a wrong password is the same as a bad link
        Assert.Null((await k.Account(Email)).EmailVerifiedAt);
        foreach (var missing in new object[] { new { token }, new { token, password = "" } })
        {
            var r = await k.Post("/api/v1/auth/verify-email", missing);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("password", out _));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token, password = new string('x', 129) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);   // the link survived every failure
        Assert.NotNull((await k.Account(Email)).EmailVerifiedAt);
    }

    [Fact]
    public async Task Guessing_the_password_through_verify_email_meets_the_same_gate()
    {
        using var k = new AuthKit(NoIpLimit());
        await k.SignUp(Email);
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token, password = Wrong })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);   // delayed: even the right password is refused for now
        Assert.Null((await k.Account(Email)).EmailVerifiedAt);
        k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);
    }

    // ---------- F5: forgot and resend do the same work for every address ----------

    private static async Task<List<string>> Statements(AuthKit k, CommandLog log, string path, string email)
    {
        log.Clear(); log.Recording = true;
        var r = await k.Post(path, new { email });
        log.Recording = false;
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        return log.Seen.ToList();
    }

    [Fact]
    public async Task Forgot_password_and_resend_verification_run_the_same_statements_for_a_known_an_unverified_and_an_unknown_address()
    {
        var log = new CommandLog();
        using var k = new AuthKit(null, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(log)));
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        await k.Mail("unverified@example.com");

        var forgot = new[] { Email, "unverified@example.com", "ghost@example.com" };
        var sequences = new List<List<string>>();
        foreach (var a in forgot) sequences.Add(await Statements(k, log, "/api/v1/auth/forgot-password", a));
        Assert.NotEmpty(sequences[0]);
        Assert.Contains(sequences[0], x => x.StartsWith("INSERT") && x.Contains("email_tokens"));
        Assert.Contains(sequences[0], x => x.StartsWith("INSERT") && x.Contains("outbox_messages"));
        Assert.All(sequences, s => Assert.Equal(sequences[0], s));                           // the same statements, in the same order

        var resend = new[] { "unverified@example.com", Email, "ghost2@example.com" };       // a waiting account, a verified one (nothing to resend) and an unknown one
        var resendSequences = new List<List<string>>();
        foreach (var a in resend) resendSequences.Add(await Statements(k, log, "/api/v1/auth/resend-verification", a));
        Assert.NotEmpty(resendSequences[0]);
        Assert.All(resendSequences, s => Assert.Equal(resendSequences[0], s));
    }

    [Fact]
    public async Task An_unknown_address_leaves_only_placeholder_rows_that_are_never_sent_and_are_purged_within_the_hour()
    {
        using var k = new AuthKit();
        await k.Post("/api/v1/auth/forgot-password", new { email = "ghost@example.com" });
        await k.Post("/api/v1/auth/resend-verification", new { email = "ghost@example.com" });
        var tokens = await k.Db(db => db.EmailTokens.AsNoTracking().ToListAsync());
        Assert.All(tokens, t => Assert.Equal(("decoy", true), (t.Purpose, t.UsedAt is not null)));          // already used and expired: no link
        Assert.Equal(2, tokens.Count);
        var outbox = await k.Db(db => db.OutboxMessages.AsNoTracking().ToListAsync());
        Assert.All(outbox, m => Assert.Equal(("auth.decoy", "delivered", null), (m.Type, m.Status, m.Payload)));
        Assert.Equal(0, await k.Dispatch());                                                 // nothing to send
        Assert.Empty(k.Sink.Sent);

        async Task<AuthMaintenance.Result> Purge() { using var s = k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<AuthMaintenance>().PurgeAsync(); }
        k.F.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, (await Purge()).Decoys);
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(4, (await Purge()).Decoys);                                             // two token rows and two outbox rows
        Assert.Equal(0, await k.Db(db => db.EmailTokens.CountAsync()));
    }

    // ---------- F4/M13: a reset is one transaction ----------

    [Fact]
    public async Task A_reset_that_fails_after_the_link_was_claimed_changes_nothing_and_the_link_still_works()
    {
        var log = new CommandLog();
        using var k = new AuthKit(null, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(log)));
        var session = await k.VerifiedSession(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        log.FailNext(t => t.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && t.Contains("buyer_accounts") && t.Contains("PasswordHash"));
        var failed = await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null((await k.Db(db => db.EmailTokens.AsNoTracking().SingleAsync(t => t.Purpose == "reset" && t.UsedAt == null))).UsedAt);   // the claim was rolled back
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", session)).StatusCode);                                       // sessions untouched
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, Pw)).Status);                                                              // password untouched
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" })).StatusCode);   // and the link works
    }

    [Fact]
    public async Task Twenty_parallel_resets_with_one_link_succeed_once_and_leave_one_consistent_password()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => k.Post("/api/v1/auth/reset-password", new { token, password = $"password number {i:D2} here" }))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(19, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        var winner = Enumerable.Range(0, 20).Single(i => results[i].StatusCode == HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, $"password number {winner:D2} here")).Status);
    }

    // ---------- F8: the reset checks the password against the address; bodies are small ----------

    [Fact]
    public async Task A_reset_refuses_a_password_equal_to_the_address_without_using_the_link_up()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var token = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        foreach (var same in new[] { Email, "JANE@example.com", Email + "" })
        {
            var r = await k.Post("/api/v1/auth/reset-password", new { token, password = same });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("password", out _));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token, password = "a brand new password" })).StatusCode);
    }

    private static HttpRequestMessage Raw(string path, int bytes)
    {
        // valid JSON of exactly <bytes> bytes: {"email":"<padding>"}
        const string head = "{\"email\":\"", tail = "\"}";
        var body = head + new string('x', bytes - head.Length - tail.Length) + tail;
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body));
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Theory]
    [InlineData("/api/v1/auth/sign-up")]
    [InlineData("/api/v1/auth/sign-in")]
    [InlineData("/api/v1/auth/verify-email")]
    [InlineData("/api/v1/auth/resend-verification")]
    [InlineData("/api/v1/auth/forgot-password")]
    [InlineData("/api/v1/auth/reset-password")]
    public async Task Auth_request_bodies_are_capped_at_16_KB(string path)
    {
        using var k = new AuthKit();
        var at = await k.Http.SendAsync(Raw(path, 16384));
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, at.StatusCode);                // exactly at the cap: read (and answered on its merits)
        var over = await k.Http.SendAsync(Raw(path, 16385));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal("too_large", (await Json(over)).GetProperty("code").GetString());
        var huge = await k.Http.SendAsync(Raw(path, 20 * 1024 * 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Fact]
    public async Task The_cap_does_not_touch_other_endpoints()
    {
        using var k = new AuthKit();
        var res = await k.Http.SendAsync(Raw("/api/portal/webhooks", 20000));                // the webhook has its own, larger cap
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task A_chunked_body_over_the_cap_is_cut_off_while_it_is_read_over_a_real_socket()
    {
        using var f = new TestFactory(new() { ["Auth:PasswordIterations"] = "1000", ["RateLimits:AuthPerMinute"] = "10000" });
        f.UseKestrel();
        f.StartServer();
        var address = f.CreateClient().BaseAddress!;
        async Task<int> Post(int bytes)
        {
            var body = Encoding.UTF8.GetBytes("{\"email\":\"" + new string('x', bytes) + "\"}");
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(address.Host, address.Port);
            var stream = tcp.GetStream();
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /api/v1/auth/sign-in HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
                for (var i = 0; i < body.Length; i += 4096)
                {
                    var n = Math.Min(4096, body.Length - i);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"));
                    await stream.WriteAsync(body.AsMemory(i, n));
                    await stream.WriteAsync("\r\n"u8.ToArray());
                }
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            catch (IOException) { }
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var line = await reader.ReadLineAsync();
            Assert.NotNull(line);
            return int.Parse(line!.Split(' ')[1]);
        }
        Assert.NotEqual(413, await Post(1000));                                              // small: answered on its merits (a 401 for the made-up address)
        Assert.Equal(413, await Post(40_000));                                               // no Content-Length to see: refused while being read
    }

    // ---------- the purge job ----------

    [Fact]
    public async Task The_auth_purge_removes_old_sessions_links_and_gate_rows_on_schedule_and_nothing_younger()
    {
        using var k = new AuthKit();
        var now = k.F.Clock.GetUtcNow();
        var week = TimeSpan.FromDays(7);
        await k.Db(async db =>
        {
            var acct = Guid.NewGuid();
            BuyerSession S(string hash, DateTimeOffset abs, DateTimeOffset? revoked = null) => new() { Id = Guid.NewGuid(), AccountId = acct, TokenHash = hash, CreatedAt = now - TimeSpan.FromDays(40), LastSeenAt = now, AuthenticatedAt = now, ExpiresAt = abs, AbsoluteExpiresAt = abs, RevokedAt = revoked };
            db.BuyerSessions.AddRange(S("s-old", now - week - TimeSpan.FromSeconds(1)), S("s-young", now - week + TimeSpan.FromSeconds(1)),
                                      S("r-old", now + TimeSpan.FromDays(5), now - week - TimeSpan.FromSeconds(1)), S("r-young", now + TimeSpan.FromDays(5), now - week + TimeSpan.FromSeconds(1)),
                                      S("live", now + TimeSpan.FromDays(5)));
            EmailToken T(DateTimeOffset expires, DateTimeOffset? used = null) => new() { Id = Guid.NewGuid(), AccountId = acct, Purpose = "reset", CreatedAt = now - TimeSpan.FromDays(40), ExpiresAt = expires, UsedAt = used };
            db.EmailTokens.AddRange(T(now - week - TimeSpan.FromSeconds(1)), T(now - week + TimeSpan.FromSeconds(1)),
                                    T(now + TimeSpan.FromDays(1), now - week - TimeSpan.FromSeconds(1)), T(now + TimeSpan.FromDays(1), now - week + TimeSpan.FromSeconds(1)),
                                    T(now + TimeSpan.FromDays(1)));
            db.SignInAttempts.AddRange(new SignInAttempt { Key = "a-old", Failures = 3, NextAllowedAt = now, UpdatedAt = now.AddHours(-48).AddSeconds(-1) }, new SignInAttempt { Key = "a-young", Failures = 3, NextAllowedAt = now, UpdatedAt = now.AddHours(-48).AddSeconds(1) });
            db.SignInIpBudgets.AddRange(new SignInIpBudget { Key = "i-old", Count = 3, WindowStart = now.AddHours(-2).AddSeconds(-1) }, new SignInIpBudget { Key = "i-young", Count = 3, WindowStart = now.AddHours(-2).AddSeconds(1) });
            await db.SaveChangesAsync();
            return 0;
        });
        AuthMaintenance.Result r;
        using (var s = k.F.Services.CreateScope()) r = await s.ServiceProvider.GetRequiredService<AuthMaintenance>().PurgeAsync();
        Assert.Equal((2, 2, 0, 2), (r.Sessions, r.Tokens, r.Decoys, r.GateRows));
        Assert.Equal(new[] { "live", "r-young", "s-young" }, (await k.Db(db => db.BuyerSessions.Select(x => x.TokenHash).ToListAsync())).Order().ToArray());
        Assert.Equal(3, await k.Db(db => db.EmailTokens.CountAsync()));
        Assert.Equal(new[] { "a-young" }, (await k.Db(db => db.SignInAttempts.Select(x => x.Key).ToListAsync())).ToArray());
        Assert.Equal(new[] { "i-young" }, (await k.Db(db => db.SignInIpBudgets.Select(x => x.Key).ToListAsync())).ToArray());
    }

    // ---------- step-up re-authentication and session rotation (orchestrator, for the web) ----------

    private const string Reauth = "/api/v1/auth/reauthenticate", Rotate = "/api/v1/auth/session/rotate";

    [Fact]
    public async Task Reauthenticating_with_the_right_password_stamps_this_session_and_a_wrong_one_does_not()
    {
        using var k = new AuthKit(NoIpLimit());
        var token = await k.VerifiedSession(Email);
        var signedIn = k.F.Clock.GetUtcNow();
        k.F.Clock.Advance(TimeSpan.FromMinutes(20));
        var wrong = await k.Post(Reauth, new { password = Wrong }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", (await Json(wrong)).GetProperty("code").GetString());
        Assert.Equal(signedIn, (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync())).AuthenticatedAt);   // unchanged
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);                       // the session itself lives on
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post(Reauth, new { password = Pw }, token)).StatusCode);
        Assert.Equal(k.F.Clock.GetUtcNow(), (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync())).AuthenticatedAt);
    }

    [Fact]
    public async Task Reauthenticating_only_touches_the_session_that_asked()
    {
        using var k = new AuthKit(NoIpLimit());
        var first = await k.VerifiedSession(Email);
        var second = (await (await k.Post(SignIn, new { email = Email, password = Pw })).Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        var t0 = k.F.Clock.GetUtcNow();
        k.F.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post(Reauth, new { password = Pw }, second)).StatusCode);
        var rows = await k.Db(db => db.BuyerSessions.AsNoTracking().ToListAsync());
        Assert.Equal(1, rows.Count(r => r.AuthenticatedAt == k.F.Clock.GetUtcNow()));
        Assert.Equal(1, rows.Count(r => r.AuthenticatedAt == t0));                                                    // the other session did not move
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", first)).StatusCode);
    }

    [Fact]
    public async Task Reauthentication_shares_the_sign_in_gate_so_a_stolen_session_cannot_guess_for_free()
    {
        using var k = new AuthKit(NoIpLimit());
        var token = await k.VerifiedSession(Email);
        var results = new List<(HttpStatusCode, string?)>();
        for (var i = 0; i < 8; i++)
        {
            var r = await k.Post(Reauth, new { password = Wrong }, token);
            results.Add((r.StatusCode, r.Headers.TryGetValues("Retry-After", out var v) ? v.Single() : null));
        }
        Assert.Equal(6, results.Count(r => r.Item1 == HttpStatusCode.Unauthorized));                                 // the same six free tries ...
        Assert.All(results.Skip(6), r => Assert.Equal((HttpStatusCode.TooManyRequests, "1"), r));                    // ... then the same delay
        Assert.Equal((HttpStatusCode.TooManyRequests, "1"), await Try(k, Email, Pw));                                // and sign-in for this address waits too
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.Post(Reauth, new { password = Pw }, token)).StatusCode);   // even the right password, while delayed
        k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post(Reauth, new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Try(k, Email, Pw)).Status);                                           // success cleared the address for sign-in as well
    }

    [Fact]
    public async Task Reauthentication_spends_the_same_per_ip_budget_as_sign_in()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "3", ["Auth:SignInFreeFailures"] = "100" });
        var token = await k.VerifiedSession(Email);
        for (var i = 0; i < 2; i++) await k.PostFrom("203.0.113.9", SignIn, new { email = $"ghost{i}@example.com", password = Wrong });
        var r = new HttpRequestMessage(HttpMethod.Post, Reauth) { Content = JsonContent.Create(new { password = Wrong }) };
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var ts = k.F.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        r.Headers.Add(ClientIpResolver.IpHeader, "203.0.113.9"); r.Headers.Add(ClientIpResolver.TimestampHeader, ts);
        r.Headers.Add(ClientIpResolver.SignatureHeader, "v1=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(DevDefaults.WebForwardingKey), Encoding.UTF8.GetBytes($"{ts}\n203.0.113.9"))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Http.SendAsync(r)).StatusCode);                          // the third failure from this address
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.PostFrom("203.0.113.9", SignIn, new { email = Email, password = Pw })).StatusCode);   // budget spent
    }

    [Fact]
    public async Task Reauthentication_needs_a_session_and_a_password_of_sensible_length_and_does_one_hash_of_work()
    {
        CountingPasswordService? counting = null;
        using var k = new AuthKit(NoIpLimit(), s => s.AddSingleton<PasswordService>(sp => counting = new CountingPasswordService(sp.GetRequiredService<IOptions<AuthOptions>>())));
        var token = await k.VerifiedSession(Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(Reauth, new { password = Pw }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(Reauth, new { password = Pw }, token + "x")).StatusCode);
        foreach (var missing in new object[] { new { }, new { password = "" } })
        {
            var r = await k.Post(Reauth, missing, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("password", out _));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(Reauth, new { password = new string('x', 129) }, token)).StatusCode);
        counting!.Reset();
        await k.Post(Reauth, new { password = Wrong }, token);
        Assert.Equal((1, 0), (counting.HashWork, counting.Hashes));
        counting.Reset();
        await k.Post(Reauth, new { password = Pw }, token);
        Assert.Equal((1, 0), (counting.HashWork, counting.Hashes));
    }

    [Fact]
    public async Task Rotating_gives_a_new_token_kills_the_old_one_and_keeps_the_sessions_age_and_proof_of_identity()
    {
        using var k = new AuthKit();
        var old = await k.VerifiedSession(Email);
        var before = await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync());
        k.F.Clock.Advance(TimeSpan.FromDays(3));
        var res = await k.Post(Rotate, null, old);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var fresh = (await Json(res)).GetProperty("token").GetString()!;
        Assert.True(fresh.Length >= 43);
        Assert.NotEqual(old, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", old)).StatusCode);              // the old cookie is dead
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", fresh)).StatusCode);
        var rows = await k.Db(db => db.BuyerSessions.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].RevokedAt);
        Assert.Null(rows[1].RevokedAt);
        Assert.Equal(before.AbsoluteExpiresAt, rows[1].AbsoluteExpiresAt);                                          // rotating does not lengthen a session
        Assert.Equal(before.AuthenticatedAt, rows[1].AuthenticatedAt);                                              // and is not a proof of identity
        Assert.Equal(Tokens.Hash(fresh), rows[1].TokenHash);
        Assert.DoesNotContain(rows, r => r.TokenHash == fresh);                                                      // only the hash is stored
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(Rotate, null, old)).StatusCode);                    // the old token cannot be rotated again
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(Rotate, null, null)).StatusCode);
    }

    [Fact]
    public async Task A_rotated_session_cannot_outlive_the_original_absolute_limit()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        for (var i = 0; i < 4; i++)
        {
            k.F.Clock.Advance(TimeSpan.FromDays(6));
            token = (await Json(await k.Post(Rotate, null, token))).GetProperty("token").GetString()!;
        }
        k.F.Clock.Advance(TimeSpan.FromDays(5));                                                                    // day 29
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));                                          // day 30: the first session's limit
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task Twenty_parallel_rotations_of_one_token_make_exactly_one_new_session()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => k.Post(Rotate, null, token))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(19, results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(1, await k.Db(db => db.BuyerSessions.CountAsync(s => s.RevokedAt == null)));
    }
}
