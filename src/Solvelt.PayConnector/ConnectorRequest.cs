using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Solvelt.PayConnector;

/// <summary>
/// What SignalWire posted to the connector. The card number and the security code are held in POOLED BYTE ARRAYS, not strings, are never copied elsewhere than into the one request to Sola, and are zeroed when
/// this object is disposed (what .NET allows: Kestrel's own read buffer and the HTTP client's connection buffers are outside our reach, see the hardening notes in VOICE.md). Nothing here is ever logged, put in an
/// exception message or stored: parse errors say only that the request is malformed.
/// </summary>
public sealed class ConnectorRequest : IDisposable
{
    private byte[]? _pan, _cvv;
    private int _panLength, _cvvLength;

    public string TransactionId { get; private set; } = "";
    /// <summary>What SignalWire says to charge (from the <c>chargeAmount</c> of OUR <c>&lt;Pay&gt;</c>). Compared with the server's own figure, never used on its own.</summary>
    public string ChargeAmount { get; private set; } = "";
    public string Currency { get; private set; } = "";
    /// <summary>Our payment attempt id, from the <c>&lt;Parameter name="attempt"&gt;</c> of OUR <c>&lt;Pay&gt;</c>.</summary>
    public string AttemptId { get; private set; } = "";
    public string ExpiryMonth { get; private set; } = "";
    public string ExpiryYear { get; private set; } = "";
    public string? PostalCode { get; private set; }

    public ReadOnlySpan<byte> Pan => _pan is null ? default : _pan.AsSpan(0, _panLength);
    public ReadOnlySpan<byte> Cvv => _cvv is null ? default : _cvv.AsSpan(0, _cvvLength);

    /// <summary>Parses and validates the body. Returns null for anything malformed or invalid (a card number that fails the Luhn check never reaches Sola).</summary>
    public static ConnectorRequest? TryParse(ReadOnlySpan<byte> body)
    {
        var r = new ConnectorRequest();
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { r.Dispose(); return null; }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) { r.Dispose(); return null; }
                switch (name)
                {
                    case "transaction_id": r.TransactionId = Str(ref reader); break;
                    case "chargeAmount": r.ChargeAmount = Str(ref reader); break;
                    case "currency_code": r.Currency = Str(ref reader); break;
                    case "expiry_month": r.ExpiryMonth = Str(ref reader); break;
                    case "expiry_year": r.ExpiryYear = Str(ref reader); break;
                    case "postal_code": r.PostalCode = Str(ref reader); break;
                    case "cardnumber": r.TakeSecret(ref reader, ref r._pan, ref r._panLength, 19); break;
                    case "cvv": r.TakeSecret(ref reader, ref r._cvv, ref r._cvvLength, 4); break;
                    case "parameters":
                        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); break; }
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                            {
                                var key = reader.GetString(); reader.Read();
                                if (key == "attempt" && reader.TokenType == JsonTokenType.String) r.AttemptId = reader.GetString() ?? "";
                                else reader.Skip();
                            }
                        }
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { r.Dispose(); return null; }   // never the exception text: it could echo input
        if (!r.Valid()) { r.Dispose(); return null; }
        return r;
    }

    private static string Str(ref Utf8JsonReader reader) => reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";

    private void TakeSecret(ref Utf8JsonReader reader, ref byte[]? target, ref int length, int max)
    {
        if (reader.TokenType != JsonTokenType.String || reader.HasValueSequence || reader.ValueIsEscaped || reader.ValueSpan.Length is 0 || reader.ValueSpan.Length > max) return;   // anything odd: left empty, which fails validation
        target = ArrayPool<byte>.Shared.Rent(max);
        reader.ValueSpan.CopyTo(target);
        length = reader.ValueSpan.Length;
    }

    private bool Valid()
    {
        if (TransactionId.Length is 0 or > 100 || AttemptId.Length is 0 or > 64 || ChargeAmount.Length is 0 or > 12) return false;
        if (!(Currency.Equals("usd", StringComparison.OrdinalIgnoreCase))) return false;
        if (ExpiryMonth.Length != 2 || ExpiryYear.Length != 2 || !ExpiryMonth.All(char.IsAsciiDigit) || !ExpiryYear.All(char.IsAsciiDigit) || ExpiryMonth is "00" || string.CompareOrdinal(ExpiryMonth, "12") > 0) return false;
        if (PostalCode is { Length: > 12 }) return false;
        var pan = Pan; var cvv = Cvv;
        if (pan.Length is < 12 or > 19 || cvv.Length is < 3 or > 4) return false;
        foreach (var b in pan) if (b is < (byte)'0' or > (byte)'9') return false;
        foreach (var b in cvv) if (b is < (byte)'0' or > (byte)'9') return false;
        return Luhn(pan);
    }

    internal static bool Luhn(ReadOnlySpan<byte> digits)
    {
        var sum = 0; var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - (byte)'0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alt = !alt;
        }
        return sum % 10 == 0;
    }

    public void Dispose()
    {
        if (_pan is not null) { CryptographicOperations.ZeroMemory(_pan); ArrayPool<byte>.Shared.Return(_pan); _pan = null; _panLength = 0; }
        if (_cvv is not null) { CryptographicOperations.ZeroMemory(_cvv); ArrayPool<byte>.Shared.Return(_cvv); _cvv = null; _cvvLength = 0; }
    }
}
