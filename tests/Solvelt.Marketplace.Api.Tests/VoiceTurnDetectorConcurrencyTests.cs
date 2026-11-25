using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The silence turn detector is called from two threads in a live call: the recogniser reader (OnEvent) and the 50 ms tick (OnTick). Every final the caller said must come out in exactly one turn, once (decision V-56).
/// </summary>
public sealed class VoiceTurnDetectorConcurrencyTests
{
    [Fact]
    public async Task Every_final_comes_out_in_exactly_one_turn_when_the_reader_and_the_tick_run_at_once()
    {
        const int finals = 200_000;
        var d = new SilenceTurnDetector(1);
        var origin = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var latest = 0L;
        var seen = new List<string>();
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < finals; i++)
            {
                Volatile.Write(ref latest, i);
                d.OnEvent(new SttEvent(SttEventKind.Final, "f" + i, "en"), origin.AddMilliseconds(i));
            }
        });
        var ticker = Task.Run(() =>
        {
            while (!reader.IsCompleted)
                if (d.OnTick(origin.AddMilliseconds(Volatile.Read(ref latest) + 5)) is { Kind: TurnSignalKind.TurnEnded } s) seen.Add(s.Text!);
        });
        await Task.WhenAll(reader, ticker);
        if (d.OnTick(origin.AddMilliseconds(finals + 100)) is { Kind: TurnSignalKind.TurnEnded } last) seen.Add(last.Text!);
        var words = seen.SelectMany(t => t.Split(' ')).ToList();
        Assert.Equal(finals, words.Count);
        Assert.Equal(finals, words.Distinct().Count());
    }
}
