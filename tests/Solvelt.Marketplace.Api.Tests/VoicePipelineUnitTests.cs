using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Option 5's orchestration (parallel engines, judges, a hard budget, a fallback) and the small parts of the chained pipeline, against fakes and fake time.</summary>
public class VoicePipelineUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    // ---------- the end-of-turn engine and the sentence chunker ----------

    [Fact]
    public void A_turn_ends_when_the_caller_has_words_and_has_been_silent_for_the_endpoint()
    {
        var d = new SilenceTurnDetector(700);
        Assert.Equal(TurnSignalKind.UserSpeaking, d.OnEvent(new SttEvent(SttEventKind.SpeechStarted), T0).Kind);
        d.OnEvent(new SttEvent(SttEventKind.Final, "I want"), T0.AddMilliseconds(400));
        Assert.Equal(TurnSignalKind.None, d.OnTick(T0.AddMilliseconds(1000)).Kind);                            // 600 ms since the last words
        d.OnEvent(new SttEvent(SttEventKind.Final, "a challah"), T0.AddMilliseconds(1000));                     // more words restart the wait
        Assert.Equal(TurnSignalKind.None, d.OnTick(T0.AddMilliseconds(1600)).Kind);
        var end = d.OnTick(T0.AddMilliseconds(1701));
        Assert.Equal((TurnSignalKind.TurnEnded, "I want a challah"), (end.Kind, end.Text));
        Assert.Equal(TurnSignalKind.None, d.OnTick(T0.AddMilliseconds(5000)).Kind);                            // the turn is handed on once
    }

    [Fact]
    public void Silence_without_words_never_ends_a_turn()
    {
        var d = new SilenceTurnDetector(700);
        d.OnEvent(new SttEvent(SttEventKind.SpeechStarted), T0);
        d.OnEvent(new SttEvent(SttEventKind.Final, "   "), T0);
        Assert.Equal(TurnSignalKind.None, d.OnTick(T0.AddSeconds(30)).Kind);
    }

    [Fact]
    public void Sentences_are_cut_as_text_arrives_and_the_rest_is_flushed()
    {
        var c = new SentenceChunker();
        Assert.Empty(c.Add("Hello and welcome"));
        Assert.Equal(["Hello and welcome to Solvelt."], c.Add(" to Solvelt. How"));
        Assert.Empty(c.Add(" can I help you today?"));                                                          // no space after the question mark yet: it may not be the end
        Assert.Equal("How can I help you today?", c.Flush());
        Assert.Null(c.Flush());
        var d = new SentenceChunker();
        Assert.Empty(d.Add("It costs 3.50 dollars"));                                                           // a decimal point is not a sentence end
        Assert.Equal("It costs 3.50 dollars", d.Flush());
        Assert.Equal(["Line one is here", "Line two follows."], new SentenceChunker().Add("Line one is here\nLine two follows.\n"));
    }

    [Fact]
    public void The_heard_part_is_whole_sentences_that_played_and_the_played_share_of_the_cut_one()
    {
        IReadOnlyList<(string, long)> spoken = [("One two three four.", 400), ("Five six seven eight.", 400)];
        Assert.Equal("", ChainedBridge.HeardPart(spoken, 0));
        Assert.Equal("One two", ChainedBridge.HeardPart(spoken, 200));                                          // half of 4 words
        Assert.Equal("One two three four.", ChainedBridge.HeardPart(spoken, 400));
        Assert.Equal("One two three four. Five six", ChainedBridge.HeardPart(spoken, 600));
        Assert.Equal("One two three four. Five six seven eight.", ChainedBridge.HeardPart(spoken, 5000));
    }

    // ---------- option 5: parallel engines + judges ----------

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeTimeProvider Time;
        private Rig(FakeTimeProvider? time) { Time = time ?? new FakeTimeProvider(); }
        public readonly FakeStt A = new(), B = new(), C = new();
        public readonly List<SttEvent> Out = [];
        public ISttSession Session = null!;
        private Task _reader = Task.CompletedTask;
        private readonly CancellationTokenSource _cts = new();

        public static async Task<Rig> Open(ITranscriptJudge[] judges, int waitMs = 500, int budgetMs = 1200, bool three = false, FakeTimeProvider? time = null)
        {
            var r = new Rig(time);
            var engines = new List<(string, IStreamingStt)> { ("alpha", r.A), ("beta", r.B) };
            if (three) engines.Add(("gamma", r.C));
            r.Session = await new ParallelStt(engines, judges, waitMs, budgetMs, r.Time).OpenAsync(CancellationToken.None);
            r._reader = Task.Run(async () => { try { await foreach (var e in r.Session.Events(r._cts.Token)) lock (r.Out) r.Out.Add(e); } catch (OperationCanceledException) { } });
            return r;
        }

        /// <summary>
        /// Waits (real time) until <paramref name="rounds"/> rounds have asked their judges and armed the budget. Call it after the final that completes a round and BEFORE moving the fake clock: the judges run on the thread
        /// pool, and a judge that enters after the clock has moved registers its fake-clock delay at a later time than the budget (the test then measures the speed of the build agent).
        /// </summary>
        public async Task Armed(int rounds = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (((ParallelStt.IProbe)Session).DecisionsArmed < rounds && DateTime.UtcNow < deadline) await Task.Delay(2);
            Assert.True(((ParallelStt.IProbe)Session).DecisionsArmed >= rounds, "the judges were not asked within 10 s");
        }

        public async Task Pump(int ms) { for (var i = 0; i < ms / 10; i++) { Time.Advance(TimeSpan.FromMilliseconds(10)); await Task.Delay(1); } await Task.Delay(20); }
        public List<SttEvent> Finals() { lock (Out) return Out.Where(e => e.Kind == SttEventKind.Final).ToList(); }

        private int _emittedFinals;
        /// <summary>
        /// Emits a final on an engine and waits (real time) until the coordinator has TAKEN it. Time may only move after that: the orchestration runs on the thread pool and the fake clock does not wait for it, so
        /// a clock that outruns a slow machine changes when a round opened and what the budget allows, and the test then measures the speed of the build agent.
        /// </summary>
        public async Task Final(FakeStt engine, string text)
        {
            engine.Emit(new SttEvent(SttEventKind.Final, text));
            var want = Interlocked.Increment(ref _emittedFinals);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (((ParallelStt.IProbe)Session).FinalsSeen < want && DateTime.UtcNow < deadline) await Task.Delay(2);
            Assert.True(((ParallelStt.IProbe)Session).FinalsSeen >= want, "the coordinator did not take the final within 10 s");
        }

        /// <summary>
        /// Waits (in REAL time; the fake clock only creeps on while nothing has arrived, see below) until <paramref name="count"/> finals have left, then gives a second chance for a wrongly doubled one to show up. The orchestration runs on the thread pool, so how
        /// long it needs depends on the machine; a test that asserts after a fixed pause tests the speed of the build agent. Fails with the state after <paramref name="seconds"/>.
        /// </summary>
        public async Task<List<SttEvent>> WaitForFinals(int count, int seconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            // the fake clock keeps creeping on while the final is awaited: a timer the coordinator creates AFTER the test stopped moving time (it computed its remaining budget a moment before the last step) would never fire
            // otherwise. A late nudge cannot change what the final is: the deadline passes first and the text is the same.
            while (Finals().Count < count && DateTime.UtcNow < deadline) { Time.Advance(TimeSpan.FromMilliseconds(10)); await Task.Delay(5); }
            await Task.Delay(30);
            return Finals();
        }
        public async ValueTask DisposeAsync() { _cts.Cancel(); await Session.DisposeAsync(); try { await _reader; } catch (Exception) { } }
    }

    private static FakeJudge Pick(string text) => new((_, _) => Task.FromResult<JudgeResult?>(new JudgeResult(text, "yi")));

    [Fact]
    public async Task Both_engines_answer_the_judge_chooses_and_exactly_one_text_leaves()
    {
        var judge = new FakeJudge((c, _) => Task.FromResult<JudgeResult?>(new JudgeResult(c.Single(x => x.Engine == "beta").Text + "!", "yi")));
        await using var r = await Rig.Open([judge]);
        await r.Final(r.A, "a challah"); await r.Final(r.B, "a hallah");
        var final = Assert.Single(await r.WaitForFinals(1));
        Assert.Equal(("a hallah!", "yi"), (final.Text, final.Language));
        Assert.Equal(["alpha", "beta"], judge.Seen.Single().Select(c => c.Engine));                              // both candidates, in engine order
    }

    [Fact]
    public async Task Only_the_primary_engine_reports_speech_start_partials_and_speech_end()
    {
        await using var r = await Rig.Open([]);
        r.A.Emit(new SttEvent(SttEventKind.SpeechStarted)); r.B.Emit(new SttEvent(SttEventKind.SpeechStarted));
        r.A.Emit(new SttEvent(SttEventKind.Partial, "a cha")); r.B.Emit(new SttEvent(SttEventKind.Partial, "a ha"));
        r.A.Emit(new SttEvent(SttEventKind.SpeechEnded)); r.B.Emit(new SttEvent(SttEventKind.SpeechEnded));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until) { lock (r.Out) if (r.Out.Count >= 3) break; await Task.Delay(5); }
        await Task.Delay(30);                                                                                    // and a moment for a wrongly forwarded second voice to show up
        lock (r.Out) Assert.Equal([SttEventKind.SpeechStarted, SttEventKind.Partial, SttEventKind.SpeechEnded], r.Out.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_silent_engine_is_waited_for_only_up_to_the_wait_then_the_judge_decides_with_what_there_is()
    {
        var judge = Pick("judged");
        await using var r = await Rig.Open([judge], waitMs: 500);
        await r.Final(r.A, "alpha text");
        await r.Pump(400);
        Assert.Empty(r.Finals());                                                                                // still inside the wait for beta
        await r.Pump(200);
        Assert.Equal("judged", Assert.Single(await r.WaitForFinals(1)).Text);
        Assert.Equal(["alpha"], judge.Seen.Single().Select(c => c.Engine));
    }

    [Fact]
    public async Task The_hard_budget_a_slow_judge_is_dropped_and_the_primary_text_is_used_on_time()
    {
        var slow = new FakeJudge(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new JudgeResult("never"); });
        await using var r = await Rig.Open([slow], waitMs: 500, budgetMs: 1200);
        await r.Final(r.A, "alpha text"); await r.Final(r.B, "beta text");
        await r.Armed();
        await r.Pump(1100);
        Assert.Empty(r.Finals());                                                                                // the judge still has time
        await r.Pump(200);
        var final = Assert.Single(await r.WaitForFinals(1));
        Assert.Equal("alpha text", final.Text);                                                                  // the FALLBACK: the primary engine, never the judge's silence
    }

    [Fact]
    public async Task The_budget_is_hard_even_for_a_judge_that_ignores_its_cancellation_token()
    {
        var gate = new TaskCompletionSource<JudgeResult?>();
        var stubborn = new FakeJudge((_, _) => gate.Task);                                                      // never looks at the token
        await using var r = await Rig.Open([stubborn], waitMs: 500, budgetMs: 1200);
        await r.Final(r.A, "alpha text"); await r.Final(r.B, "beta text");
        await r.Armed();
        await r.Pump(1300);
        Assert.Equal("alpha text", Assert.Single(await r.WaitForFinals(1)).Text);
        gate.SetResult(new JudgeResult("far too late"));
    }

    [Fact]
    public async Task The_budget_counts_from_the_first_final_so_waiting_for_a_slow_engine_shortens_the_judges_time()
    {
        // beta's text arrives 400 ms after alpha's; the judge needs 700 ms: 400 + 700 = 1100 <= 1200 works, a 900 ms judge (1300) does not
        var t1 = new FakeTimeProvider();
        var ok = new FakeJudge(async (_, ct) => { await Task.Delay(TimeSpan.FromMilliseconds(700), t1, ct); return new JudgeResult("fast enough"); });
        await using (var r = await Rig.Open([ok], waitMs: 500, budgetMs: 1200, time: t1)) { await RunLate(r); Assert.Equal("fast enough", Assert.Single(await r.WaitForFinals(1)).Text); }
        var t2 = new FakeTimeProvider();
        var tooSlow = new FakeJudge(async (_, ct) => { await Task.Delay(TimeSpan.FromMilliseconds(900), t2, ct); return new JudgeResult("too late"); });
        await using (var r = await Rig.Open([tooSlow], waitMs: 500, budgetMs: 1200, time: t2)) { await RunLate(r); Assert.Equal("alpha", Assert.Single(await r.WaitForFinals(1)).Text); }

        static async Task RunLate(Rig r)
        {
            await r.Final(r.A, "alpha");
            await r.Pump(400);
            await r.Final(r.B, "beta");
            await r.Armed();                                                                                     // the judge is entered and its delay registered before time moves
            await r.Pump(1400);
        }
    }

    [Fact]
    public async Task A_judge_that_fails_or_has_no_opinion_falls_back_at_once()
    {
        foreach (var judge in new[] { new FakeJudge((_, _) => throw new InvalidOperationException("model down")), new FakeJudge((_, _) => Task.FromResult<JudgeResult?>(null)), new FakeJudge((_, _) => Task.FromResult<JudgeResult?>(new JudgeResult("  "))) })
        {
            await using var r = await Rig.Open([judge]);
            await r.Final(r.A, "alpha text"); await r.Final(r.B, "beta text");
            Assert.Equal("alpha text", Assert.Single(await r.WaitForFinals(1)).Text);
        }
    }

    [Fact]
    public async Task Several_judges_run_at_once_and_the_first_in_priority_that_answers_wins()
    {
        var first = new FakeJudge((_, _) => Task.FromResult<JudgeResult?>(null));                                // no opinion
        var second = Pick("second judge");
        var third = Pick("third judge");
        await using var r = await Rig.Open([first, second, third]);
        await r.Final(r.A, "a"); await r.Final(r.B, "b");
        Assert.Equal("second judge", Assert.Single(await r.WaitForFinals(1)).Text);
        Assert.Equal((1, 1, 1), (first.Calls, second.Calls, third.Calls));
    }

    [Fact]
    public async Task A_late_text_from_a_slow_engine_is_not_a_new_utterance_and_a_failing_engine_never_stops_the_call()
    {
        await using var r = await Rig.Open([Pick("judged")], waitMs: 200, budgetMs: 800);
        await r.Final(r.A, "first");
        await r.Pump(400);
        Assert.Equal("judged", Assert.Single(await r.WaitForFinals(1)).Text);
        await r.Final(r.B, "first but late");                                            // beta's text for the SAME utterance, after the round closed
        await r.Pump(600);
        Assert.Single(r.Finals());                                                                               // dropped, not a second utterance
        r.B.FailOnSend = true;
        await r.Session.SendAudioAsync(new byte[] { 1 }, CancellationToken.None);                                // beta throws: ignored from now on
        await r.Session.SendAudioAsync(new byte[] { 2 }, CancellationToken.None);
        Assert.Equal(2, r.A.Audio.Count);
        await r.Final(r.A, "second");
        await r.Pump(300);
        Assert.Equal(2, (await r.WaitForFinals(2)).Count);
    }

    [Fact]
    public async Task With_no_judge_and_one_engine_the_text_passes_through_and_three_engines_all_feed_the_judge()
    {
        await using (var one = await Rig.Open([], waitMs: 100, budgetMs: 500)) { await one.Final(one.A, "only alpha"); await one.Pump(200); Assert.Equal("only alpha", Assert.Single(await one.WaitForFinals(1)).Text); }
        var judge = Pick("x");
        await using var r = await Rig.Open([judge], three: true);
        await r.Final(r.A, "a"); await r.Final(r.B, "b"); await r.Final(r.C, "c");
        await r.WaitForFinals(1);
        Assert.Equal(["alpha", "beta", "gamma"], judge.Seen.Single().Select(c => c.Engine));
    }

    [Fact]
    public async Task If_no_engine_can_be_opened_the_call_is_not_started_on_this_option()
    {
        var down = new ThrowingStt();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ParallelStt([("a", down), ("b", down)], [], 100, 500, new FakeTimeProvider()).OpenAsync(CancellationToken.None));
        var ok = new FakeStt();
        await using var s = await new ParallelStt([("a", down), ("b", ok)], [], 100, 500, new FakeTimeProvider()).OpenAsync(CancellationToken.None);   // one is enough
        await s.SendAudioAsync(new byte[] { 9 }, CancellationToken.None);
        Assert.Single(ok.Audio);
    }

    private sealed class ThrowingStt : IStreamingStt { public Task<ISttSession> OpenAsync(CancellationToken ct) => throw new HttpRequestException("down"); }
}
