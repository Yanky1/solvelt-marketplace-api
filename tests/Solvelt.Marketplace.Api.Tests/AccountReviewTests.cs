using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Every_table_with_a_buyer_account_id_belongs_to_a_module_and_every_registered_eraser_is_wired()
    {
        using var k = new AuthKit();
        await k.Db(async db =>
        {
            var linked = db.Model.GetEntityTypes().Where(e => e.FindProperty("AccountId") is not null).Select(e => e.ClrType).ToHashSet();
            var mapped = PiiRegistry.Modules.SelectMany(m => m.Entities).ToList();
            Assert.Empty(linked.Except(mapped));                                                       // a new buyer-linked table must be given a module (and an eraser)
            Assert.Empty(mapped.Except(linked));                                                       // and the list does not name tables that are not there
            Assert.Equal(mapped.Count, mapped.Distinct().Count());
            return 0;
        });
        var registered = k.F.Services.GetServices<IAccountEraser>().Select(e => e.GetType()).ToList();
        foreach (var m in PiiRegistry.Modules.Where(m => m.Eraser is not null)) Assert.Contains(m.Eraser!, registered);
        Assert.Empty(registered.Except(PiiRegistry.Modules.Where(m => m.Eraser is not null).Select(m => m.Eraser!)));   // an eraser nobody listed is a module nobody reviewed
        // The launch gate: these modules still hold buyer data with no eraser. When one registers its eraser it must move out of this list (and this assertion forces it).
        Assert.Equal(new[] { "bookings", "order-cancellations-and-returns", "orders-and-carts" }, PiiRegistry.Pending.Select(m => m.Name).Order().ToArray());
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
