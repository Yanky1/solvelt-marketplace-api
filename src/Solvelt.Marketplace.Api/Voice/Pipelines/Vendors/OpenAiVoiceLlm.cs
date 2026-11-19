using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The text model of every chained option: OpenAI's Responses API over server-sent events (<c>POST /v1/responses</c>, <c>stream: true</c>) with the SAME instructions and tool definitions as the Realtime line.
/// <c>store: false</c>, so OpenAI does not keep the conversation as a stored response. What is said and answered is never logged here; a failure is an exception whose message holds no content.
/// </summary>
public sealed class OpenAiVoiceLlm(HttpClient http, string apiKey, VoiceLlmOptions options) : IVoiceLlm
{
    internal static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");

    internal JsonObject BuildRequest(LlmRequest r)
    {
        var input = new JsonArray();
        foreach (var m in r.Messages)
        {
            switch (m.Role)
            {
                case "tool":
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId, ["output"] = m.Text ?? "" });
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(m.Text)) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Text });
                    foreach (var c in m.ToolCalls ?? []) input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = c.CallId, ["name"] = c.Name, ["arguments"] = c.Arguments });
                    break;
                default:    // user and system
                    input.Add(new JsonObject { ["role"] = m.Role == "system" ? "system" : "user", ["content"] = m.Text ?? "" });
                    break;
            }
        }
        return new JsonObject
        {
            ["model"] = options.Model, ["instructions"] = r.Instructions, ["input"] = input, ["tools"] = r.Tools.DeepClone(), ["stream"] = true, ["store"] = false,
        };
    }

    public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(BuildRequest(request).ToJsonString(), Encoding.UTF8, "application/json") };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, wait.Token);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"The text model answered {(int)resp.StatusCode}.", null, resp.StatusCode);   // the body is not read: it can echo the request
        wait.CancelAfter(Timeout.InfiniteTimeSpan);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            var ev = Parse(data);
            if (ev is not null) yield return ev;
        }
    }

    /// <summary>One SSE payload to an event; null for the kinds we do not need. A failed or incomplete response throws (no content in the message).</summary>
    internal static LlmEvent? Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            switch (t.GetString())
            {
                case "response.output_text.delta":
                    return root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } text ? new LlmTextDelta(text) : null;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var it) && it.GetString() == "function_call")
                        return new LlmToolCallEvent(new LlmToolCall(Str(item, "call_id"), Str(item, "name"), Str(item, "arguments") is { Length: > 0 } a ? a : "{}"));
                    return null;
                case "response.completed":
                    long inTok = 0, outTok = 0;
                    if (root.TryGetProperty("response", out var r) && r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        inTok = u.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                        outTok = u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
                    }
                    return new LlmCompleted(inTok, outTok);
                case "response.failed" or "response.incomplete" or "error":
                    throw new InvalidOperationException("The text model did not finish the answer.");
                default: return null;
            }
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
