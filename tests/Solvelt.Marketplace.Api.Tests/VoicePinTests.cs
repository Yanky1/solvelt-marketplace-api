using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The phone PIN (decision V-18): a second factor after the SMS code for signing in by phone, typed on the keypad only; never spoken, never given to the model, never logged.</summary>
public class VoicePinTests
{
    private const string Email = "jane@example.com", Pin = "1357924";

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

    private static Task<BuyerAccount> Account(VoiceScenario s, Guid id) => s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));

    private static async Task GivePin(VoiceScenario s, Guid account, string pin = Pin)
    {
        using var scope = s.F.Services.CreateScope();
        var pins = scope.ServiceProvider.GetRequiredService<PhonePinService>();
        await pins.SaveAsync(account, pins.Prepare(account, pin), default);
    }

    private static string SayOf(XElement response) => response.Descendants("Say").First().Value;

    /// <summary>The set-PIN dance up to the PIN prompt: the model starts it, a fresh code is typed, and the server asks for the PIN.</summary>
    private static async Task<XElement> ToPinPrompt(VoiceScenario s)
    {
        var r = await s.Tool("set_my_pin");
        Assert.Equal("code_sent", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye(); await s.AfterStream();
        return await s.Keypad(s.LastCode());
    }

    // ---------- setting ----------

    [Fact]
    public async Task A_PIN_is_chosen_on_the_keypad_read_back_by_length_only_confirmed_and_stored_hashed()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var prompt = await ToPinPrompt(s);
            Assert.Equal(VoiceScripts.PinChoose, SayOf(prompt));
            var confirm = await s.Keypad(Pin);
            Assert.Contains("PIN of seven digits", SayOf(confirm));
            Assert.DoesNotContain("1", SayOf(confirm).Replace("PIN of seven digits", ""));                                   // the LENGTH only: no digit is read back
            Assert.Null((await Account(s, jane)).PinHash);                                                                   // nothing is stored before the confirmation
            var (done, stream) = await s.Type(Pin);
            Assert.Equal(VoiceScripts.PinSaved, done.Element("Say")!.Value);
            await s.Attach(stream);
            var hash = (await Account(s, jane)).PinHash;
            Assert.NotNull(hash); Assert.DoesNotContain(Pin, hash);
            // the digits never reached the model, a text message or a log
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains(Pin));
            Assert.DoesNotContain(s.Sms.Sent, m => m.Body.Contains(Pin));
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(Pin));
        }
    }

    [Fact]
    public async Task Short_trivial_and_phone_number_PINs_are_refused_and_nothing_is_stored()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await ToPinPrompt(s);
            foreach (var bad in new[] { "12345", "1111111", "123456", "654321", "121212", "5551234", "1234567890123" })
            {
                var reply = await s.Keypad(bad);
                Assert.True(SayOf(reply).StartsWith("A PIN is") || SayOf(reply).StartsWith("That PIN") || SayOf(reply).StartsWith("Sorry"), bad + ": " + SayOf(reply));
                Assert.DoesNotContain(bad, SayOf(reply));
                if (SayOf(reply).StartsWith("Sorry")) break;                                                                 // three bad tries end the step
                Assert.Null((await Account(s, jane)).PinHash);
            }
            Assert.Null((await Account(s, jane)).PinHash);
        }
        Assert.NotNull(PhonePinService.Problem("111111", null)); Assert.Null(PhonePinService.Problem("135792", null));
        Assert.NotNull(PhonePinService.Problem("012345", null));
    }

    [Fact]
    public async Task A_PIN_typed_differently_the_second_time_changes_nothing()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await ToPinPrompt(s); await s.Keypad(Pin);
            var (done, stream) = await s.Type("1357925");
            Assert.Equal(VoiceScripts.PinMismatch, done.Element("Say")!.Value);
            await s.Attach(stream);
            Assert.Null((await Account(s, jane)).PinHash);
        }
    }

    [Fact]
    public async Task Setting_or_changing_the_PIN_needs_a_fresh_code_and_a_guest_cannot_start_it()
    {
        using (var guest = new VoiceScenario())
        {
            await guest.SyncCatalog(); await guest.Dial(); await Verify(guest);
            Assert.Equal("need_verification", (await guest.Tool("set_my_pin"))["error"]!.GetValue<string>());
        }
        var (s, jane) = await AccountCall();
        using (s)
        {
            var r = await s.Tool("set_my_pin");
            await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var wrong = await s.Keypad("000000");                                                                            // not the code that was texted
            Assert.Contains("code", SayOf(wrong), StringComparison.OrdinalIgnoreCase);                                       // asked for the code again
            Assert.Null((await Account(s, jane)).PinHash);
        }
    }

    // ---------- signing in ----------

    [Fact]
    public async Task An_account_with_a_PIN_needs_the_code_and_then_the_PIN_and_is_a_guest_until_both_are_typed()
    {
        var (s, jane) = await AccountCall(); s.Dispose();
        var t = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        using (t)
        {
            await t.SyncCatalog(); t.PortalShipping();
            await t.K.VerifiedSession(Email); await t.SetPhone(Email, "(555) 123-4567", verified: true);
            var account = (await t.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
            await GivePin(t, account);
            await t.Dial();
            await t.Tool("identify_caller"); await t.EndStreamAfterGoodbye(); await t.AfterStream();
            var prompt = await t.Keypad(t.LastCode());                                                                       // the right code
            Assert.Equal(VoiceScripts.AskPin, SayOf(prompt));                                                                // ... and now the PIN
            Assert.Equal((CallerLevel.Guest, null), (t.State().Level, t.State().AccountId));                                  // only a guest with a verified phone so far
            var wrong = await t.Keypad("9999999");
            Assert.Equal(VoiceScripts.WrongPin, SayOf(wrong));
            var (ok, stream) = await t.Type(Pin);
            Assert.Equal(VoiceScripts.CodeOk, ok.Element("Say")!.Value);
            await t.Attach(stream);
            Assert.Equal((CallerLevel.Verified, (Guid?)account), (t.State().Level, t.State().AccountId));
            Assert.DoesNotContain(t.Fake.Received, m => m.Contains(Pin) || m.Contains("9999999"));
            Assert.DoesNotContain(t.F.Logs.Lines, l => l.Contains(Pin) || l.Contains("9999999"));
        }
    }

    [Fact]
    public async Task Three_wrong_PINs_end_the_PIN_step_and_the_failures_are_counted_on_the_account_not_the_call()
    {
        var t = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        using (t)
        {
            await t.SyncCatalog(); t.PortalShipping();
            await t.K.VerifiedSession(Email); await t.SetPhone(Email, "(555) 123-4567", verified: true);
            var account = (await t.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
            await GivePin(t, account);
            await t.Dial();
            await t.Tool("identify_caller"); await t.EndStreamAfterGoodbye(); await t.AfterStream();
            await t.Keypad(t.LastCode());
            await t.Keypad("9999991"); await t.Keypad("9999992");
            var (third, s1) = await t.Type("9999993");                                                                        // three tries end this call's PIN step
            Assert.Equal(VoiceScripts.PinFailed, third.Element("Say")!.Value); await t.Attach(s1);
            Assert.Equal(CallerLevel.Guest, t.State().Level);
            Assert.Equal(3, (await Account(t, account)).PinFailures);                                                          // the count is on the ACCOUNT, not the call
        }
    }

    [Fact]
    public async Task The_lock_holds_even_for_the_right_PIN_until_it_expires_and_a_right_PIN_clears_the_count()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await GivePin(s, jane);
            using var scope = s.F.Services.CreateScope();
            var pins = scope.ServiceProvider.GetRequiredService<PhonePinService>();
            for (var i = 0; i < 4; i++) Assert.Equal(PinResult.Wrong, await pins.VerifyAsync(jane, "9999999", "+15551234567", default));
            Assert.Equal(PinResult.Locked, await pins.VerifyAsync(jane, "9999999", "+15551234567", default));               // the fifth wrong one locks
            Assert.Equal(PinResult.Locked, await pins.VerifyAsync(jane, Pin, "+15551234567", default));                       // even the right PIN, while locked
            s.F.Clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal(PinResult.Locked, await pins.VerifyAsync(jane, Pin, "+15551234567", default));
            s.F.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(PinResult.Wrong, await pins.VerifyAsync(jane, "9999999", "+15551234567", default));                  // unlocked: a wrong one counts from one again
            Assert.Equal(PinResult.Ok, await pins.VerifyAsync(jane, Pin, "+15551234567", default));
            Assert.Equal(0, (await Account(s, jane)).PinFailures);                                                             // a right PIN clears the count
        }
    }

    // ---------- the web, removal, erasure ----------

    [Fact]
    public async Task The_PIN_is_a_phone_factor_only_it_opens_nothing_on_the_web()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await GivePin(s, jane);
            var res = await s.K.Post("/api/v1/auth/sign-in", new { email = Email, password = Pin });
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);                                                              // the PIN is not a password
            Assert.Equal(HttpStatusCode.OK, (await s.K.Post("/api/v1/auth/sign-in", new { email = Email, password = AuthKit.Password })).StatusCode);   // the real password still works and is not asked for a PIN
        }
    }

    [Fact]
    public async Task Removing_the_PIN_needs_a_fresh_code_and_the_yes()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            Assert.Equal("no_pin", (await s.Tool("remove_my_pin"))["error"]!.GetValue<string>());
            await GivePin(s, jane);
            Assert.Equal("code_sent", (await s.Tool("remove_my_pin"))["status"]!.GetValue<string>());
            await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var readBack = await s.Keypad(s.LastCode());
            Assert.Contains("remove the PIN", SayOf(readBack));
            Assert.NotNull((await Account(s, jane)).PinHash);                                                                // asking removes nothing
            var (done, stream) = await s.Type("1");
            Assert.Equal(VoiceScripts.AccountSaved, done.Element("Say")!.Value);
            await s.Attach(stream);
            Assert.Null((await Account(s, jane)).PinHash);
        }
    }
}
