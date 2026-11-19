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
    public async Task Five_wrong_tries_lock_the_account_for_good_until_a_new_PIN_is_set_and_the_fifth_try_can_still_be_the_right_one()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await GivePin(s, jane);
            using var scope = s.F.Services.CreateScope();
            var pins = scope.ServiceProvider.GetRequiredService<PhonePinService>();
            for (var i = 0; i < 4; i++) Assert.Equal(PinResult.Wrong, await pins.VerifyAsync(jane, "9999999", "+15551234567", default));
            Assert.Equal(PinResult.Ok, await pins.VerifyAsync(jane, Pin, "+15551234567", default));                           // the FIFTH try may still be the right one
            Assert.Equal(0, (await Account(s, jane)).PinFailures);                                                             // and a right PIN clears the count
            for (var i = 0; i < 4; i++) await pins.VerifyAsync(jane, "9999999", "+15551234567", default);
            Assert.Equal(PinResult.LockedNow, await pins.VerifyAsync(jane, "9999999", "+15551234567", default));              // the fifth wrong one locks
            Assert.Equal(PinResult.Locked, await pins.VerifyAsync(jane, Pin, "+15551234567", default));                       // even the right PIN, while locked
            s.F.Clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(PinResult.Locked, await pins.VerifyAsync(jane, Pin, "+15551234567", default));                       // time does not unlock it
            Assert.True(await pins.IsLockedAsync(jane, default));
            await pins.SaveAsync(jane, pins.Prepare(jane, "2468013"), default);                                               // only a new PIN (set after a fresh code) does
            Assert.False(await pins.IsLockedAsync(jane, default));
            Assert.Equal(PinResult.Ok, await pins.VerifyAsync(jane, "2468013", "+15551234567", default));
        }
    }

    [Fact]
    public async Task Parallel_guesses_cannot_get_more_than_five_tries_because_each_try_is_recorded_before_it_is_checked()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await GivePin(s, jane);
            var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(async i =>
            {
                using var scope = s.F.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<PhonePinService>().VerifyAsync(jane, "88888" + (10 + i), "+1555123" + (4000 + i), default);   // 14 calls from 14 different numbers
            }));
            Assert.True(results.Count(r => r is PinResult.Wrong or PinResult.LockedNow) <= 5, string.Join(",", results));    // no more than five of them were ever compared
            Assert.Equal(5, (await Account(s, jane)).PinFailures);
            Assert.True(await Task.Run(async () => { using var scope = s.F.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<PhonePinService>().IsLockedAsync(jane, default); }));
            using var again = s.F.Services.CreateScope();
            Assert.Equal(PinResult.Locked, await again.ServiceProvider.GetRequiredService<PhonePinService>().VerifyAsync(jane, Pin, "+15551234567", default));
        }
    }

    [Fact]
    public async Task A_lock_is_texted_to_the_proven_number_and_the_locked_holder_unlocks_it_with_a_fresh_code_and_a_new_PIN()
    {
        var t = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        using (t)
        {
            await t.SyncCatalog(); t.PortalShipping();
            await t.K.VerifiedSession(Email); await t.SetPhone(Email, "(555) 123-4567", verified: true);
            var account = (await t.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
            await GivePin(t, account);
            await t.K.Db(async db => { await db.BuyerAccounts.Where(a => a.Id == account).ExecuteUpdateAsync(x => x.SetProperty(a => a.PinFailures, 3)); return 0; });   // earlier wrong tries on other calls
            await t.Dial();
            await t.Tool("identify_caller"); await t.EndStreamAfterGoodbye(); await t.AfterStream();
            await t.Keypad(t.LastCode());
            await t.Keypad("9999991");
            var (locked, stream) = await t.Type("9999992");                                                                   // the fifth wrong try in all
            Assert.Equal(VoiceScripts.PinLocked, locked.Element("Say")!.Value); await t.Attach(stream);
            Assert.Contains(t.Sms.Sent, m => m.To == "+15551234567" && m.Body.Contains("PIN was locked"));                   // the holder is told by text
            Assert.Equal((CallerLevel.Guest, null), (t.State().Level, t.State().AccountId));
            // unlock: a fresh code, then a NEW PIN
            var started = await t.Tool("set_my_pin");
            Assert.Equal("code_sent", started["status"]!.GetValue<string>());
            await t.EndStreamAfterGoodbye(); await t.AfterStream();
            Assert.Equal(VoiceScripts.PinChoose, SayOf(await t.Keypad(t.LastCode())));
            await t.Keypad("2468013");
            var (saved, s2) = await t.Type("2468013");
            Assert.Equal(VoiceScripts.PinSaved, saved.Element("Say")!.Value); await t.Attach(s2);
            var row = await Account(t, account);
            Assert.Equal((0, null), (row.PinFailures, row.PinLockedUntil));
            Assert.Contains(t.Sms.Sent, m => m.Body.Contains("PIN on your account was just set or changed"));                 // and told of the change
            using var scope = t.F.Services.CreateScope();
            var pins = scope.ServiceProvider.GetRequiredService<PhonePinService>();
            Assert.Equal(PinResult.Wrong, await pins.VerifyAsync(account, Pin, "+15551234567", default));                     // the old PIN is gone
            Assert.Equal(PinResult.Ok, await pins.VerifyAsync(account, "2468013", "+15551234567", default));
        }
    }

    [Fact]
    public async Task No_PIN_step_exists_unless_the_server_opened_one_so_digits_typed_out_of_the_blue_do_nothing()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var reply = await s.Keypad(Pin);                                                                                 // the caller is signed in; no keypad step is pending
            Assert.NotNull(reply.Element("Hangup"));
            Assert.Null((await Account(s, jane)).PinHash);
            // the model has no tool that starts a PIN entry without the fresh code: set_my_pin only texts a code
            var r = await s.Tool("set_my_pin");
            Assert.Equal("code_sent", r["status"]!.GetValue<string>());
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains("Choose a PIN"));                                         // the PIN prompt is the server's, never the model's
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
