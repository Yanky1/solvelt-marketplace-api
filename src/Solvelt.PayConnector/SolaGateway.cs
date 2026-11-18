using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solvelt.PayConnector;

public enum SolaOutcome { Approved, Declined, Error, Unknown }

/// <summary>What the connector keeps of Sola's answer: result, reference, the amount actually authorised, an error code. Nothing else (no card data, no free text from Sola).</summary>
public sealed record SolaResult(SolaOutcome Outcome, string? RefNum, long? AuthorisedCents, string? ErrorCode);

public sealed record SolaSale(string Invoice, long AmountCents, string? PostalCode);

/// <summary>What Sola's <c>cc:save</c> returned: a token plus the display facts. The card number itself never comes back (Sola masks it).</summary>
public sealed record SolaSaveResult(SolaOutcome Outcome, string? Token, string? Brand, string? Last4, string? Exp, string? ErrorCode);

public interface ISolaGateway
{
    /// <summary>One <c>cc:sale</c>. NEVER retried by the connector: a timeout or a dropped connection is <see cref="SolaOutcome.Unknown"/> (the card may have been charged) and is for a person to reconcile.</summary>
    Task<SolaResult> SaleAsync(SolaSale sale, ConnectorRequest card, CancellationToken ct);
    /// <summary><c>cc:save</c>: asks Sola for a reusable token for the card (nothing is charged). Same rules as a sale: one call, never retried by the connector.</summary>
    Task<SolaSaveResult> SaveAsync(ConnectorRequest card, CancellationToken ct);
    /// <summary>A <c>cc:sale</c> with a saved token (<c>xToken</c>) instead of a card: no card data is involved. Never retried: an unknown outcome is for a person.</summary>
    Task<SolaResult> TokenSaleAsync(SolaSale sale, string token, CancellationToken ct);
}

/// <summary>A fixed pooled buffer for the one request body: no growth means no stray copy of the card number in a discarded array; zeroed and returned after the call.</summary>
internal sealed class SecretBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(4096);
    private int _written;
    public ArraySegment<byte> Written => new(_buffer!, 0, _written);
    public void Advance(int count) => _written += count;
    public Memory<byte> GetMemory(int sizeHint = 0) => Room(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => Room(sizeHint).Span;
    private Memory<byte> Room(int hint)
    {
        if (_buffer is null || _buffer.Length - _written < Math.Max(hint, 1)) throw new InvalidOperationException("The request is larger than the fixed buffer.");
        return _buffer.AsMemory(_written);
    }
    public void Dispose() { if (_buffer is not null) { CryptographicOperations.ZeroMemory(_buffer); ArrayPool<byte>.Shared.Return(_buffer); _buffer = null; _written = 0; } }
}

/// <summary>Sola's gateway (https://docs.solapayments.com/api/transaction/credit-card.md, "Sale"): POST JSON to <c>/gatewayjson</c>, <c>xCommand=cc:sale</c>. <c>xAllowDuplicate=false</c> keeps Sola's own 10-minute duplicate guard on.</summary>
public sealed class SolaGateway(HttpClient http, IOptions<ConnectorOptions> options) : ISolaGateway
{
    public async Task<SolaResult> SaleAsync(SolaSale sale, ConnectorRequest card, CancellationToken ct)
    {
        var o = options.Value;
        using var body = new SecretBuffer();
        try
        {
            using (var w = new Utf8JsonWriter(body))
            {
                w.WriteStartObject();
                w.WriteString("xKey", o.SolaKey);
                w.WriteString("xVersion", "5.0.0");
                w.WriteString("xSoftwareName", o.SolaSoftwareName);
                w.WriteString("xSoftwareVersion", o.SolaSoftwareVersion);
                w.WriteString("xCommand", "cc:sale");
                w.WriteString("xAmount", Dollars(sale.AmountCents));
                w.WriteString("xCardNum"u8, card.Pan);
                w.WriteString("xExp", card.ExpiryMonth + card.ExpiryYear);
                w.WriteString("xCVV"u8, card.Cvv);
                if (!string.IsNullOrWhiteSpace(sale.PostalCode)) w.WriteString("xZip", sale.PostalCode);
                w.WriteString("xInvoice", sale.Invoice);
                w.WriteString("xDescription", "Solvelt phone order");
                w.WriteString("xAllowDuplicate", "false");
                w.WriteString("xTimeoutSeconds", o.SolaTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            using var content = new ByteArrayContent(body.Written.Array!, body.Written.Offset, body.Written.Count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, o.SolaUrl) { Content = content };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.SolaTimeoutSeconds + 5));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!res.IsSuccessStatusCode) return new SolaResult(SolaOutcome.Unknown, null, null, "http_" + (int)res.StatusCode);
            return Parse(await res.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return new SolaResult(SolaOutcome.Unknown, null, null, "no_answer");   // the card may have been charged: never retried, never described with the exception text
        }
    }

    public async Task<SolaSaveResult> SaveAsync(ConnectorRequest card, CancellationToken ct)
    {
        var o = options.Value;
        using var body = new SecretBuffer();
        try
        {
            using (var w = new Utf8JsonWriter(body))
            {
                w.WriteStartObject();
                w.WriteString("xKey", o.SolaKey);
                w.WriteString("xVersion", "5.0.0");
                w.WriteString("xSoftwareName", o.SolaSoftwareName);
                w.WriteString("xSoftwareVersion", o.SolaSoftwareVersion);
                w.WriteString("xCommand", "cc:save");
                w.WriteString("xCardNum"u8, card.Pan);
                w.WriteString("xExp", card.ExpiryMonth + card.ExpiryYear);
                w.WriteString("xCVV"u8, card.Cvv);
                if (!string.IsNullOrWhiteSpace(card.PostalCode)) w.WriteString("xZip", card.PostalCode);
                w.WriteEndObject();
            }
            using var content = new ByteArrayContent(body.Written.Array!, body.Written.Offset, body.Written.Count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, o.SolaUrl) { Content = content };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.SolaTimeoutSeconds + 5));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!res.IsSuccessStatusCode) return new SolaSaveResult(SolaOutcome.Unknown, null, null, null, null, "http_" + (int)res.StatusCode);
            return ParseSave(await res.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return new SolaSaveResult(SolaOutcome.Unknown, null, null, null, null, "no_answer");
        }
    }

    public async Task<SolaResult> TokenSaleAsync(SolaSale sale, string token, CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["xKey"] = o.SolaKey!, ["xVersion"] = "5.0.0", ["xSoftwareName"] = o.SolaSoftwareName, ["xSoftwareVersion"] = o.SolaSoftwareVersion, ["xCommand"] = "cc:sale",
                ["xAmount"] = Dollars(sale.AmountCents), ["xToken"] = token, ["xInvoice"] = sale.Invoice, ["xDescription"] = "Solvelt phone order (saved card)", ["xAllowDuplicate"] = "false",
                ["xTimeoutSeconds"] = o.SolaTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, o.SolaUrl) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.SolaTimeoutSeconds + 5));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!res.IsSuccessStatusCode) return new SolaResult(SolaOutcome.Unknown, null, null, "http_" + (int)res.StatusCode);
            return Parse(await res.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return new SolaResult(SolaOutcome.Unknown, null, null, "no_answer");
        }
    }

    internal static SolaSaveResult ParseSave(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var code = S("xErrorCode");
            switch (S("xResult"))
            {
                case "A":
                    var token = S("xToken"); var masked = S("xMaskedCardNumber"); var exp = S("xExp");
                    var last4 = masked is { Length: >= 4 } m && m[^4..].All(char.IsDigit) ? m[^4..] : null;
                    var brand = S("xCardType") is { Length: > 0 and <= 20 } b && b.All(c => char.IsLetterOrDigit(c) || c == ' ') ? b : "Card";
                    // a token we cannot attribute to a card (no last four, no usable expiry) is not a saved card we can offer by voice
                    if (string.IsNullOrWhiteSpace(token) || token.Length > 100 || last4 is null || exp is not { Length: 4 } || !exp.All(char.IsDigit)) return new SolaSaveResult(SolaOutcome.Unknown, null, null, null, null, "unattributable_token");
                    return new SolaSaveResult(SolaOutcome.Approved, token.Trim(), brand, last4, exp, code);
                case "D": return new SolaSaveResult(SolaOutcome.Declined, null, null, null, null, code);
                case "E": return new SolaSaveResult(SolaOutcome.Error, null, null, null, null, code);
                default: return new SolaSaveResult(SolaOutcome.Unknown, null, null, null, null, "unknown_result");
            }
        }
        catch (JsonException) { return new SolaSaveResult(SolaOutcome.Unknown, null, null, null, null, "unreadable_answer"); }
    }

    internal static SolaResult Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var code = S("xErrorCode");
            switch (S("xResult"))
            {
                case "A":
                    var cents = ToCents(S("xAuthAmount"));
                    var rf = S("xRefNum");
                    // an approval we cannot attribute to an amount and a reference is not a payment we can report: for a person
                    return cents is null || string.IsNullOrWhiteSpace(rf) ? new SolaResult(SolaOutcome.Unknown, rf, cents, "unattributable_approval") : new SolaResult(SolaOutcome.Approved, rf.Trim(), cents, code);
                case "D": return new SolaResult(SolaOutcome.Declined, S("xRefNum"), null, code);
                case "E": return new SolaResult(SolaOutcome.Error, S("xRefNum"), null, code);
                default: return new SolaResult(SolaOutcome.Unknown, null, null, "unknown_result");
            }
        }
        catch (JsonException) { return new SolaResult(SolaOutcome.Unknown, null, null, "unreadable_answer"); }
    }

    public static string Dollars(long cents) => $"{cents / 100}.{cents % 100:D2}";

    public static long? ToCents(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) || d < 0) return null;
        var c = d * 100m;
        return c == decimal.Truncate(c) && c <= long.MaxValue ? (long)c : null;
    }
}
