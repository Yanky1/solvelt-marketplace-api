using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The text model of the chained options when Claude answers (decision V-33): the official Anthropic C# SDK, the STREAMING Messages API (so the first sentence reaches the voice as soon as it exists), the same instructions
/// and the same tools as the Realtime line. The loop stays ours: this class makes ONE streaming call per round trip and the bridge runs the tools. Details that matter on a phone call:
/// <list type="bullet">
/// <item>Cancelling the token stops reading and drops the HTTP response, which ends the stream at the provider (barge-in).</item>
/// <item>The assistant turn of a tool round (text, tool_use and any thinking blocks with their signatures) is kept exactly as received and replayed unchanged when the tool results come back; a model that thinks refuses a changed turn.</item>
/// <item>Prompt caching: the last tool carries a cache breakpoint and top-level automatic caching covers the growing conversation (the system text and the tools are stable for a whole call).</item>
/// <item>Thinking is switched off: it only delays the first word on a phone call.</item>
/// <item>A refusal, or an answer cut by the token limit, ends with a short polite line in the platform's own words (<see cref="PipelineScripts"/>), never the provider's raw ending.</item>
/// <item>No content is logged here and no exception message holds any: a failure is an exception of a fixed text.</item>
/// </list>
/// </summary>
public sealed class AnthropicVoiceLlm(AnthropicClient client, string model, int maxTokens, int timeoutSeconds) : IVoiceLlm
{
    /// <summary>The provider's client for production: the key, no retries (a phone call cannot wait for a second try) and the host fixed in code, so no environment setting can send the key anywhere else.</summary>
    internal static AnthropicClient NewClient(string apiKey) => new() { ApiKey = apiKey, BaseUrl = "https://api.anthropic.com", MaxRetries = 0 };

    // the assistant turns of tool rounds, by the id of their first tool call, so the next request can replay them unchanged; small and bounded (a call has a handful of tool rounds)
    private readonly ConcurrentDictionary<string, IReadOnlyList<ContentBlockParam>> _replay = new();
    private readonly ConcurrentQueue<string> _replayOrder = new();
    private const int ReplayLimit = 512;

    internal MessageCreateParams BuildRequest(LlmRequest r)
    {
        var tools = new List<ToolUnion>();
        for (var i = 0; i < r.Tools.Count; i++)
        {
            if (r.Tools[i] is not JsonObject t || t["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            var props = new Dictionary<string, JsonElement>();
            var required = new List<string>();
            if (t["parameters"] is JsonObject schema)
            {
                if (schema["properties"] is JsonObject p) foreach (var kv in p) props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
                if (schema["required"] is JsonArray rq) required.AddRange(rq.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
            }
            tools.Add(new Tool { Name = name, Description = t["description"]?.GetValue<string>() ?? "", InputSchema = new() { Properties = props, Required = required } });
        }
        if (tools.Count > 0 && tools[^1].TryPickTool(out var last)) tools[^1] = new Tool { Name = last.Name, Description = last.Description, InputSchema = last.InputSchema, CacheControl = new CacheControlEphemeral() };

        var turns = new List<(bool Assistant, List<ContentBlockParam> Blocks)>();
        var pendingResults = new List<ContentBlockParam>();
        void FlushResults() { if (pendingResults.Count > 0) { AddUser(turns, pendingResults.ToList()); pendingResults.Clear(); } }
        foreach (var m in r.Messages)
        {
            switch (m.Role)
            {
                case "tool":
                    pendingResults.Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Text ?? "" });
                    break;
                case "assistant":
                    FlushResults();
                    var blocks = m.ToolCalls is { Count: > 0 } calls && _replay.TryGetValue(calls[0].CallId, out var kept) ? kept.ToList() : Reconstruct(m);
                    if (blocks.Count > 0) turns.Add((true, blocks));
                    break;
                default:    // user and system: a platform note is a user turn here (Claude has no system role inside the conversation)
                    FlushResults();
                    if (!string.IsNullOrEmpty(m.Text)) AddUser(turns, [new TextBlockParam { Text = m.Role == "system" ? "[platform note] " + m.Text : m.Text }]);
                    break;
            }
        }
        FlushResults();
        var messages = turns.Select(t => t.Assistant ? (MessageParam)new AssistantMessageParam { Content = t.Blocks } : new UserMessageParam { Content = t.Blocks }).ToList();
        return new MessageCreateParams
        {
            Model = model, MaxTokens = maxTokens, System = r.Instructions, Tools = tools, Messages = messages,
            Thinking = new ThinkingConfigDisabled(),
            CacheControl = new CacheControlEphemeral(),
        };
    }

    private static List<ContentBlockParam> Reconstruct(LlmMessage m)
    {
        var blocks = new List<ContentBlockParam>();
        if (!string.IsNullOrEmpty(m.Text)) blocks.Add(new TextBlockParam { Text = m.Text });
        foreach (var c in m.ToolCalls ?? [])
            blocks.Add(new ToolUseBlockParam { ID = c.CallId, Name = c.Name, Input = ToInput(c.Arguments) });
        return blocks;
    }

    /// <summary>Consecutive user turns are one turn: tool results of parallel calls must share a message, and a note after a result is part of it.</summary>
    private static void AddUser(List<(bool Assistant, List<ContentBlockParam> Blocks)> turns, List<ContentBlockParam> blocks)
    {
        if (turns.Count > 0 && !turns[^1].Assistant) turns[^1].Blocks.AddRange(blocks);
        else turns.Add((false, blocks));
    }

    private static IReadOnlyDictionary<string, JsonElement> ToInput(string? json)
    {
        var d = new Dictionary<string, JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        }
        catch (JsonException) { /* an unreadable argument object is an empty one: the tool answers bad_arguments */ }
        return d;
    }

    private sealed class Block
    {
        public string Kind = "";                       // text, thinking, redacted, tool
        public StringBuilder Text = new();
        public string Id = "", Name = "", Signature = "", Data = "";
    }

    public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var blocks = new SortedDictionary<long, Block>();
        string? stop = null; long inTok = 0, outTok = 0; var spoke = false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        IAsyncEnumerator<RawMessageStreamEvent> events;
        try { events = client.Messages.CreateStreaming(BuildRequest(request), wait.Token).GetAsyncEnumerator(wait.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new HttpRequestException("The text model did not answer."); }
        try
        {
            while (true)
            {
                RawMessageStreamEvent ev;
                try
                {
                    if (!await events.MoveNextAsync()) break;
                    ev = events.Current;
                    wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));   // the wait is for the first token or the next one, not for the whole answer
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The text model took too long."); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new HttpRequestException("The text model did not finish the answer."); }

                if (ev.TryPickStart(out var start)) { inTok += start.Message.Usage.InputTokens + (start.Message.Usage.CacheReadInputTokens ?? 0) + (start.Message.Usage.CacheCreationInputTokens ?? 0); }
                else if (ev.TryPickContentBlockStart(out var cs))
                {
                    var b = new Block();
                    if (cs.ContentBlock.TryPickToolUse(out var tu)) { b.Kind = "tool"; b.Id = tu.ID; b.Name = tu.Name; }
                    else if (cs.ContentBlock.TryPickThinking(out var th)) { b.Kind = "thinking"; b.Text.Append(th.Thinking); b.Signature = th.Signature; }
                    else if (cs.ContentBlock.TryPickRedactedThinking(out var rt)) { b.Kind = "redacted"; b.Data = rt.Data; }
                    else { b.Kind = "text"; if (cs.ContentBlock.TryPickText(out var tx) && tx.Text.Length > 0) { b.Text.Append(tx.Text); } }
                    blocks[cs.Index] = b;
                    if (b.Kind == "text" && b.Text.Length > 0) { spoke = true; yield return new LlmTextDelta(b.Text.ToString()); }
                }
                else if (ev.TryPickContentBlockDelta(out var cd) && blocks.TryGetValue(cd.Index, out var cur))
                {
                    if (cd.Delta.TryPickText(out var td)) { cur.Text.Append(td.Text); if (td.Text.Length > 0) { spoke = true; yield return new LlmTextDelta(td.Text); } }
                    else if (cd.Delta.TryPickInputJson(out var ij)) cur.Text.Append(ij.PartialJson);
                    else if (cd.Delta.TryPickThinking(out var thd)) cur.Text.Append(thd.Thinking);
                    else if (cd.Delta.TryPickSignature(out var sg)) cur.Signature = sg.Signature;
                }
                else if (ev.TryPickContentBlockStop(out var ce) && blocks.TryGetValue(ce.Index, out var done) && done.Kind == "tool")
                    yield return new LlmToolCallEvent(new LlmToolCall(done.Id, done.Name, done.Text.Length > 0 ? done.Text.ToString() : "{}"));
                else if (ev.TryPickDelta(out var md))
                {
                    stop = md.Delta.StopReason?.Raw()?.ToString()?.ToLowerInvariant();
                    outTok = md.Usage.OutputTokens;
                    if (md.Usage.InputTokens is { } it && it > 0) inTok = it + (md.Usage.CacheReadInputTokens ?? 0) + (md.Usage.CacheCreationInputTokens ?? 0);
                }
            }
        }
        finally { await events.DisposeAsync(); }   // ends the HTTP response: the provider stops generating (barge-in and every other early exit)

        Remember(blocks.Values);
        if (stop is "refusal") yield return new LlmTextDelta((spoke ? " " : "") + PipelineScripts.ModelRefused);
        else if (stop is "max_tokens" or "maxtokens") yield return new LlmTextDelta((spoke ? " " : "") + PipelineScripts.ModelRanOut);
        yield return new LlmCompleted(inTok, outTok);
    }

    /// <summary>Keeps the assistant turn of a tool round exactly as received, for the next request of this call.</summary>
    private void Remember(IEnumerable<Block> received)
    {
        var all = received.ToList();
        if (!all.Any(b => b.Kind == "tool")) return;
        var content = new List<ContentBlockParam>();
        foreach (var b in all)
            switch (b.Kind)
            {
                case "text" when b.Text.Length > 0: content.Add(new TextBlockParam { Text = b.Text.ToString() }); break;
                case "thinking": content.Add(new ThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Signature }); break;
                case "redacted": content.Add(new RedactedThinkingBlockParam { Data = b.Data }); break;
                case "tool": content.Add(new ToolUseBlockParam { ID = b.Id, Name = b.Name, Input = ToInput(b.Text.ToString()) }); break;
            }
        var key = all.First(b => b.Kind == "tool").Id;
        _replay[key] = content;
        _replayOrder.Enqueue(key);
        while (_replayOrder.Count > ReplayLimit && _replayOrder.TryDequeue(out var old)) _replay.TryRemove(old, out _);
    }
}

/// <summary>The judge of option p5 when Claude judges (decision V-33): one NON-streaming call, the same instructions, candidates quoted as data and the same refusal of an empty or runaway answer as <see cref="OpenAiTranscriptJudge"/>.</summary>
public sealed class AnthropicTranscriptJudge(AnthropicClient client, string model) : ITranscriptJudge
{
    public async Task<JudgeResult?> JudgeAsync(IReadOnlyList<SttCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count < 2) return null;      // nothing to choose between
        var data = new JsonArray();
        foreach (var c in candidates) data.Add(new JsonObject { ["engine"] = c.Engine, ["text"] = c.Text });
        Message resp;
        try
        {
            resp = await client.Messages.Create(new MessageCreateParams
            {
                Model = model, MaxTokens = 300, System = OpenAiTranscriptJudge.Instructions, Thinking = new ThinkingConfigDisabled(),
                Messages = [new UserMessageParam { Content = new List<ContentBlockParam> { new TextBlockParam { Text = "<candidates>\n" + data.ToJsonString() + "\n</candidates>" } } }],
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new HttpRequestException("The judge model did not answer."); }
        var text = resp.Content.Where(b => b.TryPickText(out _)).Select(b => { b.TryPickText(out var t); return t!.Text; }).FirstOrDefault();
        return OpenAiTranscriptJudge.Accept(text, candidates);
    }
}
