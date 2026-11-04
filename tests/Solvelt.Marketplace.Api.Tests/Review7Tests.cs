using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The orchestrator's independent review of 44b5d27: H1, M2, M3, L4, L5, L8 and the display-name note.</summary>
public class Review7Tests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private const string SignUp = "/api/v1/auth/sign-up";

    private static async Task<(HttpStatusCode, string)> Do(AuthKit k, string path, object body)
    {
        var r = await k.Post(path, body);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ---------- H1: signing up twice for a verified address ----------

    [Fact]
    public async Task Signing_up_twice_in_the_same_hour_answers_the_same_for_a_verified_an_unverified_and_an_unknown_address()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        var addresses = new[] { Email, "unverified@example.com", "brand.new@example.com" };
        var first = new List<(HttpStatusCode, string)>(); var second = new List<(HttpStatusCode, string)>();
        foreach (var a in addresses) first.Add(await Do(k, SignUp, new { email = a, password = Pw, displayName = "Jane" }));
        foreach (var a in addresses) second.Add(await Do(k, SignUp, new { email = a, password = Pw, displayName = "Jane" }));
        Assert.All(first.Concat(second), r => Assert.Equal((HttpStatusCode.NoContent, ""), r));
        // the verified owner was told once for that hour, however often the address was entered
        var mails = (await k.Mail(Email)).Where(m => !m.Subject.StartsWith("Confirm")).ToList();
        Assert.Single(mails);
    }

    // ---------- M2: one statement shape for every kind of address ----------

    private static List<string> Tables(CommandLog log) => log.Seen.SelectMany(s => s.Split(' ', 2)[1].Split('+')).ToList();

    [Fact]
    public async Task Sign_up_touches_the_same_tables_in_the_same_order_for_a_new_an_unverified_and_a_verified_address()
    {
        var log = new CommandLog();
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" }, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(log)));
        await k.VerifiedSession(Email);
        await k.SignUp("unverified@example.com");
        var shapes = new List<List<string>>();
        foreach (var a in new[] { "brand.new@example.com", "unverified@example.com", Email })
        {
            log.Clear(); log.Recording = true;
            Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(a)).StatusCode);
            log.Recording = false;
            shapes.Add(Tables(log));
        }
        Assert.Equal(5, shapes[0].Count);                                   // read the account, retire old links, write the account, write a token, write the outbox row
        Assert.Contains("outbox_messages", shapes[0]);
        Assert.All(shapes, s => Assert.Equal(shapes[0], s));
    }

    [Fact]
    public async Task A_verified_address_gets_a_placeholder_token_and_its_notice_and_an_unverified_one_changes_nothing_but_its_link()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.Mail(Email);
        var before = await k.Db(db => db.EmailTokens.AsNoTracking().CountAsync(t => t.Purpose == "verify"));
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email)).StatusCode);
        var tokens = await k.Db(db => db.EmailTokens.AsNoTracking().ToListAsync());
        Assert.Equal(before, tokens.Count(t => t.Purpose == "verify"));       // no new link for a verified address
        Assert.Contains(tokens, t => t.Purpose == "decoy" && t.UsedAt is not null);
        Assert.Contains(await k.Mail(Email), m => !m.Subject.StartsWith("Confirm"));
    }

    // ---------- M3: a session born at the moment of a reset or sign-out-all never lives ----------

    private static async Task<string> PlantSession(AuthKit k, Guid accountId, int epoch)
    {
        var token = Tokens.New();
        var now = k.F.Clock.GetUtcNow();
        await k.Db(async db =>
        {
            db.BuyerSessions.Add(new BuyerSession
            {
                Id = Guid.NewGuid(), AccountId = accountId, TokenHash = Tokens.Hash(token), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now, Epoch = epoch,
                ExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30),
            });
            await db.SaveChangesAsync(); return 0;
        });
        return token;
    }

    [Fact]
    public async Task A_session_made_by_a_rotation_racing_a_reset_does_not_survive_the_reset()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        await k.VerifiedSession(Email);
        var account = await k.Account(Email);
        // what the race leaves behind: a row inserted after the reset's snapshot, so the reset's UPDATE never saw it (it carries the epoch it was made under)
        var raced = await PlantSession(k, account.Id, account.SessionEpoch);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", raced)).StatusCode);   // the row is otherwise valid
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var link = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token = link, password = "a brand new password" })).StatusCode);
        // simulate the unlucky ordering: the row exists and is not revoked, as if the reset's UPDATE had missed it
        await k.Db(db => db.BuyerSessions.Where(s => s.TokenHash == Tokens.Hash(raced)).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", raced)).StatusCode);
        // and a fresh sign-in with the new password is a live session again
        var signIn = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "a brand new password" });
        var token = (await signIn.Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task A_session_made_by_a_rotation_racing_sign_out_all_does_not_survive_it()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var account = await k.Account(Email);
        var raced = await PlantSession(k, account.Id, account.SessionEpoch);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/sign-out-all", null, token)).StatusCode);
        await k.Db(db => db.BuyerSessions.Where(s => s.TokenHash == Tokens.Hash(raced)).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", raced)).StatusCode);
        Assert.Equal(account.SessionEpoch + 1, (await k.Account(Email)).SessionEpoch);
    }

    [Fact]
    public async Task A_rotated_session_keeps_the_epoch_of_the_one_it_replaced()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        await k.Db(async db =>
        {
            await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, 3));
            await db.BuyerSessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Epoch, 3));
            return 0;
        });
        var r = await k.Post("/api/v1/auth/session/rotate", null, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var next = (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", next)).StatusCode);
        Assert.Equal(3, (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync(s => s.TokenHash == Tokens.Hash(next)))).Epoch);
    }

    [Fact]
    public async Task A_new_sign_in_takes_the_accounts_current_epoch()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, 5)); return 0; });
        var r = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw });
        var token = (await r.Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    // ---------- L4: one spelling per guest link ----------

    [Fact]
    public void A_guest_link_has_exactly_one_spelling()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var links = new GuestLinks(Options.Create(new AuthOptions { GuestLinkKey = "dev-only-guest-link-key-not-for-production", GuestLinkDays = 30 }), clock);
        var order = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
        var (token, _) = links.Make(order);
        Assert.Equal(order, links.Parse(token));
        var parts = token.Split('.');
        Assert.Null(links.Parse(string.Join('.', parts[0], parts[1].ToUpperInvariant(), parts[2], parts[3])));        // upper-case id: same MAC, second spelling
        Assert.Null(links.Parse(string.Join('.', parts[0], parts[1], "0" + parts[2], parts[3])));                     // zero-padded expiry: same number, same MAC
        Assert.Null(links.Parse(string.Join('.', parts[0], parts[1], "+" + parts[2], parts[3])));
    }

    // ---------- L5: the gate's compare-and-set under a forced race ----------

    private sealed class BeforeWrite : DbCommandInterceptor
    {
        public Func<string, Task>? Hook;
        private bool _busy;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Hook is { } h && !_busy)
            {
                _busy = true;
                try { await h(command.CommandText); } finally { _busy = false; }
            }
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<GateResult> Claim(AuthKit k, string address, string ip)
    {
        using var scope = k.F.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SignInGate>().ClaimAsync(address, ip, CancellationToken.None);
    }

    [Fact]
    public async Task Two_attempts_that_read_the_same_failure_count_are_both_counted()
    {
        var hook = new BeforeWrite();
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" }, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        Assert.True((await Claim(k, "addr", "ip-1")).Allowed);            // the row exists with one failure
        var fired = 0;
        hook.Hook = async sql =>
        {
            if (!sql.Contains("signin_attempts") || fired++ > 0) return;
            // another attempt gets in between our read and our write
            await k.Db(db => db.SignInAttempts.Where(a => a.Key == "addr").ExecuteUpdateAsync(s => s.SetProperty(a => a.Failures, a => a.Failures + 1)));
        };
        Assert.True((await Claim(k, "addr", "ip-2")).Allowed);
        hook.Hook = null;
        Assert.Equal(3, (await k.Db(db => db.SignInAttempts.AsNoTracking().SingleAsync(a => a.Key == "addr"))).Failures);   // 1 + the other attempt + ours; a blind write would leave 2
    }

    [Fact]
    public async Task Two_attempts_from_one_address_that_read_the_same_ip_count_are_both_counted()
    {
        var hook = new BeforeWrite();
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" }, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        Assert.True((await Claim(k, "a1", "ip-x")).Allowed);
        var fired = 0;
        hook.Hook = async sql =>
        {
            if (!sql.Contains("signin_ip_budgets") || fired++ > 0) return;
            await k.Db(db => db.SignInIpBudgets.Where(b => b.Key == "ip-x").ExecuteUpdateAsync(s => s.SetProperty(b => b.Count, b => b.Count + 1)));
        };
        Assert.True((await Claim(k, "a2", "ip-x")).Allowed);
        hook.Hook = null;
        Assert.Equal(3, (await k.Db(db => db.SignInIpBudgets.AsNoTracking().SingleAsync(b => b.Key == "ip-x"))).Count);
    }

    [Fact]
    public async Task An_attempt_that_loses_the_race_every_time_is_held_back_and_not_let_through()
    {
        var hook = new BeforeWrite();
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" }, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        Assert.True((await Claim(k, "addr", "ip-1")).Allowed);
        hook.Hook = async sql =>
        {
            if (!sql.Contains("signin_attempts")) return;
            await k.Db(db => db.SignInAttempts.Where(a => a.Key == "addr").ExecuteUpdateAsync(s => s.SetProperty(a => a.Failures, a => a.Failures + 1)));
        };
        Assert.False((await Claim(k, "addr", "ip-2")).Allowed);           // too much contention on one address: wait a moment
        hook.Hook = null;
    }

    // ---------- L8: the auth purge is not run on every poll ----------

    [Fact]
    public void The_auth_purge_schedule_allows_one_run_per_interval()
    {
        var schedule = new AuthPurgeSchedule(TimeSpan.FromMinutes(15));
        var t = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.True(schedule.ShouldRun(t));
        Assert.False(schedule.ShouldRun(t.AddSeconds(5)));
        Assert.False(schedule.ShouldRun(t.AddMinutes(14)));
        Assert.True(schedule.ShouldRun(t.AddMinutes(15)));
        Assert.False(schedule.ShouldRun(t.AddMinutes(16)));
    }

    [Fact]
    public async Task The_worker_purges_the_auth_tables_once_per_interval_not_on_every_poll()
    {
        var log = new CommandLog();
        using var k = new AuthKit(null, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(log)));
        var worker = new OutboxWorker(k.F.Services.GetRequiredService<IServiceScopeFactory>(), k.F.Services.GetRequiredService<IOptions<OutboxOptions>>(), NullLogger<OutboxWorker>.Instance, k.F.Clock);
        int AuthDeletes() => log.Seen.Count(s => s == "DELETE signin_attempts");
        log.Clear(); log.Recording = true;
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, AuthDeletes());
        await worker.RunOnceAsync(CancellationToken.None);
        k.F.Clock.Advance(TimeSpan.FromMinutes(14));
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, AuthDeletes());                                    // still the first run only
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, AuthDeletes());
    }

    // ---------- Info: a name typed at an unverified sign-up is not kept after a reset ----------

    [Fact]
    public async Task A_reset_of_an_unverified_account_drops_the_name_typed_at_sign_up_and_a_verified_one_keeps_its_name()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        await k.SignUp("squatted@example.com", name: "Not The Owner");
        await k.VerifiedSession(Email, name: "Real Jane");
        foreach (var a in new[] { "squatted@example.com", Email })
        {
            await k.Post("/api/v1/auth/forgot-password", new { email = a });
            var link = AuthKit.TokenIn((await k.Mail(a)).Last(m => m.Subject == "Reset your password"), "/reset-password");
            Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token = link, password = "a brand new password" })).StatusCode);
        }
        Assert.Equal("Buyer", (await k.Account("squatted@example.com")).DisplayName);
        Assert.Equal("Real Jane", (await k.Account(Email)).DisplayName);
    }
}
