using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Caller identification (slice 2, decision V-03): a code texted to the number, typed on the keypad, never spoken and never near the model; caller ID alone authenticates nobody.</summary>
public class VoiceIdentifyTests
{
    private const string Email = "jane@example.com", Number = "+15551234567";

    private static async Task<VoiceScenario> Scenario(bool account = true, string? profilePhone = "(555) 123-4567", Dictionary<string, string?>? config = null, string from = Number)
    {
        var s = new VoiceScenario(from, config);
        await s.SyncCatalog();
        if (account)
        {
            await s.K.VerifiedSession(Email);
            if (profilePhone is not null) await s.SetPhone(Email, profilePhone, verified: true);   // a number the account has proven (see VoicePhoneVerificationTests)
        }
        await s.Dial();
        return s;
    }

    /// <summary>The caller asks to be verified: the model's tool, the goodbye, the stream ending, SignalWire's next instruction (a Gather).</summary>
    private static async Task<XElement> AskForCode(VoiceScenario s, object? args = null)
    {
        var r = await s.Tool("identify_caller", args);
        Assert.Equal("code_sent", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    private static void AssertNoSecrets(VoiceScenario s, string code)
    {
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains(code));
        Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(code));
        Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains("5551234567"));
    }

    [Fact]
    public async Task A_recognised_number_gets_a_code_by_text_and_after_typing_it_the_caller_is_their_account_in_the_same_call()
    {
        using var s = await Scenario();
        var gather = await AskForCode(s);
        // the text message went to the caller's number, with a six digit code
        var sms = Assert.Single(s.Sms.Sent);
        Assert.Equal(Number, sms.To);
        var code = s.LastCode();
        Assert.Equal(6, code.Length);
        // SignalWire's next instruction collects the digits itself (not the model)
        var g = gather.Element("Gather")!;
        Assert.Equal(("dtmf", "6"), (g.Attribute("input")!.Value, g.Attribute("numDigits")!.Value));
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, g.Attribute("action")!.Value);
        Assert.Equal("true", g.Attribute("actionOnEmptyResult")!.Value);
        Assert.Equal(VoiceScripts.AskCode, g.Element("Say")!.Value);

        var (resume, token) = await s.Type(code);
        Assert.Equal(VoiceScripts.CodeOk, resume.Element("Say")!.Value);
        await s.Attach(token);
        // the model is told, by the server, where things stand: verified, first name, shopping
        var summary = await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system");
        var text = summary["item"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("verified by SMS code in this call", text); Assert.Contains("Jane", text);
        Assert.DoesNotContain(code, text); Assert.DoesNotContain("5551234567", text); Assert.DoesNotContain(Email, text);
        var st = s.State();
        Assert.Equal((CallerLevel.Verified, true), (st.Level, st.VerifiedInThisCall));
        AssertNoSecrets(s, code);
        // the code is gone: typing it again opens nothing and no second text was sent
        Assert.Single(s.Sms.Sent);
        Assert.Null(st.Challenge);
    }

    [Fact]
    public async Task The_model_is_never_given_the_code_or_the_digits_and_is_told_not_to_ask_for_it()
    {
        using var s = await Scenario();
        var r = await s.Tool("identify_caller");
        Assert.Contains("Do not ask them to say it", r["instruction"]!.GetValue<string>());
        var code = s.LastCode();
        Assert.DoesNotContain(code, r.ToJsonString());
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (_, token) = await s.Type(code);
        await s.Attach(token);
        await Task.Delay(100);
        AssertNoSecrets(s, code);
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("\"digit"));
    }

    [Fact]
    public async Task A_wrong_code_asks_again_and_the_right_one_then_works()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        var wrong = code == "000000" ? "111111" : "000000";
        var (again, _) = await s.Type(wrong);
        Assert.Equal(VoiceScripts.WrongCode, again.Element("Gather")!.Element("Say")!.Value);
        Assert.False(s.State().VerifiedInThisCall);
        var (ok, token) = await s.Type(code);
        Assert.Equal(VoiceScripts.CodeOk, ok.Element("Say")!.Value);
        Assert.NotEmpty(token);
        Assert.True(s.State().VerifiedInThisCall);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public async Task Digits_of_the_wrong_shape_never_verify(string typed)
    {
        using var s = await Scenario();
        await AskForCode(s);
        var (xml, _) = await s.Type(typed);
        Assert.NotNull(xml.Element("Gather"));
        Assert.False(s.State().VerifiedInThisCall);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_attempt_the_call_goes_on_unverified_and_the_right_code_no_longer_works()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) Assert.NotNull((await s.Type(wrong)).Xml.Element("Gather"));
        var (last, token) = await s.Type(wrong);                              // the fifth
        Assert.Equal(VoiceScripts.CodeFailed, last.Element("Say")!.Value);
        Assert.NotNull(last.Element("Connect"));                              // the call goes on, so the agent can offer a person
        await s.Attach(token);
        var summary = await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system");
        Assert.Contains("NOT verified", summary["item"]!["content"]![0]!["text"]!.GetValue<string>());
        // the correct code now opens nothing: the keypad step is over
        var late = await s.Type(code);
        Assert.NotNull(late.Xml.Element("Hangup"));
        Assert.False(s.State().VerifiedInThisCall);
    }

    [Fact]
    public async Task A_code_expires_after_ten_minutes()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        s.F.Clock.Advance(PhoneCodeService.Lifetime + TimeSpan.FromSeconds(1));
        var (xml, _) = await s.Type(code);
        Assert.Equal(VoiceScripts.CodeFailed, xml.Element("Say")!.Value);
        Assert.False(s.State().VerifiedInThisCall);
    }

    [Fact]
    public async Task A_number_nobody_owns_still_gets_a_code_and_becomes_a_guest_with_a_verified_phone()
    {
        using var s = await Scenario(account: false);
        await AskForCode(s);
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
        var st = s.State();
        Assert.Equal((CallerLevel.Guest, true, null), (st.Level, st.VerifiedInThisCall, st.AccountId));
        Assert.Equal(Number, st.VerifiedPhone);
        var summary = await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system");
        Assert.Contains("guest whose phone is verified", summary["item"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_answer_is_the_same_whether_or_not_an_account_uses_the_number_so_it_cannot_be_used_to_find_customers()
    {
        using var known = await Scenario(account: true);
        using var unknown = await Scenario(account: false);
        var a = await known.Tool("identify_caller");
        var b = await unknown.Tool("identify_caller");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
    }

    [Fact]
    public async Task Caller_id_alone_authenticates_nobody_an_unverified_call_does_not_see_the_account_cart()
    {
        using var s = await Scenario();
        // the account has something in ITS cart
        var token = await s.K.VerifiedSession("other@example.com");                     // another account, to show carts are separate
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
        using (var scope = s.F.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Carts.CartService>().AddAsync(new Solvelt.Marketplace.Api.Carts.CartOwner(jane.Id, null), Solvelt.Marketplace.Api.Catalog.FixtureCatalogSource.Id("variant:challah-classic:0"), 2, CancellationToken.None);
        // the call comes from Jane's own number, but nothing was verified
        var view = await s.Tool("view_cart");
        Assert.Equal(0, view["items_in_cart"]!.GetValue<int>());
        Assert.Equal((CallerLevel.Anonymous, false), (s.State().Level, s.State().VerifiedInThisCall));
        // and what the call adds goes to a guest cart, not to Jane's
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        var carts = await s.K.Db(db => db.Carts.AsNoTracking().ToListAsync());
        Assert.Equal(2, carts.Count);
        Assert.Equal(2, await s.K.Db(db => db.CartLines.AsNoTracking().Where(l => db.Carts.Any(c => c.Id == l.CartId && c.AccountId == jane.Id)).SumAsync(l => l.Quantity)));
        Assert.NotNull(token);
    }

    [Fact]
    public async Task The_guest_cart_moves_into_the_account_when_the_caller_is_recognised()
    {
        using var s = await Scenario();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 2, quantity = 1 });          // as a guest
        await AskForCode(s);
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
        var view = await s.Tool("view_cart");
        Assert.Equal(1, view["items_in_cart"]!.GetValue<int>());                                       // the cart carried over
        var carts = await s.K.Db(db => db.Carts.AsNoTracking().ToListAsync());
        Assert.Single(carts); Assert.Equal(jane.Id, carts[0].AccountId);
        Assert.Null(s.State().CartToken);
        Assert.Equal(1, s.State().CartItemCount);
    }

    [Fact]
    public async Task A_second_account_that_merely_types_the_same_profile_phone_does_not_change_who_is_recognised()
    {
        using var s = await Scenario();
        await s.K.VerifiedSession("twin@example.com", name: "Twin Brown");
        await s.SetPhone("twin@example.com", "555-123-4567");                  // typed, never proven
        await AskForCode(s);
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
        Assert.Equal((CallerLevel.Verified, jane.Id), (s.State().Level, s.State().AccountId));   // the account that PROVED the number
    }

    [Fact]
    public async Task A_deleted_a_locked_or_an_unverified_account_is_never_recognised()
    {
        foreach (var kind in new[] { "deleted", "locked", "unverified" })
        {
            using var s = await Scenario(account: true);
            var now = s.F.Clock.GetUtcNow();
            await s.K.Db(async db =>
            {
                var q = db.BuyerAccounts.Where(a => a.Email == Email);
                return kind switch
                {
                    "deleted" => await q.ExecuteUpdateAsync(a => a.SetProperty(x => x.DeletedAt, now)),
                    "locked" => await q.ExecuteUpdateAsync(a => a.SetProperty(x => x.StaffLockedAt, now)),
                    _ => await q.ExecuteUpdateAsync(a => a.SetProperty(x => x.EmailVerifiedAt, (DateTimeOffset?)null)),
                };
            });
            await AskForCode(s);
            var (_, token) = await s.Type(s.LastCode());
            await s.Attach(token);
            Assert.Equal((CallerLevel.Guest, null), (s.State().Level, s.State().AccountId));
        }
    }

    [Fact]
    public async Task The_code_can_go_to_another_number_the_caller_names_and_a_bad_number_is_refused()
    {
        using var s = await Scenario(account: false);
        var bad = await s.Tool("identify_caller", new { other_number = "12345" });
        Assert.Equal("no_number", bad["error"]!.GetValue<string>());
        Assert.Empty(s.Sms.Sent);
        await AskForCode(s, new { other_number = "(555) 000-1111" });
        Assert.Equal("+15550001111", Assert.Single(s.Sms.Sent).To);
    }

    [Fact]
    public async Task A_call_with_no_known_number_asks_for_one()
    {
        using var s = await Scenario(account: false, from: "anonymous");
        var r = await s.Tool("identify_caller");
        Assert.Equal("no_number", r["error"]!.GetValue<string>());
        Assert.Empty(s.Sms.Sent);
    }

    [Fact]
    public async Task Texts_are_limited_per_call_and_per_number_so_the_line_cannot_be_used_to_text_strangers()
    {
        using var s = await Scenario(account: false);
        for (var i = 0; i < PhoneCodeService.MaxSendsPerCall; i++)
            Assert.Equal("code_sent", (await s.Tool("identify_caller", new { other_number = $"555-000-10{i}0" }))["status"]!.GetValue<string>());
        Assert.Equal("too_many_codes", (await s.Tool("identify_caller", new { other_number = "555-000-2000" }))["error"]!.GetValue<string>());
        Assert.Equal(PhoneCodeService.MaxSendsPerCall, s.Sms.Sent.Count);
    }

    [Fact]
    public async Task One_number_gets_at_most_three_texts_an_hour_across_calls_then_again_after_the_hour()
    {
        using var s = await Scenario(account: false);
        var sent = 0;
        for (var call = 0; call < 4; call++)
        {
            var phone = new VoiceScenarioProbe(s);
            var r = await phone.AskFor("+15557770000");
            if (r == "code_sent") sent++;
        }
        Assert.Equal(3, sent);
        s.F.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal("code_sent", await new VoiceScenarioProbe(s).AskFor("+15557770000"));
    }

    [Fact]
    public async Task A_text_that_cannot_be_sent_is_told_and_starts_no_keypad_step()
    {
        using var s = await Scenario(account: false);
        s.Sms.Fail = true;
        Assert.Equal("sms_failed", (await s.Tool("identify_caller"))["error"]!.GetValue<string>());
        Assert.Null(s.State().KeypadStep);
        Assert.Null(s.State().Challenge);
    }

    [Fact]
    public async Task The_keypad_endpoint_needs_a_signature_a_known_call_and_a_pending_code()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        var form = new Dictionary<string, string> { ["CallSid"] = s.Sid, ["From"] = Number, ["To"] = "+15559876543", ["Digits"] = code };
        var req = new HttpRequestMessage(HttpMethod.Post, VoiceEndpoints.KeypadPath) { Content = new FormUrlEncodedContent(form) };
        Assert.Equal(HttpStatusCode.Forbidden, (await s.C.SendAsync(req)).StatusCode);                                             // unsigned
        var forged = await VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, s.Sid, signature: "AAAA", extra: new() { ["Digits"] = code });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        var other = await VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, "CA-unknown", extra: new() { ["Digits"] = code });          // a call we never saw
        Assert.NotNull(XDocument.Parse(await other.Content.ReadAsStringAsync()).Root!.Element("Hangup"));
        Assert.False(s.State().VerifiedInThisCall);
        // the signature covers the digits: the same signature with other digits is refused
        var sig = SignalWireSignature.Compute(VoiceFactory.SigningKey, VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, new Dictionary<string, string>(form));
        var tamper = await VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, s.Sid, signature: sig, extra: new() { ["Digits"] = "000000" });
        Assert.Equal(HttpStatusCode.Forbidden, tamper.StatusCode);
    }

    [Fact]
    public async Task Typing_a_code_when_no_keypad_step_is_pending_does_nothing()
    {
        using var s = await Scenario();
        var (xml, _) = await s.Type("123456");
        Assert.NotNull(xml.Element("Hangup"));
        Assert.Equal(CallerLevel.Anonymous, s.State().Level);
    }

    [Fact]
    public async Task A_code_from_one_call_cannot_verify_another_call_even_for_the_same_number()
    {
        using var a = await Scenario();
        using var b = await Scenario();
        await AskForCode(a);
        await AskForCode(b);
        var codeA = a.LastCode();
        // b types a's code: the hash is bound to the call, so it is wrong (unless the two random codes collide)
        if (codeA != b.LastCode()) Assert.NotNull((await b.Type(codeA)).Xml.Element("Gather"));
        Assert.False(b.State().VerifiedInThisCall);
    }

    [Fact]
    public async Task A_stream_token_opens_one_stream_once()
    {
        using var s = new VoiceScenario();
        var (token, _) = await VoiceScenario.StreamFrom(await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From));
        var phone = await FakePhone.Connect(s.F, token);
        await phone.Start();
        await s.Fake.Wait("session.update");
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(s.F, token));        // a copied URL or a replayed upgrade
        phone.Dispose();
    }

    [Fact]
    public async Task A_guest_gives_a_name_and_a_name_that_is_not_one_is_refused()
    {
        using var s = await Scenario(account: false);
        Assert.Equal("saved", (await s.Tool("set_guest_name", new { name = "  Dana   Levi " }))["status"]!.GetValue<string>());
        Assert.Equal("Dana Levi", s.State().GuestName);
        foreach (var bad in new[] { "", "A", "Dana 4", new string('x', 61), "Dana\u0007Levi" })
            Assert.Equal("bad_name", (await s.Tool("set_guest_name", new { name = bad }))["error"]!.GetValue<string>());
        Assert.Equal("Dana Levi", s.State().GuestName);
    }

    /// <summary>Another call, on the same app, asking for a text to a number.</summary>
    private sealed class VoiceScenarioProbe(VoiceScenario s)
    {
        public async Task<string> AskFor(string number)
        {
            var state = new VoiceCallState(Guid.NewGuid());
            var codes = s.F.Services.GetRequiredService<PhoneCodeService>();
            var r = await codes.SendAsync(state, number, "guest", null, CancellationToken.None);
            return r == SendResult.Sent ? "code_sent" : "too_many_codes";
        }
    }
}
