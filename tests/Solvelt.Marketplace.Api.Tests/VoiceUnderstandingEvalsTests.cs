using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.Evals;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The understanding evals (decision V-59), the part that runs in CI: the fixtures are sound, they replay through the live instructions and tool definitions, the scorer scores, and nothing here (or in a workflow) contacts a vendor.</summary>
public sealed class VoiceUnderstandingEvalsTests : IDisposable
{
    private readonly TestFactory _f = new(VoiceFactory.Config());
    public void Dispose() => _f.Dispose();

    private static readonly IReadOnlyList<EvalFixture> All = UnderstandingEvals.Embedded();
    private static EvalFixture Fx(string id) => All.Single(f => f.Id == id);
    private static LlmToolCall Call(string name, string args = "{}") => new("c1", name, args);

    [Fact]
    public void There_are_about_eighty_fixtures_in_every_category_with_unique_ids_and_both_languages()
    {
        Assert.True(All.Count >= 80, $"only {All.Count} fixtures");
        Assert.Equal(All.Count, All.Select(f => f.Id).Distinct().Count());
        foreach (var c in UnderstandingEvals.Categories) Assert.True(All.Count(f => f.Category == c) >= 8, $"category {c} has fewer than 8 fixtures");
        Assert.DoesNotContain(All, f => !UnderstandingEvals.Categories.Contains(f.Category));
        Assert.Contains(All, f => f.Language == "yi");
        Assert.Contains(All, f => f.Language == "en");
    }

    [Fact]
    public void Every_fixture_expects_something_and_names_only_tools_the_live_line_has()
    {
        var names = _f.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        foreach (var f in All)
        {
            var x = f.Expect;
            Assert.True(x.Tool is not null || x.AnyTool.Count > 0 || x.NoTool || x.MustNotSay.Count > 0 || x.MustSay.Count > 0 || x.Args.Count > 0, $"{f.Id} expects nothing");
            foreach (var t in x.AnyTool.Append(x.Tool ?? "").Where(t => t.Length > 0)) Assert.True(names.Contains(t), $"{f.Id} expects tool {t}, which the line does not have");
            Assert.False(x.NoTool && (x.Tool is not null || x.AnyTool.Count > 0), $"{f.Id} contradicts itself");
        }
    }

    [Fact]
    public void A_replayed_request_is_the_live_one_the_same_instructions_and_the_same_tool_definitions_with_only_the_fixtures_turns_added()
    {
        var live = _f.Services.GetRequiredService<VoiceToolRunner>().Definitions();
        var f = Fx("cart-04");
        var r = UnderstandingEvals.BuildRequest(f, live);
        Assert.Same(VoiceScripts.Instructions, r.Instructions);
        Assert.Equal(live.ToJsonString(), r.Tools.ToJsonString());
        Assert.Equal(f.History.Count + 1, r.Messages.Count);
        Assert.Equal(new LlmMessage("user", f.Say), r.Messages[^1]);
        Assert.Contains(r.Messages, m => m.ToolCalls is { Count: > 0 });                                           // earlier tool rounds are replayed as tool rounds
        Assert.Contains(r.Messages, m => m.Role == "tool" && m.ToolCallId is not null);
    }

    [Fact]
    public void The_fixtures_hold_no_real_looking_data()
    {
        var json = new StreamReader(typeof(UnderstandingEvals).Assembly.GetManifestResourceStream("voice-understanding-evals.json")!).ReadToEnd();
        Assert.DoesNotContain("@", json);
        Assert.DoesNotMatch(@"\d{7,}", json);
    }

    [Fact]
    public void The_scorer_marks_the_tool_the_alternatives_the_no_tool_case_the_arguments_and_what_may_not_be_said()
    {
        Assert.True(UnderstandingEvals.Score(Fx("product_search-03"), [Call("search_products", """{"query":"Challah loaf"}""")], "").Passed);
        var wrongTool = UnderstandingEvals.Score(Fx("product_search-03"), [Call("view_cart")], "");
        Assert.False(wrongTool.Passed); Assert.Contains("expected search_products, got view_cart", wrongTool.Why);
        Assert.False(UnderstandingEvals.Score(Fx("product_search-03"), [], "Sure.").Passed);                      // no call
        Assert.Contains("argument query", UnderstandingEvals.Score(Fx("product_search-03"), [Call("search_products", """{"query":"wine"}""")], "").Why);
        Assert.Contains("(missing)", UnderstandingEvals.Score(Fx("product_search-03"), [Call("search_products", "{}")], "").Why);
        Assert.True(UnderstandingEvals.Score(Fx("product_search-01"), [Call("list_categories")], "").Passed);   // any of
        Assert.False(UnderstandingEvals.Score(Fx("product_search-01"), [Call("view_cart")], "").Passed);
        Assert.True(UnderstandingEvals.Score(Fx("greeting-01"), [], "Hi there!").Passed);                         // no tool, just speech
        Assert.Contains("expected no tool", UnderstandingEvals.Score(Fx("greeting-01"), [Call("off_topic")], "").Why);
        var pin = Fx("pin_and_code-01");
        Assert.True(UnderstandingEvals.Score(pin, [], "Please type your PIN on the keypad, not out loud.").Passed);
        Assert.Contains("said '4321'", UnderstandingEvals.Score(pin, [], "Okay, 4321, got it, on the keypad.").Why);
        Assert.Contains("did not say any of", UnderstandingEvals.Score(pin, [], "Okay, thanks.").Why);
        Assert.False(UnderstandingEvals.Score(Fx("off_topic-01"), [], "It looks sunny.").Passed);                // an off-topic request must go through off_topic
    }

    private static IEnumerable<LlmEvent> Perfect(LlmRequest r)
    {
        var f = All.Single(x => x.Say == ((LlmMessage)r.Messages[^1]).Text && x.History.Count + 1 == r.Messages.Count);
        var e = f.Expect;
        var tool = e.Tool ?? e.AnyTool.FirstOrDefault();
        if (tool is null) { yield return new LlmTextDelta(string.Join(" ", e.MustSay.Select(s => s.Split('|')[0].Replace("\\b", "").Replace("\\", "")).DefaultIfEmpty("Okay."))); yield return new LlmCompleted(1, 1); yield break; }
        var args = new JsonObject(); foreach (var (k, v) in e.Args) args[k] = v;
        yield return new LlmToolCallEvent(new LlmToolCall("t", tool, args.ToJsonString()));
        yield return new LlmCompleted(1, 1);
    }

    [Fact]
    public async Task A_model_that_does_what_every_fixture_expects_scores_one_hundred_percent_and_the_report_has_a_line_per_category()
    {
        var llm = new FakeLlm { Default = Perfect };
        var outcomes = await UnderstandingEvals.RunAsync(llm, _f.Services.GetRequiredService<VoiceToolRunner>().Definitions(), All, 4, CancellationToken.None);
        Assert.Equal(All.Count, outcomes.Count);
        Assert.True(outcomes.All(o => o.Passed), string.Join("; ", outcomes.Where(o => !o.Passed).Select(o => o.FixtureId + ": " + o.Why)));
        Assert.Equal(All.Count, llm.Requests.Count);
        Assert.All(llm.Requests, r => Assert.Same(VoiceScripts.Instructions, r.Instructions));
        var report = UnderstandingEvals.Report(outcomes);
        foreach (var c in UnderstandingEvals.Categories) Assert.Contains(c, report);
        Assert.Contains("100%", report);
        Assert.DoesNotContain("FAIL", report);
    }

    [Fact]
    public async Task A_model_that_only_chats_fails_the_tool_fixtures_and_the_report_names_each_failure_and_accuracy_per_category()
    {
        var llm = new FakeLlm { Default = _ => [new LlmTextDelta("Sure, how can I help?"), new LlmCompleted(1, 1)] };
        var outcomes = await UnderstandingEvals.RunAsync(llm, new JsonArray(), All, 2, CancellationToken.None);
        var byCat = UnderstandingEvals.ByCategory(outcomes).ToDictionary(c => c.Category);
        Assert.Equal(UnderstandingEvals.Categories.Length, byCat.Count);                                           // every category is reported
        Assert.True(byCat["product_search"].Accuracy < 0.2);
        Assert.True(byCat["greeting"].Accuracy > 0.5);                                                             // greetings need no tool
        var report = UnderstandingEvals.Report(outcomes);
        Assert.Contains("FAIL product_search-03: expected search_products, got (speech only)", report);
    }

    private sealed class Boom : IVoiceLlm
    {
        public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct) => throw new HttpRequestException("secret-body-text");
    }

    [Fact]
    public async Task A_vendor_failure_on_one_fixture_is_a_failed_outcome_with_a_fixed_text_never_an_exception_and_never_the_vendors_words()
    {
        var outcomes = await UnderstandingEvals.RunAsync(new Boom(), new JsonArray(), All.Take(3).ToList(), 1, CancellationToken.None);
        Assert.All(outcomes, o => { Assert.False(o.Passed); Assert.Equal("the model call failed: HttpRequestException", o.Why); });
    }

    [Fact]
    public async Task Cancelling_stops_the_run()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnderstandingEvals.RunAsync(new FakeLlm(), new JsonArray(), All, 2, cts.Token));
    }

    [Fact]
    public void No_workflow_runs_the_evals()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github"))) dir = dir.Parent;
        Assert.NotNull(dir);
        foreach (var wf in Directory.GetFiles(Path.Combine(dir.FullName, ".github", "workflows")))
            Assert.DoesNotContain("VoiceEvals", File.ReadAllText(wf));
    }
}
