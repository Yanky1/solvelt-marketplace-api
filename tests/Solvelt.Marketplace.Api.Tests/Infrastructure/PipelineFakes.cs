using System.Text.Json.Nodes;
using System.Threading.Channels;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>A scripted speech-to-text engine: records the audio it is sent and lets a test push what it "hears".</summary>
public sealed class FakeStt : IStreamingStt
{
    private readonly Channel<SttEvent> _events = Channel.CreateUnbounded<SttEvent>();
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _audio = new();
    public IReadOnlyList<byte[]> Audio => _audio.ToArray();
    public bool Opened { get; private set; }
    public bool FailOnSend { get; set; }
    public void Emit(SttEvent e) => _events.Writer.TryWrite(e);
    public void Say(string text) { Emit(new SttEvent(SttEventKind.SpeechStarted)); Emit(new SttEvent(SttEventKind.Final, text)); Emit(new SttEvent(SttEventKind.SpeechEnded)); }

    public Task<ISttSession> OpenAsync(CancellationToken ct) { Opened = true; return Task.FromResult<ISttSession>(new Session(this)); }

    private sealed class Session(FakeStt o) : ISttSession
    {
        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
        {
            if (o.FailOnSend) throw new InvalidOperationException("engine down");
            o._audio.Enqueue(mulaw8k.ToArray());
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<SttEvent> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await o._events.Reader.WaitToReadAsync(ct)) while (o._events.Reader.TryRead(out var e)) yield return e;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A scripted text-to-speech engine: each sentence becomes <see cref="ChunksPerSentence"/> chunks of <see cref="ChunkBytes"/> bytes (800 bytes = 100 ms of mu-law), and the texts it was asked for are recorded.</summary>
public sealed class FakeTts : ITts
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _texts = new();
    public IReadOnlyList<string> Texts => _texts.ToArray();
    public int ChunksPerSentence { get; set; } = 3;
    public int ChunkBytes { get; set; } = 800;
    public int Cancelled;

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _texts.Enqueue(text);
        for (var i = 0; i < ChunksPerSentence; i++)
        {
            if (ct.IsCancellationRequested) { Interlocked.Increment(ref Cancelled); yield break; }
            yield return new byte[ChunkBytes];
            await Task.Yield();
        }
    }
}

/// <summary>A scripted LLM: each call to it plays the next script; every request it receives is recorded.</summary>
public sealed class FakeLlm : IVoiceLlm
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<LlmRequest> _requests = new();
    private readonly Queue<Func<LlmRequest, IEnumerable<LlmEvent>>> _scripts = new();
    public IReadOnlyList<LlmRequest> Requests => _requests.ToArray();
    /// <summary>Fault injection: the first request (the greeting) takes this many ms to finish after it has reached the model, as on a slow runner; the caller's first turn waits for it (V-57).</summary>
    public int FirstRequestDelayMs { get; set; }
    private int _first;
    public Func<LlmRequest, IEnumerable<LlmEvent>> Default { get; set; } = _ => [new LlmTextDelta("Okay."), new LlmCompleted(10, 2)];
    public void Then(params LlmEvent[] events) => _scripts.Enqueue(_ => events);
    public void Then(Func<LlmRequest, IEnumerable<LlmEvent>> script) => _scripts.Enqueue(script);

    public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _requests.Enqueue(request);
        if (Interlocked.Increment(ref _first) == 1 && FirstRequestDelayMs > 0) await Task.Delay(FirstRequestDelayMs);   // not cancellable: the caller's turn must wait for it
        Func<LlmRequest, IEnumerable<LlmEvent>> script;
        lock (_scripts) script = _scripts.Count > 0 ? _scripts.Dequeue() : Default;
        foreach (var e in script(request)) { ct.ThrowIfCancellationRequested(); yield return e; await Task.Yield(); }
    }
}

/// <summary>The parts of one option, all fakes.</summary>
public sealed class FakeParts(string option, int endpointMs = 700) : IChainedPipelineParts
{
    public string OptionId => option;
    public FakeStt SttFake { get; } = new();
    public FakeTts TtsFake { get; } = new();
    public FakeLlm LlmFake { get; } = new();
    public IStreamingStt Stt => SttFake;
    public ITts Tts => TtsFake;
    public IVoiceLlm Llm => LlmFake;
    public ITurnDetector NewTurnDetector() => new SilenceTurnDetector(endpointMs);
}

public sealed class FakeJudge(Func<IReadOnlyList<SttCandidate>, CancellationToken, Task<JudgeResult?>> f) : ITranscriptJudge
{
    public int Calls;
    public List<IReadOnlyList<SttCandidate>> Seen { get; } = [];
    public Task<JudgeResult?> JudgeAsync(IReadOnlyList<SttCandidate> candidates, CancellationToken ct) { Interlocked.Increment(ref Calls); lock (Seen) Seen.Add(candidates); return f(candidates, ct); }
}
