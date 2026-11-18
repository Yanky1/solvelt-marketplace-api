using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The email address given by phone (decision V-13b, Ronald: "calls should save email and all account information, no exceptions"): saved, read back by the server, mailed a link, and only the click makes it
/// the account's verified email. Until then it is not usable for sign-in or password reset and it never joins an account that already has that address.
/// </summary>
public class VoiceEmailTests
{
    private const string Email = "jane@example.com", Spoken = "dana.levi@example.com", Number = "+15551234567";

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    private static async Task<(VoiceScenario S, Guid Jane)> AccountCall()
    {
        var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        await s.Dial(); await Verify(s);
        return (s, jane);
    }

    private static async Task<string> ReadBack(VoiceScenario s, string tool, object args)
    {
        var r = await s.Tool(tool, args);
        Assert.True(r["status"]?.GetValue<string>() == "reading_back", r.ToJsonString());
        await s.EndStreamAfterGoodbye();
        var said = (await s.AfterStream()).Element("Gather")!.Element("Say")!.Value;
        Assert.True(VoiceSpeech.IsSpeakable(said), said);
        return said;
    }

    private static async Task<string> Answer(VoiceScenario s, string digits)
    {
        var (resume, token) = await s.Type(digits);
        await s.Attach(token);
        return resume.Element("Say")!.Value;
    }

    private static Task<BuyerAccount> Account(VoiceScenario s, Guid id) => s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));

    private static async Task<string> LinkTo(VoiceScenario s, string address)
    {
        var mail = (await s.K.Mail(address)).Last(m => m.Subject.StartsWith("Confirm this email"));
        var url = Regex.Match(mail.TextBody, @"http\S+").Value;
        Assert.StartsWith(VoiceFactory.BaseUrl + PhoneEmailEndpoints.Path + "?token=", url);
        return new Uri(url).PathAndQuery;
    }

    private static async Task<string> Confirm(VoiceScenario s, string pathAndQuery)
    {
        var token = Regex.Match(pathAndQuery, "token=([^&]+)").Groups[1].Value;
        var res = await s.C.PostAsync(PhoneEmailEndpoints.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Uri.UnescapeDataString(token) }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }

    private static async Task<VoiceScenario> GuestWantingAccount()
    {
        var s = new VoiceScenario();
        await s.SyncCatalog(); await s.Dial();
        await Verify(s);
        return s;
    }

    // ---------- creating an account with an email ----------

    [Fact]
    public async Task A_new_account_saves_the_spoken_email_as_pending_and_only_the_click_makes_it_the_accounts_email()
    {
        using var s = await GuestWantingAccount();
        var said = await ReadBack(s, "create_account", new { name = "Dana Levi", email = "  Dana.Levi@Example.com " });
        Assert.Contains(VoiceSpeech.Email(Spoken), said);                                            // read back by the server, letter by letter
        Assert.Contains("only starts to count after you open the link", said);
        Assert.Empty(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));          // asking saves nothing
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("only starts to count"));          // the model never wrote the read-back
        Assert.Contains("link", await Answer(s, "1"));
        var a = Assert.Single(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        Assert.Equal((null, Spoken, null), (a.Email, a.PendingEmail, a.EmailVerifiedAt?.ToString()));  // saved, normalised, NOT the account's email
        Assert.Null(a.PasswordHash);

        var link = await LinkTo(s, Spoken);
        Assert.DoesNotContain("Dana", (await s.K.Mail(Spoken)).Single().TextBody);                // nothing the caller typed (a name) goes into a mail to a third party
        // a mail scanner opening the link (GET) confirms nothing
        Assert.Equal(HttpStatusCode.OK, (await s.C.GetAsync(link)).StatusCode);
        Assert.Equal((null, Spoken), ((await Account(s, a.Id)).Email, (await Account(s, a.Id)).PendingEmail));
        Assert.Contains("now belongs to the account", await Confirm(s, link));
        var after = await Account(s, a.Id);
        Assert.Equal((Spoken, null), (after.Email, after.PendingEmail)); Assert.NotNull(after.EmailVerifiedAt);
        Assert.Contains("not valid", await Confirm(s, link));                                      // single use
    }

    [Fact]
    public async Task Saying_no_or_a_bad_address_saves_nothing_and_a_guest_cannot_change_an_email()
    {
        using var s = await GuestWantingAccount();
        Assert.Equal("bad_email", (await s.Tool("create_account", new { name = "Dana Levi", email = "not an email" }))["error"]!.GetValue<string>());
        await ReadBack(s, "create_account", new { name = "Dana Levi", email = Spoken });
        Assert.Contains("leave it as it is", await Answer(s, "2"));
        Assert.Empty(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        Assert.Empty(await s.K.Mail(Spoken));
        Assert.Equal("need_verification", (await s.Tool("change_my_email", new { email = Spoken }))["error"]!.GetValue<string>());   // a guest has no account to change
    }

    // ---------- changing the email of an account ----------

    [Fact]
    public async Task Changing_the_email_keeps_the_old_one_until_the_new_link_is_clicked()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var said = await ReadBack(s, "change_my_email", new { email = Spoken });
            Assert.Contains(VoiceSpeech.Email(Spoken), said);
            Assert.Contains("link", await Answer(s, "1"));
            var pending = await Account(s, jane);
            Assert.Equal((Email, Spoken), (pending.Email, pending.PendingEmail));                   // the old address still signs in; the new one is only pending
            Assert.NotNull(pending.EmailVerifiedAt);
            Assert.DoesNotContain(await s.K.Mail(Email), m => m.Subject.StartsWith("Confirm this email"));   // nothing is mailed to the old address as a confirm link
            await Confirm(s, await LinkTo(s, Spoken));
            var done = await Account(s, jane);
            Assert.Equal((Spoken, null), (done.Email, done.PendingEmail));
        }
    }

    [Fact]
    public async Task A_replaced_link_is_dead_and_an_old_one_expires()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await ReadBack(s, "change_my_email", new { email = Spoken }); await Answer(s, "1");
            var first = await LinkTo(s, Spoken);
            await s.Tool("change_my_email", new { email = "other.address@example.com" });
            await s.EndStreamAfterGoodbye(); await s.AfterStream(); await Answer(s, "1");
            Assert.Contains("not valid", await Confirm(s, first));                                  // the link for the address that was replaced
            Assert.Equal("other.address@example.com", (await Account(s, jane)).PendingEmail);
            var second = await LinkTo(s, "other.address@example.com");
            s.F.Clock.Advance(TimeSpan.FromDays(30));
            Assert.Contains("not valid", await Confirm(s, second));                                 // expired
            Assert.Equal(Email, (await Account(s, jane)).Email);
        }
    }

    // ---------- no takeover ----------

    [Fact]
    public async Task An_address_another_account_holds_is_never_attached_and_the_caller_hears_the_same_thing()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.K.VerifiedSession("holder@example.com");
            await ReadBack(s, "change_my_email", new { email = "nobody.holds.this@example.com" });
            var normal = await Answer(s, "1");                                                       // an address nobody holds: the control sentence
            await ReadBack(s, "change_my_email", new { email = "holder@example.com" });
            var line = await Answer(s, "1");
            Assert.Equal(normal, line);                                                             // the same sentence for an address that has an account
            Assert.Equal("nobody.holds.this@example.com", (await Account(s, jane)).PendingEmail);      // the holder's address was NOT attached: the pending one is still the earlier one
            Assert.Equal(Email, (await Account(s, jane)).Email);
            var holderMail = await s.K.Mail("holder@example.com");
            Assert.Contains(holderMail, m => m.Subject == "You already have a Solvelt account");   // its owner is told, as web sign-up does
            Assert.DoesNotContain(holderMail, m => m.Subject.StartsWith("Confirm this email"));
        }
    }

    [Fact]
    public async Task An_address_that_gets_an_account_before_the_click_is_not_merged()
    {
        using var s = await GuestWantingAccount();
        await ReadBack(s, "create_account", new { name = "Dana Levi", email = Spoken }); await Answer(s, "1");
        var phoneAccount = Assert.Single(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        var link = await LinkTo(s, Spoken);
        // meanwhile somebody signs up on the web with that very address (a pending address blocks nothing) and verifies it
        await s.K.VerifiedSession(Spoken);
        Assert.Contains("not valid", await Confirm(s, link));
        Assert.Null((await Account(s, phoneAccount.Id)).Email);
        var web = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Spoken));
        Assert.NotEqual(phoneAccount.Id, web.Id);                                                    // two accounts, never merged
        Assert.Null((await Account(s, phoneAccount.Id)).PendingEmail);
    }

    [Fact]
    public async Task A_pending_address_cannot_sign_in_or_reset_a_password()
    {
        using var s = await GuestWantingAccount();
        await ReadBack(s, "create_account", new { name = "Dana Levi", email = Spoken }); await Answer(s, "1");
        Assert.DoesNotContain(await s.K.Mail(Spoken), m => m.Subject.StartsWith("Reset"));
        var reset = await s.C.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = Spoken });
        Assert.True(reset.IsSuccessStatusCode);                                                      // always the same silent answer
        Assert.DoesNotContain(await s.K.Mail(Spoken), m => m.Subject.Contains("Reset your password"));   // and no reset link: the address is nobody's account yet
        var signIn = await s.C.PostAsJsonAsync("/api/v1/auth/sign-in", new { email = Spoken, password = "whatever correct horse" });
        Assert.NotEqual(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task Requests_for_one_address_or_one_account_are_limited()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            for (var i = 0; i < 3; i++) { await ReadBack(s, "change_my_email", new { email = Spoken }); Assert.Contains("link", await Answer(s, "1")); }
            await ReadBack(s, "change_my_email", new { email = Spoken });
            Assert.Contains("could not save", await Answer(s, "1"));                                // the fourth in an hour for one address is refused (nobody is mail-bombed)
            Assert.Equal(Spoken, (await Account(s, jane)).PendingEmail);
        }
    }

    [Fact]
    public async Task The_email_is_not_in_any_log_line_the_model_or_the_text_message()
    {
        var (s, _) = await AccountCall();
        using (s)
        {
            await ReadBack(s, "change_my_email", new { email = Spoken }); await Answer(s, "1");
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(Spoken));
            Assert.DoesNotContain(s.Sms.Sent, m => m.Body.Contains(Spoken));
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains(VoiceSpeech.Email(Spoken)));                      // the read-back is the server's, never given to the model
        }
    }
}
