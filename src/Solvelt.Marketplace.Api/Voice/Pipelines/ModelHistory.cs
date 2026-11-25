using System.Reflection;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// What the text model is shown of the conversation so far (decision V-54). The platform's FIXED lines (the off-topic redirect, "Sorry, I did not catch that", the apologies, the keypad and PIN scripts, the read-backs)
/// must not be replayed to the model as its own earlier turns: two canned refusals in the history made a model refuse a question it had answered before, a self-reinforcing refusal loop (seen in Ronald's older system).
/// The caller still hears every such line and the diagnostics still keep what was said; only the MODEL'S VIEW of past turns is cleaned:
/// <list type="bullet">
/// <item>the result of an <c>off_topic</c> tool call of a COMPLETED turn is replaced by a neutral note (it carries the sentence the model was to say), and the assistant text the model said right after it (that sentence) is left out;</item>
/// <item>any other assistant text that equals one of the platform's fixed lines word for word is left out (the platform speaks those itself, outside the model, so this only guards a model that quotes one);</item>
/// <item>the CURRENT turn (from the caller's last words on) is untouched: the model has just called <c>off_topic</c> and must still be given the sentence to say.</item>
/// </list>
/// Pure and small, so it is tested without a phone.
/// </summary>
public static class ModelHistory
{
    /// <summary>What an off-topic tool result of an earlier turn is shown as: the fact, never the platform's sentence.</summary>
    public const string OffTopicNeutral = """{"status":"off_topic","note":"The caller asked for something outside the shop and was redirected."}""";

    private static readonly HashSet<string> Fixed = BuildFixed();

    private static HashSet<string> BuildFixed()
    {
        // every fixed spoken line of VoiceScripts (the instructions the model is GIVEN are not lines the caller hears)
        var skip = new HashSet<string> { nameof(VoiceScripts.Greeting), nameof(VoiceScripts.Resume), nameof(VoiceScripts.Filler), nameof(VoiceScripts.Instructions) };
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in typeof(VoiceScripts).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            if (f.IsLiteral && f.FieldType == typeof(string) && !skip.Contains(f.Name) && f.GetRawConstantValue() is string s) set.Add(Normalize(s));
        return set;
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True when the text is one of the platform's fixed lines, word for word (spacing aside).</summary>
    public static bool IsFixedLine(string? text) => !string.IsNullOrWhiteSpace(text) && Fixed.Contains(Normalize(text));

    public static IReadOnlyList<LlmMessage> ForModel(IReadOnlyList<LlmMessage> history)
    {
        var current = -1;
        for (var i = history.Count - 1; i >= 0; i--) if (history[i].Role == "user") { current = i; break; }
        if (current < 0) return history;                                    // no caller words yet: nothing is a past turn
        var offTopicCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in history.Take(current)) foreach (var c in m.ToolCalls ?? []) if (c.Name == "off_topic") offTopicCalls.Add(c.CallId);
        if (offTopicCalls.Count == 0 && !history.Take(current).Any(m => m.Role == "assistant" && IsFixedLine(m.Text))) return history;   // the usual case: nothing to clean, the same list
        var shown = new List<LlmMessage>(history.Count);
        var afterOffTopic = false;
        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            if (i >= current) { shown.Add(m); continue; }
            switch (m.Role)
            {
                case "tool" when m.ToolCallId is not null && offTopicCalls.Contains(m.ToolCallId):
                    shown.Add(m with { Text = OffTopicNeutral });
                    afterOffTopic = true;
                    break;
                case "assistant":
                    var hasCalls = m.ToolCalls is { Count: > 0 };
                    var dropText = !string.IsNullOrEmpty(m.Text) && ((afterOffTopic && !hasCalls) || IsFixedLine(m.Text));
                    afterOffTopic = false;
                    if (!dropText) shown.Add(m);
                    else if (hasCalls) shown.Add(m with { Text = null });   // the calls stay (a result must have its call), only the fixed words go
                    break;
                case "user": afterOffTopic = false; shown.Add(m); break;
                default: shown.Add(m); break;
            }
        }
        return shown;
    }
}
