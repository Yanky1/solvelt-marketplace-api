using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Portal;

public class PortalUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public class PortalRequestTooLargeException() : Exception($"Portal request body exceeds {PortalSigning.MaxBodyBytes} bytes.");
public class PortalNotConfiguredException(string what) : InvalidOperationException($"Portal is not configured: {what}.");

public interface ISignedPortalClient
{
    Task<PortalResult<CreateOrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> RequestCancellationAsync(string marketplaceOrderId, CancelRequestBody body, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> RequestReturnAsync(string marketplaceOrderId, ReturnRequestBody body, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> ConfirmDeliveryAsync(string marketplaceOrderId, DeliveryConfirmationBody body, CancellationToken ct = default);
    Task<PortalResult<ShippingSettings>> GetShippingSettingsAsync(Guid sellerId, Guid brandId, CancellationToken ct = default);
    Task<PortalResult<LocalDeliveryEligibility>> CheckLocalDeliveryEligibilityAsync(LocalDeliveryEligibilityRequest request, CancellationToken ct = default);
    Task<PortalResult<ServiceAvailability>> GetServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken ct = default);
    Task<PortalResult<PaymentEventResponse>> SendPaymentEventAsync(PaymentEventRequest request, CancellationToken ct = default);
}

/// <summary>
/// Signs and sends every call to the seller portal exactly as CONTRACT.md §1.1 says: the body is serialised once and
/// those exact bytes are hashed, signed and sent; every attempt (including retries) gets a fresh timestamp and nonce.
/// </summary>
public sealed partial class SignedPortalClient(HttpClient http, IOptions<PortalOptions> options, TimeProvider time) : ISignedPortalClient
{
    private const string ApiBase = "/api/internal/v1";
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")] private static partial Regex SafeId();

    private enum Retry { Standard, PaymentEvent }

    // ---------- typed methods ----------
    public Task<PortalResult<CreateOrderResponse>> CreateOrderAsync(CreateOrderRequest r, CancellationToken ct = default)
        => Call<CreateOrderResponse>(HttpMethod.Post, $"{ApiBase}/orders", r, Retry.Standard, ct);

    public Task<PortalResult<JsonElement>> RequestCancellationAsync(string mid, CancelRequestBody b, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/orders/{Id(mid)}/cancel-requests", b, Retry.Standard, ct);

    public Task<PortalResult<JsonElement>> RequestReturnAsync(string mid, ReturnRequestBody b, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/orders/{Id(mid)}/return-requests", b, Retry.Standard, ct);

    public Task<PortalResult<JsonElement>> ConfirmDeliveryAsync(string mid, DeliveryConfirmationBody b, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/orders/{Id(mid)}/delivery-confirmations", b, Retry.Standard, ct);

    public Task<PortalResult<ShippingSettings>> GetShippingSettingsAsync(Guid sellerId, Guid brandId, CancellationToken ct = default)
        => Call<ShippingSettings>(HttpMethod.Get, $"{ApiBase}/shipping/settings?sellerId={sellerId:D}&brandId={brandId:D}", null, Retry.Standard, ct);

    public Task<PortalResult<LocalDeliveryEligibility>> CheckLocalDeliveryEligibilityAsync(LocalDeliveryEligibilityRequest r, CancellationToken ct = default)
        => Call<LocalDeliveryEligibility>(HttpMethod.Post, $"{ApiBase}/local-delivery/eligibility", r, Retry.Standard, ct);

    public Task<PortalResult<ServiceAvailability>> GetServiceAvailabilityAsync(ServiceAvailabilityQuery q, CancellationToken ct = default)
    {
        if (q.To <= q.From) throw new ArgumentException("'to' must be after 'from'.", nameof(q));
        if (q.To - q.From > TimeSpan.FromDays(62)) throw new ArgumentException("The availability window is at most 62 days.", nameof(q));
        static string Iso(DateTimeOffset d) => Uri.EscapeDataString(d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        var qs = new List<string> { $"from={Iso(q.From)}", $"to={Iso(q.To)}", $"location={Uri.EscapeDataString(q.Location)}" };
        if (q.PostalCode is not null) qs.Add($"postalCode={Uri.EscapeDataString(q.PostalCode)}");
        if (q.ProviderId is { } p) qs.Add($"providerId={p:D}");
        if (q.Lat is { } lat) qs.Add($"lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (q.Lng is { } lng) qs.Add($"lng={lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (q.Minutes is { } m) qs.Add($"minutes={m}");
        if (q.AddOns is { Count: > 0 } a) qs.Add($"addOns={string.Join(",", a.Select(Uri.EscapeDataString))}");
        return Call<ServiceAvailability>(HttpMethod.Get, $"{ApiBase}/services/{q.ServiceId:D}/availability?{string.Join("&", qs)}", null, Retry.Standard, ct);
    }

    public Task<PortalResult<PaymentEventResponse>> SendPaymentEventAsync(PaymentEventRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.EventId) || r.EventId.Length > 100) throw new ArgumentException("eventId is required and at most 100 characters.", nameof(r));
        return Call<PaymentEventResponse>(HttpMethod.Post, $"{ApiBase}/payment-events", r, Retry.PaymentEvent, ct);
    }

    // ---------- core ----------
    private static string Id(string id)
        => SafeId().IsMatch(id) ? id : throw new ArgumentException("Identifier contains characters that are not allowed in a signed path.", nameof(id));

    private async Task<PortalResult<T>> Call<T>(HttpMethod method, string pathAndQuery, object? body, Retry profile, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.InboundSecret)) throw new PortalNotConfiguredException("Portal:InboundSecret");
        if (http.BaseAddress is null) throw new PortalNotConfiguredException("Portal:BaseUrl");

        // Serialise once: these exact bytes are hashed, signed and sent on every attempt.
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, PortalJson.Options);
        if (bytes.Length > PortalSigning.MaxBodyBytes) throw new PortalRequestTooLargeException();

        var maxAttempts = Math.Max(1, o.MaxAttempts);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(o.AttemptTimeoutSeconds, 1, 30));
        for (var attempt = 1; ; attempt++)
        {
            var last = attempt >= maxAttempts;
            var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString();
            var nonce = PortalSigning.NewNonce();   // new per attempt, including retries
            var signature = PortalSigning.Sign(o.InboundSecret, PortalSigning.Canonical(timestamp, nonce, method.Method, pathAndQuery, bytes));

            using var req = new HttpRequestMessage(method, pathAndQuery);
            req.Headers.TryAddWithoutValidation(PortalSigning.TimestampHeader, timestamp);
            req.Headers.TryAddWithoutValidation(PortalSigning.NonceHeader, nonce);
            req.Headers.TryAddWithoutValidation(PortalSigning.SignatureHeader, signature);
            if (method != HttpMethod.Get)
            {
                req.Content = new ByteArrayContent(bytes);
                req.Content.Headers.ContentType = new("application/json");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            HttpResponseMessage res;
            string text;
            try
            {
                res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                text = await ReadCapped(res, cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (last) throw new PortalUnavailableException($"Portal unreachable after {attempt} attempt(s).", ex);
                await Task.Delay(Backoff(o, attempt, null), time, ct);
                continue;
            }

            using (res)
            {
                var status = (int)res.StatusCode;
                var error = status is >= 200 and < 300 ? null : ParseError(text);
                var retryable = ShouldRetry(profile, status, error?.Code);
                if (retryable && !last)
                {
                    await Task.Delay(Backoff(o, attempt, res.Headers.RetryAfter?.Delta), time, ct);
                    continue;
                }
                T? value = default;
                if (error is null && text.Length > 0)
                {
                    try { value = JsonSerializer.Deserialize<T>(text, PortalJson.Options); } catch (JsonException) { }
                }
                return new PortalResult<T>(status, value, error, attempt) { Retryable = retryable };
            }
        }
    }

    /// <summary>CONTRACT.md §1.1. Never retry 401, 413, 422 (or any other 4xx not listed).</summary>
    private static bool ShouldRetry(Retry profile, int status, string? code) => status switch
    {
        503 => true,
        429 => true,
        409 when code is "concurrent_update" or "version_conflict" => true,
        409 when profile == Retry.PaymentEvent && code == "sale_not_recorded" => true,
        404 when profile == Retry.PaymentEvent && code is "order_not_found" or "booking_not_found" => true,
        _ => false,
    };

    private static TimeSpan Backoff(PortalOptions o, int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra && ra >= TimeSpan.Zero) return ra > MaxBackoff ? MaxBackoff : ra;
        if (o.RetryBaseDelayMs <= 0) return TimeSpan.Zero;
        var ms = o.RetryBaseDelayMs * Math.Pow(2, attempt - 1);
        var jittered = ms * (0.5 + Random.Shared.NextDouble() * 0.5);
        var d = TimeSpan.FromMilliseconds(jittered);
        return d > MaxBackoff ? MaxBackoff : d;
    }

    private static async Task<string> ReadCapped(HttpResponseMessage res, CancellationToken ct)
    {
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxResponseBytes) throw new HttpRequestException("Portal response too large.");
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static PortalError ParseError(string text)
    {
        if (text.Length == 0) return new PortalError(null, null);
        try { return JsonSerializer.Deserialize<PortalError>(text, PortalJson.Options) ?? new PortalError(null, null); }
        catch (JsonException) { return new PortalError(null, null); }
    }
}

public static class PortalClientRegistration
{
    public static IServiceCollection AddPortalClient(this IServiceCollection services)
    {
        services.AddHttpClient<ISignedPortalClient, SignedPortalClient>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<PortalOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(o.BaseUrl))
            {
                var uri = new Uri(o.BaseUrl, UriKind.Absolute);
                // The signed path is the absolute path we send, so the base must not carry a path prefix.
                if (uri.AbsolutePath != "/") throw new InvalidOperationException("Portal:BaseUrl must not contain a path.");
                c.BaseAddress = uri;
            }
            c.Timeout = Timeout.InfiniteTimeSpan;   // per-attempt deadlines are enforced by the client
        });
        return services;
    }
}
