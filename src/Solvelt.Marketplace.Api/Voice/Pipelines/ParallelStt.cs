using System.Threading.Channels;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>One engine's finished text for one utterance. Held in memory while the round is judged; never stored or logged.</summary>
public sealed record SttCandidate(string Engine, string Text, string? Language, double? Confidence);

public sealed record JudgeResult(string Text, string? Language = null);

/// <summary>Picks or reconstructs what the caller meant from several engines' texts (an LLM in production, a fake in tests). Null or empty means "no opinion".</summary>
public interface ITranscriptJudge
{
    Task<JudgeResult?> JudgeAsync(IReadOnlyList<SttCandidate> candidates, CancellationToken ct);
}

/// <summary>
/// Option p5 (decision V-19): two or three recognisers run on the same audio at once and judges choose, or rebuild, the words. The shape is fixed here, against fakes, so the vendors can be dropped in:
/// the FIRST engine is the primary (it alone reports speech start, partials and speech end, which the barge-in and turn logic read), every engine's finals are collected for one utterance, and
/// a HARD latency budget runs from the first final: wait <c>waitMs</c> for the slower engines, then judge (all judges at once), and whatever has not answered when <c>budgetMs</c> is spent is dropped.
/// The FALLBACK, used when no judge answers in time, throws, or returns nothing: the primary engine's text, else the first text that arrived. A failing engine never stops the call: its audio send and its events
/// are ignored from then on. The text of every utterance leaves this class exactly once, as one <see cref="SttEventKind.Final"/>.
/// </summary>
public sealed class ParallelStt : IStreamingStt
{
    /// <summary>A test seam: how many finals the coordinator has taken from the engines so far (opening a round, joining one, or dropping a late one). A test that moves the fake clock must first wait until the coordinator has consumed what it emitted, or the clock outruns the thread pool and the test measures the speed of the machine.</summary>
    internal interface IProbe
    {
        int FinalsSeen { get; }
        /// <summary>A test seam: how many rounds have asked their judges AND armed the budget wait (every judge has been entered, so any fake-clock delay of a judge is registered, and the budget timers exist). A test that moves the fake clock after a round was completed must first wait for this, or the clock can outrun the thread pool and a judge's delay is measured from a later time than the budget.</summary>
        int DecisionsArmed { get; }
    }

    private readonly IReadOnlyList<(string Name, IStreamingStt Stt)> engines;
    private readonly IReadOnlyList<ITranscriptJudge> judges;
    private readonly int waitMs, budgetMs;
    private readonly TimeProvider time;
    private readonly ILogger? log;

    public ParallelStt(IReadOnlyList<(string Name, IStreamingStt Stt)> engines, IReadOnlyList<ITranscriptJudge> judges, int waitMs, int budgetMs, TimeProvider time, ILogger? log = null)
    {
        this.engines = engines; this.judges = judges; this.waitMs = waitMs; this.budgetMs = budgetMs; this.time = time; this.log = log;
    }

    public async Task<ISttSession> OpenAsync(CancellationToken ct)
    {
        var sessions = new List<ISttSession?>();
        foreach (var (name, stt) in engines)
        {
            try { sessions.Add(await stt.OpenAsync(ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { sessions.Add(null); log?.LogWarning("Speech engine {Engine} could not be opened ({Type}); the call goes on without it.", name, ex.GetType().Name); }
        }
        if (sessions.All(s => s is null)) throw new InvalidOperationException("no speech engine could be opened");
        return new Session(this, sessions);
    }

    private sealed class Session : ISttSession, IProbe
    {
        private int _finalsSeen;
        public int FinalsSeen => Volatile.Read(ref _finalsSeen);
        private int _decisionsArmed;
        public int DecisionsArmed => Volatile.Read(ref _decisionsArmed);
        private readonly ParallelStt _o;
        private readonly List<ISttSession?> _engines;
        private readonly Channel<(int Engine, SttEvent Event)> _merged = Channel.CreateUnbounded<(int, SttEvent)>();
        private readonly Channel<SttEvent> _out = Channel.CreateUnbounded<SttEvent>();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _tasks = [];

        public Session(ParallelStt o, List<ISttSession?> engines)
        {
            _o = o; _engines = engines;
            for (var i = 0; i < engines.Count; i++) { var idx = i; if (engines[i] is not null) _tasks.Add(Task.Run(() => ReadEngineAsync(idx))); }
            _tasks.Add(Task.Run(CoordinateAsync));
        }

        public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
        {
            for (var i = 0; i < _engines.Count; i++)
            {
                if (_engines[i] is not { } e) continue;
                try { await e.SendAudioAsync(mulaw8k, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _engines[i] = null; _o.log?.LogWarning("Speech engine {Engine} failed ({Type}); the call goes on without it.", _o.engines[i].Name, ex.GetType().Name); }
            }
        }

        public async IAsyncEnumerable<SttEvent> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _out.Reader.WaitToReadAsync(ct)) while (_out.Reader.TryRead(out var e)) yield return e;
        }

        private async Task ReadEngineAsync(int idx)
        {
            try { await foreach (var e in _engines[idx]!.Events(_cts.Token)) _merged.Writer.TryWrite((idx, e)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _engines[idx] = null; _o.log?.LogWarning("Speech engine {Engine} failed ({Type}); the call goes on without it.", _o.engines[idx].Name, ex.GetType().Name); }
            catch (OperationCanceledException) { }
        }

        private async Task CoordinateAsync()
        {
            var ct = _cts.Token;
            var pending = new Queue<(int Engine, SttEvent Event)>();
            var owed = new int[_engines.Count];   // finals an engine still owes for an utterance whose round has closed without it: they are late, not a new utterance
            try
            {
                while (true)
                {
                    (int Engine, SttEvent Event) item;
                    var fromPending = pending.Count > 0;
                    if (fromPending) item = pending.Dequeue();
                    else { if (!await _merged.Reader.WaitToReadAsync(ct)) break; if (!_merged.Reader.TryRead(out item)) continue; }
                    if (item.Event.Kind != SttEventKind.Final) { Forward(item); continue; }
                    if (!fromPending) Interlocked.Increment(ref _finalsSeen);
                    if (owed[item.Engine] > 0) { owed[item.Engine]--; continue; }   // a slow engine's text for an utterance already decided: dropped

                    // a round: this final opens it; the others may join until waitMs has passed
                    var start = _o.time.GetTimestamp();
                    var round = new SortedDictionary<int, SttEvent> { [item.Engine] = item.Event };
                    var wait = TimeSpan.FromMilliseconds(Math.Max(0, _o.waitMs));
                    while (round.Count < _engines.Count(e => e is not null) && time_left(start, wait) is { } left && left > TimeSpan.Zero)
                    {
                        using var step = new CancellationTokenSource(left, _o.time);
                        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, step.Token);
                        try { if (!await _merged.Reader.WaitToReadAsync(link.Token)) break; }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                        while (_merged.Reader.TryRead(out var next))
                        {
                            if (next.Event.Kind != SttEventKind.Final) Forward(next);
                            else if (Count(next) && !round.ContainsKey(next.Engine)) round[next.Engine] = next.Event;
                            else pending.Enqueue(next);   // a second final of an engine belongs to the next utterance
                        }
                    }
                    for (var i = 0; i < _engines.Count; i++) if (_engines[i] is not null && !round.ContainsKey(i)) owed[i]++;
                    var spent = _o.time.GetElapsedTime(start);
                    var final = await DecideAsync(round, TimeSpan.FromMilliseconds(Math.Max(0, _o.budgetMs)) - spent, ct);
                    if (final is not null) _out.Writer.TryWrite(final);
                }
            }
            catch (OperationCanceledException) { }
            finally { _out.Writer.TryComplete(); }

            bool Count((int Engine, SttEvent Event) _) { Interlocked.Increment(ref _finalsSeen); return true; }
            TimeSpan? time_left(long since, TimeSpan total) => total - _o.time.GetElapsedTime(since);
        }

        /// <summary>The primary engine (the first) is the only voice of speech start, partials and speech end; the others are only read for their finals.</summary>
        private void Forward((int Engine, SttEvent Event) item) { if (item.Engine == FirstAlive() || item.Engine == 0) _out.Writer.TryWrite(item.Event); }

        private int FirstAlive() { for (var i = 0; i < _engines.Count; i++) if (_engines[i] is not null) return i; return 0; }

        private async Task<SttEvent?> DecideAsync(SortedDictionary<int, SttEvent> round, TimeSpan remaining, CancellationToken ct)
        {
            var candidates = round.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Text)).Select(kv => new SttCandidate(_o.engines[kv.Key].Name, kv.Value.Text.Trim(), kv.Value.Language, kv.Value.Confidence)).ToList();
            if (candidates.Count == 0) return null;
            // the fallback, ready before any judge is asked: the primary engine's text, else the first one that arrived
            var fallback = candidates.FirstOrDefault(c => c.Engine == _o.engines[0].Name) ?? candidates[0];
            var chosen = new SttEvent(SttEventKind.Final, fallback.Text, fallback.Language, fallback.Confidence);
            if (candidates.Count == 1 && _o.judges.Count == 0) return chosen;
            if (remaining <= TimeSpan.Zero || _o.judges.Count == 0) return chosen;
            using var budget = new CancellationTokenSource(remaining, _o.time);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            var asks = _o.judges.Select(async j =>
            {
                try { return await j.JudgeAsync(candidates, link.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { _o.log?.LogWarning("A transcript judge failed or ran out of time ({Type}); the fallback text is used.", ex.GetType().Name); return null; }
            }).ToList();
            var answered = Task.WhenAll(asks).WaitAsync(remaining, _o.time, ct);
            Interlocked.Increment(ref _decisionsArmed);   // the judges have been entered and both budget timers exist: see IProbe
            try { await answered; }
            catch (TimeoutException) { /* the budget is spent: whoever has answered counts, the rest is dropped */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            foreach (var ask in asks)   // judges in priority order; the first real answer wins
                if (ask.IsCompletedSuccessfully && ask.Result is { } r && !string.IsNullOrWhiteSpace(r.Text)) return new SttEvent(SttEventKind.Final, r.Text.Trim(), r.Language ?? fallback.Language, fallback.Confidence);
            return chosen;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Task.WhenAll(_tasks); } catch (Exception) { /* ended by cancellation */ }
            foreach (var e in _engines) if (e is not null) { try { await e.DisposeAsync(); } catch (Exception) { /* already gone */ } }
            _cts.Dispose();
        }
    }
}
