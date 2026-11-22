using System.Xml.Linq;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The test-line menu explains each option (decision V-32): only the available ones are read, the wording is one setting, a digit selects at once, star reads the list again.</summary>
public sealed class VoiceMenuDescriptionsTests
{
    private const string Number = "+15551234567";

    private static async Task<XElement> Xml(HttpResponseMessage r) => XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;
    private static Task<XElement> Inbound(VoiceScenario s) => VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, Number).ContinueWith(t => Xml(t.Result)).Unwrap();
    private static Task<XElement> Press(VoiceScenario s, string digit) => VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, Number, extra: new() { ["Digits"] = digit }).ContinueWith(t => Xml(t.Result)).Unwrap();
    private static string Spoken(XElement response) => response.Element("Gather")!.Element("Say")!.Value;

    private static Dictionary<string, string?> Settings(bool p2 = false, bool p4 = false, Action<Dictionary<string, string?>>? f = null)
    {
        var d = new Dictionary<string, string?>();
        if (p2) foreach (var kv in VoiceP2PipelineTests.P2Settings()) d[kv.Key] = kv.Value;
        if (p4) foreach (var kv in VoiceP4PipelineTests.P4Settings()) d[kv.Key] = kv.Value;
        f?.Invoke(d); return d;
    }

    [Fact]
    public async Task With_nothing_configured_only_option_1_is_read_and_there_is_nothing_to_repeat()
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig());
        var text = Spoken(await Inbound(s));
        Assert.Equal(PipelineScripts.Menu, text);
        Assert.Equal("Test line. Press 1 for OpenAI live voice. One model hears and speaks directly. The fastest and most natural, English first.", text);
        Assert.DoesNotContain("Press 2", text); Assert.DoesNotContain("star", text);
    }

    [Fact]
    public async Task Only_the_available_options_are_read_in_order_with_their_description_and_the_others_are_not_mentioned_at_all()
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Settings(p2: true, p4: true)));
        var text = Spoken(await Inbound(s));
        Assert.StartsWith("Test line. Press 1 for OpenAI live voice.", text);
        Assert.Contains("Press 2 for Deepgram and ElevenLabs.", text);
        Assert.Contains("Press 4 for Yiddish Labs. Yiddish speech recognition and a Yiddish voice.", text);
        Assert.True(text.IndexOf("Press 1", StringComparison.Ordinal) < text.IndexOf("Press 2", StringComparison.Ordinal) && text.IndexOf("Press 2", StringComparison.Ordinal) < text.IndexOf("Press 4", StringComparison.Ordinal));
        Assert.DoesNotContain("Press 3", text); Assert.DoesNotContain("Press 5", text);                 // not available: not read
        Assert.DoesNotContain("Gladia", text); Assert.DoesNotContain("judge", text);                    // and not described either
        Assert.EndsWith("Press star to hear this again.", text);
    }

    [Fact]
    public async Task The_list_is_read_inside_the_Gather_for_one_digit_so_a_digit_pressed_during_it_selects_at_once()
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Settings(p2: true)));
        var g = (await Inbound(s)).Element("Gather")!;
        Assert.Equal("1", g.Attribute("numDigits")!.Value);
        Assert.Equal("dtmf", g.Attribute("input")!.Value);
        Assert.Equal("6", g.Attribute("timeout")!.Value);                                               // the existing timeout stays
        Assert.Single(g.Elements("Say"));                                                                // the text is the Gather's own: SignalWire collects the digit while it plays
        await S2(s);

        static async Task S2(VoiceScenario s)
        {
            var chosen = await Press(s, "2");                                                            // a digit selects
            Assert.Equal("Option 2.", chosen.Element("Say")!.Value);
            Assert.Equal("p2", (await s.Row()).Tier);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("0")]
    public async Task No_key_or_an_unknown_key_is_still_option_1(string digit)
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Settings(p2: true)));
        await Inbound(s);
        var r = await Press(s, digit);
        Assert.Equal("Option 1.", r.Element("Say")!.Value);
        Assert.Equal("p1", (await s.Row()).Tier);
    }

    [Fact]
    public async Task Star_reads_the_list_again_chooses_nothing_and_starts_no_stream()
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Settings(p2: true)));
        var first = Spoken(await Inbound(s));
        var again = await Press(s, "*");
        Assert.Equal(first, Spoken(again));
        Assert.Equal("1", again.Element("Gather")!.Attribute("numDigits")!.Value);
        Assert.Null(again.Descendants("Stream").SingleOrDefault());
        Assert.Equal("full", (await s.Row()).Tier);                                                      // nothing was recorded by the star
        Assert.Equal("Option 2.", (await Press(s, "2")).Element("Say")!.Value);                          // and the next digit still chooses
    }

    [Fact]
    public async Task Editing_a_description_changes_what_is_said_and_an_empty_entry_means_the_default()
    {
        var more = Settings(p2: true, f: d => { d["Voice:PipelineMenu:Descriptions:P2"] = "The English comparison from the owner"; d["Voice:PipelineMenu:Descriptions:P1"] = "   "; });
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: more));
        var text = Spoken(await Inbound(s));
        Assert.Contains("Press 2 for The English comparison from the owner.", text);                     // the owner's words, with a full stop added
        Assert.DoesNotContain("Deepgram", text);                                                         // instead of the default
        Assert.Contains("Press 1 for OpenAI live voice.", text);                                         // a blank entry is the default
    }

    [Fact]
    public void Every_option_has_a_default_the_wording_lives_in_one_place_and_a_description_is_one_plain_sentence_not_an_essay()
    {
        var m = new VoicePipelineMenuOptions();
        foreach (var o in VoicePipelineIds.All) Assert.False(string.IsNullOrWhiteSpace(m.Descriptions.For(o)), o);
        foreach (var o in new[] { "p2", "p5" }) { Assert.Contains("Claude", m.Descriptions.For(o)); Assert.DoesNotContain("an AI", m.Descriptions.For(o)); }   // Claude is the default provider of the chained options (V-33)
        Assert.Equal("SignalWire built-in. The phone company's own speech recognition, with Claude.", m.Descriptions.For("p6"));   // option 6 (V-35)
        Assert.Equal("", m.Descriptions.For("p7"));                                                      // there is no option 7
        Assert.Equal("Ends already!", PipelineScripts.Compose(new VoicePipelineMenuOptions { Descriptions = new PipelineDescriptions { P1 = "Ends already!" } }, ["p1"])["Test line. Press 1 for ".Length..]);
        Assert.Equal("Needs a stop.", PipelineScripts.Compose(new VoicePipelineMenuOptions { Descriptions = new PipelineDescriptions { P1 = "Needs a stop" } }, ["p1"])["Test line. Press 1 for ".Length..]);

        VoiceOptions V(string text, bool enabled = true)
        {
            var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" };
            o.PipelineMenu.Enabled = enabled; o.PipelineMenu.Descriptions.P3 = text; return o;
        }
        var sw = new SignalWireOptions { SigningKey = "sw" };
        Assert.Empty(VoiceConfig.Problems(V("Short and plain."), sw, true));
        Assert.Contains(VoiceConfig.Problems(V(new string('x', PipelineDescriptions.MaxLength + 1)), sw, true), p => p.Contains("Descriptions:P3"));
        Assert.Empty(VoiceConfig.Problems(V(new string('x', PipelineDescriptions.MaxLength + 1), enabled: false), sw, true));   // the menu off: nothing here stops the start
    }
}
