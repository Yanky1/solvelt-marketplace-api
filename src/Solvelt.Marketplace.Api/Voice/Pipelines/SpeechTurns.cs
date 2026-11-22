using System.Text;
using Solvelt.Marketplace.Api.Data;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What one option 6 turn produced: the words for SignalWire to say, and whether the AI part of the call ends (a goodbye, the off-topic limit).</summary>
public sealed record SpeechTurnResult(string Reply, bool End, bool EndNow, bool Failed);

/// <summary>
/// Option 6 (decision V-35): one turn of the conversation as a webhook. The caller's words come from SignalWire's own recogniser (the <c>SpeechResult</c> of a cXML <c>Gather</c>); the text model is ours, with the same
/// instructions and the same tools as the Realtime line. The tool loop is the one of the chained pipelines (assistant text, tool calls, tool answers, again), without sentence streaming: the whole reply is handed back and
/// SignalWire speaks it. Words are held in the call's memory only: never stored, never logged.
/// </summary>
public sealed class SpeechTurns(VoicePipelineRegistry registry, VoiceToolRunner tools, VoiceCallStore store, IOptions<VoiceOptions> options, ILogger<SpeechTurns> log)
{
    private const int Rounds = 6;

    /// <summary>
    /// Runs one turn. <paramref name="said"/> is the caller's words (null for an opening or resumed turn, which carries <paramref name="instruction"/> instead). Any failure of the text model or the clock is
    /// <see cref="SpeechTurnResult.Failed"/>: the caller of this method decides what the platform says; the model never does.
    /// </summary>
    public async Task<SpeechTurnResult> RunAsync(VoiceCall call, VoiceCallState state, string? said, string? instruction, CancellationToken ct, string? turnNote = null)
    {
        var llm = registry.SpeechLlm;
        if (llm is null) return new("", false, false, true);
        var o = options.Value;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.SignalWireSpeech.TurnSeconds, 3, 14)));
        long tokensIn = 0, tokensOut = 0;
        try
        {
            var reply = new StringBuilder();
            var end = false;
            lock (state.SpeechHistory)
            {
                if (said is null && instruction == VoiceScripts.Resume) state.SpeechHistory.Add(new LlmMessage("system", state.Summary()));   // a turn after a keypad step: where things stand
                if (said is not null) state.SpeechHistory.Add(new LlmMessage("user", said));
            }
            for (var round = 0; round < Rounds; round++)
            {
                List<LlmMessage> messages;
                lock (state.SpeechHistory) messages = [.. state.SpeechHistory];
                if (round == 0 && instruction is not null) messages.Add(new LlmMessage("system", instruction));
                if (turnNote is not null) messages.Add(new LlmMessage("system", turnNote));   // V-43: this turn's doubt, not kept in the history
                var text = new StringBuilder();
                var calls = new List<LlmToolCall>();
                await foreach (var ev in llm.StreamAsync(new LlmRequest(VoiceScripts.Instructions, messages, tools.Definitions()), budget.Token))
                {
                    switch (ev)
                    {
                        case LlmTextDelta d: text.Append(d.Text); break;
                        case LlmToolCallEvent t: calls.Add(t.Call); break;
                        case LlmCompleted c: tokensIn += c.InputTokens; tokensOut += c.OutputTokens; break;
                    }
                }
                if (calls.Count == 0)
                {
                    if (text.Length > 0) { lock (state.SpeechHistory) state.SpeechHistory.Add(new LlmMessage("assistant", text.ToString())); reply.Append(text); }
                    return new(reply.ToString().Trim(), end, false, false);
                }
                lock (state.SpeechHistory) state.SpeechHistory.Add(new LlmMessage("assistant", text.Length > 0 ? text.ToString() : null, calls));
                reply.Append(text);
                foreach (var c in calls)
                {
                    var result = await tools.RunAsync(new VoiceToolContext(call.Id, call.Tier, state), c.Name, c.Arguments, budget.Token);
                    lock (state.SpeechHistory) state.SpeechHistory.Add(new LlmMessage("tool", result.Output, null, c.CallId));
                    end |= result.EndCall;
                    if (result.EndNow) return new("", true, true, false);   // the server ends the AI part (the off-topic limit): the PLATFORM speaks next, never the model
                }
                // a keypad step has begun: the model's words so far are said, then the keypad runs; the turn continues after it (a resumed turn)
                if (state.KeypadStep is not null) return new(reply.ToString().Trim(), end, false, false);
            }
            return new(reply.ToString().Trim(), end, false, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Voice call {Call}: an option 6 turn failed ({Type}).", call.Id, ex.GetType().Name);   // never the message: it could hold what was said
            return new("", false, false, true);
        }
        finally
        {
            if (tokensIn + tokensOut > 0)
            {
                var p = options.Value.PipelineMenu.Prices;
                var micros = (long)Math.Round((tokensIn / 1_000_000m * p.LlmInPerMillionTokens + tokensOut / 1_000_000m * p.LlmOutPerMillionTokens) * 1_000_000m);
                try { await store.AddPipelineUsageAsync(call.Id, tokensIn, tokensOut, micros, CancellationToken.None); } catch (Exception) { /* the call's row is gone */ }
            }
        }
    }
}
