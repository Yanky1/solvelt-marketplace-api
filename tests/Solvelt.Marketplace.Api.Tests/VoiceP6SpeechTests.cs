using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Option 6 (decision V-35): SignalWire's own speech recognition, one Gather per turn, with our text model and the same tools. All on fakes: no network, no SignalWire.</summary>
public sealed class VoiceP6SpeechTests : IDisposable
{
    private const string Number = "+15551234567";
    private readonly VoiceScenario _s;
    private readonly FakeParts _parts = new("p6");
    private FakeLlm Llm => _parts.LlmFake;

    public VoiceP6SpeechTests(Dictionary<string, string?>? more = null, bool configured = true)
        => _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig([Number], more), services: sv => { if (configured) sv.AddSingleton<IChainedPipelineParts>(_parts); });

    public void Dispose() => _s.Dispose();

    private async Task<XElement> Post(string path, Dictionary<string, string>? extra = null)
        => XDocument.Parse(await (await VoiceFactory.Hook(_s.C, path, _s.Sid, _s.From, extra: extra)).Content.ReadAsStringAsync()).Root!;

    private static string Speech(string query = "") => VoiceEndpoints.SpeechPath + query;

    /// <summary>Starts the call and picks option 6; returns the choice answer.</summary>
    private async Task<XElement> Choose()
    {
        (await VoiceFactory.Hook(_s.C, VoiceEndpoints.InboundPath, _s.Sid, _s.From)).EnsureSuccessStatusCode();
        return await Post(VoiceEndpoints.PipelinePath, new() { ["Digits"] = "6" });
    }

    [Fact]
    public async Task The_menu_offers_six_when_it_is_configured_and_the_choice_goes_to_the_greeting_turn_without_any_stream()
    {
        var inbound = XDocument.Parse(await (await VoiceFactory.Hook(_s.C, VoiceEndpoints.InboundPath, _s.Sid, _s.From)).Content.ReadAsStringAsync()).Root!;
        Assert.Contains("Say six for SignalWire built-in. The phone company's own speech recognition, with Claude.", inbound.Element("Gather")!.Element("Say")!.Value);
        var xml = await Post(VoiceEndpoints.PipelinePath, new() { ["Digits"] = "6" });
        Assert.Equal("Option 6.", xml.Element("Say")!.Value);
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.SpeechPath + "?first=1", xml.Element("Redirect")!.Value);
        Assert.Empty(xml.Descendants("Stream"));
        Assert.Equal("p6", (await _s.Row()).Tier);
        Assert.True(_s.State().SpeechMode);
    }

    [Fact]
    public async Task The_menu_does_not_mention_six_when_it_is_not_configured_and_choosing_it_gives_option_one()
    {
        using var plain = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig([Number]));
        (await VoiceFactory.Hook(plain.C, VoiceEndpoints.InboundPath, plain.Sid, plain.From)).EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await (await VoiceFactory.Hook(plain.C, VoiceEndpoints.PipelinePath, plain.Sid, plain.From, extra: new() { ["Digits"] = "6" })).Content.ReadAsStringAsync()).Root!;
        Assert.Equal(PipelineScripts.Unavailable, xml.Element("Say")!.Value);
        Assert.NotNull(xml.Descendants("Stream").SingleOrDefault());
        Assert.Equal("p1", (await plain.Row()).Tier);
    }

    [Fact]
    public async Task The_greeting_is_written_by_the_text_model_with_the_same_instructions_and_tools_and_spoken_inside_a_speech_gather()
    {
        await Choose();
        var xml = await Post(Speech("?first=1"));
        var g = xml.Element("Gather")!;
        Assert.Equal(("speech dtmf", "en-US", "auto", "true"), (g.Attribute("input")!.Value, g.Attribute("language")!.Value, g.Attribute("speechTimeout")!.Value, g.Attribute("actionOnEmptyResult")!.Value));
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.SpeechPath, g.Attribute("action")!.Value);
        Assert.Equal("Okay.", g.Element("Say")!.Value);                                       // the reply plays INSIDE the Gather: speech or a key ends it
        Assert.Equal(("true", "phone_call"), (g.Attribute("enhanced")!.Value, g.Attribute("speechModel")!.Value));   // the telephone defaults (V-36): enhanced, the 8 kHz model
        var r = Llm.Requests.Single();
        Assert.Equal(VoiceScripts.Instructions, r.Instructions);
        Assert.Equal(_s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(d => d!["name"]!.GetValue<string>()).Order(), r.Tools.Select(d => d!["name"]!.GetValue<string>()).Order());
        Assert.Contains(r.Messages, m => m.Role == "system" && m.Text == VoiceScripts.Greeting);
    }

    [Fact]
    public async Task A_turn_sends_what_the_caller_said_to_the_model_and_keeps_the_conversation_in_memory()
    {
        await Choose();
        await Post(Speech("?first=1"));
        Llm.Then(new LlmTextDelta("Sure, one bagel."), new LlmCompleted(10, 3));
        var xml = await Post(Speech(), new() { ["SpeechResult"] = "  I want a bagel ", ["Confidence"] = "0.9" });
        Assert.Equal("Sure, one bagel.", xml.Element("Gather")!.Element("Say")!.Value);
        Assert.Contains(Llm.Requests[^1].Messages, m => m is { Role: "user", Text: "I want a bagel" });
        Assert.Contains(_s.State().SpeechHistory, m => m is { Role: "assistant", Text: "Sure, one bagel." });
        var row = await _s.Row();
        Assert.Equal((20, 5), (row.TextInTokens, row.TextOutTokens));                           // counters only
    }

    [Fact]
    public async Task A_tool_call_runs_the_shared_tool_and_the_model_then_answers()
    {
        await Choose();
        await Post(Speech("?first=1"));
        Llm.Then(new LlmToolCallEvent(new LlmToolCall("c1", "off_topic", "{}")), new LlmCompleted(5, 1));
        Llm.Then(new LlmTextDelta("Let's get back to your order."), new LlmCompleted(8, 2));
        var xml = await Post(Speech(), new() { ["SpeechResult"] = "what is the weather" });
        Assert.Equal("Let's get back to your order.", xml.Element("Gather")!.Element("Say")!.Value);
        Assert.Contains(Llm.Requests[^1].Messages, m => m.Role == "tool" && m.ToolCallId == "c1" && m.Text!.Contains("off_topic"));
    }

    [Fact]
    public async Task An_empty_result_asks_again_twice_then_the_platform_says_goodbye_and_the_call_ends()
    {
        await Choose();
        await Post(Speech("?first=1"));
        var calls = Llm.Requests.Count;
        Assert.Equal(SpeechScripts.Reprompt, (await Post(Speech(), new() { ["SpeechResult"] = "" })).Element("Gather")!.Element("Say")!.Value);
        Assert.Equal(SpeechScripts.StillThere, (await Post(Speech(), new() { ["Digits"] = "5" })).Element("Gather")!.Element("Say")!.Value);   // a key is not words
        var last = await Post(Speech());
        Assert.Equal(SpeechScripts.Goodbye, last.Element("Say")!.Value);
        Assert.NotNull(last.Element("Hangup"));
        Assert.Equal(calls, Llm.Requests.Count);                                                // the model was never asked about silence
        Assert.NotNull((await _s.Row()).EndedAt);
    }

    [Fact]
    public async Task A_goodbye_says_the_words_then_goes_to_after_stream_and_ends_the_call()
    {
        using var t = new VoiceP6SpeechTests(new() { ["Voice:SupportNumber"] = "+15550001111" });
        await t.Choose();
        await t.Post(Speech("?first=1"));
        t.Llm.Then(new LlmToolCallEvent(new LlmToolCall("c1", "transfer_to_person", "{}")), new LlmCompleted(5, 1));
        t.Llm.Then(new LlmTextDelta("Connecting you now."), new LlmCompleted(8, 2));
        var xml = await t.Post(Speech(), new() { ["SpeechResult"] = "let me talk to a person" });
        Assert.Equal("Connecting you now.", xml.Element("Say")!.Value);
        Assert.Null(xml.Element("Gather"));
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, xml.Element("Redirect")!.Value);
        Assert.NotNull((await t._s.Row()).EndedAt);
        var after = await t._s.AfterStream();
        Assert.Equal("+15550001111", after.Element("Dial")!.Value);                           // the existing transfer path dials the person
    }

    [Fact]
    public async Task A_keypad_step_goes_through_after_stream_and_the_next_turn_after_it_is_the_resumed_one()
    {
        await Choose();
        await Post(Speech("?first=1"));
        var state = _s.State();
        Llm.Then(_ => { state.BeginReadBack("Your total is five dollars. Say yes or press 1."); state.BeginKeypadStep("confirm"); return [new LlmTextDelta("One moment."), new LlmCompleted(1, 1)]; });
        var xml = await Post(Speech(), new() { ["SpeechResult"] = "that is all" });
        Assert.Equal("One moment.", xml.Element("Say")!.Value);
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, xml.Element("Redirect")!.Value);
        Assert.Null((await _s.Row()).EndedAt);                                                  // the call goes on
        var gather = await _s.AfterStream();
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, gather.Element("Gather")!.Attribute("action")!.Value);   // the existing keypad path, unchanged
        var back = await _s.Keypad("1");
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.SpeechPath + "?r=1", back.Element("Redirect")!.Value);              // not a stream
        Assert.Empty(back.Descendants("Stream"));
        var resumed = await Post(Speech("?r=1"));
        Assert.Equal("Okay.", resumed.Element("Gather")!.Element("Say")!.Value);
        Assert.Contains(Llm.Requests[^1].Messages, m => m.Role == "system" && m.Text == VoiceScripts.Resume);
    }

    [Fact]
    public async Task When_the_model_cannot_answer_the_greeting_the_call_goes_to_the_realtime_line_and_a_later_failure_ends_it_with_the_platforms_apology()
    {
        await Choose();
        Llm.Default = _ => throw new HttpRequestException("boom: secret words");
        var xml = await Post(Speech("?first=1"));
        Assert.NotNull(xml.Descendants("Stream").SingleOrDefault());                           // the Realtime stream, like any other option's fallback
        var fell = await _s.Row();
        Assert.Equal(("full", "p6"), (fell.Tier, fell.FallbackFrom));                         // the tier and model the call started with are back, and the option that failed is recorded
        Assert.False(_s.State().SpeechMode);

        using var later = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig([Number]), services: sv => sv.AddSingleton<IChainedPipelineParts>(_parts));
        (await VoiceFactory.Hook(later.C, VoiceEndpoints.InboundPath, later.Sid, later.From)).EnsureSuccessStatusCode();
        await VoiceFactory.Hook(later.C, VoiceEndpoints.PipelinePath, later.Sid, later.From, extra: new() { ["Digits"] = "6" });
        var failed = XDocument.Parse(await (await VoiceFactory.Hook(later.C, VoiceEndpoints.SpeechPath, later.Sid, later.From, extra: new() { ["SpeechResult"] = "hello" })).Content.ReadAsStringAsync()).Root!;
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, failed.Element("Redirect")!.Value);
        var after = await later.AfterStream();
        Assert.Equal(PipelineScripts.VendorLost, after.Element("Say")!.Value);
        Assert.Equal("vendor_lost", (await later.Row()).Outcome);
        Assert.DoesNotContain(_s.F.Logs.ToString() ?? "", "secret words");
    }

    [Fact]
    public async Task An_unsigned_request_is_refused_and_a_call_that_is_not_on_option_six_is_sent_on_to_after_stream()
    {
        await Choose();
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await VoiceFactory.Hook(_s.C, VoiceEndpoints.SpeechPath, _s.Sid, _s.From, signature: "bad")).StatusCode);
        using var other = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig([Number]), services: sv => sv.AddSingleton<IChainedPipelineParts>(_parts));
        (await VoiceFactory.Hook(other.C, VoiceEndpoints.InboundPath, other.Sid, other.From)).EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await (await VoiceFactory.Hook(other.C, VoiceEndpoints.SpeechPath, other.Sid, other.From, extra: new() { ["SpeechResult"] = "hi" })).Content.ReadAsStringAsync()).Root!;
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, xml.Element("Redirect")!.Value);
        Assert.Empty(Llm.Requests);
    }

    [Fact]
    public async Task The_gather_carries_the_configured_language_model_hints_and_voice()
    {
        using var t = new VoiceP6SpeechTests(new()
        {
            ["Voice:SignalWireSpeech:Language"] = "he-IL", ["Voice:SignalWireSpeech:Enhanced"] = "true", ["Voice:SignalWireSpeech:SpeechModel"] = "phone_call", ["Voice:SignalWireSpeech:SpeechTimeout"] = "2",
            ["Voice:SignalWireSpeech:Hints"] = "bagel, challah", ["Voice:SignalWireSpeech:Voice"] = "Polly.Joanna", ["Voice:SignalWireSpeech:NoInputSeconds"] = "5",
        });
        await t.Choose();
        var g = (await t.Post(Speech("?first=1"))).Element("Gather")!;
        Assert.Equal(("he-IL", "true", "phone_call", "2", "bagel, challah", "5"), (g.Attribute("language")!.Value, g.Attribute("enhanced")!.Value, g.Attribute("speechModel")!.Value, g.Attribute("speechTimeout")!.Value, g.Attribute("hints")!.Value, g.Attribute("timeout")!.Value));
        Assert.Equal("Polly.Joanna", g.Element("Say")!.Attribute("voice")!.Value);
    }

    [Fact]
    public void Option_six_is_available_only_when_switched_on_and_the_text_model_has_its_key_and_a_wrong_value_is_a_start_up_problem_only_with_the_menu_on()
    {
        VoiceOptions Ok() { var o = new VoiceOptions(); o.Pipelines.P6.Enabled = true; o.Anthropic.ApiKey = "k"; o.PipelineMenu.Enabled = true; return o; }
        Assert.Empty(VoicePipelineVendors.Missing(Ok(), "p6"));
        var off = Ok(); off.Pipelines.P6.Enabled = false;
        Assert.Contains("Voice:Pipelines:P6:Enabled", VoicePipelineVendors.Missing(off, "p6"));
        var nokey = Ok(); nokey.Anthropic.ApiKey = "";
        Assert.Contains("Voice:Anthropic:ApiKey", VoicePipelineVendors.Missing(nokey, "p6"));
        List<string> Problems(Action<VoiceOptions> f) { var o = Ok(); f(o); var p = new List<string>(); VoicePipelineVendors.Problems(o, p); return p; }
        Assert.Empty(Problems(_ => { }));
        Assert.Contains(Problems(o => o.SignalWireSpeech.Language = "en US<"), p => p.Contains("SignalWireSpeech:Language"));
        Assert.Contains(Problems(o => o.SignalWireSpeech.SpeechTimeout = "forever"), p => p.Contains("SpeechTimeout"));
        Assert.Contains(Problems(o => o.SignalWireSpeech.TurnSeconds = 60), p => p.Contains("TurnSeconds"));
        Assert.Contains(Problems(o => o.SignalWireSpeech.Hints = new string('h', 501)), p => p.Contains("Hints"));
        Assert.Empty(Problems(o => { o.PipelineMenu.Enabled = false; o.SignalWireSpeech.Language = "!!"; }));
    }
}
