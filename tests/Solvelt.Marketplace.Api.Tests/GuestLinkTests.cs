using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Records every claim it is asked for; optionally throws.</summary>
public sealed class RecordingClaimer : IGuestOrderClaimer
{
    private readonly object _lock = new();
    public List<(Guid AccountId, string Email)> Calls { get; } = [];
    public bool Throw { get; set; }
    public int Claimed { get; set; } = 1;

    public Task<int> ClaimAsync(Guid accountId, string normalisedEmail, CancellationToken ct)
    {
        lock (_lock) Calls.Add((accountId, normalisedEmail));
        if (Throw) throw new InvalidOperationException("claimer broke for " + normalisedEmail);
        return Task.FromResult(Claimed);
    }
}

public class GuestLinkTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private static readonly Guid Order = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static GuestLinks Links(TimeProvider time, string key = "dev-only-guest-link-key-not-for-production", int days = 30)
        => new(Options.Create(new AuthOptions { GuestLinkKey = key, GuestLinkDays = days }), time);

    // ---------- the link ----------

    [Fact]
    public void A_link_names_one_order_and_round_trips()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var links = Links(clock);
        var (token, expires) = links.Make(Order);
        Assert.Matches("^g1\\.[0-9a-f]{32}\\.[0-9]+\\.[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(clock.GetUtcNow().AddDays(30), expires);
        Assert.Equal(Order, links.Parse(token));
        Assert.DoesNotContain("@", token);                                                           // no address, nothing personal
        Assert.NotEqual(token, links.Make(Guid.NewGuid()).Token);
    }

    [Fact]
    public void A_link_stops_working_at_the_second_it_names_and_not_a_second_before()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var links = Links(clock, days: 30);
        var (token, expires) = links.Make(Order);
        clock.SetUtcNow(expires - TimeSpan.FromSeconds(1));
        Assert.Equal(Order, links.Parse(token));
        clock.SetUtcNow(expires);
        Assert.Null(links.Parse(token));
        clock.SetUtcNow(expires + TimeSpan.FromDays(400));
        Assert.Null(links.Parse(token));
    }

    [Fact]
    public void The_lifetime_follows_the_configuration_and_an_explicit_expiry_wins()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(clock.GetUtcNow().AddDays(7), Links(clock, days: 7).Make(Order).ExpiresAt);
        Assert.Equal(clock.GetUtcNow().AddDays(1), Links(clock, days: 0).Make(Order).ExpiresAt);     // never shorter than a day
        var fixedEnd = clock.GetUtcNow().AddHours(5);
        var (token, expires) = Links(clock).Make(Order, fixedEnd);
        Assert.Equal(fixedEnd, expires);
        clock.Advance(TimeSpan.FromHours(5));
        Assert.Null(Links(clock).Parse(token));
    }

    [Fact]
    public void A_forged_edited_or_foreign_link_is_refused()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var links = Links(clock);
        var (good, expires) = links.Make(Order);
        var parts = good.Split('.');
        var other = Guid.NewGuid();
        var forgeries = new[]
        {
            $"g1.{other:N}.{parts[2]}.{parts[3]}",                                                   // another order, copied MAC
            $"g1.{parts[1]}.{expires.ToUnixTimeSeconds() + 86400}.{parts[3]}",                       // a later expiry, copied MAC
            $"g1.{parts[1]}.{parts[2]}.{new string('A', 43)}",                                      // wrong MAC
            Links(clock, "another-key-another-key-another-key-0000").Make(Order).Token,              // another key
            $"g2.{parts[1]}.{parts[2]}.{parts[3]}",                                                  // another version
            good + "x", good[..^1], good.Replace('.', '-'), good.ToUpperInvariant(), "", "   ", "g1", "g1...", new string('g', 5000),
            $"g1.{parts[1]}.-1.{parts[3]}", $"g1.{parts[1]}.99999999999999999999.{parts[3]}", $"g1.{parts[1]}.1e9.{parts[3]}",
        };
        foreach (var f in forgeries) Assert.Null(links.Parse(f));
        Assert.Null(links.Parse(null));
        Assert.Equal(Order, links.Parse(good));
    }

    [Fact]
    public void A_guest_link_can_never_stand_in_for_an_account_link_or_the_other_way_round()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var emailLinks = new LinkTokens(Options.Create(new AuthOptions { EmailLinkKey = "dev-only-guest-link-key-not-for-production" }));   // even with the SAME key text
        var (guest, _) = Links(clock).Make(Order);
        Assert.Null(emailLinks.Parse("verify", guest));
        Assert.Null(emailLinks.Parse("reset", guest));
        Assert.Null(Links(clock).Parse(emailLinks.Make("verify", Order)));
        Assert.Null(Links(clock).Parse(emailLinks.Make("reset", Order)));
    }

    // ---------- configuration ----------

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40), ["Auth:GuestLinkKey"] = new string('g', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Notifications:PublicApiBaseUrl"] = "https://api.example.test",
        ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> d)
        => StartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env("Production"));

    [Fact]
    public void The_guest_link_key_is_required_long_distinct_and_never_a_dev_default_in_production()
    {
        Assert.Empty(Validate(GoodProd()));
        var missing = GoodProd(); missing.Remove("Auth:GuestLinkKey");
        Assert.Contains(Validate(missing), p => p.Contains("Auth:GuestLinkKey") && p.Contains("required"));
        var shortKey = GoodProd(); shortKey["Auth:GuestLinkKey"] = "short";
        Assert.Contains(Validate(shortKey), p => p.Contains("Auth:GuestLinkKey") && p.Contains("32"));
        var dev = GoodProd(); dev["Auth:GuestLinkKey"] = DevDefaults.GuestLinkKey;
        Assert.Contains(Validate(dev), p => p.Contains("Auth:GuestLinkKey") && p.Contains("Development default"));
        var reusedEmailKey = GoodProd(); reusedEmailKey["Auth:GuestLinkKey"] = reusedEmailKey["Auth:EmailLinkKey"];
        Assert.Contains(Validate(reusedEmailKey), p => p.Contains("must differ"));
        var reusedWeb = GoodProd(); reusedWeb["Auth:GuestLinkKey"] = reusedWeb["Web:ForwardingKey"];
        Assert.Contains(Validate(reusedWeb), p => p.Contains("must differ"));
    }

    [Fact]
    public void Development_and_testing_get_a_fixed_dev_key_and_a_configured_one_wins()
    {
        var o = new AuthOptions();
        OptionsSetup.Auth(o, new ConfigurationBuilder().AddInMemoryCollection().Build(), devLike: true);
        Assert.Equal(DevDefaults.GuestLinkKey, o.GuestLinkKey);
        var p = new AuthOptions();
        OptionsSetup.Auth(p, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:GuestLinkKey"] = "configured-key" }).Build(), devLike: true);
        Assert.Equal("configured-key", p.GuestLinkKey);
        var q = new AuthOptions();
        OptionsSetup.Auth(q, new ConfigurationBuilder().AddInMemoryCollection().Build(), devLike: false);
        Assert.Null(q.GuestLinkKey);                                                                // never invented outside Development/Testing
    }

    // ---------- claiming guest orders for a proven address ----------

    private static AuthKit WithClaimer(RecordingClaimer claimer, Dictionary<string, string?>? config = null)
        => new(config, s => s.AddSingleton<IGuestOrderClaimer>(claimer));

    [Fact]
    public async Task Verifying_an_address_claims_the_guest_orders_for_that_exact_address_and_account()
    {
        var claimer = new RecordingClaimer();
        using var k = WithClaimer(claimer);
        await k.SignUp("  Jane@Example.COM ");
        Assert.Empty(claimer.Calls);                                                                // signing up proves nothing
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Post("/api/v1/auth/verify-email", new { token, password = "wrong password!!" })).StatusCode);
        Assert.Empty(claimer.Calls);                                                                // a failed verification proves nothing either
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);
        var call = Assert.Single(claimer.Calls);
        Assert.Equal((await k.Account(Email)).Id, call.AccountId);
        Assert.Equal(Email, call.Email);                                                            // trimmed and lower-cased
    }

    [Fact]
    public async Task A_successful_sign_in_claims_orders_placed_as_a_guest_since_and_a_failed_one_does_not()
    {
        var claimer = new RecordingClaimer();
        using var k = WithClaimer(claimer, new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        await k.VerifiedSession(Email);
        claimer.Calls.Clear();
        await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" });
        await k.Post("/api/v1/auth/sign-in", new { email = "ghost@example.com", password = Pw });
        Assert.Empty(claimer.Calls);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = " JANE@example.com ", password = Pw })).StatusCode);
        var call = Assert.Single(claimer.Calls);
        Assert.Equal((await k.Account(Email)).Id, call.AccountId);
        Assert.Equal(Email, call.Email);
    }

    [Fact]
    public async Task An_unverified_account_never_reaches_a_claimer_even_with_the_right_password()
    {
        var claimer = new RecordingClaimer();
        using var k = WithClaimer(claimer);
        await k.SignUp(Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);   // email_not_verified
        Assert.Empty(claimer.Calls);                                                                // only a verified address can claim guest orders
    }

    [Fact]
    public async Task A_password_reset_proves_the_mailbox_and_claims_too()
    {
        var claimer = new RecordingClaimer();
        using var k = WithClaimer(claimer);
        await k.SignUp(Email, "attacker password 2");                                               // registered first by someone else, never verified
        await k.Mail(Email);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var reset = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Post("/api/v1/auth/reset-password", new { token = reset, password = Email })).StatusCode);
        Assert.Empty(claimer.Calls);                                                                // a refused reset claims nothing
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token = reset, password = "victim password 1" })).StatusCode);
        var call = Assert.Single(claimer.Calls);
        Assert.Equal((await k.Account(Email)).Id, call.AccountId);
        Assert.Equal(Email, call.Email);
    }

    [Fact]
    public async Task A_claimer_that_fails_never_fails_the_sign_in_and_the_others_still_run()
    {
        var broken = new RecordingClaimer { Throw = true };
        var fine = new RecordingClaimer();
        using var k = new AuthKit(null, s => { s.AddSingleton<IGuestOrderClaimer>(broken); s.AddSingleton<IGuestOrderClaimer>(fine); });
        await k.SignUp(Email);
        var token = AuthKit.TokenIn((await k.Mail(Email)).Single(), "/verify-email");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token, password = Pw })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        Assert.Equal(2, broken.Calls.Count);
        Assert.Equal(2, fine.Calls.Count);                                                          // the working claimer ran after the broken one each time
        var logs = string.Join("\n", k.F.Logs.Lines);
        Assert.Contains("InvalidOperationException", logs);                                         // the type only
        Assert.DoesNotContain("claimer broke", logs);                                               // never the message: it names the address
        Assert.DoesNotContain(Email, logs);
    }

    [Fact]
    public async Task Without_any_claimer_the_flows_work_as_before()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", token)).StatusCode);
    }

    [Fact]
    public async Task The_guest_links_service_and_claims_are_available_to_other_modules()
    {
        using var k = new AuthKit();
        var links = k.F.Services.GetRequiredService<GuestLinks>();
        var (token, expires) = links.Make(Order);
        Assert.Equal(Order, links.Parse(token));
        Assert.Equal(k.F.Clock.GetUtcNow().AddDays(30), expires);
        using var scope = k.F.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<GuestClaims>().ClaimAsync(Guid.NewGuid(), Email, default));
    }
}
