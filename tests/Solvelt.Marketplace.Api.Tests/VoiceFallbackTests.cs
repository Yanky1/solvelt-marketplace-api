using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// What a failing vendor does (decision V-25). Before the call begins: the call is handed to the Realtime line on the same call, with the tier and model it started with put back, the failed option recorded and the call priced
/// by the real model. During the call: continuing on Realtime is not possible without a second greeting (the Realtime bridge needs the phone stream's start event, which the chained bridge has consumed), so the platform
/// apologises (never through the failing vendor) and the call ends as <c>vendor_lost</c> with the reason logged by type.
/// </summary>
public sealed class VoiceFallbackTests : IDisposable
{
    private const string Number = "+15551234567";
    private const string Hello = "Hello and welcome to Solvelt. How can I help you today?";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    private readonly CancellationTokenSource _stop = new();

    public void Dispose() { _stop.Cancel(); _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    private static Dictionary<string, string?> Mini(Dictionary<string, string?> settings) { settings["Voice:ModelMode"] = "mini"; return settings; }

    /// <summary>A call that chose option <paramref name="digit"/> with the vendors behind it scripted by the caller of this method; the phone is connected when this returns.</summary>
    private async Task Call(string digit, Dictionary<string, string?> settings, FakeSocketConnector sockets, FakeLlmHandler llm, FakeLlmHandler? gladia = null)
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Mini(settings)), services: svc =>
        {
            svc.RemoveAll<IVendorSocketConnector>(); svc.AddSingleton<IVendorSocketConnector>(sockets);
            svc.AddHttpClient(VoicePipelineVendors.LlmClient).ConfigurePrimaryHttpMessageHandler(() => llm);
            if (gladia is not null) svc.AddHttpClient(VoicePipelineVendors.GladiaClient).ConfigurePrimaryHttpMessageHandler(() => gladia);
        });
        _ = Task.Run(() => VoiceP2PipelineTests.ElevenLabsVendor(sockets, _stop.Token));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, Number, extra: new() { ["Digits"] = digit });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
    }

    private static FakeLlmHandler Llm() => new() { Body = FakeLlmHandler.Sse($$"""{"type":"response.output_text.delta","delta":"{{Hello}}"}""", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9}}}""") };
    private static async Task Until(Func<bool> done, int seconds = 5) => await FakeRealtime.Poll(() => done() ? "x" : null, seconds);
    private IEnumerable<System.Text.Json.Nodes.JsonNode> Frames(string name) => _phone!.Got.Where(n => n["event"]?.GetValue<string>() == name);

    // ---------- before the call begins ----------

    [Fact]
    public async Task When_Deepgram_refuses_at_the_start_the_call_goes_on_on_the_Realtime_line_with_its_real_model_and_is_priced_by_it()
    {
        await Call("2", VoiceP2PipelineTests.P2Settings(), new FakeSocketConnector { FailWhen = u => u.Host.Contains("deepgram") }, Llm());
        await Until(() => S.Fake.Of("session.update").Any());                                  // the Realtime session opened for this call
        var row = await S.Row();
        Assert.Equal(("mini", "model-mini"), (row.Tier, row.Model));                           // the models the call started with, put back
        Assert.Equal(("mini", "model-mini"), (row.BaseTier, row.BaseModel));
        Assert.Equal("p2", row.FallbackFrom);                                                  // counted for the option that failed
        Assert.Null(row.EndedAt);
        await S.Fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>(), usage = new { input_token_details = new { audio_tokens = 1_000_000, text_tokens = 0 }, output_token_details = new { audio_tokens = 0, text_tokens = 0 } } } });
        await Until(() => S.Row().Result.EstimatedCostMicros > 0);
        Assert.Equal(10_000_000, (await S.Row()).EstimatedCostMicros);                          // the mini model's price, not a pipeline price
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("could not open its speech recogniser") && l.Contains("WebSocketException") && l.Contains("Realtime line"));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("vendor refused") || l.Contains("dg-key-not-real"));
    }

    [Fact]
    public async Task When_Gladia_refuses_the_session_p3_hands_over_the_same_way()
    {
        var gladia = new FakeLlmHandler { Status = HttpStatusCode.Unauthorized, Body = "no" };
        await Call("3", VoiceP3PipelineTests.P3Settings(), new FakeSocketConnector(), Llm(), gladia);
        await Until(() => S.Fake.Of("session.update").Any());
        var row = await S.Row();
        Assert.Equal(("mini", "model-mini", "p3"), (row.Tier, row.Model, row.FallbackFrom));
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("option p3 could not open") && l.Contains("HttpRequestException"));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("gl-key-not-real"));
    }

    [Fact]
    public async Task The_fallback_happens_once_and_only_for_a_call_that_chose_a_pipeline()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Mini(VoiceP2PipelineTests.P2Settings())));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var store = S.F.Services.GetRequiredService<VoiceCallStore>();
        var id = (await S.Row()).Id;
        Assert.Null(await store.FallBackToRealtimeAsync(id, CancellationToken.None));          // a call that did not choose a pipeline has nothing to fall back to
        Assert.True(await store.SetPipelineAsync(id, "p2", CancellationToken.None));
        var first = await store.FallBackToRealtimeAsync(id, CancellationToken.None);
        Assert.Equal(("mini", "model-mini", "p2"), (first!.Tier, first.Model, first.FallbackFrom));
        Assert.Null(await store.FallBackToRealtimeAsync(id, CancellationToken.None));          // once
    }

    [Fact]
    public async Task The_menu_keeps_the_original_choice_once_even_when_it_is_used_twice_and_option_one_changes_nothing_else()
    {
        _s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: Mini(VoiceP2PipelineTests.P2Settings())));
        await S.SyncCatalog();
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, Number);
        var store = S.F.Services.GetRequiredService<VoiceCallStore>();
        var id = (await S.Row()).Id;
        Assert.Equal(("mini", "model-mini", "", ""), ((await S.Row()).Tier, (await S.Row()).Model, (await S.Row()).BaseTier, (await S.Row()).BaseModel));
        await store.SetPipelineAsync(id, "p2", CancellationToken.None);
        await store.SetPipelineAsync(id, "p3", CancellationToken.None);                        // a second choice must not overwrite the kept original with "pipeline:p2"
        var row = await S.Row();
        Assert.Equal(("p3", "pipeline:p3", "mini", "model-mini"), (row.Tier, row.Model, row.BaseTier, row.BaseModel));
        Assert.Null(row.FallbackFrom);
    }

    // ---------- during the call ----------

    private async Task<XElement> EndedWithApology()
    {
        await Until(() => S.Row().Result.EndedAt is not null);
        var row = await S.Row();
        Assert.Equal(VoiceOutcome.VendorLost, row.Outcome);
        Assert.Equal("p2", row.Tier);                                                          // the option that was running is what the record says
        Assert.Null(row.FallbackFrom);
        Assert.True(await _phone!.WaitClosed());                                               // the stream is closed by the server
        var xml = await S.AfterStream();
        Assert.Contains(PipelineScripts.VendorLost, xml.Descendants("Say").Select(e => e.Value));   // the PLATFORM apologises
        Assert.NotNull(xml.Element("Hangup"));
        return xml;
    }

    [Fact]
    public async Task When_the_recogniser_goes_away_during_the_call_the_platform_apologises_and_the_call_ends_as_vendor_lost()
    {
        var sockets = new FakeSocketConnector();
        await Call("2", VoiceP2PipelineTests.P2Settings(), sockets, Llm());
        await Until(() => Frames("media").Count() >= 4);                                       // the call was working
        var dg = sockets.Connections.First(c => c.Uri.Host == "api.deepgram.com");
        dg.Pair.Server.Abort();                                                                // Deepgram drops
        await EndedWithApology();
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("ended on a vendor failure") && l.Contains("the speech recogniser"));
    }

    [Fact]
    public async Task When_the_voice_vendor_fails_during_the_call_the_platform_apologises_and_only_the_type_is_logged()
    {
        var sockets = new FakeSocketConnector { FailWhen = u => u.Host.Contains("elevenlabs") };          // Deepgram opens fine, the voice never speaks
        await Call("2", VoiceP2PipelineTests.P2Settings(), sockets, Llm());
        await EndedWithApology();
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("the speech vendor failed") && l.Contains("WebSocketException"));
        Assert.Contains(S.F.Logs.Lines, l => l.Contains("ended on a vendor failure") && l.Contains("a turn"));
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("vendor refused") || l.Contains("el-key-not-real"));
    }

    [Fact]
    public async Task A_failing_text_model_turn_is_not_a_vendor_loss_the_call_stays_open_as_decided_in_V_19()
    {
        var llm = new FakeLlmHandler { Status = HttpStatusCode.InternalServerError, Body = "my secret words" };
        await Call("2", VoiceP2PipelineTests.P2Settings(), new FakeSocketConnector(), llm);
        await Until(() => S.F.Logs.Lines.Any(l => l.Contains("a turn failed") && l.Contains("HttpRequestException")));
        await Task.Delay(150);
        var row = await S.Row();
        Assert.Null(row.EndedAt);                                                              // the call goes on
        Assert.NotEqual(VoiceOutcome.VendorLost, row.Outcome);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, _phone!.Socket.State);
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("secret") || l.Contains("ended on a vendor failure"));
    }

    [Fact]
    public async Task A_caller_who_hangs_up_is_not_a_vendor_failure()
    {
        var sockets = new FakeSocketConnector();
        await Call("2", VoiceP2PipelineTests.P2Settings(), sockets, Llm());
        await Until(() => Frames("media").Count() >= 4);
        await _phone!.Socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);   // the caller hangs up
        await Until(() => S.Row().Result.EndedAt is not null);
        var row = await S.Row();
        Assert.Equal(VoiceOutcome.Abandoned, row.Outcome);
        Assert.DoesNotContain(S.F.Logs.Lines, l => l.Contains("ended on a vendor failure"));
        var xml = await S.AfterStream();
        Assert.DoesNotContain(PipelineScripts.VendorLost, xml.Descendants("Say").Select(e => e.Value));
    }
}
