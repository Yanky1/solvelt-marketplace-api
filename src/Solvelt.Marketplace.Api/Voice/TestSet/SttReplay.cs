using System.Diagnostics;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>How a recording is played into a recogniser. Real time (the default) sends 20 ms of audio every 20 ms, as a call does; fast sends it at once, which is quicker but not how a phone call arrives.</summary>
public sealed record ReplayOptions(bool RealTime = true, int TailSilenceMs = 1500, int IdleMs = 2500, int MaxWaitMs = 12000);

/// <summary>What one recogniser wrote for one recording. <see cref="Error"/> is only the kind of failure (an exception type), never a message that could carry vendor or audio detail.</summary>
public sealed record ReplayResult(string Text, int? LatencyMs, bool TimedOut, string? Error);

/// <summary>Plays one recording into one recogniser and collects the finished words (decision V-46). The same <see cref="IStreamingStt"/> the telephone line uses, so the numbers are the line's numbers.</summary>
public static class SttReplay
{
    private const int FrameBytes = 160;   // 20 ms of G.711 at 8 kHz

    public static async Task<ReplayResult> RunAsync(IStreamingStt stt, byte[] mulaw8k, ReplayOptions o, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var finals = new List<string>();
        long lastEventMs = 0, lastFinalMs = -1;
        var gate = new object();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using var session = await stt.OpenAsync(ct);
            var reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in session.Events(stop.Token))
                    {
                        lock (gate)
                        {
                            lastEventMs = clock.ElapsedMilliseconds;
                            if (e.Kind == SttEventKind.Final && !string.IsNullOrWhiteSpace(e.Text)) { finals.Add(e.Text.Trim()); lastFinalMs = lastEventMs; }
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            async Task Send(ReadOnlyMemory<byte> frame, int index)
            {
                if (o.RealTime) { var due = index * 20L - clock.ElapsedMilliseconds; if (due > 0) await Task.Delay((int)due, ct); }
                await session.SendAudioAsync(frame, ct);
            }
            var n = 0;
            for (var i = 0; i < mulaw8k.Length; i += FrameBytes) await Send(mulaw8k.AsMemory(i, Math.Min(FrameBytes, mulaw8k.Length - i)), n++);
            var audioEndMs = clock.ElapsedMilliseconds;
            var silence = new byte[FrameBytes]; Array.Fill(silence, (byte)0xFF);                 // mu-law digital silence: lets the recogniser's own end-of-speech detection fire
            for (var t = 0; t < o.TailSilenceMs; t += 20) await Send(silence, n++);
            lock (gate) lastEventMs = Math.Max(lastEventMs, clock.ElapsedMilliseconds);

            var timedOut = false;
            while (true)
            {
                await Task.Delay(25, ct);
                long now = clock.ElapsedMilliseconds, quietSince; int count;
                lock (gate) { quietSince = lastEventMs; count = finals.Count; }
                if (count > 0 && now - quietSince >= o.IdleMs) break;
                if (now - audioEndMs >= o.MaxWaitMs) { timedOut = count == 0; break; }
            }
            stop.Cancel();
            await reader;
            lock (gate) return new ReplayResult(string.Join(' ', finals), lastFinalMs >= 0 ? (int)Math.Max(0, lastFinalMs - audioEndMs) : null, timedOut, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (gate) return new ReplayResult(string.Join(' ', finals), null, false, ex.GetType().Name);
        }
    }
}
