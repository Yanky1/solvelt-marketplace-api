using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Solvelt.Marketplace.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Passkeys (decision M1-80): registration, sign-in and step-up with a software authenticator.</summary>
public class PasskeyTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;
    private const string AccountOptions = "/api/v1/account/passkeys/options", AccountPasskeys = "/api/v1/account/passkeys";
    private const string SignInOptions = "/api/v1/auth/passkey/options", SignInPath = "/api/v1/auth/passkey/sign-in";
    private const string ReauthOptions = "/api/v1/auth/reauthenticate/passkey/options", ReauthPath = "/api/v1/auth/reauthenticate/passkey";

    private static Dictionary<string, string?> Open() => new() { ["Auth:SignInIpFailuresPerHour"] = "100000", ["RateLimits:AuthPerMinute"] = "100000" };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<(Guid ChallengeId, JsonElement Options)> Opts(AuthKit k, string path, string? token)
    {
        var r = await k.Send(Post, path, null, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        return (j.GetProperty("challengeId").GetGuid(), j.GetProperty("options"));
    }

    private static async Task<HttpResponseMessage> Register(AuthKit k, string token, VirtualAuthenticator a, string? name = "Laptop")
    {
        var (cid, opts) = await Opts(k, AccountOptions, token);
        return await k.Send(Post, AccountPasskeys, new { challengeId = cid, name, credential = a.Register(opts) }, token);
    }

    private static async Task<(VirtualAuthenticator Authn, Guid Id)> AddPasskey(AuthKit k, string token, string name = "Laptop", VirtualAuthenticator? authn = null)
    {
        authn ??= new VirtualAuthenticator();
        var r = await Register(k, token, authn, name);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (authn, (await Json(r)).GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> SignInWith(AuthKit k, VirtualAuthenticator a)
    {
        var (cid, opts) = await Opts(k, SignInOptions, null);
        return await k.Post(SignInPath, new { challengeId = cid, credential = a.Assert(opts) });
    }

    private static async Task<string> TokenOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;

    [Fact]
    public async Task A_passkey_can_be_registered_listed_and_used_to_sign_in()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, id) = await AddPasskey(k, token, "Work laptop");
        var list = await Json(await k.Send(Get, AccountPasskeys, null, token));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(("Work laptop", id), (list[0].GetProperty("name").GetString(), list[0].GetProperty("id").GetGuid()));
        var signIn = await SignInWith(k, authn);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var passkeyToken = await TokenOf(signIn);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", passkeyToken)).StatusCode);
    }

    private static async Task<string> SecondSession(AuthKit k, string email = Email, string password = Pw)
        => await TokenOf(await k.Post("/api/v1/auth/sign-in", new { email, password }));

    private static async Task<JsonElement> Problem(HttpResponseMessage r) => await Json(r);

    // ---------- registration: recency, options, rules ----------

    [Fact]
    public async Task Adding_a_passkey_needs_a_recent_sign_in_and_a_step_up_restores_it()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        k.F.Clock.Advance(TimeSpan.FromMinutes(20));
        var r = await k.Send(Post, AccountOptions, null, token);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("reauthentication_required", (await Problem(r)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reauthenticate", new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Send(Post, AccountOptions, null, token)).StatusCode);
    }

    [Fact]
    public async Task The_creation_options_name_the_site_demand_a_resident_verified_passkey_and_exclude_the_ones_already_there()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var account = await k.Account(Email);
        var (_, first) = await Opts(k, AccountOptions, token);
        Assert.Equal(("localhost", "Solvelt"), (first.GetProperty("rp").GetProperty("id").GetString(), first.GetProperty("rp").GetProperty("name").GetString()));
        Assert.Equal(account.Id.ToByteArray(), VirtualAuthenticator.FromB64(first.GetProperty("user").GetProperty("id").GetString()!));      // the handle is the account id: an opaque random id
        Assert.Equal("required", first.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("required", first.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
        Assert.Equal("none", first.GetProperty("attestation").GetString());
        Assert.True(VirtualAuthenticator.FromB64(first.GetProperty("challenge").GetString()!).Length >= 32);
        Assert.Equal(0, first.GetProperty("excludeCredentials").GetArrayLength());
        var (authn, _) = await AddPasskey(k, token);
        var (_, second) = await Opts(k, AccountOptions, token);
        Assert.Equal(authn.CredentialId, VirtualAuthenticator.FromB64(second.GetProperty("excludeCredentials")[0].GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task What_is_stored_is_the_public_key_and_the_list_shows_no_key_material()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token, "Phone");
        var row = await k.Db(db => db.BuyerPasskeys.AsNoTracking().SingleAsync());
        Assert.Equal(authn.CredentialId, row.CredentialId);
        Assert.NotEmpty(row.PublicKey);
        Assert.Equal(("Phone", authn.Counter), (row.Name, (uint)row.SignCount));
        var text = await (await k.Send(Get, AccountPasskeys, null, token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(VirtualAuthenticator.B64(authn.CredentialId), text);
        Assert.DoesNotContain("publicKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "backedUp", "createdAt", "id", "lastUsedAt", "name" }, JsonDocument.Parse(text).RootElement[0].EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [Fact]
    public async Task A_registration_challenge_works_once_only()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (cid, opts) = await Opts(k, AccountOptions, token);
        var authn = new VirtualAuthenticator();
        var body = new { challengeId = cid, name = "Laptop", credential = authn.Register(opts) };
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, AccountPasskeys, body, token)).StatusCode);
        var again = await k.Send(Post, AccountPasskeys, body, token);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_challenge", (await Problem(again)).GetProperty("code").GetString());
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task Two_parallel_requests_with_one_challenge_register_one_passkey()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (cid, opts) = await Opts(k, AccountOptions, token);
        var body = new { challengeId = cid, name = "Laptop", credential = new VirtualAuthenticator().Register(opts) };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => k.Send(Post, AccountPasskeys, body, token)));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest }, results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task A_challenge_is_dead_after_five_minutes()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (cid, opts) = await Opts(k, AccountOptions, token);
        var credential = new VirtualAuthenticator().Register(opts);
        k.F.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Send(Post, AccountPasskeys, new { challengeId = cid, name = "x", credential }, token)).StatusCode);
        Assert.Equal(0, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task A_challenge_cannot_be_used_from_another_session_or_another_account()
    {
        using var k = new AuthKit(Open());
        var one = await k.VerifiedSession(Email);
        var oneElsewhere = await SecondSession(k);
        var two = await k.VerifiedSession("other@example.com");
        var (cid, opts) = await Opts(k, AccountOptions, one);
        var credential = new VirtualAuthenticator().Register(opts);
        foreach (var thief in new[] { oneElsewhere, two })
        {
            var r = await k.Send(Post, AccountPasskeys, new { challengeId = cid, name = "x", credential }, thief);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_challenge", (await Problem(r)).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, AccountPasskeys, new { challengeId = cid, name = "x", credential }, one)).StatusCode);   // still the owner's, untouched by the attempts
    }

    [Fact]
    public async Task A_challenge_made_for_one_purpose_is_refused_for_another()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var (signInChallenge, signInOptions) = await Opts(k, SignInOptions, null);
        var (reauthChallenge, reauthOptions) = await Opts(k, ReauthOptions, token);
        var (registerChallenge, registerOptions) = await Opts(k, AccountOptions, token);
        // a sign-in challenge at registration, a registration challenge and a step-up challenge at sign-in, a sign-in challenge at step-up
        var (_, forgedRegistration) = await Opts(k, AccountOptions, token);
        var atRegistration = await k.Send(Post, AccountPasskeys, new { challengeId = signInChallenge, name = "x", credential = new VirtualAuthenticator().Register(forgedRegistration) }, token);
        Assert.Equal(HttpStatusCode.BadRequest, atRegistration.StatusCode);
        Assert.Equal("invalid_challenge", (await Problem(atRegistration)).GetProperty("code").GetString());   // refused for its purpose, not for what was inside
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, new { challengeId = registerChallenge, credential = authn.Assert(registerOptions) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, new { challengeId = reauthChallenge, credential = authn.Assert(reauthOptions) })).StatusCode);
        // a step-up challenge at registration and a registration challenge at step-up: the same account and session, so only the purpose tells them apart
        var (reauthChallenge2, _) = await Opts(k, ReauthOptions, token);
        var (registerChallenge2, registerOptions2) = await Opts(k, AccountOptions, token);
        var reauthAtRegistration = await k.Send(Post, AccountPasskeys, new { challengeId = reauthChallenge2, name = "x", credential = new VirtualAuthenticator().Register(registerOptions2) }, token);
        Assert.Equal("invalid_challenge", (await Problem(reauthAtRegistration)).GetProperty("code").GetString());
        var (_, reauthOptions3) = await Opts(k, ReauthOptions, token);
        var registerAtReauth = await k.Send(Post, ReauthPath, new { challengeId = registerChallenge2, credential = authn.Assert(reauthOptions3) }, token);
        Assert.Equal("invalid_challenge", (await Problem(registerAtReauth)).GetProperty("code").GetString());
        var (freshSignIn, freshOptions) = await Opts(k, SignInOptions, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await k.Send(Post, ReauthPath, new { challengeId = freshSignIn, credential = authn.Assert(freshOptions) }, token)).StatusCode);
    }

    [Fact]
    public async Task A_missing_or_unknown_challenge_is_an_invalid_challenge()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (_, opts) = await Opts(k, AccountOptions, token);
        var credential = new VirtualAuthenticator().Register(opts);
        foreach (var body in new object[] { new { name = "x", credential }, new { challengeId = Guid.NewGuid(), name = "x", credential } })
        {
            var r = await k.Send(Post, AccountPasskeys, body, token);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_challenge", (await Problem(r)).GetProperty("code").GetString());
        }
    }

    // ---------- registration: the response must really come from this site's ceremony ----------

    public static IEnumerable<object[]> BadRegistrations() =>
    [
        ["wrong origin", (Action<VirtualAuthenticator>)(a => a.Origin = "https://evil.example")],
        ["wrong relying party", (Action<VirtualAuthenticator>)(a => a.RpId = "evil.example")],
        ["wrong challenge", (Action<VirtualAuthenticator>)(a => a.ChallengeOverride = VirtualAuthenticator.B64(new byte[32]))],
        ["user not verified", (Action<VirtualAuthenticator>)(a => a.UserVerified = false)],
        ["assertion ceremony instead", (Action<VirtualAuthenticator>)(a => a.ClientDataType = "webauthn.get")],
    ];

    [Theory]
    [MemberData(nameof(BadRegistrations))]
    public async Task A_registration_that_does_not_verify_is_refused_and_stores_nothing(string why, Action<VirtualAuthenticator> spoil)
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var authn = new VirtualAuthenticator();
        spoil(authn);
        var r = await Register(k, token, authn);
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, why);
        Assert.Equal("invalid_credential", (await Problem(r)).GetProperty("code").GetString());
        Assert.Equal(0, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task Garbage_where_a_credential_should_be_is_refused_and_never_a_server_error()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        foreach (var credential in new object?[]
        {
            new { foo = 1 }, "just a string", 42, new { id = "x", rawId = "x", type = "public-key", response = new { attestationObject = "AAAA", clientDataJSON = "AAAA" } },
            new { id = "", rawId = "", type = "public-key", response = new { attestationObject = "", clientDataJSON = "" } }, null,
        })
        {
            var (cid, _) = await Opts(k, AccountOptions, token);
            var r = await k.Send(Post, AccountPasskeys, new { challengeId = cid, name = "x", credential }, token);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(0, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task The_same_authenticator_cannot_be_registered_twice_by_anyone()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var other = await k.VerifiedSession("other@example.com");
        var (authn, _) = await AddPasskey(k, token);
        foreach (var t in new[] { token, other })
        {
            var r = await Register(k, t, authn);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal("already_registered", (await Problem(r)).GetProperty("code").GetString());
        }
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task A_buyer_keeps_at_most_the_configured_number_of_passkeys()
    {
        var config = Open(); config["Auth:MaxPasskeys"] = "2";
        using var k = new AuthKit(config);
        var token = await k.VerifiedSession(Email);
        await AddPasskey(k, token); await AddPasskey(k, token);
        var r = await k.Send(Post, AccountOptions, null, token);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("too_many_passkeys", (await Problem(r)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Jane\u200BPhone")]
    public async Task A_passkey_name_must_be_a_real_name(string name)
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var r = await Register(k, token, new VirtualAuthenticator(), name.Length == 0 ? new string('x', 61) : name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task A_registration_without_a_name_is_called_passkey()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var r = await Register(k, token, new VirtualAuthenticator(), null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("Passkey", (await Json(r)).GetProperty("name").GetString());
    }

    // ---------- sign-in ----------

    [Fact]
    public async Task Signing_in_with_a_passkey_opens_a_session_of_the_accounts_current_epoch_and_remembers_the_use()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, id) = await AddPasskey(k, token);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionEpoch, 7L)); return 0; });
        var r = await SignInWith(k, authn);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal((await k.Account(Email)).Id, j.GetProperty("user").GetProperty("id").GetGuid());
        var session = await TokenOf(r);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", session)).StatusCode);
        Assert.Equal(7L, (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync(s => s.TokenHash == Tokens.Hash(session)))).Epoch);
        var row = await k.Db(db => db.BuyerPasskeys.AsNoTracking().SingleAsync(p => p.Id == id));
        Assert.Equal((long)authn.Counter, row.SignCount);
        Assert.Equal(k.F.Clock.GetUtcNow(), row.LastUsedAt);
        Assert.Equal(HttpStatusCode.OK, (await SignInWith(k, authn)).StatusCode);                 // and again: the counter went up
    }

    [Fact]
    public async Task The_sign_in_options_name_no_account_and_demand_user_verification()
    {
        using var k = new AuthKit(Open());
        var (_, opts) = await Opts(k, SignInOptions, null);
        Assert.Equal("required", opts.GetProperty("userVerification").GetString());
        Assert.Equal("localhost", opts.GetProperty("rpId").GetString());
        Assert.True(!opts.TryGetProperty("allowCredentials", out var allow) || allow.GetArrayLength() == 0);
        Assert.True(VirtualAuthenticator.FromB64(opts.GetProperty("challenge").GetString()!).Length >= 32);
    }

    public static IEnumerable<object[]> BadAssertions() =>
    [
        ["wrong origin", (Action<VirtualAuthenticator>)(a => a.Origin = "https://evil.example")],
        ["wrong relying party", (Action<VirtualAuthenticator>)(a => a.RpId = "evil.example")],
        ["wrong challenge", (Action<VirtualAuthenticator>)(a => a.ChallengeOverride = VirtualAuthenticator.B64(new byte[32]))],
        ["user not verified", (Action<VirtualAuthenticator>)(a => a.UserVerified = false)],
        ["signed with another key", (Action<VirtualAuthenticator>)(a => a.Key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))],
        ["another buyer's user handle", (Action<VirtualAuthenticator>)(a => a.UserHandle = Guid.NewGuid().ToByteArray())],
        ["registration ceremony instead", (Action<VirtualAuthenticator>)(a => a.ClientDataType = "webauthn.create")],
    ];

    [Theory]
    [MemberData(nameof(BadAssertions))]
    public async Task A_sign_in_that_does_not_verify_is_refused_with_the_same_answer_every_time(string why, Action<VirtualAuthenticator> spoil)
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var refusal = await SignInWith(k, new VirtualAuthenticator { CredentialId = [1, 2, 3] });                 // an unknown credential: the reference answer
        Assert.Equal(HttpStatusCode.Unauthorized, refusal.StatusCode);
        var reference = await refusal.Content.ReadAsStringAsync();
        spoil(authn);
        var r = await SignInWith(k, authn);
        Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, why);
        Assert.Equal(reference, await r.Content.ReadAsStringAsync());
        Assert.Equal(0, await k.Db(db => db.BuyerSessions.CountAsync(s => s.AuthenticatedAt > k.F.Clock.GetUtcNow().AddMinutes(-1) && s.TokenHash != "")) - 1);   // only the sign-up session exists
    }

    [Fact]
    public async Task A_replayed_assertion_and_a_stale_counter_are_refused()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var (cid, opts) = await Opts(k, SignInOptions, null);
        var credential = authn.Assert(opts);
        var body = new { challengeId = cid, credential };
        Assert.Equal(HttpStatusCode.OK, (await k.Post(SignInPath, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, body)).StatusCode);                  // the challenge is spent
        // a clone that kept an old counter: a fresh challenge, but the counter does not go up
        var (cid2, opts2) = await Opts(k, SignInOptions, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, new { challengeId = cid2, credential = authn.Assert(opts2, bumpCounter: false) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInWith(k, authn)).StatusCode);                                // the real one still works
    }

    [Fact]
    public async Task A_sign_in_challenge_is_dead_after_five_minutes_and_garbage_is_not_a_server_error()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var (cid, opts) = await Opts(k, SignInOptions, null);
        var credential = authn.Assert(opts);
        k.F.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, new { challengeId = cid, credential })).StatusCode);
        foreach (var body in new object[] { new { }, new { challengeId = Guid.NewGuid(), credential }, new { challengeId = cid, credential = "x" }, new { challengeId = cid, credential = new { id = "a" } } })
            Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post(SignInPath, body)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_or_unverified_account_cannot_sign_in_with_its_passkey()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, (DateTimeOffset?)null)); return 0; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInWith(k, authn)).StatusCode);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailVerifiedAt, k.F.Clock.GetUtcNow()).SetProperty(a => a.DeletedAt, k.F.Clock.GetUtcNow())); return 0; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInWith(k, authn)).StatusCode);
    }

    // ---------- step-up ----------

    [Fact]
    public async Task A_passkey_proves_who_the_buyer_is_for_a_sensitive_change()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        Assert.Equal("no_passkeys", (await Problem(await k.Send(Post, ReauthOptions, null, token))).GetProperty("code").GetString());
        var (authn, _) = await AddPasskey(k, token);
        k.F.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Forbidden, (await k.Send(Post, AccountOptions, null, token)).StatusCode);
        var (cid, opts) = await Opts(k, ReauthOptions, token);
        Assert.Equal(authn.CredentialId, VirtualAuthenticator.FromB64(opts.GetProperty("allowCredentials")[0].GetProperty("id").GetString()!));   // only this buyer's own passkeys
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, ReauthPath, new { challengeId = cid, credential = authn.Assert(opts) }, token)).StatusCode);
        Assert.Equal(k.F.Clock.GetUtcNow(), (await k.Db(db => db.BuyerSessions.AsNoTracking().SingleAsync())).AuthenticatedAt);
        Assert.Equal(HttpStatusCode.OK, (await k.Send(Post, AccountOptions, null, token)).StatusCode);
    }

    [Fact]
    public async Task A_step_up_challenge_belongs_to_the_session_that_asked_and_only_the_buyers_own_passkey_counts()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var elsewhere = await SecondSession(k);
        var otherToken = await k.VerifiedSession("other@example.com");
        var (mine, _) = await AddPasskey(k, token);
        var (theirs, _) = await AddPasskey(k, otherToken);
        var (cid, opts) = await Opts(k, ReauthOptions, token);
        Assert.Equal(1, opts.GetProperty("allowCredentials").GetArrayLength());                            // somebody else's passkey is not offered
        Assert.Equal(mine.CredentialId, VirtualAuthenticator.FromB64(opts.GetProperty("allowCredentials")[0].GetProperty("id").GetString()!));
        var credential = mine.Assert(opts);
        var stolen = await k.Send(Post, ReauthPath, new { challengeId = cid, credential }, elsewhere);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Equal("invalid_challenge", (await Problem(stolen)).GetProperty("code").GetString());
        var (cid2, opts2) = await Opts(k, ReauthOptions, token);
        var foreign = await k.Send(Post, ReauthPath, new { challengeId = cid2, credential = theirs.Assert(opts2) }, token);   // somebody else's passkey, even a genuine one
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("invalid_credential", (await Problem(foreign)).GetProperty("code").GetString());
    }

    // ---------- rename, remove, the last way in ----------

    [Fact]
    public async Task A_passkey_can_be_renamed_by_its_owner_only()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var other = await k.VerifiedSession("other@example.com");
        var (_, id) = await AddPasskey(k, token, "Old");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Patch, $"{AccountPasskeys}/{id}", new { name = "  New name " }, token)).StatusCode);
        Assert.Equal("New name", (await Json(await k.Send(Get, AccountPasskeys, null, token)))[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await k.Send(Patch, $"{AccountPasskeys}/{id}", new { name = "Mine now" }, other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await k.Send(Patch, $"{AccountPasskeys}/{Guid.NewGuid()}", new { name = "x" }, token)).StatusCode);
        foreach (var bad in new object?[] { new { name = "" }, new { name = new string('x', 61) }, new { name = "a\u202Eb" }, new { } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(Patch, $"{AccountPasskeys}/{id}", bad, token)).StatusCode);
        Assert.Equal("New name", (await k.Db(db => db.BuyerPasskeys.AsNoTracking().SingleAsync())).Name);
    }

    [Fact]
    public async Task Removing_a_passkey_needs_a_recent_sign_in_and_only_the_owner_can_do_it()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var other = await k.VerifiedSession("other@example.com");
        var (authn, id) = await AddPasskey(k, token);
        Assert.Equal(HttpStatusCode.NotFound, (await k.Send(Delete, $"{AccountPasskeys}/{id}", null, other)).StatusCode);
        k.F.Clock.Advance(TimeSpan.FromMinutes(20));
        var late = await k.Send(Delete, $"{AccountPasskeys}/{id}", null, token);
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.Equal("reauthentication_required", (await Problem(late)).GetProperty("code").GetString());
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
        await k.Post("/api/v1/auth/reauthenticate", new { password = Pw }, token);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"{AccountPasskeys}/{id}", null, token)).StatusCode);       // the password is still there, so this is not the last way in
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInWith(k, authn)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await k.Send(Delete, $"{AccountPasskeys}/{id}", null, token)).StatusCode);
    }

    [Fact]
    public async Task The_last_way_to_sign_in_cannot_be_removed()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (_, first) = await AddPasskey(k, token);
        var (_, second) = await AddPasskey(k, token);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, (string?)null)); return 0; });   // an account with passkeys only
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"{AccountPasskeys}/{first}", null, token)).StatusCode);
        var r = await k.Send(Delete, $"{AccountPasskeys}/{second}", null, token);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("last_sign_in_method", (await Problem(r)).GetProperty("code").GetString());
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task Two_parallel_removals_cannot_take_both_of_the_last_two_passkeys()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (_, first) = await AddPasskey(k, token);
        var (_, second) = await AddPasskey(k, token);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, (string?)null)); return 0; });
        var results = await Task.WhenAll(new[] { first, second }.Select(id => k.Send(Delete, $"{AccountPasskeys}/{id}", null, token)));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    // ---------- the owner is told, a reset recovers the account, erasure and retention ----------

    [Fact]
    public async Task The_owner_gets_a_mail_when_a_passkey_is_added_or_removed_and_the_outbox_holds_ids_only()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (_, id) = await AddPasskey(k, token, "Secret name");
        await k.Send(Delete, $"{AccountPasskeys}/{id}", null, token);
        var subjects = (await k.Mail(Email)).Select(m => m.Subject).ToList();
        Assert.Contains("A passkey was added to your account", subjects);
        Assert.Contains("A passkey was removed from your account", subjects);
        var payloads = string.Concat((await k.Db(db => db.OutboxMessages.AsNoTracking().ToListAsync())).Select(m => m.Payload));
        Assert.DoesNotContain("Secret name", payloads);
        Assert.DoesNotContain("@", payloads);
    }

    [Fact]
    public async Task A_password_reset_removes_every_passkey_so_nothing_that_got_in_survives_the_recovery()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var link = AuthKit.TokenIn((await k.Mail(Email)).Last(m => m.Subject == "Reset your password"), "/reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/reset-password", new { token = link, password = "a brand new password" })).StatusCode);
        Assert.Equal(0, await k.Db(db => db.BuyerPasskeys.CountAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInWith(k, authn)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "a brand new password" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_erases_its_passkeys_and_challenges()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        await AddPasskey(k, token);
        await Opts(k, AccountOptions, token);                                                             // an open challenge too
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);
        while (await k.Dispatch() > 0) { }
        Assert.Equal((0, 0), (await k.Db(db => db.BuyerPasskeys.CountAsync()), await k.Db(db => db.WebAuthnChallenges.CountAsync(c => c.AccountId != null))));
    }

    [Fact]
    public async Task Challenges_are_purged_an_hour_after_they_expire()
    {
        using var k = new AuthKit(Open());
        await Opts(k, SignInOptions, null);
        async Task<AuthMaintenance.Result> Purge() { using var s = k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<AuthMaintenance>().PurgeAsync(); }
        k.F.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromMinutes(59));
        Assert.Equal(0, (await Purge()).Challenges);
        Assert.Equal(1, await k.Db(db => db.WebAuthnChallenges.CountAsync()));
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await Purge()).Challenges);
        Assert.Equal(0, await k.Db(db => db.WebAuthnChallenges.CountAsync()));
    }

    [Fact]
    public async Task Every_passkey_answer_is_private_and_not_stored_refusals_included()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession(Email);
        var (authn, id) = await AddPasskey(k, token);
        var cases = new (HttpMethod, string, object?, string?)[]
        {
            (Get, AccountPasskeys, null, token), (Post, AccountOptions, null, token), (Post, AccountPasskeys, new { }, token), (Patch, $"{AccountPasskeys}/{id}", new { name = "x" }, token),
            (Post, SignInOptions, null, null), (Post, SignInPath, new { }, null), (Post, ReauthOptions, null, token), (Post, ReauthPath, new { }, token), (Get, AccountPasskeys, null, null),
        };
        foreach (var (m, path, body, t) in cases)
        {
            var r = await k.Send(m, path, body, t);
            Assert.True(r.Headers.CacheControl is { NoStore: true, Private: true }, $"{m} {path} -> {(int)r.StatusCode} Cache-Control: {r.Headers.CacheControl}");
        }
    }

    // ---------- configuration ----------

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IReadOnlyList<string> Problems(Dictionary<string, string?> values, string environment)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return StartupValidator.WebAuthnProblems(config, new Env(environment));
    }

    [Fact]
    public void Production_needs_the_relying_party_and_https_origins_on_it_and_development_needs_nothing()
    {
        Assert.Empty(Problems(new(), "Development"));
        Assert.Empty(Problems(new(), "Testing"));
        Assert.Equal(2, Problems(new(), "Production").Count);                                              // both are required
        var good = new Dictionary<string, string?> { ["Auth:WebAuthnRpId"] = "solvelt.com", ["Auth:WebAuthnOrigins:0"] = "https://www.solvelt.com", ["Auth:WebAuthnOrigins:1"] = "https://solvelt.com" };
        Assert.Empty(Problems(good, "Production"));
        foreach (var (key, value) in new[]
        {
            ("Auth:WebAuthnRpId", "https://solvelt.com"), ("Auth:WebAuthnRpId", "solvelt.com:443"), ("Auth:WebAuthnRpId", "Solvelt.com"), ("Auth:WebAuthnRpId", "192.0.2.1"), ("Auth:WebAuthnRpId", "localhost"),
            ("Auth:WebAuthnOrigins:0", "http://www.solvelt.com"), ("Auth:WebAuthnOrigins:0", "https://www.solvelt.com/"), ("Auth:WebAuthnOrigins:0", "https://www.solvelt.com/app"),
            ("Auth:WebAuthnOrigins:0", "https://evil.example"), ("Auth:WebAuthnOrigins:0", "https://notsolvelt.com"), ("Auth:WebAuthnOrigins:0", "www.solvelt.com"), ("Auth:WebAuthnOrigins:0", "https://user@www.solvelt.com"),
        })
        {
            var bad = new Dictionary<string, string?>(good) { [key] = value };
            Assert.NotEmpty(Problems(bad, "Production"));
        }
        // what is set is checked in development too
        Assert.NotEmpty(Problems(new() { ["Auth:WebAuthnOrigins:0"] = "https://evil.example", ["Auth:WebAuthnRpId"] = "solvelt.com" }, "Development"));
        Assert.Empty(Problems(new() { ["Auth:WebAuthnOrigins:0"] = "http://localhost:3000" }, "Development"));
    }

    [Fact]
    public void Start_up_runs_the_passkey_checks_along_with_the_shared_validation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Assert.Contains(StartupValidator.AllProblems(config, new Env("Production")), p => p.Contains("Auth:WebAuthnRpId"));      // what Program.cs refuses to start with
        Assert.DoesNotContain(StartupValidator.AllProblems(config, new Env("Development")), p => p.Contains("WebAuthn"));
    }

    [Fact]
    public async Task The_relying_party_comes_from_configuration_and_defaults_to_localhost_in_tests()
    {
        var config = Open(); config["Auth:WebAuthnRpId"] = "solvelt.test"; config["Auth:WebAuthnOrigins:0"] = "https://www.solvelt.test"; config["Auth:WebAuthnRpName"] = "Solvelt Test";
        using var k = new AuthKit(config);
        var token = await k.VerifiedSession(Email);
        var (_, opts) = await Opts(k, AccountOptions, token);
        Assert.Equal(("solvelt.test", "Solvelt Test"), (opts.GetProperty("rp").GetProperty("id").GetString(), opts.GetProperty("rp").GetProperty("name").GetString()));
        var authn = new VirtualAuthenticator { RpId = "solvelt.test", Origin = "https://www.solvelt.test" };
        Assert.Equal(HttpStatusCode.Created, (await Register(k, token, authn)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(k, token, new VirtualAuthenticator())).StatusCode);   // the default localhost origin is not accepted here
    }

    // ---------- races ----------

    /// <summary>Runs a callback once, right before the first statement that matches, then stays out of the way (the callback's own statements are not intercepted).</summary>
    private sealed class BeforeStatement : DbCommandInterceptor
    {
        public Func<string, bool>? When;
        public Func<Task>? Then;
        private bool _busy;
        private async Task Fire(string sql)
        {
            if (_busy || When is not { } w || Then is not { } go || !w(sql)) return;
            When = null; _busy = true;
            try { await go(); } finally { _busy = false; }
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Fire(command.CommandText); return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await Fire(command.CommandText); return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }
    }

    [Fact]
    public async Task Two_valid_assertions_that_read_the_same_counter_cannot_both_sign_in()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var (cid1, opts1) = await Opts(k, SignInOptions, null);
        var (cid2, opts2) = await Opts(k, SignInOptions, null);
        var first = authn.Assert(opts1);                          // counter 1
        authn.Counter = 0;
        var second = authn.Assert(opts2);                         // counter 1 again: a clone, or a request that raced
        HttpStatusCode inner = default;
        // while the first request is about to write the new counter, the second runs through completely
        hook.When = sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && sql.Contains("buyer_passkeys");
        hook.Then = async () => inner = (await k.Post(SignInPath, new { challengeId = cid2, credential = second })).StatusCode;
        var outer = await k.Post(SignInPath, new { challengeId = cid1, credential = first });
        Assert.Null(hook.When);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized }, new[] { inner, outer.StatusCode }.Order().ToArray());   // exactly one gets in
        Assert.Equal(1L, (await k.Db(db => db.BuyerPasskeys.AsNoTracking().SingleAsync())).SignCount);
    }

    [Fact]
    public async Task The_cap_holds_when_two_registrations_were_started_before_either_finished()
    {
        var config = Open(); config["Auth:MaxPasskeys"] = "2";
        using var k = new AuthKit(config);
        var token = await k.VerifiedSession(Email);
        await AddPasskey(k, token);                                                                          // one of two
        var (cidA, optsA) = await Opts(k, AccountOptions, token);
        var (cidB, optsB) = await Opts(k, AccountOptions, token);
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, AccountPasskeys, new { challengeId = cidA, name = "A", credential = new VirtualAuthenticator().Register(optsA) }, token)).StatusCode);
        var r = await k.Send(Post, AccountPasskeys, new { challengeId = cidB, name = "B", credential = new VirtualAuthenticator().Register(optsB) }, token);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("too_many_passkeys", (await Problem(r)).GetProperty("code").GetString());
        Assert.Equal(2, await k.Db(db => db.BuyerPasskeys.CountAsync()));
    }

    [Fact]
    public async Task A_session_opened_by_a_passkey_does_not_survive_a_sign_out_everywhere_that_lands_during_the_sign_in()
    {
        var hook = new BeforeStatement();
        using var k = new AuthKit(Open(), s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(hook)));
        var token = await k.VerifiedSession(Email);
        var (authn, _) = await AddPasskey(k, token);
        var (cid, opts) = await Opts(k, SignInOptions, null);
        var credential = authn.Assert(opts);
        // the epoch moves (sign-out-all, a reset) after the account was read and before the session row is written
        // either the separate epoch read an older build made (a query of the epoch alone) or, in the fixed code, just before the session row is written
        hook.When = sql => (sql.Contains("SessionEpoch") && !sql.Contains("PasswordHash") && sql.Contains("buyer_accounts") && !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                           || (sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && sql.Contains("buyer_sessions"));
        hook.Then = async () => await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(a => a.SetProperty(x => x.SessionEpoch, x => x.SessionEpoch + 1)); return 0; });
        var r = await k.Post(SignInPath, new { challengeId = cid, credential });
        Assert.Null(hook.When);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", await TokenOf(r))).StatusCode);
    }
}
