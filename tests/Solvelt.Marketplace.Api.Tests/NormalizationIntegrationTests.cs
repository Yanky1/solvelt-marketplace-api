using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Where the normaliser is used (decision M1-81): emails, passwords, names and address lines are compared and stored in Unicode normal form C.</summary>
public class NormalizationIntegrationTests
{
    // "josé" typed with the accent as a combining mark (NFD) and with the precomposed letter (NFC)
    private const string EmailNfd = "josé@example.com", EmailNfc = "josé@example.com";
    // "über-Pässwörd-2026" in both forms
    private static readonly string PwNfc = "über-Pässwörd-2026", PwNfd = "über-Pässvörd-2026".Replace("ssvo", "ssw" + "o");
    private static Dictionary<string, string?> Open() => new() { ["Auth:SignInIpFailuresPerHour"] = "100000" };

    [Fact]
    public void The_two_spellings_of_the_test_password_really_differ_and_normalise_to_one()
    {
        Assert.NotEqual(PwNfc, PwNfd);
        Assert.Equal(PwNfc, Solvelt.Marketplace.Api.Text.UnicodeNormalizer.Nfc(PwNfd));
    }

    [Fact]
    public void An_email_is_trimmed_composed_and_lower_cased_in_one_form()
    {
        Assert.Equal(EmailNfc, AuthValidation.NormaliseEmail("  José@EXAMPLE.com "));
        Assert.Equal(EmailNfc, AuthValidation.NormaliseEmail(EmailNfc.ToUpperInvariant().Replace("É", "É")));
        Assert.Equal(AuthValidation.NormaliseEmail(EmailNfd), AuthValidation.NormaliseEmail(EmailNfc));
        Assert.Equal("jane@example.com", AuthValidation.NormaliseEmail(" Jane@Example.COM "));
    }

    [Fact]
    public async Task One_address_typed_two_ways_is_one_account()
    {
        using var k = new AuthKit(Open());
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(EmailNfd)).StatusCode);
        var verify = AuthKit.TokenIn((await k.Mail(EmailNfc)).Single(), "/verify-email");                     // the mail went to the one stored form
        Assert.Equal(HttpStatusCode.NoContent, (await k.Post("/api/v1/auth/verify-email", new { token = verify, password = AuthKit.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(EmailNfc)).StatusCode);                       // the "other" spelling: the same, existing address
        Assert.Equal(1, await k.Db(db => db.BuyerAccounts.CountAsync()));
        Assert.Equal(EmailNfc, (await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync())).Email);
        foreach (var typed in new[] { EmailNfd, EmailNfc, EmailNfd.ToUpperInvariant().Replace("́", "́") })
            Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = typed, password = AuthKit.Password })).StatusCode);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_password_works_in_either_spelling_whichever_was_used_to_set_it(bool setNfc, bool signInNfc)
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession("jane@example.com", setNfc ? PwNfc : PwNfd);
        var r = await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = signInNfc ? PwNfc : PwNfd });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = PwNfc + "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = PwNfd + "x" })).StatusCode);   // a wrong password is wrong in every spelling
        // what is stored is the hash of the normal form
        var hash = (await k.Account("jane@example.com")).PasswordHash;
        var plain = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        Assert.NotEqual(PasswordVerificationResult.Failed, plain.VerifyHashedPassword(new object(), hash!, PwNfc));
        Assert.Equal(PasswordVerificationResult.Failed, plain.VerifyHashedPassword(new object(), hash!, PwNfd));
    }

    [Fact]
    public async Task A_hash_made_before_normalising_from_the_text_as_typed_still_works_and_is_upgraded()
    {
        using var k = new AuthKit(Open());
        await k.VerifiedSession("jane@example.com");
        // the old way: the hash of the password exactly as it was typed, here the decomposed spelling
        var old = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })).HashPassword(new object(), PwNfd);
        await k.Db(async db => { await db.BuyerAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, old)); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = PwNfd })).StatusCode);   // typed the same way: works
        var upgraded = (await k.Account("jane@example.com")).PasswordHash;
        Assert.NotEqual(old, upgraded);                                                                       // and is now the hash of the normal form
        var plain = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        Assert.NotEqual(PasswordVerificationResult.Failed, plain.VerifyHashedPassword(new object(), upgraded!, PwNfc));
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = PwNfc })).StatusCode);   // the other spelling now works as well
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = "jane@example.com", password = PwNfd })).StatusCode);
    }

    [Fact]
    public async Task A_password_and_its_checks_count_the_normal_form()
    {
        using var k = new AuthKit(Open());
        // thirteen code units, eight characters once the accents are composed: too short (10 needed)
        var short8 = string.Concat(Enumerable.Repeat("ü", 5)) + "abc";
        var r = await k.SignUp("jane@example.com", short8);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        // the same text as the email address in the other spelling is still "your email address"
        var asPassword = await k.SignUp(EmailNfc, EmailNfd);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asPassword.StatusCode);
    }

    [Fact]
    public async Task Names_and_address_lines_are_stored_in_normal_form_and_their_length_is_the_stored_length()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession("jane@example.com", name: "José Müller");
        Assert.Equal("José Müller", (await k.Account("jane@example.com")).DisplayName);
        var profile = await k.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "Zoë", phone = (string?)null }, token);
        Assert.Equal("Zoë", (await profile.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("displayName").GetString());
        var address = await k.Send(HttpMethod.Post, "/api/v1/account/addresses", new { fullName = "José", line1 = "Rue de l'Église 1", city = "Géneve", postalCode = "1204", country = "CH" }, token);
        Assert.Equal(HttpStatusCode.Created, address.StatusCode);
        var row = await k.Db(db => db.BuyerAddresses.AsNoTracking().SingleAsync());
        Assert.Equal(("Jos\u00E9", "Rue de l'\u00C9glise 1", "G\u00E9neve"), (row.FullName, row.Line1, row.City));
        // U+0344 becomes TWO characters when composed (and the first ones merge into the letter): 32 typed characters are 62 stored ones (limit 60), 53 typed are 104 stored (limit 100 for a recipient)
        var tooLongName = string.Concat(Enumerable.Repeat("̈́", 31));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "A" + tooLongName, phone = (string?)null }, token)).StatusCode);
        var tooLongRecipient = "A" + string.Concat(Enumerable.Repeat("̈́", 52));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(HttpMethod.Post, "/api/v1/account/addresses", new { fullName = tooLongRecipient, line1 = "1 Main St", city = "New York", region = "NY", postalCode = "10001", country = "US" }, token)).StatusCode);
        Assert.Equal(1, await k.Db(db => db.BuyerAddresses.CountAsync()));
    }

    [Fact]
    public async Task A_passkey_name_is_stored_in_normal_form()
    {
        using var k = new AuthKit(Open());
        var token = await k.VerifiedSession("jane@example.com");
        var authn = new VirtualAuthenticator();
        var options = await k.Send(HttpMethod.Post, "/api/v1/account/passkeys/options", null, token);
        var j = await options.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var r = await k.Send(HttpMethod.Post, "/api/v1/account/passkeys", new { challengeId = j.GetProperty("challengeId").GetGuid(), name = "Café Mac Pro".Replace("é", "é"), credential = authn.Register(j.GetProperty("options")) }, token);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("Café Mac Pro", (await k.Db(db => db.BuyerPasskeys.AsNoTracking().SingleAsync())).Name);
    }

    [Fact]
    public async Task An_email_address_is_measured_in_the_form_that_is_stored()
    {
        using var k = new AuthKit(Open());
        // 130 typed characters of the local part, 258 stored ones (U+0344 is two marks when composed, the first merges into the letter): over the 254 the column holds
        var grows = "a" + string.Concat(Enumerable.Repeat("\u0344", 128)) + "@example.com";
        Assert.True(grows.Length < 254);
        Assert.True(AuthValidation.NormaliseEmail(grows).Length > 254);
        Assert.NotNull(AuthValidation.EmailProblem(grows));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.SignUp(grows)).StatusCode);
        Assert.Equal(0, await k.Db(db => db.BuyerAccounts.CountAsync()));
    }
}
