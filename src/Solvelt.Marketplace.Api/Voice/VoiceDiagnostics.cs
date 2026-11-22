using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Settings of the recognition diagnostics (<c>Voice:Diagnostics</c>, decision V-39): off by default, test numbers only, short retention.</summary>
public sealed class DiagnosticsOptions
{
    public bool Enabled { get; set; }
    public const int DefaultRetentionHours = 72, MaxRetentionHours = 168;
    /// <summary>How long rows are kept: 1 to 168 hours (the code clamps; a value outside the range is a start-up problem when the menu is on).</summary>
    public int RetentionHours { get; set; } = DefaultRetentionHours;
}

/// <summary>What one recognised caller turn looks like to the recorder. The text is masked by the recorder; nothing here is audio.</summary>
public sealed record DiagnosticTurn(string Option, string Recogniser, string Text, IReadOnlyList<SttCandidate>? Candidates, string? JudgePick, string? Language, double? Confidence,
    long? SpeechEndToTextMs, long? SpeechEndToFirstAudioMs, string EndpointingReason);

/// <summary>Best-effort masking of what must not be kept even in the owner's own test calls: runs of four or more digits (a spoken number, a code or a card typed as words that the recogniser wrote as digits) and email addresses.</summary>
public static partial class DiagnosticText
{
    [GeneratedRegex(@"\d(?:[\s.\-]?\d){3,}")] private static partial Regex Digits();
    [GeneratedRegex(@"\S+@\S+\.\S+")] private static partial Regex Email();

    public static string Mask(string? text)
    {
        var t = (text ?? "").Replace('\0', ' ');
        t = Email().Replace(t, "[email]");
        t = Digits().Replace(t, "[digits]");
        t = string.Concat(t.Where(c => !char.IsControl(c) || c is '\n' or '\t'));
        return t.Length > 2000 ? t[..2000] : t;
    }
}

public interface IVoiceDiagnostics
{
    /// <summary>True only when diagnostics are on, the caller is one of the test numbers, and the call is not in a keypad, payment or verification step. Cheap; call before building a <see cref="DiagnosticTurn"/>.</summary>
    bool Active(VoiceCallState state);
    /// <summary>Stores one turn; returns its id (or null: not active or the write failed, which never affects the call).</summary>
    Task<Guid?> RecordAsync(VoiceCall call, VoiceCallState state, DiagnosticTurn turn, CancellationToken ct);
    /// <summary>Adds the time to the reply's first audio once it is known.</summary>
    Task SetFirstAudioAsync(Guid id, long ms, CancellationToken ct);
}

public sealed class VoiceDiagnostics(IServiceScopeFactory scopes, IOptions<VoiceOptions> options, TimeProvider time, ILogger<VoiceDiagnostics> log) : IVoiceDiagnostics
{
    private int _warned;

    public bool Active(VoiceCallState state)
    {
        var v = options.Value;
        if (!v.Diagnostics.Enabled) return false;
        var numbers = v.PipelineMenu.TestNumbers.Select(PhoneNumbers.ToE164).Where(n => n is not null).ToList();
        if (numbers.Count == 0)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) log.LogWarning("Voice diagnostics are on but Voice:PipelineMenu:TestNumbers has no usable number: nothing is stored (decision V-39).");   // tolerant: off, one warning
            return false;
        }
        if (state.SuppressDiagnostics || state.KeypadStep is not null) return false;        // a keypad, payment or verification step: never
        return state.CallerNumber is { } caller && numbers.Contains(caller);                // the caller's own number is a test number; anyone else stores nothing
    }

    public async Task<Guid?> RecordAsync(VoiceCall call, VoiceCallState state, DiagnosticTurn t, CancellationToken ct)
    {
        if (!Active(state)) return null;
        try
        {
            var engines = EnginesJson(t.Candidates, t.JudgePick);
            var row = new VoiceDiagnosticTurn
            {
                Id = Guid.NewGuid(), CallId = call.Id, At = time.GetUtcNow(), Turn = state.NextDiagnosticTurn(), Option = t.Option, Recogniser = Clip(t.Recogniser, 60), Text = DiagnosticText.Mask(t.Text),
                Engines = engines, Language = t.Language is { Length: > 0 } l ? Clip(l, 16) : null, Confidence = t.Confidence,
                SpeechEndToTextMs = t.SpeechEndToTextMs, SpeechEndToFirstAudioMs = t.SpeechEndToFirstAudioMs, EndpointingReason = Clip(t.EndpointingReason, 40),
            };
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            db.VoiceDiagnosticTurns.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("A diagnostics row could not be stored ({Type}).", ex.GetType().Name);   // never the text; the call is not affected
            return null;
        }
    }

    public async Task SetFirstAudioAsync(Guid id, long ms, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.Where(t => t.Id == id && t.SpeechEndToFirstAudioMs == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SpeechEndToFirstAudioMs, ms), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("A diagnostics timing could not be stored ({Type}).", ex.GetType().Name); }
    }

    internal const int EnginesColumn = 6000, MaxCandidates = 4;
    private static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // Hebrew letters stay one character (the default escapes each as six)

    /// <summary>
    /// The option 5 cell: each engine's masked text and the judge's pick as ONE valid JSON value that always fits the column. The texts are capped BEFORE serialising (never the serialised JSON, which would cut a string
    /// in the middle): the cap starts at 1000 characters and is halved until the whole value fits, and at most four candidates are kept. Null when nothing fits or there are no candidates.
    /// </summary>
    internal static string? EnginesJson(IReadOnlyList<SttCandidate>? candidates, string? judge)
    {
        if (candidates is not { Count: > 0 }) return null;
        for (var cap = 1000; cap >= 40; cap /= 2)
        {
            var json = JsonSerializer.Serialize(new
            {
                engines = candidates.Take(MaxCandidates).Select(c => new { engine = Clip(c.Engine, 40), text = Clip(DiagnosticText.Mask(c.Text), cap) }),
                judge = judge is null ? null : Clip(DiagnosticText.Mask(judge), cap),
            }, Raw);
            if (json.Length <= EnginesColumn) return json;
        }
        return null;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Deletes rows older than the retention (clamped to 1 to 168 hours). Called by the maintenance worker.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var hours = Math.Clamp(options.Value.Diagnostics.RetentionHours, 1, DiagnosticsOptions.MaxRetentionHours);
        var cutoff = time.GetUtcNow().AddHours(-hours);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.Where(t => t.At < cutoff).ExecuteDeleteAsync(ct);
    }
}
