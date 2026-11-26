using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The chained pipeline skeleton (decision V-19) against fakes: speech-to-text -> end-of-turn -> our LLM with the same instructions and tools -> text-to-speech, on the Realtime bridge's phone side
/// (marks, clear, keypad steps). No vendor is involved; every stage is a fake.
/// </summary>
public sealed class VoiceChainedPipelineTests : IDisposable
{
    private const string Number = "+15551234567", Email = "jane@example.com";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;

    public void Dispose() { _phone?.Dispose(); _s?.Dispose(); }

    private VoiceScenario S => _s!;

    /// <summary>A call that chose option p2 on the test-line menu and is connected to the fake stages. The opening LLM turn plays <paramref name="greeting"/>.</summary>
    private async Task<FakeParts> Call(string greeting = Hello, Dictionary<string, string?>? more = null, bool account = false, Action<FakeParts>? script = null)
    {
        var parts = new FakeParts("p2");
        parts.LlmFake.Then(new LlmTextDelta(greeting), new LlmCompleted(100, 15));
        script?.Invoke(parts);
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: more), services: svc => svc.AddSingleton<IChainedPipelineParts>(parts));
        await S.SyncCatalog();
        if (account) { await S.K.VerifiedSession(Email); await S.SetPhone(Email, "(555) 123-4567", verified: true); }
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = "2" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return parts;
    }

    /// <summary>Lets fake time run (the end-of-turn engine and the tool filler wait on it) while the stages work.</summary>
    private async Task Advance(int ms)
    {
        for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); }
    }

    /// <summary>
    /// After the caller has spoken: lets fake time run until <paramref name="done"/> holds. A fixed <c>Advance(1000)</c> is a race: the recogniser's events reach the end-of-turn engine on their own thread, and the engine
    /// stamps a final with the fake time at ITS arrival, so a final that arrives after the fixed span has run out never sees the silence it waits for and the turn never ends (main CI run 637).
    /// </summary>
    private async Task AdvanceUntil(Func<bool> done, int seconds = 5)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("the expected event did not arrive");
            S.F.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(3);
        }
    }

    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);

    private IEnumerable<JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);

    // ---------- the call itself ----------

    [Fact]
    public async Task The_greeting_is_written_by_our_LLM_with_the_same_instructions_and_tools_and_spoken_to_the_phone_with_marks()
    {
        var p = await Call();
        await Until(() => Frames("media").Count() >= 6);
        var first = p.LlmFake.Requests[0];
        Assert.Equal(VoiceScripts.Instructions, first.Instructions);                                         // the SAME instructions as the Realtime line
        var runner = S.F.Services.GetRequiredService<VoiceToolRunner>();
        Assert.Equal(runner.Definitions().Select(d => d!["name"]!.GetValue<string>()).Order(), first.Tools.Select(d => d!["name"]!.GetValue<string>()).Order());   // the SAME tools, off_topic included
        Assert.Contains("off_topic", first.Tools.Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(("system", VoiceScripts.Greeting), (first.Messages[^1].Role, first.Messages[^1].Text));
        // two sentences, spoken one after the other; every audio message is followed by its mark (the V-12 marks)
        Assert.Equal(["Hello and welcome to Solvelt.", "How can I help you today?"], p.TtsFake.Texts);
        var frames = _phone!.Got.Where(n => n["event"]!.GetValue<string>() is "media" or "mark").Select(n => n["event"]!.GetValue<string>()).ToList();
        Assert.Equal(Enumerable.Repeat(new[] { "media", "mark" }, 6).SelectMany(x => x), frames.Take(12));
        Assert.All(Frames("media"), m => Assert.Equal(800, Convert.FromBase64String(m["media"]!["payload"]!.GetValue<string>()).Length));
        var row = await S.Row();
        Assert.Equal(("p2", "pipeline:p2"), (row.Tier, row.Model));
        Assert.Empty(S.Fake.Of("session.update"));                                                           // not the Realtime line
    }

    [Fact]
    public async Task A_turn_ends_after_the_silence_the_caller_audio_reaches_only_the_STT_and_the_answer_is_timed()
    {
        var p = await Call(script: p => p.LlmFake.Then(new LlmTextDelta("Sure, one challah coming up."), new LlmCompleted(120, 12)));
        await Until(() => Frames("media").Count() >= 6);
        await _phone!.Media(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));
        await Until(() => p.SttFake.Audio.Count == 1);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, p.SttFake.Audio[0]);
        p.SttFake.Say("I would like a challah");
        await Advance(300);
        Assert.Single(p.LlmFake.Requests);                                                                   // 300 ms of silence is not the end of the turn yet
        await AdvanceUntil(() => p.LlmFake.Requests.Count == 2);
        var second = p.LlmFake.Requests[1];
        Assert.Contains(second.Messages, m => m is { Role: "user", Text: "I would like a challah" });
        await Until(() => p.TtsFake.Texts.Contains("Sure, one challah coming up."));
        var row = await FakeRealtimeRow(1);
        Assert.True(row.LatencySamples >= 1);                                                                // caller stops -> first audio, per call
    }

    private async Task<VoiceCall> FakeRealtimeRow(int samples)
    {
        await FakeRealtime.Poll(() => S.Row().Result.LatencySamples >= samples ? "x" : null, 5);
        return await S.Row();
    }

    [Fact]
    public async Task Tokens_and_the_estimated_cost_are_recorded_per_call_from_the_priced_stages()
    {
        var more = new Dictionary<string, string?> { ["Voice:PipelineMenu:Prices:LlmInPerMillionTokens"] = "2", ["Voice:PipelineMenu:Prices:LlmOutPerMillionTokens"] = "4", ["Voice:PipelineMenu:Prices:TtsPerMillionChars"] = "10" };
        var p = await Call(more: more, script: p => p.LlmFake.Then(new LlmTextDelta("Okay, noted."), new LlmCompleted(100, 15)));
        await Until(() => Frames("media").Count() >= 6);
        p.SttFake.Say("hello there");
        await AdvanceUntil(() => p.LlmFake.Requests.Count == 2);
        await Until(() => p.TtsFake.Texts.Contains("Okay, noted."));
        await _phone!.Send(new { @event = "stop" });                                                          // the caller hangs up
        await FakeRealtime.Poll(() => S.Row().Result.EndedAt is not null ? "x" : null, 5);
        var row = await S.Row();
        // 100+100 input tokens at 2 micro-dollars and 15+15 output at 4 = 400 + 120; spoken characters at $10 per million are 10 micro-dollars each: 29 + 25 + 12 = 66 -> 660
        Assert.Equal((200, 30), (row.TextInTokens, row.TextOutTokens));
        Assert.Equal(1180, row.EstimatedCostMicros);
    }

    // ---------- barge-in with the same marks ----------

    [Fact]
    public async Task When_the_caller_talks_over_the_answer_the_phone_queue_is_cleared_and_the_answer_is_cut_to_what_was_heard()
    {
        var p = await Call();
        await Until(() => Frames("mark").Count() >= 6);                                                       // 600 ms of audio are queued on the "phone", none played back yet
        S.F.Clock.Advance(TimeSpan.FromMilliseconds(150));                                                    // 150 ms of it has played
        p.SttFake.Emit(new SttEvent(SttEventKind.SpeechStarted));
        await _phone!.Wait("clear");                                                                          // the phone's queue was flushed
        p.SttFake.Emit(new SttEvent(SttEventKind.Final, "never mind"));
        await AdvanceUntil(() => p.LlmFake.Requests.Count == 2);
        var history = p.LlmFake.Requests[1].Messages;
        var heard = history.Single(m => m.Role == "assistant").Text!;
        Assert.StartsWith("Hello", heard);
        Assert.True(heard.Length < "Hello and welcome to Solvelt.".Length);                                   // only the part that had played (150 of the 300 ms of the first sentence)
        Assert.DoesNotContain("How can I help", heard);
        Assert.Contains(history, m => m is { Role: "user", Text: "never mind" });
    }

    [Fact]
    public async Task Talking_when_nothing_is_queued_clears_nothing()
    {
        var p = await Call(script: p => { });
        await Until(() => Frames("mark").Count() >= 6);
        foreach (var m in Frames("mark").Select(x => x["mark"]!["name"]!.GetValue<string>()).ToList()) await _phone!.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = m } });   // the phone played it all
        await Task.Delay(100);
        p.SttFake.Emit(new SttEvent(SttEventKind.SpeechStarted));
        await Task.Delay(300);
        Assert.Empty(Frames("clear"));
    }

    // ---------- the same tools, off_topic and the keypad ----------

    [Fact]
    public async Task The_off_topic_tool_works_unchanged_three_strikes_end_the_AI_part_and_the_platform_speaks()
    {
        var p = await Call(script: p =>
        {
            p.LlmFake.Default = req => req.Messages[^1].Role switch
            {
                "user" => [new LlmToolCallEvent(new LlmToolCall("c" + req.Messages.Count, "off_topic", """{"category":"news_or_weather"}""")), new LlmCompleted(50, 5)],
                "tool" when req.Messages[^1].Text!.Contains("call_ending") => [new LlmCompleted(10, 0)],
                _ => [new LlmTextDelta("I can only help with Solvelt, shopping and your orders."), new LlmCompleted(40, 12)],
            };
        });
        await Until(() => Frames("media").Count() >= 6);
        for (var i = 1; i <= 3; i++)
        {
            p.SttFake.Say("what is the weather " + i);
            await AdvanceUntil(() => p.LlmFake.Requests.Count >= 1 + i * 2 - (i == 3 ? 1 : 0));
        }
        var end = await _phone!.Wait(n => n["event"]?.GetValue<string>() == "mark" && n["mark"]?["name"]?.GetValue<string>() == "end");   // the SERVER ends the AI part: the hand-off mark
        Assert.NotNull(end);
        await _phone.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = "end" } });
        Assert.True(await _phone.WaitClosed());
        var after = await S.AfterStream();
        Assert.Equal(VoiceScripts.OffTopicGoodbye, after.Element("Say")!.Value);                              // the platform says the line, never the model
        Assert.Equal(VoiceOutcome.EndedOffTopic, (await S.Row()).Outcome);
    }

    [Fact]
    public async Task A_keypad_step_ends_the_stream_the_code_is_typed_outside_every_stage_and_never_reaches_any_of_them()
    {
        var p = await Call(account: true, script: p =>
        {
            p.LlmFake.Then(new LlmToolCallEvent(new LlmToolCall("c1", "identify_caller", "{}")), new LlmCompleted(60, 4));
            p.LlmFake.Then(new LlmTextDelta("I have texted you a code. Please type it on your keypad."), new LlmCompleted(90, 14));
        });
        await Until(() => Frames("media").Count() >= 6);
        p.SttFake.Say("please sign me in");
        await AdvanceUntil(() => S.Sms.Sent.Count == 1);
        var code = S.LastCode();
        await Until(() => Frames("mark").Any(m => m["mark"]!["name"]!.GetValue<string>() == "end"));
        await _phone!.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = "end" } });
        Assert.True(await _phone.WaitClosed());
        // SignalWire's next instruction collects the digits ITSELF; the chained pipeline paused the call, it did not end it
        var gather = (await S.AfterStream()).Element("Gather")!;
        Assert.Equal("speech", gather.Attribute("input")!.Value);                                                   // the code is SAID to SignalWire's recogniser (V-41), no keypad
        Assert.Null((await S.Row()).EndedAt);
        var (resume, _) = await S.Type(code);
        Assert.Equal(VoiceScripts.CodeOk, resume.Element("Say")!.Value);
        // nothing the stages saw holds the code or the digits: the audio, the texts, the requests and the logs
        Assert.DoesNotContain(code, string.Concat(p.TtsFake.Texts));
        Assert.DoesNotContain(p.LlmFake.Requests, r => System.Text.Json.JsonSerializer.Serialize(r.Messages).Contains(code));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains(code));
    }

    [Fact]
    public async Task Keypad_digits_in_the_stream_are_dropped_before_every_stage_and_zero_still_asks_for_a_person()
    {
        var p = await Call(more: new() { ["Voice:SupportNumber"] = "+15550001111" });
        await Until(() => Frames("media").Count() >= 6);
        foreach (var d in new[] { "1", "5", "7", "#" }) await _phone!.Dtmf(d);
        await Task.Delay(200);
        Assert.Empty(p.SttFake.Audio);                                                                        // no digit became audio, text or a request
        Assert.Single(p.LlmFake.Requests);
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("digit"));
        await _phone!.Dtmf("0");
        Assert.True(await _phone.WaitClosed());
        var after = await S.AfterStream();
        Assert.Equal(VoiceScripts.Connecting, after.Element("Say")!.Value);                                   // the person is asked for exactly as on the Realtime line
        Assert.Equal("+15550001111", after.Element("Dial")!.Value);
    }

    // ---------- memory only ----------

    [Fact]
    public async Task What_was_said_stays_in_memory_nothing_of_it_is_stored_or_logged()
    {
        const string said = "my secret phrase about purple elephants", answer = "Purple elephants are not sold here, sorry about that.";
        var p = await Call(script: p => p.LlmFake.Then(new LlmTextDelta(answer), new LlmCompleted(50, 10)));
        await Until(() => Frames("media").Count() >= 6);
        p.SttFake.Say(said);
        await AdvanceUntil(() => p.TtsFake.Texts.Contains(answer));
        await _phone!.Send(new { @event = "stop" });
        await FakeRealtime.Poll(() => S.Row().Result.EndedAt is not null ? "x" : null, 5);
        var row = await S.Row();
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.DoesNotContain("purple", json, StringComparison.OrdinalIgnoreCase);                           // the call's row holds counters and a masked number only
        Assert.DoesNotContain("elephants", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("purple", StringComparison.OrdinalIgnoreCase) || l.Contains("elephant", StringComparison.OrdinalIgnoreCase) || l.Contains(Number.Replace("+", "")));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("welcome", StringComparison.OrdinalIgnoreCase));  // not even the greeting
        // and the state of the call is gone from memory once it is over
        Assert.Null(S.F.Services.GetRequiredService<VoiceStateStore>().Find(row.Id));
        // no table holds text of the call: the only voice table is the counters row (and payment attempts, which this call has none of)
        await S.K.Db(async db => { Assert.Equal(0, await db.VoicePaymentAttempts.CountAsync()); return 0; });
    }

    [Fact]
    public async Task A_failing_LLM_turn_never_ends_the_call_or_leaks_its_message_to_the_log()
    {
        var p = await Call(script: p => p.LlmFake.Then(_ => throw new InvalidOperationException("the secret in the error text")));
        // the greeting's script (queued first in Call) plays; the next script throws in the turn after the caller speaks
        await Until(() => Frames("media").Count() >= 6);
        p.SttFake.Say("hello");
        await AdvanceUntil(() => p.LlmFake.Requests.Count == 2);
        await Task.Delay(150);
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("the secret in the error text"));
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("a turn failed"));                                    // the type is logged, never the message
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, WebSocketStateOf(_phone!));
    }

    private static System.Net.WebSockets.WebSocketState WebSocketStateOf(FakePhone p) => p.Socket.State;
}
