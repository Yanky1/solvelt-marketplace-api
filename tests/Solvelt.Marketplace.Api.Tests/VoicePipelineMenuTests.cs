using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The test-line menu (decision V-19): off by default, offered only to the listed test numbers, press 1 to 5 before the AI starts, the choice recorded per call in the tier column.</summary>
public class VoicePipelineMenuTests
{
    private const string Number = "+15551234567", Other = "+15557770000";

    internal static Dictionary<string, string?> MenuConfig(string[]? numbers = null, Dictionary<string, string?>? more = null, bool enabled = true)
    {
        var d = new Dictionary<string, string?> { ["Voice:PipelineMenu:Enabled"] = enabled ? "true" : "false" };
        var list = numbers ?? [Number];
        for (var i = 0; i < list.Length; i++) d[$"Voice:PipelineMenu:TestNumbers:{i}"] = list[i];
        if (more is not null) foreach (var kv in more) d[kv.Key] = kv.Value;
        return d;
    }

    private static async Task<XElement> Inbound(VoiceScenario s, string? to = null)
        => XDocument.Parse(await (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, s.From, extra: to is null ? null : new() { ["To"] = to })).Content.ReadAsStringAsync()).Root!;

    private static async Task<(HttpResponseMessage Res, XElement Xml)> Choose(VoiceScenario s, string digits, string? to = null, string? signature = null)
    {
        var extra = new Dictionary<string, string> { ["Digits"] = digits };
        if (to is not null) extra["To"] = to;
        var r = await VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, s.From, signature, extra);
        return (r, r.IsSuccessStatusCode ? XDocument.Parse(await r.Content.ReadAsStringAsync()).Root! : new XElement("none"));
    }

    [Fact]
    public async Task The_menu_is_off_by_default_and_the_line_answers_exactly_as_before()
    {
        using var s = new VoiceScenario();
        var xml = await Inbound(s);
        Assert.Null(xml.Element("Gather"));
        Assert.Equal(VoiceScripts.Disclosure, xml.Element("Say")!.Value);
        Assert.NotNull(xml.Descendants("Stream").SingleOrDefault());
    }

    [Fact]
    public async Task Switched_on_it_is_still_not_offered_to_a_number_that_is_not_listed()
    {
        using var s = new VoiceScenario(Other, MenuConfig([Number]));
        var xml = await Inbound(s);
        Assert.Null(xml.Element("Gather"));
        Assert.NotNull(xml.Descendants("Stream").SingleOrDefault());
        // an empty list offers it to nobody, and a listed number with the switch off gets nothing either
        using var empty = new VoiceScenario(Number, MenuConfig([]));
        Assert.Null((await Inbound(empty)).Element("Gather"));
        using var off = new VoiceScenario(Number, MenuConfig([Number], enabled: false));
        Assert.Null((await Inbound(off)).Element("Gather"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_listed_caller_or_a_listed_dialed_test_line_hears_the_disclosure_then_the_menu(bool byDialedNumber)
    {
        using var s = byDialedNumber ? new VoiceScenario(Other, MenuConfig(["+15559876543"])) : new VoiceScenario(Number, MenuConfig([Number]));
        var xml = await Inbound(s);
        Assert.Equal(VoiceScripts.Disclosure, xml.Elements("Say").Single().Value);                          // the AI disclosure is the platform's and comes first
        var g = xml.Element("Gather")!;
        Assert.Equal(("dtmf", "1", "6", "true"), (g.Attribute("input")!.Value, g.Attribute("numDigits")!.Value, g.Attribute("timeout")!.Value, g.Attribute("actionOnEmptyResult")!.Value));
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.PipelinePath, g.Attribute("action")!.Value);
        Assert.Equal(PipelineScripts.Menu, g.Element("Say")!.Value);
        Assert.Null(xml.Descendants("Stream").SingleOrDefault());                                          // no AI until a choice is made
    }

    [Theory]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("abc")]
    public async Task One_an_empty_or_an_unknown_choice_is_option_one_the_current_line(string digits)
    {
        using var s = new VoiceScenario(Number, MenuConfig());
        await Inbound(s);
        var (res, xml) = await Choose(s, digits);
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal(PipelineScripts.Chosen("p1"), xml.Element("Say")!.Value);
        var (token, _) = await VoiceScenario.StreamFrom(res);
        Assert.NotEmpty(token);
        var row = await s.Row();
        Assert.Equal("p1", row.Tier);
        Assert.Equal("model-full", row.Model);                                                               // option 1 keeps the Realtime model it was assigned
        // and it really is the Realtime line: the stream opens the scripted Realtime session
        using var phone = await s.Attach(token);
        Assert.NotEmpty(s.Fake.Of("session.update"));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    public async Task An_option_whose_pipeline_is_not_configured_says_so_and_falls_back_to_option_one(string digit)
    {
        using var s = new VoiceScenario(Number, MenuConfig());
        await Inbound(s);
        var (res, xml) = await Choose(s, digit);
        Assert.Equal(PipelineScripts.Unavailable, xml.Element("Say")!.Value);
        var row = await s.Row();
        Assert.Equal(("p1", "model-full"), (row.Tier, row.Model));
        using var phone = await s.Attach((await VoiceScenario.StreamFrom(res)).Token);
        Assert.NotEmpty(s.Fake.Of("session.update"));                                                        // the Realtime line took the call
    }

    [Fact]
    public async Task A_configured_option_is_recorded_in_the_tier_column_and_names_the_pipeline_in_the_model_column()
    {
        var parts = new FakeParts("p3");
        using var s = new VoiceScenario(Number, MenuConfig(), services: svc => svc.AddSingleton<IChainedPipelineParts>(parts));
        await Inbound(s);
        var (res, xml) = await Choose(s, "3");
        Assert.Equal(PipelineScripts.Chosen("p3"), xml.Element("Say")!.Value);
        Assert.NotNull(xml.Descendants("Stream").SingleOrDefault());
        var row = await s.Row();
        Assert.Equal(("p3", "pipeline:p3"), (row.Tier, row.Model));
        Assert.Empty(s.Fake.Of("session.update"));                                                           // the Realtime line was not used
    }

    [Fact]
    public async Task The_choice_endpoint_needs_SignalWires_signature_and_is_closed_to_unlisted_numbers_and_to_a_call_that_already_streams()
    {
        var parts = new FakeParts("p2");
        using var s = new VoiceScenario(Number, MenuConfig(), services: svc => svc.AddSingleton<IChainedPipelineParts>(parts));
        await Inbound(s);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Choose(s, "2", signature: "forged")).Res.StatusCode);
        Assert.Equal("full", (await s.Row()).Tier);                                                         // nothing was recorded by the forged request: still the assigned tier
        // the same number is listed, so the first real choice works ...
        var (res, _) = await Choose(s, "1");
        using var phone = await s.Attach((await VoiceScenario.StreamFrom(res)).Token);
        // ... but a replay once the stream is open changes nothing (no switching pipelines in the middle of a call)
        var (_, replay) = await Choose(s, "2");
        Assert.Equal("Redirect", replay.Elements().First().Name.LocalName);
        Assert.Equal("p1", (await s.Row()).Tier);
        // a number that is not listed gets no choice at all
        using var other = new VoiceScenario(Other, MenuConfig([Number]), services: svc => svc.AddSingleton<IChainedPipelineParts>(new FakeParts("p2")));
        await Inbound(other);
        var (_, refused) = await Choose(other, "2");
        Assert.Equal("Redirect", refused.Elements().First().Name.LocalName);
        Assert.Equal("full", (await other.Row()).Tier);
    }

    [Fact]
    public async Task A_call_on_option_one_is_priced_by_its_model_not_by_its_tier_label()
    {
        using var s = new VoiceScenario(Number, MenuConfig(more: new() { ["Voice:ModelMode"] = "mini" }));
        await Inbound(s);
        var (res, _) = await Choose(s, "1");
        using var phone = await s.Attach((await VoiceScenario.StreamFrom(res)).Token);
        var row = await s.Row();
        Assert.Equal(("p1", "model-mini"), (row.Tier, row.Model));
        await s.Fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>(), usage = new { input_token_details = new { audio_tokens = 1_000_000, text_tokens = 0 }, output_token_details = new { audio_tokens = 0, text_tokens = 0 } } } });
        await FakeRealtime.Poll(() => s.Row().Result.EstimatedCostMicros > 0 ? "x" : null, 5);
        Assert.Equal(10_000_000, (await s.Row()).EstimatedCostMicros);                                      // mini audio-in is $10 per million tokens; full would be $32
    }

    [Fact]
    public void The_choice_and_the_options_are_read_strictly()
    {
        Assert.Equal(["p1", "p2", "p3", "p4", "p5"], VoicePipelineIds.All);
        Assert.Equal("p4", VoicePipelineIds.FromDigits("4")); Assert.Equal("p4", VoicePipelineIds.FromDigits(" 4 "));
        foreach (var bad in new string?[] { null, "", "6", "0", "44", "a" }) Assert.Equal("p1", VoicePipelineIds.FromDigits(bad));
        Assert.True(VoicePipelineIds.IsOption("p5")); Assert.False(VoicePipelineIds.IsOption("full")); Assert.False(VoicePipelineIds.IsOption("p6"));
        Assert.Equal("p1", VoicePipelineIds.OptionOf("mini")); Assert.Equal("p3", VoicePipelineIds.OptionOf("p3"));
    }

    [Fact]
    public void Start_up_refuses_a_menu_with_a_wrong_number_or_a_nonsense_budget()
    {
        VoiceOptions V(Action<VoicePipelineMenuOptions> f) { var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://v.test" }; f(o.PipelineMenu); return o; }
        var s = new SignalWireOptions { SigningKey = "sw" };
        Assert.Empty(VoiceConfig.Problems(V(m => { m.Enabled = true; m.TestNumbers = [Number]; }), s, true));
        Assert.Contains(VoiceConfig.Problems(V(m => { m.Enabled = true; m.TestNumbers = ["not a number"]; }), s, true), p => p.Contains("TestNumbers"));
        Assert.Contains(VoiceConfig.Problems(V(m => m.MenuTimeoutSeconds = 0), s, true), p => p.Contains("MenuTimeoutSeconds"));
        Assert.Contains(VoiceConfig.Problems(V(m => m.EndpointMs = 10), s, true), p => p.Contains("EndpointMs"));
        Assert.Contains(VoiceConfig.Problems(V(m => { m.ParallelWaitMs = 900; m.ParallelBudgetMs = 500; }), s, true), p => p.Contains("ParallelBudgetMs"));
    }
}
