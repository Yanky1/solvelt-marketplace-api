using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>What a speaker said after a phrase: go on, say it again, or finish.</summary>
public enum TestSetIntent { Unclear, Next, Again, Stop }

/// <summary>
/// The phone recording of the recognition test set (decision V-46): the owner's test line reads a phrase, records the answer (SignalWire's Record), and asks "next, again or stop" by voice.
/// Test numbers only, off by default; the audio is copied into the private container by <see cref="TestSetCopyHandler"/> and deleted at SignalWire. Every step is a signed SignalWire request;
/// the phrase number, the take and the speaker label travel in the signed URL, so nothing is kept on the server between steps.
/// </summary>
public static class TestSetFlow
{
    public const string Path = "/api/v1/voice/signalwire/testset";
    public const string MenuLine = "To record the test set, say nine.";
    public const string Goodbye = "Thank you. The recordings are saved. Goodbye.";
    public const string NothingHeard = "I did not hear anything. Let us try that phrase again.";
    public const string AskNext = "Got it. Say next, again, or stop.";
    public const string AskNextAgain = "Please say next, again, or stop.";
    public const int MaxSilentTries = 3, MaxUnclear = 2, MaxTakes = 9;

    private static readonly Lazy<PhraseList> Phrases = new(PhraseList.Embedded);

    /// <summary>An anonymous, stable label for the speaker: a keyed hash of the number, never the number (lower-case letters and digits, as a recording name needs).</summary>
    public static string SpeakerLabel(VoiceOptions v, string? from)
    {
        var e164 = PhoneNumbers.ToE164(from) ?? "unknown";
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(v.CallerHashKey ?? ""));
        return "p" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes("testset:" + e164)), 0, 3).ToLowerInvariant();
    }

    /// <summary>Four letters or digits of the call, so two sessions of one speaker never meet in one recording name.</summary>
    public static string SessionLabel(string callSid)
        => "s" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callSid)), 0, 2).ToLowerInvariant();

    public static string RecordingLabel(string speaker, string callSid, int take) => speaker + SessionLabel(callSid) + (take > 1 ? "r" + take : "");

    public static TestSetIntent Parse(string? said)
    {
        var words = ErrorRates.Words(said);
        if (words.Length is 0 or > 6) return TestSetIntent.Unclear;
        if (words.Any(w => w is "stop" or "done" or "finish" or "finished" or "quit" or "end" or "enough" or "exit")) return TestSetIntent.Stop;
        if (words.Any(w => w is "again" or "repeat" or "retry" or "redo" or "once" or "more")) return TestSetIntent.Again;
        if (words.Any(w => w is "next" or "continue" or "okay" or "ok" or "yes" or "skip" or "go" or "ready")) return TestSetIntent.Next;
        return TestSetIntent.Unclear;
    }

    private static string Url(VoiceOptions v, string step, int i, int t, string b, int tries = 0)
        => v.PublicBaseUrl!.TrimEnd('/') + Path + $"?step={step}&i={i}&t={t}&b={b}" + (tries > 0 ? $"&k={tries}" : "");

    public static XElement Start(VoiceOptions v, string speaker)
        => new("Response", new XElement("Say", $"Test set recording. There are {Phrases.Value.Phrases.Count} phrases. After each beep, say the phrase, then stop and wait."),
            new XElement("Redirect", new XAttribute("method", "POST"), Url(v, "phrase", 0, 1, speaker)));

    private static XElement Phrase(VoiceOptions v, int i, int t, string b, int silent)
    {
        var p = Phrases.Value.Phrases[i];
        var say = p.Language == "en" ? $"Phrase {i + 1}. {p.Text}" : $"Phrase {i + 1} is Yiddish. Read it from your list.";
        return new XElement("Response", new XElement("Say", say),
            new XElement("Record", new XAttribute("action", Url(v, "done", i, t, b, silent)), new XAttribute("method", "POST"), new XAttribute("playBeep", "true"), new XAttribute("trim", "trim-silence"),
                new XAttribute("timeout", 3), new XAttribute("maxLength", v.TestSet.MaxRecordSeconds), new XAttribute("transcribe", "false"),
                new XAttribute("recordingStatusCallback", Url(v, "recorded", i, t, b)), new XAttribute("recordingStatusCallbackMethod", "POST"), new XAttribute("recordingStatusCallbackEvent", "completed")));
    }

    private static XElement AskNextGather(VoiceOptions v, int i, int t, string b, int unclear, string say)
        => new("Response", new XElement("Gather", new XAttribute("input", "speech"), new XAttribute("language", v.SignalWireSpeech.Language), new XAttribute("speechTimeout", "auto"), new XAttribute("timeout", 8),
            new XAttribute("hints", "next, again, stop"), new XAttribute("actionOnEmptyResult", "true"), new XAttribute("method", "POST"), new XAttribute("action", Url(v, "next", i, t, b, unclear)), new XElement("Say", say)));

    internal static XElement Done(VoiceOptions v, int i, int t, string b, int silent, string? duration)
    {
        var heard = int.TryParse(duration, out var seconds) && seconds >= 1;
        if (!heard) return silent + 1 >= MaxSilentTries ? new XElement("Response", new XElement("Say", Goodbye), new XElement("Hangup")) : new XElement("Response", new XElement("Say", NothingHeard), new XElement("Redirect", new XAttribute("method", "POST"), Url(v, "phrase", i, t, b, silent + 1)));
        return AskNextGather(v, i, t, b, 0, AskNext);
    }

    internal static XElement Next(VoiceOptions v, int i, int t, string b, int unclear, string? said)
    {
        switch (Parse(said))
        {
            case TestSetIntent.Stop: return new XElement("Response", new XElement("Say", Goodbye), new XElement("Hangup"));
            case TestSetIntent.Again:
                return t >= MaxTakes ? AskNextGather(v, i, t, b, unclear, "That is the most takes of one phrase. Say next, or stop.")
                    : new XElement("Response", new XElement("Redirect", new XAttribute("method", "POST"), Url(v, "phrase", i, t + 1, b)));
            case TestSetIntent.Next:
                return i + 1 >= Phrases.Value.Phrases.Count ? new XElement("Response", new XElement("Say", "That was the last phrase. " + Goodbye), new XElement("Hangup"))
                    : new XElement("Response", new XElement("Redirect", new XAttribute("method", "POST"), Url(v, "phrase", i + 1, 1, b)));
            default:
                return unclear + 1 >= MaxUnclear ? new XElement("Response", new XElement("Say", Goodbye), new XElement("Hangup")) : AskNextGather(v, i, t, b, unclear + 1, AskNextAgain);
        }
    }

    internal static XElement PhraseStep(VoiceOptions v, int i, int t, string b, int silent) => Phrase(v, i, t, b, silent);
    internal static int Count => Phrases.Value.Phrases.Count;
    internal static Phrase PhraseAt(int i) => Phrases.Value.Phrases[i];

    private static bool TryInt(string? s, int max, out int n) => int.TryParse(s, out n) && n >= 0 && n <= max;

    public static IEndpointRouteBuilder MapTestSet(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, Step);
        return app;
    }

    private static async Task<IResult> Step(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled || !v.TestSet.Enabled) return Results.NotFound();
        var (ok, form) = await VoiceEndpoints.VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        var call = form.TryGetValue("CallSid", out var sid) && sid.Length is > 0 and <= 64 ? await store.FindBySidAsync(sid, ct) : null;
        if (call is null) return Results.NotFound();
        form.TryGetValue("From", out var from); form.TryGetValue("To", out var to);
        var step = ctx.Request.Query["step"].ToString();
        // the speaking steps are for the owner's test numbers only; the recording callback carries no number, and is trusted for its signed address
        if (step != "recorded" && !PipelineMenu.Offered(v, from, to)) return Results.NotFound();
        var q = ctx.Request.Query;
        var b = q["b"].ToString();
        if (!TryInt(q["i"], Count - 1, out var i) || !TryInt(q["t"], MaxTakes, out var t) || t < 1 || !System.Text.RegularExpressions.Regex.IsMatch(b, "^p[0-9a-f]{6}$")) return Results.BadRequest();
        TryInt(q["k"], 9, out var k);
        switch (step)
        {
            case "phrase": return call.EndedAt is not null ? Results.NotFound() : VoiceEndpoints.Cxml(PhraseStep(v, i, t, b, k));
            case "done": form.TryGetValue("RecordingDuration", out var duration); return VoiceEndpoints.Cxml(Done(v, i, t, b, k, duration));
            case "next": form.TryGetValue("SpeechResult", out var said); return VoiceEndpoints.Cxml(Next(v, i, t, b, k, said));
            case "recorded":
            {
                // the recording is accessible: queue the copy (a retried delivery is the same job, by its dedupe key); the work is the outbox's, with retries
                form.TryGetValue("RecordingSid", out var rsid); form.TryGetValue("RecordingStatus", out var status);
                if (!SignalWireRecordings.IsValidSid(rsid)) return Results.BadRequest();
                if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) return Results.Ok();
                var name = new RecordingName(v.TestSet.SetName, PhraseAt(i).Id, RecordingLabel(b, sid!, t)).ToString();
                await new OutboxWriter(db, time).TryEnqueueAsync(TestSetCopyHandler.Type, new TestSetCopyHandler.Payload(rsid!, name), "voice.testset.copy:" + rsid, ct: ct);
                return Results.Ok();
            }
            default: return Results.BadRequest();
        }
    }
}
