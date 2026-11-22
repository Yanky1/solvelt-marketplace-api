using System.Text.Json;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The audio format the stream says it carries (decision V-38). Every recogniser and the Realtime line are set up for G.711 mu-law at 8 kHz, which is what SignalWire streams for a phone-number call
/// (<c>start.mediaFormat.encoding</c> is <c>audio/x-mulaw</c>, <c>sampleRate</c> 8000). We never ask for another codec (the stream codec stays PCMU: L16 would add no quality on a phone call and a stream whose
/// codec the account does not allow does not start at all). This guard only NOTICES a different format, so a surprise is one clear warning in the log, not a call that sounds wrong for no visible reason.
/// </summary>
internal static class StreamFormat
{
    /// <summary>True when the start event carries a media format that is not mu-law 8 kHz. A start event without a media format is fine (nothing is claimed).</summary>
    internal static bool IsUnexpected(JsonElement start, out string encoding, out int sampleRate)
    {
        encoding = ""; sampleRate = 0;
        if (start.ValueKind != JsonValueKind.Object || !start.TryGetProperty("mediaFormat", out var f) || f.ValueKind != JsonValueKind.Object) return false;
        var enc = f.TryGetProperty("encoding", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var rate = f.TryGetProperty("sampleRate", out var r) && r.TryGetInt32(out var n) ? n : 0;
        if (enc is null && rate == 0) return false;
        if (string.Equals(enc, "audio/x-mulaw", StringComparison.OrdinalIgnoreCase) && rate is 0 or 8000) return false;
        // only a short, plain label is logged (the value comes from the network)
        encoding = enc is { Length: > 0 and <= 40 } && enc.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '_') ? enc : "unrecognised";
        sampleRate = rate;
        return true;
    }

    /// <summary>Logs the surprise once per stream; the call carries on.</summary>
    internal static void Note(JsonElement start, ILogger log, Guid callId, ref bool noted)
    {
        if (noted || !IsUnexpected(start, out var encoding, out var rate)) return;
        noted = true;
        log.LogWarning("Voice call {Call}: the stream reports the audio format {Encoding} at {Rate} Hz, not mu-law 8000 Hz; recognition and speech will be wrong (decision V-38).", callId, encoding, rate);
    }
}
