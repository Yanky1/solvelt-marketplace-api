using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Voice;

// The stages of a chained pipeline (decision V-19): streaming speech-to-text, an end-of-turn engine, our own LLM with the existing tools, streaming text-to-speech. Interfaces only: the vendor adapters come
// after the owner has chosen the vendors, and tests use fakes. Audio is G.711 mu-law 8 kHz mono both ways (what SignalWire's stream carries); an adapter whose vendor wants another format converts inside itself.

public enum SttEventKind { SpeechStarted, Partial, Final, SpeechEnded }

/// <summary>One thing the recogniser reports. <see cref="Text"/> is the words so far (<c>Partial</c>) or the finished words (<c>Final</c>); it is held in memory for the turn and is never stored or logged.</summary>
public sealed record SttEvent(SttEventKind Kind, string Text = "", string? Language = null, double? Confidence = null, IReadOnlyList<SttCandidate>? Candidates = null, string? JudgePick = null, IReadOnlyList<WordConfidence>? Words = null);

/// <summary>One word the recogniser wrote and how sure it was (0 to 1): only the least sure few are carried (decision V-43).</summary>
public sealed record WordConfidence(string Word, double Confidence);

public interface IStreamingStt
{
    /// <summary>Opens one recognition stream for one call.</summary>
    Task<ISttSession> OpenAsync(CancellationToken ct);
}

public interface ISttSession : IAsyncDisposable
{
    /// <summary>Caller audio, G.711 mu-law 8 kHz, in the order it arrives.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct);
    /// <summary>What the recogniser reports, until the session is closed.</summary>
    IAsyncEnumerable<SttEvent> Events(CancellationToken ct);
}

public enum TurnSignalKind { None, UserSpeaking, TurnEnded }

/// <summary><see cref="Text"/> is the caller's whole turn when <see cref="Kind"/> is <c>TurnEnded</c>.</summary>
public sealed record TurnSignal(TurnSignalKind Kind, string Text = "")
{
    public static readonly TurnSignal None = new(TurnSignalKind.None);
}

/// <summary>Decides when the caller has finished their turn. One instance per call. Engines that decide themselves (a vendor's end-of-turn model) answer from <see cref="OnEvent"/>; time-based ones from <see cref="OnTick"/>.</summary>
public interface ITurnDetector
{
    TurnSignal OnEvent(SttEvent e, DateTimeOffset now);
    TurnSignal OnTick(DateTimeOffset now);
    /// <summary>Why this engine finishes a caller's turn, for the owner's test-call diagnostics (V-39): a short plain label.</summary>
    string EndReason => "other";
}

public interface ITts
{
    /// <summary>The speech for <paramref name="text"/> as mu-law 8 kHz chunks, as they are produced. Must stop promptly when <paramref name="ct"/> is cancelled (barge-in).</summary>
    IAsyncEnumerable<byte[]> SynthesizeAsync(string text, CancellationToken ct);
}

public sealed record LlmToolCall(string CallId, string Name, string Arguments);

/// <summary><see cref="Role"/> is <c>user</c>, <c>assistant</c>, <c>system</c> or <c>tool</c> (the answer to <see cref="ToolCallId"/>).</summary>
public sealed record LlmMessage(string Role, string? Text, IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null);

/// <summary>The same instructions and the same tool definitions as the Realtime line (<see cref="VoiceScripts.Instructions"/> and <see cref="VoiceToolRunner.Definitions"/>), plus the conversation so far.</summary>
public sealed record LlmRequest(string Instructions, IReadOnlyList<LlmMessage> Messages, JsonArray Tools);

public abstract record LlmEvent;
public sealed record LlmTextDelta(string Text) : LlmEvent;
public sealed record LlmToolCallEvent(LlmToolCall Call) : LlmEvent;
public sealed record LlmCompleted(long InputTokens, long OutputTokens) : LlmEvent;

public interface IVoiceLlm
{
    IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>The stages of one option, registered when (and only when) its vendors are configured. With none registered, the option answers "not available on this line" and the call is on option 1.</summary>
public interface IChainedPipelineParts
{
    /// <summary><c>p2</c> to <c>p5</c>.</summary>
    string OptionId { get; }
    IStreamingStt Stt { get; }
    ITurnDetector NewTurnDetector();
    ITts Tts { get; }
    IVoiceLlm Llm { get; }
}

/// <summary>The default end-of-turn engine, vendor neutral: the caller's turn ends when they have words and have been silent for <c>EndpointMs</c>.</summary>
public sealed class SilenceTurnDetector(int endpointMs) : ITurnDetector
{
    public string EndReason => "silence";
    private readonly List<string> _finals = [];
    private readonly object _gate = new();   // OnEvent (the recogniser reader) and OnTick (the 50 ms timer) run on two threads (V-56)
    private DateTimeOffset _lastActivity;

    public TurnSignal OnEvent(SttEvent e, DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastActivity = now;
            switch (e.Kind)
            {
                case SttEventKind.SpeechStarted or SttEventKind.Partial:
                    return new(TurnSignalKind.UserSpeaking);
                case SttEventKind.Final:
                    if (!string.IsNullOrWhiteSpace(e.Text)) _finals.Add(e.Text.Trim());
                    return new(TurnSignalKind.UserSpeaking);
                default:   // SpeechEnded
                    return TurnSignal.None;
            }
        }
    }

    public TurnSignal OnTick(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_finals.Count == 0 || now - _lastActivity < TimeSpan.FromMilliseconds(Math.Max(1, endpointMs))) return TurnSignal.None;
            var text = string.Join(' ', _finals);
            _finals.Clear();
            return new(TurnSignalKind.TurnEnded, text);
        }
    }
}

/// <summary>
/// For a recogniser that only hands over a finished transcript after the end of speech has already been waited for (the adapter's own voice-activity wait): the turn is over when a final transcript arrives, so there is
/// no second silence wait on top of it.
/// </summary>
public sealed class FinalTurnDetector : ITurnDetector
{
    public string EndReason => "vendor_final";
    public TurnSignal OnEvent(SttEvent e, DateTimeOffset now)
        => e.Kind switch
        {
            SttEventKind.Final when !string.IsNullOrWhiteSpace(e.Text) => new(TurnSignalKind.TurnEnded, e.Text.Trim()),
            SttEventKind.SpeechStarted or SttEventKind.Partial => new(TurnSignalKind.UserSpeaking),
            _ => TurnSignal.None,
        };

    public TurnSignal OnTick(DateTimeOffset now) => TurnSignal.None;
}

/// <summary>Cuts an assistant answer into sentences as text arrives, so the first sentence can be spoken while the model is still writing the rest.</summary>
public sealed class SentenceChunker
{
    private readonly System.Text.StringBuilder _buffer = new();
    private const int MinLength = 12;

    /// <summary>Adds text; returns the sentences that are now complete.</summary>
    public IReadOnlyList<string> Add(string text)
    {
        _buffer.Append(text);
        var done = new List<string>();
        while (true)
        {
            var s = _buffer.ToString();
            var cut = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (i + 1 < MinLength) continue;
                // a sentence ends at . ! ? followed by a space (a "3.5" or an unfinished buffer end does not), or at a line break
                if (s[i] == '\n' || (s[i] is '.' or '!' or '?' && i + 1 < s.Length && char.IsWhiteSpace(s[i + 1]))) { cut = i + 1; break; }
            }
            if (cut < 0) break;
            var sentence = s[..cut].Trim();
            _buffer.Remove(0, cut);
            if (sentence.Length > 0) done.Add(sentence);
        }
        return done;
    }

    /// <summary>The rest at the end of the answer.</summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Length == 0 ? null : rest;
    }
}

/// <summary>
/// Option 6 (decision V-35): only the text model. The caller is heard and the reply is spoken by SignalWire itself inside cXML <c>Gather</c> / <c>Say</c>, so there is no recogniser, no end-of-turn engine and no
/// speech synthesis of ours, and no stream. The registry keeps it apart from the chained options.
/// </summary>
public sealed class SignalWireSpeechParts(IVoiceLlm llm) : IChainedPipelineParts
{
    public string OptionId => VoicePipelineIds.SignalWireSpeech;
    public IStreamingStt Stt => throw new NotSupportedException("Option 6 has no stream.");
    public ITurnDetector NewTurnDetector() => throw new NotSupportedException("Option 6 has no stream.");
    public ITts Tts => throw new NotSupportedException("Option 6 has no stream.");
    public IVoiceLlm Llm => llm;
}
