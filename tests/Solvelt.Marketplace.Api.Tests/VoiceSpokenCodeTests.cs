using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The verification code and the test-menu choice are SPOKEN (decision V-41): to SignalWire's own recogniser, matched by the server, never near a model, a log or the diagnostics, with no keypad fallback.</summary>
public sealed class VoiceSpokenCodeTests
{
    private const string Email = "jane@example.com", Number = "+15551234567";

    // ---------- turning speech into digits ----------

    [Theory]
    [InlineData("472913", "472913")]
    [InlineData("4 7 2 9 1 3", "472913")]
    [InlineData("four seven two nine one three", "472913")]
    [InlineData("Four, seven, two. Nine-one-three.", "472913")]
    [InlineData("oh one two three four five", "012345")]
    [InlineData("zero zero for to ate won", "004281")]                                              // the usual sound-alikes
    [InlineData("my code is 4 7 2 um 9 1 3 thanks", "472913")]                                      // everything else is ignored
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("hello there", "")]
    public void Spoken_digits_become_digits(string? said, string expected) => Assert.Equal(expected, SpokenDigits.Parse(said));

    [Fact]
    public void A_long_utterance_gives_at_most_32_digits_and_a_choice_is_one_digit_or_star()
    {
        Assert.Equal(32, SpokenDigits.Parse(string.Join(' ', Enumerable.Repeat("seven", 100))).Length);
        Assert.Equal("2", SpokenDigits.Choice("two")); Assert.Equal("2", SpokenDigits.Choice("option 2 please")); Assert.Equal("5", SpokenDigits.Choice("Number five."));
        Assert.Equal("*", SpokenDigits.Choice("again")); Assert.Equal("*", SpokenDigits.Choice("say that again")); Assert.Equal("*", SpokenDigits.Choice("star"));
        Assert.Null(SpokenDigits.Choice("")); Assert.Null(SpokenDigits.Choice("hmm")); Assert.Null(SpokenDigits.Choice(null));
        Assert.Equal("4, 7, 2", SpokenDigits.ReadOut("472")); Assert.Equal("", SpokenDigits.ReadOut("")); Assert.Equal(12, SpokenDigits.ReadOut(new string('7', 40)).Split(", ").Length);
    }

    // ---------- the verification code ----------

    private static async Task<VoiceScenario> Scenario(Dictionary<string, string?>? config = null)
    {
        var s = new VoiceScenario(Number, config);
        await s.SyncCatalog();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        await s.Dial();
        return s;
    }

    private static async Task<XElement> AskForCode(VoiceScenario s)
    {
        Assert.Equal("code_sent", (await s.Tool("identify_caller"))["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    private static Task<HttpResponseMessage> Say(VoiceScenario s, string? said, string? digitsField = null)
    {
        var extra = new Dictionary<string, string>();
        if (said is not null) extra["SpeechResult"] = said;
        if (digitsField is not null) extra["Digits"] = digitsField;
        return VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, s.Sid, s.From, extra: extra);
    }

    private static async Task<XElement> Xml(HttpResponseMessage r) => XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;

    private static string Words(string digits) => string.Join(' ', digits.Select(c => "zero one two three four five six seven eight nine".Split(' ')[c - '0']));

    [Fact]
    public async Task The_code_prompt_is_a_speech_only_gather_with_digit_hints_and_no_keypad_input_at_all()
    {
        using var s = await Scenario();
        var g = (await AskForCode(s)).Element("Gather")!;
        Assert.Equal("speech", g.Attribute("input")!.Value);                                           // not "dtmf speech": no keypad fallback (Ronald's choice)
        Assert.Null(g.Attribute("numDigits")); Assert.Null(g.Attribute("finishOnKey"));
        Assert.Equal(VoiceScripts.AskCode, g.Element("Say")!.Value);
        Assert.Equal("true", g.Attribute("enhanced")!.Value); Assert.Equal("phone_call", g.Attribute("speechModel")!.Value);   // the telephone defaults of option 6 apply to this Gather too
        var hints = g.Attribute("hints")!.Value;
        Assert.DoesNotContain(s.LastCode(), hints);                                                    // the hint is digit WORDS, never the code
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, g.Attribute("action")!.Value);
    }

    [Fact]
    public async Task The_spoken_right_code_verifies_in_any_of_its_spoken_forms_and_the_digits_field_alone_never_does()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        // a typed code (a "Digits" field with no speech) is NOT accepted: it counts as a wrong try
        var typed = await Xml(await Say(s, null, digitsField: code));
        Assert.Equal(VoiceScripts.WrongCodeHeard(""), typed.Element("Gather")!.Element("Say")!.Value);
        Assert.False(s.State().VerifiedInThisCall);
        // the same code said as digits separated by spaces verifies
        var ok = await Say(s, string.Join(' ', code.ToCharArray()));
        var (token, xml) = await VoiceScenario.StreamFrom(ok);
        Assert.Equal(VoiceScripts.CodeOk, xml.Element("Say")!.Value);
        Assert.True(s.State().VerifiedInThisCall); Assert.NotEmpty(token);
    }

    [Fact]
    public async Task A_wrong_or_unheard_code_is_read_back_as_what_was_heard_never_the_real_code_and_the_limits_are_the_same_five_tries()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var code = s.LastCode();
        var wrong = code == "111111" ? "222222" : "111111";
        for (var i = 1; i <= 4; i++)
        {
            var again = await Xml(await Say(s, Words(wrong)));
            var line = again.Element("Gather")!.Element("Say")!.Value;
            Assert.StartsWith($"I heard {SpokenDigits.ReadOut(wrong)}. That did not match.", line);      // "I heard 1, 1, 1, 1, 1, 1. That did not match."
            Assert.DoesNotContain(code, line);
        }
        var fifth = await Xml(await Say(s, Words(wrong)));                                             // the fifth wrong one locks it, as before
        Assert.Equal(VoiceScripts.CodeFailed, fifth.Element("Say")!.Value);
        Assert.False(s.State().VerifiedInThisCall);
        Assert.NotNull(await VerifyLater(s, code));                                                    // and even the right code afterwards finds no challenge
    }

    private static async Task<object?> VerifyLater(VoiceScenario s, string code)
    {
        var r = await Xml(await Say(s, Words(code)));
        Assert.False(s.State().VerifiedInThisCall);                                                    // the challenge is gone: nothing verifies it any more
        return r;
    }

    [Fact]
    public async Task Nothing_heard_counts_as_a_try_and_says_so_without_reading_anything_back()
    {
        using var s = await Scenario();
        await AskForCode(s);
        var again = await Xml(await Say(s, ""));
        Assert.Equal(VoiceScripts.WrongCodeHeard(""), again.Element("Gather")!.Element("Say")!.Value);   // "Sorry, I did not catch that..."
        Assert.StartsWith("Sorry, I did not catch that", again.Element("Gather")!.Element("Say")!.Value);
    }

    [Fact]
    public async Task The_spoken_code_is_in_no_model_request_no_log_and_no_diagnostics_row_and_the_text_message_no_longer_says_never_to_say_it()
    {
        using var s = await Scenario(new() { ["Voice:Diagnostics:Enabled"] = "true", ["Voice:PipelineMenu:TestNumbers:0"] = Number });
        await AskForCode(s);
        var code = s.LastCode();
        Assert.DoesNotContain("never ask you to say", s.Sms.Sent.Last().Body);                           // the message tells the caller to say it only when the automated line asks
        Assert.Contains("Say it only when the automated line asks", s.Sms.Sent.Last().Body);
        var wrong = code == "111111" ? "222222" : "111111";
        await Say(s, Words(wrong));
        var (token, resume) = await VoiceScenario.StreamFrom(await Say(s, Words(code)));
        Assert.Equal(VoiceScripts.CodeOk, resume.Element("Say")!.Value);
        await s.Attach(token);
        await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system");   // the model is told, by the server, only where things stand
        var spokenForms = new[] { code, Words(code), string.Join(' ', code.ToCharArray()), string.Join(", ", code.ToCharArray()), wrong, Words(wrong) };
        foreach (var form in spokenForms)
        {
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains(form, StringComparison.OrdinalIgnoreCase));                             // nothing sent to the model's session holds the code or the attempt
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(form, StringComparison.OrdinalIgnoreCase));                             // no log line
        }
        var rows = await s.F.Services.CreateScope().ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(rows, r => spokenForms.Any(f => (r.Text + r.Engines).Contains(f, StringComparison.OrdinalIgnoreCase)));   // and none in the diagnostics (a keypad step stores nothing)
    }

    // ---------- the test-menu choice ----------

    private static Dictionary<string, string?> Menu() => VoicePipelineMenuTests.MenuConfig([Number]);

    private static async Task<XElement> Choose(VoiceScenario s, string? said, string? digits = null)
    {
        var extra = new Dictionary<string, string>();
        if (said is not null) extra["SpeechResult"] = said;
        if (digits is not null) extra["Digits"] = digits;
        return await Xml(await VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, s.From, extra: extra));
    }

    [Fact]
    public async Task The_menu_listens_for_a_key_or_a_spoken_number_and_a_spoken_choice_is_the_same_as_the_key()
    {
        using var s = new VoiceScenario(Number, Menu(), services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p2")));
        var inbound = await Xml(await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From));
        var g = inbound.Element("Gather")!;
        Assert.Equal("dtmf speech", g.Attribute("input")!.Value);
        Assert.Contains("one", g.Attribute("hints")!.Value); Assert.Contains("again", g.Attribute("hints")!.Value);
        Assert.Contains("Say or press 2 for", g.Element("Say")!.Value);
        Assert.Contains("Say again or press star to hear this again.", g.Element("Say")!.Value);
        Assert.NotNull((await Choose(s, "two")).Descendants("Stream").SingleOrDefault());
        Assert.Equal("p2", (await s.Row()).Tier);                                                      // "two" chose option 2
    }

    [Fact]
    public async Task A_spoken_again_repeats_the_menu_and_unclear_speech_is_option_one_and_a_key_wins_over_speech()
    {
        using var s = new VoiceScenario(Number, Menu(), services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p2")));
        await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From);
        var again = await Choose(s, "say that again");
        Assert.NotNull(again.Element("Gather")); Assert.Empty(again.Descendants("Stream"));            // read again, nothing chosen
        Assert.NotEqual("p2", (await s.Row()).Tier);
        var unclear = await Choose(s, "hmm what");
        Assert.NotNull(unclear.Descendants("Stream").SingleOrDefault());
        Assert.Equal("p1", (await s.Row()).Tier);                                                      // unknown is option 1, as for an unknown key
        using var s2 = new VoiceScenario(Number, Menu(), services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p2")));
        await VoiceFactory.Hook(s2.C, VoiceEndpoints.InboundPath, s2.Sid, s2.From);
        await Choose(s2, "three", digits: "2");
        Assert.Equal("p2", (await s2.Row()).Tier);                                                     // the key, when there is one, is what counts
    }
}
