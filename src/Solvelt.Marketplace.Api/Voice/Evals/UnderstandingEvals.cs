using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solvelt.Marketplace.Api.Voice.Evals;

/// <summary>
/// The understanding evals (decision V-59): about eighty short fixtures, each one caller utterance (with the context that came before it) and what the text model is expected to do with it:
/// call a particular tool, call none and just speak, or refuse. They measure whether the model, given the SAME instructions and the SAME tool definitions as the live line, understands callers.
/// The fixtures, the request builder and the scorer live here (no network, tested in CI); only the opt-in runner in <c>tools/VoiceEvals</c> contacts a vendor, and CI never runs it.
/// </summary>
public sealed record EvalFixture(string Id, string Category, string Language, string Say, IReadOnlyList<EvalTurn> History, EvalExpect Expect);

/// <summary>One earlier turn: a caller or assistant text, an assistant tool call, or a tool result (hand-written to look like what the server returns).</summary>
public sealed record EvalTurn(string Role, string? Text, string? ToolName, string? ToolArgs, string? CallId);

/// <summary>
/// What counts as right. <see cref="Tool"/>: that tool must be called first. <see cref="AnyTool"/>: any of these. <see cref="NoTool"/>: no tool at all and a spoken answer.
/// <see cref="Args"/>: each named argument of the call must contain the text (case-insensitive). <see cref="MustNotSay"/>: none of these may appear in what the model says.
/// <see cref="MustSay"/>: at least one of these patterns (regex, case-insensitive) must match what the model says.
/// </summary>
public sealed record EvalExpect(string? Tool, IReadOnlyList<string> AnyTool, bool NoTool, IReadOnlyDictionary<string, string> Args, IReadOnlyList<string> MustNotSay, IReadOnlyList<string> MustSay);

public sealed record EvalOutcome(string FixtureId, string Category, bool Passed, string Got, string? Why);

public sealed record EvalCategoryResult(string Category, int Total, int Passed)
{
    public double Accuracy => Total == 0 ? 0 : (double)Passed / Total;
}

public static class UnderstandingEvals
{
    public static readonly string[] Categories = ["greeting", "product_search", "cart", "orders", "account", "pin_and_code", "off_topic", "mixed", "language"];

    /// <summary>The set that ships with the API (an embedded copy of docs/voice-evals/understanding.json).</summary>
    public static IReadOnlyList<EvalFixture> Embedded()
    {
        using var s = typeof(UnderstandingEvals).Assembly.GetManifestResourceStream("voice-understanding-evals.json") ?? throw new InvalidOperationException("The understanding fixtures are not embedded.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static IReadOnlyList<EvalFixture> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<EvalFixture>();
        foreach (var f in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var history = new List<EvalTurn>();
            if (f.TryGetProperty("history", out var h))
                foreach (var t in h.EnumerateArray())
                    history.Add(new(t.GetProperty("role").GetString()!, Str(t, "text"), Str(t, "tool"), t.TryGetProperty("args", out var a) ? a.GetRawText() : null, Str(t, "id")));
            var e = f.GetProperty("expect");
            list.Add(new(f.GetProperty("id").GetString()!, f.GetProperty("category").GetString()!, Str(f, "language") ?? "en", f.GetProperty("say").GetString()!, history,
                new(Str(e, "tool"), Strings(e, "anyTool"), e.TryGetProperty("noTool", out var nt) && nt.GetBoolean(),
                    e.TryGetProperty("args", out var args) ? args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "") : new Dictionary<string, string>(),
                    Strings(e, "mustNotSay"), Strings(e, "mustSay"))));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static IReadOnlyList<string> Strings(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

    /// <summary>
    /// The request the live line would send for this utterance: the SAME <see cref="VoiceScripts.Instructions"/> and the SAME tool definitions (<paramref name="tools"/> comes from the running app's tool registry),
    /// the earlier turns, then the caller's words. Nothing else is added and nothing is left out.
    /// </summary>
    public static LlmRequest BuildRequest(EvalFixture f, JsonArray tools)
    {
        var messages = new List<LlmMessage>();
        foreach (var t in f.History)
        {
            switch (t.Role)
            {
                case "user": messages.Add(new("user", t.Text)); break;
                case "assistant" when t.ToolName is not null: messages.Add(new("assistant", t.Text, [new LlmToolCall(t.CallId ?? "call_" + messages.Count, t.ToolName, t.ToolArgs ?? "{}")])); break;
                case "assistant": messages.Add(new("assistant", t.Text)); break;
                case "tool": messages.Add(new("tool", t.Text, ToolCallId: t.CallId)); break;
                case "system": messages.Add(new("system", t.Text)); break;
            }
        }
        messages.Add(new("user", f.Say));
        return new LlmRequest(VoiceScripts.Instructions, messages, tools);
    }

    /// <summary>Marks one answer. <paramref name="calls"/> are the tool calls the model made, in order; <paramref name="said"/> is the text it spoke before or without them.</summary>
    public static EvalOutcome Score(EvalFixture f, IReadOnlyList<LlmToolCall> calls, string said)
    {
        var x = f.Expect;
        var first = calls.Count > 0 ? calls[0].Name : null;
        var got = first is null ? "(speech only)" : first;
        string? why = null;
        if (x.NoTool && first is not null) why = $"expected no tool, got {first}";
        else if (x.Tool is not null && first != x.Tool) why = $"expected {x.Tool}, got {got}";
        else if (x.AnyTool.Count > 0 && (first is null || !x.AnyTool.Contains(first))) why = $"expected one of {string.Join("/", x.AnyTool)}, got {got}";
        else if (x.Args.Count > 0)
        {
            var args = calls.Count > 0 ? ParseArgs(calls[0].Arguments) : null;
            foreach (var (k, v) in x.Args)
            {
                var actual = args is not null && args.TryGetPropertyValue(k, out var n) && n is not null ? n.ToString() : null;
                if (actual is null || !actual.Contains(v, StringComparison.OrdinalIgnoreCase)) { why = $"argument {k} should contain '{v}', was '{actual ?? "(missing)"}'"; break; }
            }
        }
        if (why is null) foreach (var bad in x.MustNotSay) if (said.Contains(bad, StringComparison.OrdinalIgnoreCase)) { why = $"said '{bad}'"; break; }
        if (why is null && x.MustSay.Count > 0 && !x.MustSay.Any(p => Regex.IsMatch(said, p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))) why = "did not say any of: " + string.Join(" | ", x.MustSay);
        return new(f.Id, f.Category, why is null, got, why);
    }

    private static JsonObject? ParseArgs(string json) { try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject; } catch (JsonException) { return null; } }

    /// <summary>Plays every fixture through <paramref name="llm"/> (the first round only: the model's first move) and marks it. A vendor failure for one fixture is a failed outcome, never an exception that stops the run.</summary>
    public static async Task<IReadOnlyList<EvalOutcome>> RunAsync(IVoiceLlm llm, JsonArray tools, IReadOnlyList<EvalFixture> fixtures, int parallel, CancellationToken ct)
    {
        var results = new EvalOutcome[fixtures.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        await Task.WhenAll(fixtures.Select(async (f, i) =>
        {
            await gate.WaitAsync(ct);
            try { results[i] = await RunOneAsync(llm, tools, f, ct); }
            finally { gate.Release(); }
        }));
        return results;
    }

    private static async Task<EvalOutcome> RunOneAsync(IVoiceLlm llm, JsonArray tools, EvalFixture f, CancellationToken ct)
    {
        var calls = new List<LlmToolCall>();
        var said = new System.Text.StringBuilder();
        try
        {
            await foreach (var e in llm.StreamAsync(BuildRequest(f, tools), ct))
            {
                if (e is LlmTextDelta d) said.Append(d.Text);
                else if (e is LlmToolCallEvent t) calls.Add(t.Call);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(f.Id, f.Category, false, "(error)", "the model call failed: " + ex.GetType().Name); }
        return Score(f, calls, said.ToString());
    }

    private static string Pct(double x) => Math.Round(x * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public static IReadOnlyList<EvalCategoryResult> ByCategory(IEnumerable<EvalOutcome> outcomes)
        => outcomes.GroupBy(o => o.Category).Select(g => new EvalCategoryResult(g.Key, g.Count(), g.Count(o => o.Passed))).OrderBy(c => Array.IndexOf(Categories, c.Category)).ToList();

    /// <summary>The report: accuracy per category and overall, then each failure with the fixture id and the reason. It names fixtures (test sentences), never a real caller.</summary>
    public static string Report(IReadOnlyList<EvalOutcome> outcomes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Understanding evals");
        foreach (var c in ByCategory(outcomes)) sb.AppendLine($"  {c.Category,-15} {c.Passed,3}/{c.Total,-3} {Pct(c.Accuracy)}");
        sb.AppendLine($"  {"all",-15} {outcomes.Count(o => o.Passed),3}/{outcomes.Count,-3} {Pct(outcomes.Count == 0 ? 0 : (double)outcomes.Count(o => o.Passed) / outcomes.Count)}");
        foreach (var o in outcomes.Where(o => !o.Passed)) sb.AppendLine($"  FAIL {o.FixtureId}: {o.Why}");
        return sb.ToString();
    }
}
