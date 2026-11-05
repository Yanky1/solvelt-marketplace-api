using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Payments;

/// <summary>
/// Stripe settings. All come from the environment (<c>Stripe__SecretKey</c> ...), never a file. Test mode only: a live key (<c>sk_live_</c>, <c>rk_live_</c>,
/// <c>pk_live_</c>) stops the application at start-up in every environment. <see cref="WebhookSecret"/> verifies Stripe's webhooks and is a key of its own.
/// </summary>
public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    public string? PublishableKey { get; set; }
    public string? WebhookSecret { get; set; }
    public string BaseUrl { get; set; } = "https://api.stripe.com";
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Stripe answers 409 idempotency_key_in_use while the first request with the same key is still running (a double submit). Wait this long and ask again, up to <see cref="InUseRetries"/> times, before giving up as "try again".</summary>
    public int InUseRetryDelayMs { get; set; } = 400;
    public int InUseRetries { get; set; } = 3;
    /// <summary>
    /// Development/Testing only, and only with the in-process fake provider (no secret key): a verified <c>payment_intent.succeeded</c> webhook also marks the fake's intent as captured, so a later refund of it
    /// can succeed. There is no card flow in front of the fake; the journey (tools/journey) plays Stripe by posting the signed event, and a real Stripe has captured the money by then. Off by default; start-up refuses it elsewhere.
    /// </summary>
    public bool FakeCaptureOnWebhook { get; set; }
}

public sealed record PaymentIntentResult(string Id, string ClientSecret, string Status);
public sealed record RefundResult(string Id, string Status, long AmountCents);
/// <summary>A refund as Stripe lists it for an intent: <see cref="RefundKey"/> is OUR <c>refund_key</c> metadata (null for a refund made in the dashboard). Status: pending, requires_action, succeeded, failed, canceled.</summary>
public sealed record ProviderRefund(string Id, long AmountCents, string Status, string? RefundKey);

/// <summary>Raised when payments are not configured or the provider cannot be reached or refuses: nothing was charged.</summary>
public sealed class PaymentsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
/// <summary>The provider refused the request (a 4xx that waiting cannot fix: for example a refund above what was paid).</summary>
public sealed class PaymentRejectedException(string code) : Exception(code) { public string Code { get; } = code; }

/// <summary>
/// The only seam to the card processor. Every write carries OUR idempotency key (Stripe's <c>Idempotency-Key</c>), so a retry or a parallel request returns
/// the first result instead of charging or refunding twice. Amounts are <c>long</c> cents.
/// </summary>
public interface IPaymentProvider
{
    Task<PaymentIntentResult> CreateIntentAsync(long amountCents, string currency, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
    Task CancelIntentAsync(string intentId, string idempotencyKey, CancellationToken ct);
    /// <summary>Stripe's current status of an intent (<c>requires_payment_method</c>, <c>processing</c>, <c>requires_capture</c>, <c>succeeded</c>, <c>canceled</c> ...). Read-only.</summary>
    Task<string> GetIntentStatusAsync(string intentId, CancellationToken ct);
    /// <summary>What Stripe says has been refunded on the intent's charge so far, in cents (everything: ours and the dashboard's). A fresh read, the only truth about money that has left.</summary>
    Task<long> GetAmountRefundedAsync(string intentId, CancellationToken ct);
    /// <summary>Reads an intent we created (status and client secret). Stripe forgets idempotency keys after 24 hours, so a second create for an old key would open a SECOND intent: callers reuse the stored one through this instead (M4-15).</summary>
    Task<PaymentIntentResult> GetIntentAsync(string intentId, CancellationToken ct);
    /// <summary>Every refund Stripe has for the intent (up to 100), with our <c>refund_key</c> metadata: the only way to tell a refund of OURS that Stripe executed (our call timed out) from one made in the dashboard (decision M3-34).</summary>
    Task<IReadOnlyList<ProviderRefund>> ListRefundsAsync(string intentId, CancellationToken ct);
    Task<RefundResult> RefundAsync(string intentId, long amountCents, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
}

/// <summary>Production: Stripe's REST API over HTTPS with form bodies (no SDK dependency, so the idempotency header and the request shape are ours and tested).</summary>
public sealed class StripeHttpProvider(HttpClient http, IOptions<StripeOptions> options) : IPaymentProvider
{
    public async Task<PaymentIntentResult> CreateIntentAsync(long amountCents, string currency, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", amountCents.ToString(CultureInfo.InvariantCulture)), new("currency", currency.ToLowerInvariant()),
            new("payment_method_types[]", "card"),   // cards and wallets only at launch (decision O-19): no delayed methods (ACH, SEPA) that could sit in "processing" for days
        };
        foreach (var (k, v) in metadata) form.Add(new($"metadata[{k}]", v));
        var json = await PostAsync("/v1/payment_intents", form, idempotencyKey, ct);
        return new PaymentIntentResult(Str(json, "id"), Str(json, "client_secret"), Str(json, "status"));
    }

    public async Task<IReadOnlyList<ProviderRefund>> ListRefundsAsync(string intentId, CancellationToken ct)
    {
        // Follows starting_after while has_more (review L3): a charge with more than one page of refunds must not hide ours. A runaway list is cut at 50 pages (5000 refunds) and reported as unavailable.
        var list = new List<ProviderRefund>();
        string? after = null;
        for (var page = 0; page < 50; page++)
        {
            var json = await PostAsync($"/v1/refunds?payment_intent={Uri.EscapeDataString(intentId)}&limit=100" + (after is null ? "" : $"&starting_after={Uri.EscapeDataString(after)}"), null, null, ct);
            string? last = null;
            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var r in data.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    last = id.GetString();
                    var amount = r.TryGetProperty("amount", out var a) && a.TryGetInt64(out var n) ? n : 0;
                    var status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "succeeded";
                    string? key = null;
                    if (r.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object && md.TryGetProperty("refund_key", out var k) && k.ValueKind == JsonValueKind.String) key = k.GetString();
                    list.Add(new ProviderRefund(id.GetString()!, amount, status, key));
                }
            if (!(json.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True) || last is null) return list;
            after = last;
        }
        throw new PaymentsUnavailableException("too_many_refunds");
    }

    public async Task<PaymentIntentResult> GetIntentAsync(string intentId, CancellationToken ct)
    {
        var json = await PostAsync($"/v1/payment_intents/{Uri.EscapeDataString(intentId)}", null, null, ct);
        return new PaymentIntentResult(Str(json, "id"), Str(json, "client_secret"), Str(json, "status"));
    }

    public async Task<string> GetIntentStatusAsync(string intentId, CancellationToken ct)
        => Str(await PostAsync($"/v1/payment_intents/{Uri.EscapeDataString(intentId)}", null, null, ct), "status");

    public async Task<long> GetAmountRefundedAsync(string intentId, CancellationToken ct)
    {
        var json = await PostAsync($"/v1/payment_intents/{Uri.EscapeDataString(intentId)}?expand%5B%5D=latest_charge", null, null, ct);
        return json.TryGetProperty("latest_charge", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("amount_refunded", out var a) && a.TryGetInt64(out var n) ? n
            : throw new PaymentsUnavailableException("bad_response");
    }

    public async Task CancelIntentAsync(string intentId, string idempotencyKey, CancellationToken ct)
        => await PostAsync($"/v1/payment_intents/{Uri.EscapeDataString(intentId)}/cancel", [], idempotencyKey, ct);

    public async Task<RefundResult> RefundAsync(string intentId, long amountCents, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("payment_intent", intentId), new("amount", amountCents.ToString(CultureInfo.InvariantCulture)) };
        foreach (var (k, v) in metadata) form.Add(new($"metadata[{k}]", v));
        var json = await PostAsync("/v1/refunds", form, idempotencyKey, ct);
        return new RefundResult(Str(json, "id"), Str(json, "status"), json.TryGetProperty("amount", out var a) && a.TryGetInt64(out var n) ? n : amountCents);
    }

    /// <summary>A POST with a form body and an idempotency key; with no form and no key it is a GET (read-only).</summary>
    private async Task<JsonElement> PostAsync(string path, List<KeyValuePair<string, string>>? form, string? idempotencyKey, CancellationToken ct)
    {
        var key = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(key)) throw new PaymentsUnavailableException("not_configured");
        var read = form is null && idempotencyKey is null;
        if (!read && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 255)) throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(read ? HttpMethod.Get : HttpMethod.Post, options.Value.BaseUrl.TrimEnd('/') + path);
            if (!read) { req.Content = new FormUrlEncodedContent(form!); req.Headers.Add("Idempotency-Key", idempotencyKey); }
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60)));
            HttpResponseMessage res;
            try { res = await http.SendAsync(req, timeout.Token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new PaymentsUnavailableException("unreachable", ex); }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return JsonDocument.Parse(text).RootElement.Clone();
                // 409 = the same key is still being processed: wait briefly and ask again (the answer will be the first request's result), then give up as "try again".
                if ((int)res.StatusCode == 409 && attempt < Math.Max(0, options.Value.InUseRetries))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(options.Value.InUseRetryDelayMs, 0, 5000)), ct);
                    continue;
                }
                // 429 and 5xx are worth retrying with the same key; any other 4xx will not change (never echo Stripe's message: it may carry card details).
                if ((int)res.StatusCode is 429 or >= 500 or 409) throw new PaymentsUnavailableException($"stripe_{(int)res.StatusCode}");
                string? code = null;
                try { code = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("code").GetString(); } catch { /* not JSON */ }
                throw new PaymentRejectedException(string.IsNullOrEmpty(code) ? $"stripe_{(int)res.StatusCode}" : new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(60).ToArray()));
            }
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new PaymentsUnavailableException("bad_response");
}

/// <summary>Where no processor is configured outside Development/Testing: checkout answers 503, nothing is charged.</summary>
public sealed class UnconfiguredPaymentProvider : IPaymentProvider
{
    public Task<PaymentIntentResult> CreateIntentAsync(long a, string c, string k, IReadOnlyDictionary<string, string> m, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task CancelIntentAsync(string i, string k, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task<string> GetIntentStatusAsync(string i, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task<long> GetAmountRefundedAsync(string i, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task<PaymentIntentResult> GetIntentAsync(string i, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task<IReadOnlyList<ProviderRefund>> ListRefundsAsync(string i, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
    public Task<RefundResult> RefundAsync(string i, long a, string k, IReadOnlyDictionary<string, string> m, CancellationToken ct) => throw new PaymentsUnavailableException("not_configured");
}

/// <summary>
/// CI, tests and local development (no Stripe key): behaves like Stripe where it matters. Writes are idempotent on the key (a repeated key returns the first result,
/// a reused key with other parameters is refused as Stripe does), a refund can never exceed what was captured, and a captured amount is only set by <see cref="Capture"/>.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public sealed record Intent(string Id, string ClientSecret, long AmountCents, string Currency, IReadOnlyDictionary<string, string> Metadata)
    {
        public long CapturedCents { get; internal set; }
        public long RefundedCents { get; internal set; }
        public bool Cancelled { get; internal set; }
        /// <summary>Test hook: a delayed payment method is in flight (Stripe status <c>processing</c>); cannot be cancelled.</summary>
        public bool Processing { get; internal set; }
    }

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, (string Fingerprint, object Result)> _byKey = new();
    private readonly ConcurrentDictionary<string, Intent> _intents = new();
    private int _next;

    /// <summary>Every request that reached the processor, including repeats (a repeated idempotency key still counts here, but not in <see cref="Intents"/>).</summary>
    public int CreateCalls { get; private set; }
    public IReadOnlyCollection<Intent> Intents => _intents.Values.ToList();
    public IReadOnlyList<RefundResult> Refunds { get { lock (_lock) return _refunds.ToList(); } }
    private readonly List<RefundResult> _refunds = [];
    private readonly List<(string IntentId, ProviderRefund Refund)> _listed = [];
    /// <summary>Test hook: the next refund IS executed at the processor but the answer is lost (a timeout): the caller sees "unreachable" and will retry with the same key.</summary>
    public bool TimeoutAfterExecuteNext { get; set; }
    /// <summary>Test hook: fail the next N calls as an unreachable processor.</summary>
    public int FailNext { get; set; }
    /// <summary>Test hook: the next cancel is refused by the processor with this error code (for example <c>api_key_expired</c>).</summary>
    public string? RejectCancelWith { get; set; }
    /// <summary>Test hook: the next refund is refused by the processor with this error code (one that moved no money, for example <c>api_key_expired</c>).</summary>
    public string? RejectRefundWith { get; set; }
    /// <summary>Test hook: runs once, just before a refund is refused for exceeding what is left (a slow worker is overtaken here by a parallel one). It runs AFTER the fake's lock is released: its continuations may resume on other threads, which could never take a lock held by this one.</summary>
    public Func<Task>? OnRefusal { get; set; }

    /// <summary>Test hook: Stripe now lists the refund with this metadata key under another status. failed and canceled refunds return the money (amount_refunded drops); pending ones stay counted.</summary>
    public void SetListedStatus(string refundKey, string status)
    {
        lock (_lock)
            for (var n = 0; n < _listed.Count; n++)
                if (_listed[n].Refund.RefundKey == refundKey)
                {
                    var was = _listed[n].Refund;
                    if (was.Status is "succeeded" or "pending" && status is "failed" or "canceled") _intents[_listed[n].IntentId].RefundedCents -= was.AmountCents;
                    _listed[n] = (_listed[n].IntentId, was with { Status = status });
                }
    }

    public Task<PaymentIntentResult> CreateIntentAsync(long amountCents, string currency, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        lock (_lock)
        {
            CreateCalls++;
            MaybeFail();
            var fp = $"create|{amountCents}|{currency.ToLowerInvariant()}|{string.Join(",", metadata.OrderBy(m => m.Key).Select(m => m.Key + "=" + m.Value))}";
            if (_byKey.TryGetValue(idempotencyKey, out var prior))
                return prior.Fingerprint == fp ? Task.FromResult((PaymentIntentResult)prior.Result) : throw new PaymentRejectedException("idempotency_key_in_use");
            if (amountCents < 50) throw new PaymentRejectedException("amount_too_small");
            var id = $"pi_fake_{Interlocked.Increment(ref _next):D6}";
            var result = new PaymentIntentResult(id, $"{id}_secret_{Guid.NewGuid():N}"[..40], "requires_payment_method");
            _intents[id] = new Intent(id, result.ClientSecret, amountCents, currency.ToLowerInvariant(), metadata);
            if (!ForgetKeysAfterCreate) _byKey[idempotencyKey] = (fp, result);
            return Task.FromResult(result);
        }
    }

    public Task<PaymentIntentResult> GetIntentAsync(string intentId, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (!_intents.TryGetValue(intentId, out var i)) throw new PaymentRejectedException("resource_missing");
            return Task.FromResult(new PaymentIntentResult(i.Id, i.ClientSecret, i.Cancelled ? "canceled" : i.CapturedCents > 0 ? "succeeded" : "requires_payment_method"));
        }
    }

    /// <summary>Test hook: Stripe forgets idempotency keys after 24 hours, so a repeated create would open a NEW intent.</summary>
    public void ForgetIdempotencyKeys() { lock (_lock) _byKey.Clear(); }
    /// <summary>Test hook: every create is forgotten at once (a key that expired between two parallel requests).</summary>
    public bool ForgetKeysAfterCreate { get; set; }

    public Task CancelIntentAsync(string intentId, string idempotencyKey, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (RejectCancelWith is { } code) { RejectCancelWith = null; throw new PaymentRejectedException(code); }
            if (!_intents.TryGetValue(intentId, out var i)) throw new PaymentRejectedException("resource_missing");
            if (i.CapturedCents > 0 || i.Processing) throw new PaymentRejectedException("payment_intent_unexpected_state");
            i.Cancelled = true;
            return Task.CompletedTask;
        }
    }

    public Task<string> GetIntentStatusAsync(string intentId, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (!_intents.TryGetValue(intentId, out var i)) throw new PaymentRejectedException("resource_missing");
            return Task.FromResult(i.Cancelled ? "canceled" : i.CapturedCents > 0 ? "succeeded" : i.Processing ? "processing" : "requires_payment_method");
        }
    }

    public Task<long> GetAmountRefundedAsync(string intentId, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (!_intents.TryGetValue(intentId, out var i)) throw new PaymentRejectedException("resource_missing");
            return Task.FromResult(i.RefundedCents);
        }
    }

    public Task<IReadOnlyList<ProviderRefund>> ListRefundsAsync(string intentId, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (!_intents.ContainsKey(intentId)) throw new PaymentRejectedException("resource_missing");
            return Task.FromResult<IReadOnlyList<ProviderRefund>>(_listed.Where(x => x.IntentId == intentId).Select(x => x.Refund).ToList());
        }
    }

    /// <summary>Test hook: somebody refunded in the Stripe dashboard (Stripe's cumulative amount_refunded grows without one of our refunds).</summary>
    public void RefundInDashboard(string intentId, long cents) { lock (_lock) _intents[intentId].RefundedCents += cents; }

    /// <summary>Test hook: the buyer chose a delayed method; the money is in flight.</summary>
    public void SetProcessing(string intentId) { lock (_lock) _intents[intentId].Processing = true; }

    public Task<RefundResult> RefundAsync(string intentId, long amountCents, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        lock (_lock)
        {
            MaybeFail();
            if (RejectRefundWith is { } refusal) { RejectRefundWith = null; throw new PaymentRejectedException(refusal); }
            var fp = $"refund|{intentId}|{amountCents}";
            if (_byKey.TryGetValue(idempotencyKey, out var prior))
                return prior.Fingerprint == fp ? Task.FromResult((RefundResult)prior.Result) : throw new PaymentRejectedException("idempotency_key_in_use");
            if (!_intents.TryGetValue(intentId, out var i)) throw new PaymentRejectedException("resource_missing");
            if (amountCents <= 0 || i.RefundedCents + amountCents > i.CapturedCents)
            {
                var hook = OnRefusal; OnRefusal = null;
                return RefuseAfterAsync(hook);   // never more than was paid
            }
            i.RefundedCents += amountCents;
            var r = new RefundResult($"re_fake_{_refunds.Count + 1:D6}", "succeeded", amountCents);
            _refunds.Add(r);
            _byKey[idempotencyKey] = (fp, r);
            _listed.Add((intentId, new ProviderRefund(r.Id, amountCents, "succeeded", metadata.TryGetValue("refund_key", out var rk) ? rk : null)));
            if (TimeoutAfterExecuteNext) { TimeoutAfterExecuteNext = false; throw new PaymentsUnavailableException("unreachable"); }
            return Task.FromResult(r);
        }
    }

    private static async Task<RefundResult> RefuseAfterAsync(Func<Task>? hook)
    {
        await Task.Yield();                                 // leave the caller's lock first
        if (hook is not null) await hook();
        throw new PaymentRejectedException("charge_exceeds_amount");
    }

    /// <summary>Test hook: the buyer paid (the whole amount). Stripe would send <c>payment_intent.succeeded</c>.</summary>
    public void Capture(string intentId) { lock (_lock) { var i = _intents[intentId]; i.CapturedCents = i.AmountCents; } }
    /// <summary>Like <see cref="Capture"/>, for an intent this process may not know (a restart forgets them): false when unknown.</summary>
    public bool TryCapture(string intentId) { lock (_lock) { if (!_intents.TryGetValue(intentId, out var i)) return false; i.CapturedCents = i.AmountCents; return true; } }

    private void MaybeFail() { if (FailNext > 0) { FailNext--; throw new PaymentsUnavailableException("unreachable"); } }
}
