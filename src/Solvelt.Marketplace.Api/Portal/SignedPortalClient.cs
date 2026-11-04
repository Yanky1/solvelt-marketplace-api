using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Portal;

public class PortalUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
/// <summary>The portal answered with something we refuse to read (oversized). Not retried: the write may already have been applied.</summary>
public class PortalResponseTooLargeException() : Exception("Portal response exceeds the allowed size.");
public class PortalRequestTooLargeException() : Exception($"Portal request body exceeds {PortalSigning.MaxBodyBytes} bytes.");
public class PortalNotConfiguredException(string what) : InvalidOperationException($"Portal is not configured: {what}.");

public interface ISignedPortalClient
{
    /// <summary>The portal's attachment limit is 5 MB; a little more is read so an over-size file is recognised, never stored.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024 + 1024;
    Task<PortalResult<CreateOrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> RequestCancellationAsync(string marketplaceOrderId, CancelRequestBody body, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> RequestReturnAsync(string marketplaceOrderId, ReturnRequestBody body, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> ConfirmDeliveryAsync(string marketplaceOrderId, DeliveryConfirmationBody body, CancellationToken ct = default);
    /// <summary>A fresh short-lived link to a return label (<c>POST /returns/{marketplaceReturnId}/label-link</c>, SHIPPING.md §4).</summary>
    Task<PortalResult<ReturnLabelLink>> GetReturnLabelLinkAsync(string marketplaceReturnId, CancellationToken ct = default);
    /// <summary><c>POST /messages</c>: a buyer message, idempotent on <c>messageId</c> (ORDERS.md §3.3).</summary>
    Task<PortalResult<JsonElement>> SendMessageAsync(BuyerMessageBody body, CancellationToken ct = default);
    /// <summary><c>POST /messages/{id}/receipts</c>: <c>delivered | read | failed</c> for a seller message (the id from <c>message.sent</c>); the status only moves forward.</summary>
    Task<PortalResult<JsonElement>> SendMessageReceiptAsync(string portalMessageId, string status, CancellationToken ct = default, string? sellerOrderNumber = null, Guid? sellerId = null);
    /// <summary><c>GET /attachments/{id}</c>: a seller attachment's bytes (signed GET, empty body). One attempt inside the 30 s deadline, at most <see cref="MaxAttachmentBytes"/>; the outbox retries.</summary>
    Task<PortalBytes> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default);
    /// <summary><c>POST /content/reports</c> (CATALOG.md §6): idempotent on <c>reportId</c>.</summary>
    Task<PortalResult<JsonElement>> ReportContentAsync(ContentReportBody body, CancellationToken ct = default);
    Task<PortalResult<ShippingSettings>> GetShippingSettingsAsync(Guid sellerId, Guid brandId, CancellationToken ct = default);
    Task<PortalResult<LocalDeliveryEligibility>> CheckLocalDeliveryEligibilityAsync(LocalDeliveryEligibilityRequest request, CancellationToken ct = default);
    Task<PortalResult<ServiceAvailability>> GetServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken ct = default);
    Task<PortalResult<PaymentEventResponse>> SendPaymentEventAsync(PaymentEventRequest request, CancellationToken ct = default);
    /// <summary><c>POST /bookings/holds</c>: reserve a slot for 10 minutes (idempotent on <c>marketplaceBookingId</c>).</summary>
    Task<PortalResult<BookingResponse>> CreateBookingHoldAsync(BookingRequestBody request, CancellationToken ct = default);
    /// <summary><c>POST /bookings/{mid}/confirm</c>: turns a hold into Confirmed (instant) or Requested (request mode). The portal re-checks an expired hold and may answer 409.</summary>
    Task<PortalResult<PortalBookingSummary>> ConfirmBookingAsync(string marketplaceBookingId, BookingConfirmBody body, CancellationToken ct = default);
    /// <summary><c>POST /bookings/{mid}/cancellations</c> (idempotent on the booking and <c>requestId</c>): the portal decides the fee by policy.</summary>
    Task<PortalResult<PortalBookingSummary>> CancelBookingAsync(string marketplaceBookingId, BookingCancelBody body, CancellationToken ct = default);
    /// <summary><c>POST /bookings/{mid}/reschedule-requests</c>: applied at once (instant services) or pending (request mode); idempotent on <c>requestId</c>.</summary>
    Task<PortalResult<PortalBookingSummary>> RescheduleBookingAsync(string marketplaceBookingId, BookingRescheduleBody body, CancellationToken ct = default);
    /// <summary><c>POST /bookings/{mid}/proposal-responses</c>: accept or decline the time the provider proposed.</summary>
    Task<PortalResult<PortalBookingSummary>> RespondToProposalAsync(string marketplaceBookingId, BookingProposalBody body, CancellationToken ct = default);
    /// <summary><c>GET /bookings/{mid}</c>.</summary>
    Task<PortalResult<PortalBookingSummary>> GetBookingAsync(string marketplaceBookingId, CancellationToken ct = default);

    // The catalog feed (docs/CATALOG.md in the portal). The documents come back as raw JSON: the one file that reads them is Catalog/HttpCatalogSource.cs.
    Task<PortalResult<JsonElement>> GetCatalogListingsAsync(string? since, int limit, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> GetCatalogListingAsync(Guid listingId, CancellationToken ct = default);
    Task<PortalResult<JsonElement>> GetCatalogCategoriesAsync(CancellationToken ct = default);
    Task<PortalResult<JsonElement>> GetCatalogBrandsAsync(Guid? since, int limit, CancellationToken ct = default);
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
    /// <summary>A feed page holds up to 200 full documents (Markdown and HTML descriptions): its own, larger cap. The catalog source halves its page size when even this is exceeded.</summary>
    public const int CatalogMaxResponseBytes = 16 * 1024 * 1024;

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

    public Task<PortalResult<JsonElement>> SendMessageAsync(BuyerMessageBody b, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/messages", b, Retry.Standard, ct);

    public Task<PortalResult<JsonElement>> SendMessageReceiptAsync(string id, string status, CancellationToken ct = default, string? sellerOrderNumber = null, Guid? sellerId = null)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/messages/{Id(id)}/receipts", new { status, sellerOrderNumber, sellerId }, Retry.Standard, ct);

    public async Task<PortalBytes> GetAttachmentAsync(Guid id, CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.InboundSecret)) throw new PortalNotConfiguredException("Portal:InboundSecret");
        if (http.BaseAddress is null) throw new PortalNotConfiguredException("Portal:BaseUrl");
        var path = $"{ApiBase}/attachments/{id:D}";
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString();
        var nonce = PortalSigning.NewNonce();
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation(PortalSigning.TimestampHeader, timestamp);
        req.Headers.TryAddWithoutValidation(PortalSigning.NonceHeader, nonce);
        req.Headers.TryAddWithoutValidation(PortalSigning.SignatureHeader, PortalSigning.Sign(o.InboundSecret, PortalSigning.Canonical(timestamp, nonce, "GET", path, [])));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(o.AttemptTimeoutSeconds, 1, 30)), time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)res.StatusCode;
            if (status is < 200 or >= 300) return new PortalBytes(status, null, null, status is 503 or 429 or 408 or 401 || status >= 500);
            if (res.Content.Headers.ContentLength > ISignedPortalClient.MaxAttachmentBytes) throw new PortalResponseTooLargeException();
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > ISignedPortalClient.MaxAttachmentBytes) throw new PortalResponseTooLargeException();
            }
            return new PortalBytes(status, ms.ToArray(), res.Content.Headers.ContentType?.MediaType, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        { throw new PortalUnavailableException("Portal unreachable (attachment).", ex); }
    }

    public Task<PortalResult<JsonElement>> ReportContentAsync(ContentReportBody b, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Post, $"{ApiBase}/content/reports", b, Retry.Standard, ct);

    public Task<PortalResult<ReturnLabelLink>> GetReturnLabelLinkAsync(string mid, CancellationToken ct = default)
        => Call<ReturnLabelLink>(HttpMethod.Post, $"{ApiBase}/returns/{Id(mid)}/label-link", new { }, Retry.Standard, ct);

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

    public Task<PortalResult<BookingResponse>> CreateBookingHoldAsync(BookingRequestBody r, CancellationToken ct = default)
        => Call<BookingResponse>(HttpMethod.Post, $"{ApiBase}/bookings/holds", r, Retry.Standard, ct);

    public Task<PortalResult<PortalBookingSummary>> ConfirmBookingAsync(string mid, BookingConfirmBody b, CancellationToken ct = default)
        => Call<PortalBookingSummary>(HttpMethod.Post, $"{ApiBase}/bookings/{Id(mid)}/confirm", b, Retry.Standard, ct);

    public Task<PortalResult<PortalBookingSummary>> CancelBookingAsync(string mid, BookingCancelBody b, CancellationToken ct = default)
        => Call<PortalBookingSummary>(HttpMethod.Post, $"{ApiBase}/bookings/{Id(mid)}/cancellations", b, Retry.Standard, ct);

    public Task<PortalResult<PortalBookingSummary>> RescheduleBookingAsync(string mid, BookingRescheduleBody b, CancellationToken ct = default)
        => Call<PortalBookingSummary>(HttpMethod.Post, $"{ApiBase}/bookings/{Id(mid)}/reschedule-requests", b, Retry.Standard, ct);

    public Task<PortalResult<PortalBookingSummary>> RespondToProposalAsync(string mid, BookingProposalBody b, CancellationToken ct = default)
        => Call<PortalBookingSummary>(HttpMethod.Post, $"{ApiBase}/bookings/{Id(mid)}/proposal-responses", b, Retry.Standard, ct);

    public Task<PortalResult<PortalBookingSummary>> GetBookingAsync(string mid, CancellationToken ct = default)
        => Call<PortalBookingSummary>(HttpMethod.Get, $"{ApiBase}/bookings/{Id(mid)}", null, Retry.Standard, ct);

    public Task<PortalResult<PaymentEventResponse>> SendPaymentEventAsync(PaymentEventRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.EventId) || r.EventId.Length > 100) throw new ArgumentException("eventId is required and at most 100 characters.", nameof(r));
        return Call<PaymentEventResponse>(HttpMethod.Post, $"{ApiBase}/payment-events", r, Retry.PaymentEvent, ct);
    }

    // ---------- catalog feed (read-only) ----------
    public Task<PortalResult<JsonElement>> GetCatalogListingsAsync(string? since, int limit, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Get, $"{ApiBase}/catalog/listings?{(string.IsNullOrEmpty(since) ? "" : $"since={Uri.EscapeDataString(since)}&")}limit={Math.Clamp(limit, 1, 200)}", null, Retry.Standard, ct, CatalogMaxResponseBytes);

    public Task<PortalResult<JsonElement>> GetCatalogListingAsync(Guid listingId, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Get, $"{ApiBase}/catalog/listings/{listingId:D}", null, Retry.Standard, ct, CatalogMaxResponseBytes);

    public Task<PortalResult<JsonElement>> GetCatalogCategoriesAsync(CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Get, $"{ApiBase}/catalog/categories", null, Retry.Standard, ct, CatalogMaxResponseBytes);

    public Task<PortalResult<JsonElement>> GetCatalogBrandsAsync(Guid? since, int limit, CancellationToken ct = default)
        => Call<JsonElement>(HttpMethod.Get, $"{ApiBase}/catalog/brands?{(since is { } b ? $"since={b:D}&" : "")}limit={Math.Clamp(limit, 1, 200)}", null, Retry.Standard, ct, CatalogMaxResponseBytes);

    // ---------- core ----------
    private static string Id(string id)
        => SafeId().IsMatch(id) ? id : throw new ArgumentException("Identifier contains characters that are not allowed in a signed path.", nameof(id));

    private async Task<PortalResult<T>> Call<T>(HttpMethod method, string pathAndQuery, object? body, Retry profile, CancellationToken ct, int maxResponseBytes = MaxResponseBytes)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.InboundSecret)) throw new PortalNotConfiguredException("Portal:InboundSecret");
        if (http.BaseAddress is null) throw new PortalNotConfiguredException("Portal:BaseUrl");

        // Serialise once: these exact bytes are hashed, signed and sent on every attempt.
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, PortalJson.Options);
        if (bytes.Length > PortalSigning.MaxBodyBytes) throw new PortalRequestTooLargeException();

        var maxAttempts = Math.Max(1, o.MaxAttempts);
        var attemptTimeout = TimeSpan.FromSeconds(Math.Clamp(o.AttemptTimeoutSeconds, 1, 30));
        // One deadline for the whole call (all attempts and backoffs). Longer persistence is the outbox's job.
        var total = TimeSpan.FromSeconds(Math.Clamp(o.TotalDeadlineSeconds, 1, 60));
        var started = time.GetTimestamp();

        PortalResult<T>? lastAnswer = null;      // the last retryable answer, returned if the deadline ends the retrying
        Exception? lastFailure = null;           // the last network failure, thrown (wrapped) if the deadline ends the retrying

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt >= maxAttempts;
            var remaining = total - time.GetElapsedTime(started);
            if (attempt > 1 && remaining <= TimeSpan.Zero)
            {
                // The backoff itself overran the deadline (the timer fired late): never start another attempt, and never build a timer from a
                // negative delay. Answer with what the previous attempt produced.
                if (lastAnswer is not null) return lastAnswer;
                throw new PortalUnavailableException($"Portal unreachable after {attempt - 1} attempt(s).", lastFailure);
            }
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

            // The deadline timer runs on the injected TimeProvider so tests control it; an attempt never outlives the total budget.
            using var deadline = new CancellationTokenSource(attemptTimeout < remaining ? attemptTimeout : remaining, time);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            HttpResponseMessage? res = null;
            try   // the response is disposed on every path: success, retry, an oversized or broken body, a timeout
            {
                string text;
                try
                {
                    res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    text = await ReadCapped(res, maxResponseBytes, cts.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    lastFailure = ex;
                    lastAnswer = null;
                    var delay = Backoff(o, attempt, null);
                    if (last || !HasBudgetFor(delay, started, total))
                        throw new PortalUnavailableException($"Portal unreachable after {attempt} attempt(s).", ex);
                    await Task.Delay(delay, time, ct);
                    continue;
                }

                var status = (int)res.StatusCode;
                var error = status is >= 200 and < 300 ? null : ParseError(text);
                var transient = IsTransient(profile, status, error?.Code);
                T? value = default;
                if (error is null && text.Length > 0)
                {
                    try { value = JsonSerializer.Deserialize<T>(text, PortalJson.Options); } catch (JsonException) { }
                }
                var answer = new PortalResult<T>(status, value, error, attempt) { Retryable = transient };
                // 429 is transient but never retried here: the portal's limiter is a fixed one-minute window without
                // Retry-After, so short backoffs only add load. It is surfaced (Retryable) for the outbox to retry later.
                if (transient && status != 429 && !last)
                {
                    var delay = Backoff(o, attempt, RetryAfterDelay(res));
                    if (HasBudgetFor(delay, started, total))
                    {
                        lastAnswer = answer;
                        lastFailure = null;
                        await Task.Delay(delay, time, ct);
                        continue;
                    }
                }
                return answer;
            }
            finally { res?.Dispose(); }
        }
    }

    /// <summary>The next attempt may only start if the wait still leaves part of the total deadline.</summary>
    private bool HasBudgetFor(TimeSpan delay, long started, TimeSpan total) => time.GetElapsedTime(started) + delay < total;

    /// <summary>Retry-After as delta seconds or as an HTTP date (date minus now, never negative).</summary>
    private TimeSpan? RetryAfterDelay(HttpResponseMessage res)
    {
        var ra = res.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra?.Date is { } date) { var d = date - time.GetUtcNow(); return d < TimeSpan.Zero ? TimeSpan.Zero : d; }
        return null;
    }

    /// <summary>
    /// CONTRACT.md §1.1: answers the portal says to re-send. 503 is transient with or without Retry-After (without it we back off
    /// ourselves, inside the total deadline). Never retry 401, 413, 422 or any other 4xx.
    /// </summary>
    private static bool IsTransient(Retry profile, int status, string? code) => status switch
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

    private static async Task<string> ReadCapped(HttpResponseMessage res, int maxBytes, CancellationToken ct)
    {
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new PortalResponseTooLargeException();
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
                // The signed path is the absolute path we send, so the base must not carry a path prefix.
                if (StartupValidator.BaseUrlProblem(o.BaseUrl, requireHttps: false) is { } problem) throw new InvalidOperationException(problem);
                c.BaseAddress = new Uri(o.BaseUrl, UriKind.Absolute);
            }
            c.Timeout = Timeout.InfiniteTimeSpan;   // per-attempt deadlines are enforced by the client
        });
        return services;
    }
}
