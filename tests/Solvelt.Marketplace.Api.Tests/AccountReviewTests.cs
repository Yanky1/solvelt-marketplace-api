using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The orchestrator's independent review of the account slice (a597e57..aafaa29): A1, A2, A3, L1 to L5 and the E5 test.</summary>
public class AccountReviewTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private const string NewPw = "a brand new password";
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get;

    private static Dictionary<string, string?> Open() => new() { ["Auth:SignInIpFailuresPerHour"] = "100000" };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Runs a callback before the first statement that matches, once, then stays out of the way (the callback's own statements are not intercepted).</summary>
    private sealed class BeforeStatement : DbCommandInterceptor
    {
        public Func<string, bool>? When;
        public Func<Task>? Then;
        public Func<string, Task>? EveryInsert;
        private bool _busy;
        private async Task Fire(string sql)
        {
            if (_busy) return;
            if (When is { } w && Then is { } go && w(sql))
            {
                When = null; _busy = true;
                try { await go(); } finally { _busy = false; }
            }
            if (EveryInsert is { } each && sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && sql.Contains("buyer_addresses"))
            {
                _busy = true;
                try { await each(sql); } finally { _busy = false; }
            }
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Fire(command.CommandText); return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await Fire(command.CommandText); return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }
    }

    /// <summary>
    /// The moment after the password was checked and before the change or delete begins its transaction: the re-authentication stamps the session (a transaction of the
    /// change itself would hold SQLite's write lock, so the window to hit is this one, which is also the one the slow hash widens on a real server).
    /// </summary>
    private static bool AfterThePasswordCheck(string sql) => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && sql.Contains("buyer_sessions") && sql.Contains("AuthenticatedAt");

    /// <summary>The writes of a password reset that lands from somewhere else: a new hash and the next epoch.</summary>
    private static Func<Task> ResetLanding(AuthKit k) => async () =>
    {
        using var scope = k.F.Services.CreateScope();
        var hash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash("owner chose this one");
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().BuyerAccounts
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, hash).SetProperty(a => a.SessionEpoch, a => a.SessionEpoch + 1));
    };

    // ---------- A1: a password change or a delete never overwrites a reset that landed during it ----------

    [Fact]
    public async Task A_password_change_does_not_overwrite_a_reset_that_lands_during_it_and_does_not_revive_the_session()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var attacker = await k.VerifiedSession(Email);
        hook.When = AfterThePasswordCheck; hook.Then = ResetLanding(k);
        var r = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = NewPw }, attacker);
        Assert.Null(hook.When);                                                                          // the reset did land in the middle
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("account_changed", (await Json(r)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", attacker)).StatusCode);   // the old-password session stays dead
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "owner chose this one" })).StatusCode);   // the owner's password stands
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = NewPw })).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_does_not_go_through_when_a_reset_landed_during_it()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var attacker = await k.VerifiedSession(Email);
        hook.When = AfterThePasswordCheck; hook.Then = ResetLanding(k);
        var r = await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, attacker);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var account = await k.Account(Email);
        Assert.Null(account.DeletedAt);                                                                  // the owner keeps the account
        Assert.Equal(0, await k.Db(db => db.OutboxMessages.CountAsync(m => m.Type == "account.redact")));
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "owner chose this one" })).StatusCode);
    }

    // ---------- A2: the guarantees that need real concurrency (they bite on PostgreSQL; the SQLite run is the same code) ----------

    [Fact]
    public async Task Two_creates_at_nineteen_addresses_make_exactly_twenty_not_twenty_one()
    {
        var gate = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(gate)));
        var token = await k.VerifiedSession(Email);
        object Body(int i) => new { fullName = $"Jane {i}", line1 = $"{i} Main St", city = "New York", region = "NY", postalCode = "10001", country = "US" };
        for (var i = 0; i < AccountValidation.MaxAddresses - 1; i++) Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, "/api/v1/account/addresses", Body(i), token)).StatusCode);
        // Both requests are held right before their INSERT until the other one gets there too. With the account's row lock the second cannot get there until the first has
        // committed, so the first simply waits for its turn to time out (2 s); without the lock both pass the count and the buyer ends with 21.
        var barrier = new Barrier(2);
        gate.EveryInsert = _ => Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(2)));
        var results = await Task.WhenAll(Enumerable.Range(100, 2).Select(i => k.Send(Post, "/api/v1/account/addresses", Body(i), token)));
        gate.EveryInsert = null;
        Assert.Equal(AccountValidation.MaxAddresses, await k.Db(db => db.BuyerAddresses.CountAsync()));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, results.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task Setting_a_default_while_the_default_is_deleted_always_leaves_exactly_one_default()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        object Body(int i) => new { fullName = $"Jane {i}", line1 = $"{i} Main St", city = "New York", region = "NY", postalCode = "10001", country = "US" };
        for (var round = 0; round < 12; round++)
        {
            var ids = new List<Guid>();
            for (var i = 0; i < 3; i++) ids.Add((await Json(await k.Send(Post, "/api/v1/account/addresses", Body(i), token))).GetProperty("id").GetGuid());
            var current = await k.Db(db => db.BuyerAddresses.AsNoTracking().Where(a => a.IsDefault).Select(a => a.Id).SingleAsync());
            var other = ids.First(i => i != current);
            await Task.WhenAll(
                k.Send(HttpMethod.Delete, $"/api/v1/account/addresses/{current}", null, token),
                k.Send(Post, $"/api/v1/account/addresses/{other}/default", null, token),
                k.Send(HttpMethod.Delete, $"/api/v1/account/addresses/{ids.First(i => i != current && i != other)}", null, token));
            var rows = await k.Db(db => db.BuyerAddresses.AsNoTracking().ToListAsync());
            Assert.True(rows.Count == 0 || rows.Count(a => a.IsDefault) == 1, $"round {round}: {rows.Count} addresses, {rows.Count(a => a.IsDefault)} defaults");
            foreach (var a in rows) await k.Send(HttpMethod.Delete, $"/api/v1/account/addresses/{a.Id}", null, token);
        }
    }

    // ---------- A3: every table that holds a buyer's data is accounted for in the erasure ----------

    [Fact]
    public async Task Every_table_that_may_hold_a_buyers_data_is_claimed_or_cleared_and_every_registered_eraser_is_wired()
    {
        using var k = new AuthKit();
        await k.Db(async db =>
        {
            var entities = db.Model.GetEntityTypes().ToList();
            var claimed = PiiRegistry.Modules.SelectMany(m => m.Entities).ToList();
            var allowed = PiiRegistry.NoBuyerData.Select(n => n.Entity).ToList();
            Assert.Empty(claimed.Intersect(allowed));                                                  // claimed or "no buyer data", not both
            Assert.Equal(claimed.Count, claimed.Distinct().Count());
            Assert.Equal(allowed.Count, allowed.Distinct().Count());
            Assert.All(PiiRegistry.NoBuyerData, n => Assert.False(string.IsNullOrWhiteSpace(n.Reason)));
            // what looks like it holds a buyer's data: an AccountId, a property that looks like personal data, or a child (EF foreign key) of a claimed entity
            string[] looksLike = ["Email", "Phone", "Line1", "PostalCode", "FullName", "BuyerName", "FileName", "Lat", "Lng"];
            var suspect = entities.Where(e =>
                e.FindProperty("AccountId") is not null
                || e.GetProperties().Any(p => looksLike.Any(t => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                || e.GetForeignKeys().Any(f => claimed.Contains(f.PrincipalEntityType.ClrType))).Select(e => e.ClrType).ToHashSet();
            Assert.Empty(suspect.Except(claimed).Except(allowed));                                     // a table nobody has decided about: give it a module (and an eraser) or a reason
            Assert.Empty(claimed.Except(entities.Select(e => e.ClrType)));                             // the registry does not name tables that are not there
            Assert.Empty(allowed.Except(suspect));                                                     // and an allow-list entry that is no longer needed goes
            return 0;
        });
        var registered = k.F.Services.GetServices<IAccountEraser>().Select(e => e.GetType()).ToList();
        foreach (var m in PiiRegistry.Modules.Where(m => m.Eraser is not null)) Assert.Contains(m.Eraser!, registered);
        Assert.Empty(registered.Except(PiiRegistry.Modules.Where(m => m.Eraser is not null).Select(m => m.Eraser!)));   // an eraser nobody listed is a module nobody reviewed
        // The launch gate: these modules still hold buyer data with no eraser. When one registers its eraser it must move out of this list (and this assertion forces it).
        Assert.Empty(PiiRegistry.Pending);   // every module that holds buyer data has its eraser (the launch gate is open)
    }

    // ---------- the erasure is one transaction: a failure halfway changes nothing ----------

    public sealed class PartialFlag { public volatile bool Throw = true; }

    /// <summary>An eraser on the shared DbContext like M5's: it erases the buyer's addresses (its first statement, which would commit on its own outside a transaction), then fails.</summary>
    public sealed class HalfwayEraser(MarketplaceDb db, PartialFlag flag) : IAccountEraser
    {
        public async Task EraseAsync(Guid accountId, CancellationToken ct)
        {
            await db.BuyerAddresses.Where(a => a.AccountId == accountId).ExecuteDeleteAsync(ct);
            if (flag.Throw) throw new InvalidOperationException("second statement failed");
        }
    }

    [Fact]
    public async Task An_eraser_that_fails_after_its_first_statement_leaves_nothing_changed_and_the_retry_completes_the_erasure()
    {
        var flag = new PartialFlag();
        using var k = new AuthKit(Open(), s => { s.AddSingleton(flag); s.AddScoped<IAccountEraser, HalfwayEraser>(); });
        var token = await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        var body = new { fullName = "Jane", line1 = "1 Main St", city = "New York", region = "NY", postalCode = "10001", country = "US" };
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, "/api/v1/account/addresses", body, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);

        await k.Dispatch();                                                                              // the job runs, the eraser fails halfway
        Assert.Equal(1, await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == id)));          // the eraser's first statement was rolled back with everything else
        var half = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Null(half.RedactedAt);
        Assert.Equal("Jane Brown", half.DisplayName);

        flag.Throw = false;
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        while (await k.Dispatch() > 0) { }                                                               // the retry
        Assert.Equal(0, await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == id)));
        var done = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.NotNull(done.RedactedAt);
        Assert.Equal("Deleted buyer", done.DisplayName);
    }

    // ---------- review of 1725a68: items 1 to 5 ----------

    /// <summary>A gate row for the address appears right after the password check, as an attempt from another client would leave one.</summary>
    private static Func<Task> GateRowAppears(AuthKit k, string email) => async () =>
    {
        using var scope = k.F.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<GateKeys>();
        await scope.ServiceProvider.GetRequiredService<SignInGate>().ClaimAsync(keys.Address(email), keys.Ip("198.51.100.9"), CancellationToken.None);
    };

    [Fact]
    public async Task Deleting_clears_the_sign_in_gate_for_the_address_in_its_own_transaction()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var token = await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        hook.When = AfterThePasswordCheck; hook.Then = GateRowAppears(k, Email);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);
        Assert.Null(hook.When);                                                                          // the attempt did land in the middle
        Assert.Equal(0, await k.Db(db => db.SignInAttempts.CountAsync()));                                // ... and the delete took the gate's memory of the address with it
        Assert.Null((await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).Email);   // the address is free for a new sign-up at once
        Assert.DoesNotContain("@", string.Concat((await k.Db(db => db.OutboxMessages.AsNoTracking().ToListAsync())).Select(m => m.Payload)));   // the outbox holds ids only
    }

    [Fact]
    public async Task A_sign_in_with_an_old_password_does_not_write_its_old_hash_back_over_a_reset_that_landed_meanwhile()
    {
        var hook = new BeforeStatement();
        var config = Open(); config["Auth:PasswordIterations"] = "5000";                                // today's hashing is stronger than the 1000 iterations stored below
        using var k = new AuthKit(config, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        await k.VerifiedSession(Email);
        // an account whose stored hash is weaker than today's, so a right password triggers the silent rehash
        var weak = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })).HashPassword(new object(), Pw);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(a => a.SetProperty(x => x.PasswordHash, weak)); return 0; });
        hook.When = sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && sql.Contains("buyer_accounts") && sql.Contains("PasswordHash");   // the rehash write
        hook.Then = ResetLanding(k);
        var r = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw });
        Assert.Null(hook.When);                                                                          // the reset did land between the check and the rehash
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "owner chose this one" })).StatusCode);   // the owner's password stands
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);              // the old one is not back
    }

    [Fact]
    public async Task A_password_change_does_not_restamp_a_session_that_is_no_longer_on_the_epoch_the_check_saw()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var token = await k.VerifiedSession(Email);
        hook.When = AfterThePasswordCheck;
        hook.Then = async () => { using var scope = k.F.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().BuyerSessions.ExecuteUpdateAsync(x => x.SetProperty(y => y.Epoch, 99L)); };
        var r = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = NewPw }, token);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);                                            // the account itself was unchanged, so the change goes through
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);   // but the session that had moved is not given the new epoch
    }

    [Fact]
    public void Forbidden_characters_are_judged_per_character_not_per_code_unit()
    {
        Assert.False(AuthValidation.HasForbiddenCharacters("Jane Brown"));
        Assert.False(AuthValidation.HasForbiddenCharacters("Jane 😀"));                  // emoji: a valid surrogate pair
        Assert.False(AuthValidation.HasForbiddenCharacters("𠀀 Chen"));                 // CJK Extension B: a letter outside the basic plane
        Assert.True(AuthValidation.HasForbiddenCharacters("Jane\ud800"));               // a high half with no partner
        Assert.True(AuthValidation.HasForbiddenCharacters("\udc00Jane"));               // a low half with no partner
        Assert.True(AuthValidation.HasForbiddenCharacters("a\ud800b"));
        Assert.True(AuthValidation.HasForbiddenCharacters("Jane\U000F0000"));           // supplementary private use
        Assert.True(AuthValidation.HasForbiddenCharacters("Jane\u200BBrown"));          // zero width space
    }

    [Fact]
    public async Task A_name_with_characters_outside_the_basic_plane_is_accepted_deliberately()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var r = await k.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "Ada 😀 𠀀", phone = (string?)null }, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Ada 😀 𠀀", (await Json(r)).GetProperty("displayName").GetString());
    }

    // ---------- L1, L2: the relay address is retired, the email is free at once ----------

    [Fact]
    public async Task A_deleted_buyers_address_can_sign_up_again_at_once_and_gets_a_new_relay_address()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var old = await k.Account(Email);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email)).StatusCode);                       // before the redaction job has run
        var fresh = await k.Account(Email);
        Assert.NotEqual(old.Id, fresh.Id);
        Assert.NotEqual(old.RelayEmail, fresh.RelayEmail);
        Assert.Contains(await k.Mail(Email), m => m.Subject.StartsWith("Confirm"));                        // a real verification mail, not a swallowed sign-up
    }

    // ---------- L3: look-alike and invisible characters ----------

    [Theory]
    [InlineData("Jane​Brown")]        // zero width space (format)
    [InlineData("Jane‮Brown")]        // right-to-left override (format)
    [InlineData("JaneBrown")]        // private use
    [InlineData("Jane⁦Brown")]        // bidi isolate (format)
    public async Task Names_and_addresses_with_format_or_private_use_characters_are_refused(string text)
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var body = new { fullName = text, line1 = "1 Main St", city = "New York", region = "NY", postalCode = "10001", country = "US" };
        var r = await k.Send(Post, "/api/v1/account/addresses", body, token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = text, phone = (string?)null }, token)).StatusCode);
        Assert.Equal(0, await k.Db(db => db.BuyerAddresses.CountAsync()));
    }

    // ---------- L4, L5, E5 ----------

    [Fact]
    public async Task The_phone_column_has_the_length_the_validation_allows()
    {
        using var k = new AuthKit();
        await k.Db(db =>
        {
            Assert.Equal(30, db.Model.FindEntityType(typeof(BuyerAccount))!.FindProperty("Phone")!.GetMaxLength());
            Assert.Equal(30, db.Model.FindEntityType(typeof(BuyerAddress))!.FindProperty("Phone")!.GetMaxLength());
            return Task.FromResult(0);
        });
    }

    [Fact]
    public async Task A_deleted_account_takes_no_address_book_writes_even_from_a_request_already_in_flight()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        using var scope = k.F.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var body = new AddressBody("Jane", "1 Main St", null, "New York", "NY", "10001", "US", null);
        var (address, outcome) = await accounts.CreateAddressAsync(id, body, CancellationToken.None);
        Assert.Null(address);
        Assert.Equal(AccountOutcome.NotFound, outcome);
        Assert.Equal(0, await k.Db(db => db.BuyerAddresses.CountAsync()));
    }

    [Fact]
    public async Task Session_epochs_work_above_the_32_bit_range()
    {
        using var k = new AuthKit(Open());
        await k.VerifiedSession(Email);
        const long big = (long)int.MaxValue + 5;
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, big)); return 0; });
        var signIn = await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw });
        var token = (await signIn.Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        Assert.Equal(big, (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync(s => s.TokenHash == Tokens.Hash(token)))).Epoch);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
        // a password change adds one without wrapping, and the session that asked lives on
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = NewPw }, token)).StatusCode);
        Assert.Equal(big + 1, (await k.Account(Email)).SessionEpoch);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }
}
