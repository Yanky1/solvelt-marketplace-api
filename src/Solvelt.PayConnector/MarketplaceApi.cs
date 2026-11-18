using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solvelt.PayConnector;

/// <summary>What the marketplace API says is to be charged for an attempt: from ITS server state (the order total and the order number), never from the call.</summary>
public sealed record Expectation(long AmountCents, string Invoice, string Currency);

/// <summary>The connector's word to the marketplace API about one attempt. The API checks the amount and the invoice against its own order before it believes a <c>paid</c> (decision V-07).</summary>
public sealed record ChargeReport(string AttemptId, string Invoice, string Status, long PaidCents, string? TransactionId, string? Code);

public interface IMarketplaceApi
{
    /// <summary>Null when the attempt is unknown, no longer open, or the API cannot be reached: the connector then charges nothing.</summary>
    Task<Expectation?> ExpectAsync(string attemptId, CancellationToken ct);
    Task<bool> ReportAsync(ChargeReport report, CancellationToken ct);
}

/// <summary>
/// The signed channel between the connector and the marketplace API: <c>X-Connector-Timestamp</c> (unix seconds) and <c>X-Connector-Signature</c> = base64 HMAC-SHA256 of <c>"{timestamp}.{body}"</c> with the shared key,
/// in BOTH directions (the API signs its answers too). No card data ever travels on it: only attempt ids, invoices, amounts and results.
/// </summary>
public static class ConnectorSigning
{
    public const string TimestampHeader = "X-Connector-Timestamp", SignatureHeader = "X-Connector-Signature";

    public static string Sign(string key, string timestamp, string body)
        => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(timestamp + "." + body)));

    public static bool Verify(string key, string? timestamp, string body, string? signature, DateTimeOffset now, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(signature) || !long.TryParse(timestamp, out var ts)) return false;
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(ts)).TotalSeconds) > window.TotalSeconds) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, timestamp!, body)), Encoding.ASCII.GetBytes(signature.Trim()));
    }
}

public sealed class MarketplaceApi(HttpClient http, IOptions<ConnectorOptions> options, TimeProvider time, ILogger<MarketplaceApi> log) : IMarketplaceApi
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const string ExpectPath = "/api/v1/voice/pay-connector/expect", ResultPath = "/api/v1/voice/pay-connector/result";

    public async Task<Expectation?> ExpectAsync(string attemptId, CancellationToken ct)
    {
        var (status, text, ok) = await PostAsync(ExpectPath, JsonSerializer.Serialize(new { attemptId }), ct);
        if (!ok || status != 200) return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            var r = d.RootElement;
            if (!r.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.True) return null;
            return new Expectation(r.GetProperty("amountCents").GetInt64(), r.GetProperty("invoice").GetString()!, r.GetProperty("currency").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public async Task<bool> ReportAsync(ChargeReport report, CancellationToken ct)
    {
        var (status, _, ok) = await PostAsync(ResultPath, JsonSerializer.Serialize(report), ct);
        return ok && status is >= 200 and < 300;
    }

    private async Task<(int Status, string Body, bool Authentic)> PostAsync(string path, string body, CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
            using var req = new HttpRequestMessage(HttpMethod.Post, o.ApiBaseUrl!.TrimEnd('/') + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add(ConnectorSigning.TimestampHeader, ts);
            req.Headers.Add(ConnectorSigning.SignatureHeader, ConnectorSigning.Sign(o.SharedKey!, ts, body));
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            // the API signs its answers too: an answer that is not signed with the shared key is not believed
            var authentic = ConnectorSigning.Verify(o.SharedKey!, res.Headers.TryGetValues(ConnectorSigning.TimestampHeader, out var t) ? t.FirstOrDefault() : null, text,
                res.Headers.TryGetValues(ConnectorSigning.SignatureHeader, out var s) ? s.FirstOrDefault() : null, time.GetUtcNow(), Window);
            return ((int)res.StatusCode, text, authentic);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("The marketplace API could not be reached ({Path}).", path);
            return (0, "", false);
        }
    }
}
