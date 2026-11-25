using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Decision V-54: the platform's fixed lines (the off-topic redirect and the other canned sentences) are never replayed to the text model as its own earlier turns (a self-reinforcing refusal loop seen in an older
/// system). What is shown to the model is cleaned; what the caller hears is not. Unit tests of the cleaning, and the whole chained pipeline on fakes.
/// </summary>
public sealed class VoiceFixedLinesTests : IDisposable
{
    private const string Number = "+15551234567";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    public void Dispose() { _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    private static LlmMessage U(string t) => new("user", t);
    private static LlmMessage A(string t) => new("assistant", t);
    private static LlmMessage Call(string id, string name = "off_topic") => new("assistant", null, [new LlmToolCall(id, name, "{}")]);
    private static LlmMessage Result(string id, string text) => new("tool", text, null, id);
    private static string OffTopicResult => $$"""{"status":"off_topic","say":"{{VoiceScripts.OffTopicRedirect}}"}""";

    // ---------- the cleaning, alone ----------

    [Fact]
    public void Past_off_topic_redirects_are_cleaned_and_the_current_turn_is_left_alone()
    {
        var h = new List<LlmMessage>
        {
            U("what is the weather"), Call("c1"), Result("c1", OffTopicResult), A(VoiceScripts.OffTopicRedirect),
            U("tell me a joke"), Call("c2"), Result("c2", OffTopicResult), A(VoiceScripts.OffTopicRedirect),
            U("what do you sell"),
        };
        var shown = ModelHistory.ForModel(h);
        Assert.DoesNotContain(shown, m => (m.Text ?? "").Contains("I can only help with Solvelt"));
        Assert.Equal(2, shown.Count(m => m.Role == "tool" && m.Text == ModelHistory.OffTopicNeutral));
        Assert.Equal(["user", "assistant", "tool", "user", "assistant", "tool", "user"], shown.Select(m => m.Role));     // the calls stay with their results, the echoed sentences go
        Assert.Equal("what do you sell", shown[^1].Text);
        // the model has JUST called off_topic: the sentence it must say is still given to it
        var inFlight = new List<LlmMessage> { U("what is the weather"), Call("c1"), Result("c1", OffTopicResult) };
        Assert.Same(inFlight, ModelHistory.ForModel(inFlight));
        Assert.Contains(VoiceScripts.OffTopicRedirect, ModelHistory.ForModel(inFlight)[^1].Text!);
        // also when an earlier turn was an off-topic one and this turn calls it again
        var again = h.Take(8).Append(U("one more joke")).Append(Call("c3")).Append(Result("c3", OffTopicResult)).ToList();
        var shownAgain = ModelHistory.ForModel(again);
        Assert.Equal(OffTopicResult, shownAgain[^1].Text);
        Assert.Equal(2, shownAgain.Count(m => m.Text == ModelHistory.OffTopicNeutral));
    }

    [Fact]
    public void Any_other_fixed_line_the_model_quotes_is_left_out_and_ordinary_history_is_the_same_list()
    {
        var h = new List<LlmMessage> { U("hello"), A(VoiceScripts.ConfirmRetry), U("yes"), A("Sure, one moment."), U("thanks") };
        var shown = ModelHistory.ForModel(h);
        Assert.DoesNotContain(shown, m => m.Text == VoiceScripts.ConfirmRetry);
        Assert.Contains(shown, m => m.Text == "Sure, one moment.");                                                     // the model's own words stay
        var plain = new List<LlmMessage> { U("hello"), A("Hi there."), U("what do you sell") };
        Assert.Same(plain, ModelHistory.ForModel(plain));                                                               // nothing to clean: no copy
        Assert.Same(plain.Take(0).ToList() is var empty ? empty : null, ModelHistory.ForModel(empty));                  // no history at all
        Assert.True(ModelHistory.IsFixedLine("  Sorry,   I can only help with Solvelt products, orders and your account. What can I help you with? "));
        Assert.False(ModelHistory.IsFixedLine("Sorry, I can only help with the weather."));
        Assert.False(ModelHistory.IsFixedLine(VoiceScripts.Greeting));                                                  // an instruction GIVEN to the model, not a line the caller hears
    }

    [Fact]
    public void Every_fixed_spoken_line_is_known_and_the_instructions_are_not()
    {
        foreach (var line in new[] { VoiceScripts.Disclosure, VoiceScripts.AskCode, VoiceScripts.WrongCode, VoiceScripts.ConfirmRetry, VoiceScripts.PayFailed, VoiceScripts.AskPin, VoiceScripts.PinLocked, VoiceScripts.Trouble, VoiceScripts.OffTopicRedirect, VoiceScripts.OffTopicTransfer, VoiceScripts.Connecting })
            Assert.True(ModelHistory.IsFixedLine(line), line);
        foreach (var instruction in new[] { VoiceScripts.Greeting, VoiceScripts.Resume, VoiceScripts.Filler, VoiceScripts.Instructions }) Assert.False(ModelHistory.IsFixedLine(instruction));
    }

    // ---------- the chained pipeline, on fakes ----------

    private async Task<FakeParts> Open()
    {
        var parts = new FakeParts("p5");
        parts.LlmFake.Then(new LlmTextDelta("Hello."), new LlmCompleted(10, 2));
        parts.LlmFake.Default = req => req.Messages[^1].Role switch
        {
            "user" when req.Messages[^1].Text!.Contains("weather") => [new LlmToolCallEvent(new LlmToolCall("c" + req.Messages.Count, "off_topic", "{}")), new LlmCompleted(10, 2)],
            "tool" => [new LlmTextDelta(VoiceScripts.OffTopicRedirect), new LlmCompleted(10, 2)],                          // the model says the one sentence the server gave it
            _ => [new LlmTextDelta("We sell kitchenware."), new LlmCompleted(10, 2)],
        };
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(), services: sv => sv.AddSingleton<IChainedPipelineParts>(parts));
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "5" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        for (var i = 0; i < 20 && parts.LlmFake.Requests.Count < 1; i++) await Advance(100);                               // the greeting has been asked for
        return parts;
    }

    private async Task Advance(int ms) { for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); } }

    /// <summary>One caller turn; waits (fake time moving) until the model has been asked <paramref name="requests"/> more times, then lets the reply play out.</summary>
    private async Task Turn(FakeParts p, string text, int requests)
    {
        var want = p.LlmFake.Requests.Count + requests;
        p.SttFake.Emit(new SttEvent(SttEventKind.SpeechStarted)); p.SttFake.Emit(new SttEvent(SttEventKind.Final, text, "en", null, null)); p.SttFake.Emit(new SttEvent(SttEventKind.SpeechEnded));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (p.LlmFake.Requests.Count < want && DateTime.UtcNow < deadline) await Advance(100);
        Assert.True(p.LlmFake.Requests.Count >= want, "the text model was not asked");
        await Advance(800);
    }

    [Fact]
    public async Task Two_off_topic_redirects_then_an_on_topic_question_the_model_sees_neither_redirect_but_the_turn_that_needed_it_got_it_V54()
    {
        var p = await Open();
        await Turn(p, "what is the weather", 2);    // the model calls off_topic, then says the sentence
        await Turn(p, "what is the weather again", 2);
        await Turn(p, "what do you sell", 1);
        var requests = p.LlmFake.Requests;
        // each off-topic turn's second request carried the sentence it was to say
        Assert.Equal(2, requests.Count(r => r.Messages[^1].Role == "tool" && r.Messages[^1].Text!.Contains(VoiceScripts.OffTopicRedirect)));
        // the on-topic request carries neither redirect, as a tool result or as an assistant turn
        var last = requests.Last(r => r.Messages[^1].Role == "user" && r.Messages[^1].Text == "what do you sell");
        Assert.DoesNotContain(last.Messages, m => (m.Text ?? "").Contains("I can only help with Solvelt"));
        Assert.Equal(2, last.Messages.Count(m => m.Role == "tool" && m.Text == ModelHistory.OffTopicNeutral));
        // the caller heard the redirect twice: it is only the model's view that is cleaned
        Assert.Equal(2, p.TtsFake.Texts.Count(t => t.Contains("I can only help with Solvelt")));
    }
}
