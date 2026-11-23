using System.Text;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>One recogniser setting (a name) and the recogniser built with it.</summary>
public sealed record ScoringVariant(string Name, IStreamingStt Stt);

/// <summary>One recording scored against its reference for one variant.</summary>
public sealed record ScoreRow(string Variant, string PhraseId, string Language, string Kind, string Speaker, bool Draft, string Reference, string Hypothesis, EditCount Wer, EditCount Cer, int? LatencyMs, bool TimedOut, string? Error)
{
    public bool Failed => Error is not null || TimedOut;
}

public sealed record ScoringRun(IReadOnlyList<ScoreRow> Rows, IReadOnlyList<string> Skipped);

public sealed record ScoringOptions(string Set, IReadOnlyCollection<string>? Speakers = null, bool IncludeDraft = false, ReplayOptions? Replay = null);

/// <summary>Replays every recording of a set through every variant and scores the words (decision V-46). On demand only; it contacts the vendors the variants name, so it is never part of a test run against live services.</summary>
public static class TestSetScorer
{
    public static async Task<ScoringRun> RunAsync(ITestSetStore store, PhraseList phrases, IReadOnlyList<ScoringVariant> variants, ScoringOptions o, CancellationToken ct, Action<string>? progress = null)
    {
        var skipped = new List<string>();
        var recordings = new List<(RecordingName Name, Phrase Phrase)>();
        await foreach (var blob in store.ListAsync(o.Set, ct))
        {
            if (!RecordingName.TryParse(blob, out var name)) { skipped.Add($"{blob}: not a recording name"); continue; }
            var phrase = phrases.Find(name.PhraseId);
            if (phrase is null) { skipped.Add($"{blob}: no such phrase"); continue; }
            if (phrase.IsDraft && !o.IncludeDraft) { skipped.Add($"{blob}: draft phrase (use --include-draft)"); continue; }
            if (o.Speakers is { Count: > 0 } && !o.Speakers.Contains(name.Speaker)) continue;
            recordings.Add((name, phrase));
        }
        var rows = new List<ScoreRow>();
        foreach (var (name, phrase) in recordings.OrderBy(r => r.Name.PhraseId, StringComparer.Ordinal).ThenBy(r => r.Name.Speaker, StringComparer.Ordinal))
        {
            byte[] audio;
            try { audio = WavAudio.ToMulaw8k(await store.ReadAsync(name.ToString(), ct)); }
            catch (Exception ex) when (ex is FormatException or IOException or Azure.RequestFailedException) { skipped.Add($"{name}: {ex.GetType().Name}"); continue; }
            foreach (var v in variants)
            {
                progress?.Invoke($"{v.Name} {name.PhraseId}-{name.Speaker}");
                var r = await SttReplay.RunAsync(v.Stt, audio, o.Replay ?? new ReplayOptions(), ct);
                rows.Add(new ScoreRow(v.Name, phrase.Id, phrase.Language, phrase.Kind, name.Speaker, phrase.IsDraft, phrase.Text, r.Text, ErrorRates.Wer(phrase.Text, r.Text), ErrorRates.Cer(phrase.Text, r.Text), r.LatencyMs, r.TimedOut, r.Error));
            }
        }
        return new ScoringRun(rows, skipped);
    }
}

/// <summary>The numbers, as text for the console and as CSV for a file kept OUTSIDE the repository. They hold the owner's test phrases and what each recogniser heard, never audio.</summary>
public static class ScoreReport
{
    public sealed record Summary(string Variant, string Group, int Recordings, int Failed, EditCount Wer, EditCount Cer, int? MedianLatencyMs);

    public static IReadOnlyList<Summary> Summarise(IReadOnlyList<ScoreRow> rows)
    {
        var o = new List<Summary>();
        foreach (var byVariant in rows.GroupBy(r => r.Variant))
        {
            void Add(string group, IEnumerable<ScoreRow> g)
            {
                var l = g.ToList(); if (l.Count == 0) return;
                var lat = l.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).OrderBy(x => x).ToList();
                o.Add(new Summary(byVariant.Key, group, l.Count, l.Count(r => r.Failed), l.Aggregate(default(EditCount), (a, r) => a + r.Wer), l.Aggregate(default(EditCount), (a, r) => a + r.Cer), lat.Count == 0 ? null : lat[lat.Count / 2]));
            }
            Add("all", byVariant);
            foreach (var lang in new[] { "en", "yi" }) Add(lang, byVariant.Where(r => r.Language == lang));
            foreach (var kind in byVariant.Select(r => r.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal)) Add("kind:" + kind, byVariant.Where(r => r.Kind == kind));
        }
        return o;
    }

    private static string Pct(double r) => (r * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public static string Text(ScoringRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recognition test set: word and character error rates (lower is better). Counted over all words and characters, not averaged per phrase.");
        sb.AppendLine();
        sb.AppendLine($"{"variant",-28}{"group",-14}{"n",4}{"fail",5}{"WER",9}{"CER",9}{"median ms",11}");
        foreach (var s in Summarise(run.Rows))
            sb.AppendLine($"{s.Variant,-28}{s.Group,-14}{s.Recordings,4}{s.Failed,5}{Pct(s.Wer.Rate),9}{Pct(s.Cer.Rate),9}{(s.MedianLatencyMs?.ToString() ?? "-"),11}");
        sb.AppendLine();
        sb.AppendLine("Per recording (S/D/I = substitutions, deletions, insertions of words):");
        foreach (var r in run.Rows.OrderBy(r => r.PhraseId, StringComparer.Ordinal).ThenBy(r => r.Speaker, StringComparer.Ordinal).ThenBy(r => r.Variant, StringComparer.Ordinal))
        {
            sb.AppendLine($"[{r.Variant}] {r.PhraseId}-{r.Speaker}{(r.Draft ? " (draft)" : "")}  WER {Pct(r.Wer.Rate)} (S{r.Wer.Substitutions} D{r.Wer.Deletions} I{r.Wer.Insertions} of {r.Wer.ReferenceLength})  CER {Pct(r.Cer.Rate)}{(r.Error is { } e ? "  ERROR " + e : r.TimedOut ? "  NOTHING HEARD" : "")}");
            sb.AppendLine($"    said: {r.Reference}");
            sb.AppendLine($"    heard: {r.Hypothesis}");
        }
        if (run.Skipped.Count > 0) { sb.AppendLine(); sb.AppendLine("Skipped:"); foreach (var s in run.Skipped) sb.AppendLine("  " + s); }
        return sb.ToString();
    }

    /// <summary>One line per recording and variant. A cell that starts with = + - @ (a spreadsheet formula) gets a leading apostrophe, so what a vendor wrote can never run as one.</summary>
    public static string Csv(ScoringRun run)
    {
        static string Cell(string? s)
        {
            s ??= "";
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new StringBuilder("variant,phrase,speaker,language,kind,draft,wer,wer_s,wer_d,wer_i,wer_ref_words,cer,cer_errors,cer_ref_chars,latency_ms,failed,reference,hypothesis\n");
        foreach (var r in run.Rows)
            sb.Append(string.Join(',', Cell(r.Variant), r.PhraseId, Cell(r.Speaker), r.Language, r.Kind, r.Draft ? "1" : "0",
                r.Wer.Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), r.Wer.Substitutions, r.Wer.Deletions, r.Wer.Insertions, r.Wer.ReferenceLength,
                r.Cer.Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), r.Cer.Errors, r.Cer.ReferenceLength, r.LatencyMs?.ToString() ?? "", r.Failed ? "1" : "0", Cell(r.Reference), Cell(r.Hypothesis))).Append('\n');
        return sb.ToString();
    }
}
