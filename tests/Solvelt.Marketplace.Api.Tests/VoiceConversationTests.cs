using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Conversation quality and continuity through whole scripted calls (brief: Vapi-level conversation, multi-turn).</summary>
public class VoiceConversationTests
{
    private sealed class SlowTool(TimeProvider time) : IVoiceTool
    {
        public string Name => "slow_lookup";
        public JsonObject Definition => new() { ["type"] = "function", ["name"] = Name, ["description"] = "test", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
        public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct) { await Task.Delay(TimeSpan.FromSeconds(2), time, ct); return new("""{"ok":true}"""); }
    }

    private sealed class KeypadTool : IVoiceTool
    {
        public string Name => "begin_keypad";
        public JsonObject Definition => new() { ["type"] = "function", ["name"] = Name, ["description"] = "test", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
        public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
        {
            ctx.State.SetCart("c1", 2); ctx.State.SetAddress("home"); ctx.State.SetTask("delivery");
            ctx.State.BeginKeypadStep("sms_code");
            return Task.FromResult(new VoiceToolResult("""{"status":"keypad"}""", EndCall: true));
        }
    }

    private static TestFactory Create(FakeRealtime fake, Dictionary<string, string?>? extra = null) => new(VoiceFactory.Config(extra), s =>
    {
        s.RemoveAll<IRealtimeConnector>(); s.AddSingleton<IRealtimeConnector>(fake);
        s.AddScoped<IVoiceTool, SlowTool>(); s.AddScoped<IVoiceTool, KeypadTool>();
    });

    private static object Call(string name, string callId) => new { type = "response.done", response = new { output = new object[] { new { type = "function_call", name, call_id = callId, arguments = "{}" } } } };
    private static bool IsOutput(JsonNode n) => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["type"]!.GetValue<string>() == "function_call_output";

    [Fact]
    public async Task A_slow_tool_makes_the_agent_say_a_short_filler_first_and_the_answer_waits_for_it()
    {
        var fake = new FakeRealtime();
        using var f = Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Wait("session.update");
        await fake.Send(Call("slow_lookup", "call_s"));
        await Task.Delay(100);
        Assert.DoesNotContain(fake.Of("response.create"), n => n["response"]?["instructions"]?.GetValue<string>().Contains("waiting for a lookup") == true);   // not before the delay
        f.Clock.Advance(TimeSpan.FromMilliseconds(800));                                          // past the filler delay, before the tool is done
        var filler = await fake.Wait(n => n["type"]?.GetValue<string>() == "response.create" && n["response"]?["instructions"]?.GetValue<string>().Contains("waiting for a lookup") == true);
        Assert.Equal("none", filler["response"]!["tool_choice"]!.GetValue<string>());
        Assert.DoesNotContain(fake.Received, m => m.Contains("function_call_output"));            // no answer yet
        f.Clock.Advance(TimeSpan.FromSeconds(2));                                                 // the tool is done, the filler is still speaking
        await Task.Delay(150);
        Assert.DoesNotContain(fake.Received, m => m.Contains("function_call_output"));            // the answer waits for the filler
        await fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });   // the filler is finished
        var output = await fake.Wait(IsOutput);
        Assert.Equal("call_s", output["item"]!["call_id"]!.GetValue<string>());
        Assert.Contains("ok", output["item"]!["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_fast_tool_gets_no_filler()
    {
        var fake = new FakeRealtime();
        using var f = Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Wait("session.update");
        await fake.Send(Call("transfer_to_person", "call_f"));
        await fake.Wait(IsOutput);
        Assert.DoesNotContain(fake.Of("response.create"), n => n["response"]?["instructions"]?.GetValue<string>().Contains("waiting for a lookup") == true);
    }

    [Fact]
    public async Task A_keypad_step_ends_the_stream_without_closing_the_call_and_the_next_stream_resumes_with_the_server_summary_in_the_same_voice()
    {
        var fake = new FakeRealtime();
        using var f = Create(fake, new() { ["Voice:OpenAiVoice"] = "cedar" });
        var c = f.CreateClient();
        var token = await VoiceFactory.Answer(c, "CA-kp");
        var phone = await FakePhone.Connect(f, token);
        await phone.Start();
        await fake.Wait("session.update");
        await fake.Send(Call("begin_keypad", "call_k"));
        await fake.Wait(IsOutput);
        await fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });   // the model's last words before the keypad
        var mark = await phone.Wait("mark");
        await phone.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = mark["mark"]!["name"]!.GetValue<string>() } });
        Assert.True(await phone.WaitClosed());
        phone.Dispose();
        await Task.Delay(200);
        // the call is NOT over: no end time, no outcome, the state is kept
        using (var scope = f.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Data.MarketplaceDb>().VoiceCalls.Where(x => x.CallSid == "CA-kp").Select(x => new { x.EndedAt, x.Outcome }).SingleAsync();
            Assert.Null(row.EndedAt); Assert.Equal("in_progress", row.Outcome);
        }
        var before = fake.Received.Count;
        var state = f.Services.GetRequiredService<VoiceStateStore>().Find((await Task.Run(async () => { using var s = f.Services.CreateScope(); return (await s.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Data.MarketplaceDb>().VoiceCalls.SingleAsync()).Id; })))!;
        state.EndKeypadStep();   // slice 2's Gather ends here
        // SignalWire asks again: the same call gets a fresh stream token
        var again = await VoiceFactory.Answer(c, "CA-kp");
        using var phone2 = await FakePhone.Connect(f, again);
        await phone2.Start();
        var item = await fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system");
        var text = item["item"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("2 items", text); Assert.Contains("delivery", text); Assert.Contains("home", text);
        var resume = await fake.Wait(n => n["type"]?.GetValue<string>() == "response.create" && n["response"]?["instructions"]?.GetValue<string>().Contains("keypad step") == true);
        Assert.NotNull(resume);
        var second = fake.Received.Skip(before).Select(m => JsonNode.Parse(m)!).ToList();
        Assert.DoesNotContain(second, n => n["response"]?["instructions"]?.GetValue<string>() == VoiceScripts.Greeting);   // no second greeting
        Assert.Equal("cedar", second.First(n => n["type"]!.GetValue<string>() == "session.update")["session"]!["audio"]!["output"]!["voice"]!.GetValue<string>());   // same voice and prompt
        Assert.Equal(VoiceScripts.Instructions, second.First(n => n["type"]!.GetValue<string>() == "session.update")["session"]!["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_state_is_dropped_when_the_call_ends()
    {
        var fake = new FakeRealtime();
        using var f = Create(fake);
        var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Wait("session.update");
        Assert.Equal(1, f.Services.GetRequiredService<VoiceStateStore>().Count);
        await phone.Send(new { @event = "stop" });
        await FakeRealtime.Poll(() => f.Services.GetRequiredService<VoiceStateStore>().Count == 0 ? "x" : null, 5);
    }

    [Fact]
    public void The_instructions_set_the_persona_and_the_speaking_rules()
    {
        var i = VoiceScripts.Instructions;
        foreach (var must in new[] { "Short spoken sentences", "contractions", "one question at a time", "at most three options", "twelve ninety-nine", "wait, go back", "where things stand", "one consistent person", "keypad" })
            Assert.Contains(must, i);
    }
}
