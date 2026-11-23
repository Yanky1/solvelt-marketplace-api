using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Payments;

public enum SolaRefundOutcome
{
    /// <summary>Sola approved (<c>xResult</c> A): <see cref="SolaRefundResult.RefNum"/> is the refund's own <c>xRefNum</c>.</summary>
    Approved,
    /// <summary>Sola answered and said no (declined or error, with its code): nothing moved.</summary>
    Declined,
    /// <summary>We cannot tell whether the money moved (timeout, a broken connection, a 5xx, an answer we cannot read). NEVER retried blindly.</summary>
    Ambiguous,
}

/// <summary>What the gateway answered. <see cref="Code"/> is Sola's <c>xErrorCode</c> (or a short word of ours); never the key, never a card number, never Sola's free text.</summary>
public sealed record SolaRefundResult(SolaRefundOutcome Outcome, long? RefNum, string? Code);

/// <summary>
/// The only seam to Sola's transaction API for refunds (docs.solapayments.com/api/transaction/credit-card.md, read 2026-11-23): <c>cc:voidrefund</c> for a FULL refund (voids while unsettled, else refunds in full),
/// <c>cc:refund</c> with <c>xAmount</c> for a partial one. Sola has no idempotency key: the safety is ours (one call per refund, an attempt marker written first, no retry after an ambiguous result).
/// </summary>
public interface ISolaRefundGateway
{
    Task<SolaRefundResult> RefundAsync(long originalRefNum, long amountCents, bool full, string description, CancellationToken ct);
}

/// <summary>Sola has not been given an API key: every refund is "unavailable" and the outbox tries again later (nothing was sent).</summary>
public sealed class SolaUnavailableException(string message) : Exception(message);

public sealed class SolaRefundHttpGateway(HttpClient http, IOptions<SolaOptions> options) : ISolaRefundGateway
{
    public async Task<SolaRefundResult> RefundAsync(long originalRefNum, long amountCents, bool full, string description, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey)) throw new SolaUnavailableException("sola_api_key_missing");
        var body = new Dictionary<string, string>
        {
            ["xKey"] = o.ApiKey!, ["xVersion"] = "5.0.0", ["xSoftwareName"] = o.SoftwareName, ["xSoftwareVersion"] = o.SoftwareVersion,
            ["xCommand"] = full ? "cc:voidrefund" : "cc:refund", ["xRefNum"] = originalRefNum.ToString(CultureInfo.InvariantCulture),
            ["xDescription"] = description.Length > 100 ? description[..100] : description, ["xTimeoutSeconds"] = Math.Max(1, o.TimeoutSeconds - 2).ToString(CultureInfo.InvariantCulture),
        };
        if (!full) body["xAmount"] = (amountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var json = JsonSerializer.Serialize(body);
        foreach (var url in new[] { o.GatewayUrl }.Concat(o.GatewayBackupUrls))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                using var res = await http.SendAsync(req, cts.Token);
                if (!res.IsSuccessStatusCode) return new(SolaRefundOutcome.Ambiguous, null, "http_" + (int)res.StatusCode);
                return Parse(await res.Content.ReadAsStringAsync(cts.Token));
            }
            // Only a request that never left (nobody listens, no such host) may go to the backup host: anything else could have reached Sola.
            catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NoData }) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(SolaRefundOutcome.Ambiguous, null, "timeout"); }
            catch (HttpRequestException) { return new(SolaRefundOutcome.Ambiguous, null, "connection"); }
        }
        throw new SolaUnavailableException("sola_unreachable");   // no host could even be connected to: nothing was sent
    }

    /// <summary>xResult A = approved, D = declined, E = error (Sola's documented results). An approval whose <c>xRefNum</c> cannot be read is AMBIGUOUS (money moved, the reference is lost): a person looks.</summary>
    public static SolaRefundResult Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new(SolaRefundOutcome.Ambiguous, null, "unreadable");
            var result = Str(r, "xResult");
            var code = Clean(Str(r, "xErrorCode")) ?? Clean(Str(r, "xStatus"));
            if (result == "A")
                return long.TryParse(Str(r, "xRefNum"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? new(SolaRefundOutcome.Approved, n, null) : new(SolaRefundOutcome.Ambiguous, null, "approved_without_ref");
            return result is "D" or "E" ? new(SolaRefundOutcome.Declined, null, code ?? (result == "D" ? "declined" : "error")) : new(SolaRefundOutcome.Ambiguous, null, "unreadable");
        }
        catch (JsonException) { return new(SolaRefundOutcome.Ambiguous, null, "unreadable"); }
    }

    private static string? Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null : null;
    private static string? Clean(string? s) { if (string.IsNullOrWhiteSpace(s)) return null; var c = new string(s.Where(char.IsAsciiLetterOrDigit).Take(20).ToArray()); return c.Length == 0 ? null : c; }
}

/// <summary>Development and tests only: approves, declines, times out or reports a duplicate as it is told; never touches a network.</summary>
public sealed class FakeSolaRefundGateway : ISolaRefundGateway
{
    public sealed record Call(long OriginalRefNum, long AmountCents, bool Full);
    private readonly object _lock = new();
    private readonly List<Call> _calls = [];
    private long _next = 900_000_000_000;
    /// <summary>What the next calls answer (consumed one per call); empty: approve.</summary>
    public Queue<SolaRefundResult> Script { get; } = new();
    public IReadOnlyList<Call> Calls { get { lock (_lock) return _calls.ToList(); } }

    public Task<SolaRefundResult> RefundAsync(long originalRefNum, long amountCents, bool full, string description, CancellationToken ct)
    {
        lock (_lock)
        {
            _calls.Add(new(originalRefNum, amountCents, full));
            return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : new SolaRefundResult(SolaRefundOutcome.Approved, ++_next, null));
        }
    }
}

/// <summary>No key and not a development environment: refunds of Sola orders wait (the outbox retries); nothing is sent.</summary>
public sealed class UnavailableSolaRefundGateway : ISolaRefundGateway
{
    public Task<SolaRefundResult> RefundAsync(long originalRefNum, long amountCents, bool full, string description, CancellationToken ct) => throw new SolaUnavailableException("sola_api_key_missing");
}
