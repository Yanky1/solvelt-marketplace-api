using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// <c>X-SignalWire-Signature</c>: cXML is Twilio compatible, so the signature is base64(HMAC-SHA1(signing key, full URL + each form parameter name and value, sorted by name)).
/// A JSON body is signed over the URL, which then carries <c>bodySHA256</c> (hex SHA-256 of the raw body). NOT verified against SignalWire's current reference from the build sandbox (V-01).
/// </summary>
public static class SignalWireSignature
{
    public const string Header = "X-SignalWire-Signature";

    public static string Compute(string signingKey, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var sb = new StringBuilder(url);
        foreach (var kv in form.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(kv.Value);
        using var h = new HMACSHA1(Encoding.UTF8.GetBytes(signingKey));
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static bool Verify(string signingKey, string url, IEnumerable<KeyValuePair<string, string>> form, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || string.IsNullOrEmpty(signature)) return false;
        var expected = Encoding.ASCII.GetBytes(Compute(signingKey, url, form));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature.Trim()));
    }
}

/// <summary>Signed, short-lived, single-use token in the stream URL: <c>{callId:N}.{expiryUnix}.{nonce}.{hex hmac}</c>. Authenticates the WebSocket upgrade; it names one call and says nothing else.</summary>
public sealed class StreamTokens(Microsoft.Extensions.Options.IOptions<VoiceOptions> options, TimeProvider time)
{
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.StreamTokenKey ?? throw new InvalidOperationException("Voice:StreamTokenKey is not set."));

    private string Mac(string payload)
    {
        using var h = new HMACSHA256(Key);
        return Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes("voice-stream-v1:" + payload)));
    }

    public string Issue(Guid callId)
    {
        var payload = callId.ToString("N") + "." + (time.GetUtcNow().ToUnixTimeSeconds() + options.Value.StreamTokenSeconds).ToString(CultureInfo.InvariantCulture) + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        return payload + "." + Mac(payload);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _used = new();

    /// <summary>Validates AND spends the token: it opens one stream, once (a copied URL or a replayed upgrade is refused). Spent marks are kept until the token would have expired anyway.</summary>
    public Guid? Use(string? token)
    {
        var id = Validate(token);
        if (id is null) return null;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var kv in _used) if (kv.Value < now) _used.TryRemove(kv.Key, out _);
        return _used.TryAdd(token!.Split('.')[3], long.Parse(token.Split('.')[1], CultureInfo.InvariantCulture)) ? id : null;
    }

    public Guid? Validate(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 4 } || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp) || !Guid.TryParseExact(parts[0], "N", out var id)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(parts[0] + "." + parts[1] + "." + parts[2])), Encoding.ASCII.GetBytes(parts[3]))) return null;
        return time.GetUtcNow().ToUnixTimeSeconds() > exp ? null : id;
    }
}

public static class VoiceModelSelector
{
    /// <summary>The model for one call: <c>mini</c>/<c>full</c> as configured, or in <c>split</c> mode a stable bucket of the call id (so a call keeps its model, and the share is exact in the long run and testable).</summary>
    public static (string Model, string Tier) Pick(VoiceOptions o, Guid callId)
    {
        var mini = o.ModelMode switch
        {
            "mini" => true,
            "split" => (BitConverter.ToUInt32(SHA256.HashData(callId.ToByteArray()), 0) % 100) < (uint)Math.Clamp(o.MiniPercent, 0, 100),
            _ => false,
        };
        return mini ? (o.OpenAiModelMini, "mini") : (o.OpenAiModel, "full");
    }

    public static VoicePrices Prices(VoiceOptions o, string tier) => tier == "mini" ? o.PricePerMillionMini : o.PricePerMillionFull;
}

public sealed record VoiceUsage(long AudioIn, long AudioOut, long TextIn, long TextOut)
{
    public static readonly VoiceUsage Zero = new(0, 0, 0, 0);
    public VoiceUsage Add(VoiceUsage o) => new(AudioIn + o.AudioIn, AudioOut + o.AudioOut, TextIn + o.TextIn, TextOut + o.TextOut);

    /// <summary>Estimated cost in micro-dollars (a price per million tokens is exactly micro-dollars per token). Whole numbers, no floats.</summary>
    public long EstimateMicros(VoicePrices p) => (long)Math.Round(AudioIn * p.AudioIn + AudioOut * p.AudioOut + TextIn * p.TextIn + TextOut * p.TextOut, MidpointRounding.AwayFromZero);
}

public static class PhoneMask
{
    /// <summary>The only form of a phone number that is logged or stored by this module: country-code-free last four digits.</summary>
    public static string Mask(string? number)
    {
        var d = new string((number ?? "").Where(char.IsDigit).ToArray());
        return d.Length < 4 ? "***" : "***" + d[^4..];
    }
}

public static class SupportHours
{
    /// <summary>Is <paramref name="now"/> inside the configured hours? Format <c>[Mon-Fri ]HH:mm-HH:mm</c>; empty means always; unparsable means closed (fail closed).</summary>
    public static bool IsOpen(VoiceOptions o, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(o.SupportHours)) return true;
        try
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(o.SupportTimeZone));
            var parts = o.SupportHours.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var range = parts[^1].Split('-');
            var from = TimeSpan.ParseExact(range[0], "hh\\:mm", CultureInfo.InvariantCulture);
            var to = TimeSpan.ParseExact(range[1], "hh\\:mm", CultureInfo.InvariantCulture);
            if (parts.Length == 2)
            {
                var days = parts[0].Split('-');
                var a = (int)Enum.Parse<DayOfWeek>(Full(days[0]));
                var b = days.Length > 1 ? (int)Enum.Parse<DayOfWeek>(Full(days[1])) : a;
                var d = (int)local.DayOfWeek;
                if (!(a <= b ? d >= a && d <= b : d >= a || d <= b)) return false;
            }
            var t = local.TimeOfDay;
            return t >= from && t < to;
        }
        catch (Exception) { return false; }
    }

    private static string Full(string s) => s.ToLowerInvariant() switch
    {
        "mon" => "Monday", "tue" => "Tuesday", "wed" => "Wednesday", "thu" => "Thursday", "fri" => "Friday", "sat" => "Saturday", "sun" => "Sunday", _ => s,
    };
}
