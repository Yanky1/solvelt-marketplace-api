using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What happens after a keypad or link payment was settled as paid: the call's state, the order marked placed, the confirmation text. One place for every provider (the Sola webhook and the pay connector).</summary>
internal static class PhonePaymentCompletion
{
    public static async Task AfterPaidAsync(VoicePaymentAttempt attempt, VoiceCallStore store, VoiceStateStore states, ISmsSender sms, CancellationToken ct)
    {
        await store.MarkOrderPlacedAsync(attempt.CallId, ct);
        if (states.Find(attempt.CallId) is { } st)
        {
            st.SetOrderPlaced(attempt.OrderNumber);
            if (st.Payment is { } p) p.Status = "paid";
            st.SetKeypadOutcome("paid");
        }
        var to = states.Find(attempt.CallId)?.VerifiedPhone;
        if (to is not null) await sms.SendAsync(to, $"Solvelt: your order {attempt.OrderNumber} is placed and paid ({SolaNotification.Dollars(attempt.AmountCents)} USD). We will email you updates.", ct);
    }
}

/// <summary>
/// The two endpoints the pay connector (our own separate service, V-13) talks to. NO card data ever reaches this API: the connector sends only attempt ids, invoices, amounts and results, signed with the key
/// the two share (<c>X-Connector-Timestamp</c> + <c>X-Connector-Signature</c> = base64 HMAC-SHA256 of <c>"{timestamp}.{body}"</c>, five-minute window, and this API signs its answers the same way).
/// <c>expect</c> tells the connector what to charge FROM THE SERVER'S ORDER (the connector refuses a request whose amount differs). <c>result</c> is the connector's report, and it settles through the
/// SAME path as every other provider: <see cref="PhoneOrderService.SettleAsync"/> believes a payment only when the amount equals our order total and the invoice is our order number.
/// Off (404) unless the provider is <c>sola</c> and the connector is configured.
/// </summary>
public static class PayConnectorEndpoints
{
    public const string ExpectPath = "/api/v1/voice/pay-connector/expect", ResultPath = "/api/v1/voice/pay-connector/result";
    private const string TimestampHeader = "X-Connector-Timestamp", SignatureHeader = "X-Connector-Signature";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapPayConnector(this IEndpointRouteBuilder app)
    {
        app.MapPost(ExpectPath, Expect);
        app.MapPost(ResultPath, Result);
        return app;
    }

    internal static string Sign(string key, string timestamp, string body) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(timestamp + "." + body)));

    private static async Task<(bool Ok, string Body)> ReadSignedAsync(HttpContext ctx, SolaOptions sola, TimeProvider time, CancellationToken ct)
    {
        if (ctx.Request.ContentLength is null or 0 or > 4096) return (false, "");
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > 4096 || string.IsNullOrEmpty(sola.PayConnectorKey)) return (false, "");
        var ok = long.TryParse(ctx.Request.Headers[TimestampHeader].ToString(), out var ts) && Math.Abs((time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ts)).TotalSeconds) <= Window.TotalSeconds
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(sola.PayConnectorKey, ts.ToString(), body)), Encoding.ASCII.GetBytes(ctx.Request.Headers[SignatureHeader].ToString().Trim()));
        return (ok, body);
    }

    private static IResult Signed(HttpContext ctx, SolaOptions sola, TimeProvider time, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
        ctx.Response.Headers[TimestampHeader] = ts;
        ctx.Response.Headers[SignatureHeader] = Sign(sola.PayConnectorKey!, ts, json);
        return Results.Content(json, "application/json");
    }

    private static bool Enabled(VoiceOptions v, SolaOptions s) => v.Enabled && v.PhonePaymentProvider == "sola" && s.KeypadEnabled;

    private static async Task<IResult> Expect(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SolaOptions> so, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        if (!Enabled(vo.Value, so.Value)) return Results.NotFound();
        var (ok, body) = await ReadSignedAsync(ctx, so.Value, time, ct);
        if (!ok) return Results.StatusCode(401);
        Guid id;
        try { using var d = JsonDocument.Parse(body); if (!Guid.TryParseExact(d.RootElement.GetProperty("attemptId").GetString(), "N", out id)) return Signed(ctx, so.Value, time, new { open = false }); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return Results.StatusCode(400); }
        var a = await db.VoicePaymentAttempts.AsNoTracking().Where(x => x.Id == id && x.Method == PhonePaymentMethod.Keypad && x.Status == "pending").Select(x => new { x.AmountCents, x.OrderNumber, x.Currency }).FirstOrDefaultAsync(ct);
        return Signed(ctx, so.Value, time, a is null ? new { open = false } : new { open = true, amountCents = a.AmountCents, invoice = a.OrderNumber, currency = a.Currency });
    }

    private sealed record Report(string? AttemptId, string? Invoice, string? Status, long PaidCents, string? TransactionId, string? Code);

    private static async Task<IResult> Result(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SolaOptions> so, MarketplaceDb db, PhoneOrderService orders, VoiceCallStore store, VoiceStateStore states,
        ISmsSender sms, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        if (!Enabled(vo.Value, so.Value)) return Results.NotFound();
        var (ok, body) = await ReadSignedAsync(ctx, so.Value, time, ct);
        if (!ok) return Results.StatusCode(401);
        Report? r;
        try { r = JsonSerializer.Deserialize<Report>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { return Results.StatusCode(400); }
        if (r is null || !Guid.TryParseExact(r.AttemptId, "N", out var id) || string.IsNullOrWhiteSpace(r.Invoice)) return Results.StatusCode(400);
        var attempt = await db.VoicePaymentAttempts.FirstOrDefaultAsync(x => x.Id == id && x.Method == PhonePaymentMethod.Keypad, ct);
        if (attempt is null) return Signed(ctx, so.Value, time, new { ok = false, settle = "unknown_attempt" });
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice.PayConnector");
        var status = r.Status switch { "paid" => PhonePaymentStatus.Paid, "declined" => PhonePaymentStatus.Declined, _ => PhonePaymentStatus.Failed };
        if (r.Status == "unknown") log.LogError("The pay connector could not confirm the payment for order {Number} (attempt {Attempt}): the card may have been charged. It needs a person.", attempt.OrderNumber, attempt.Id);
        // the connector's word is checked like every provider's: the amount must equal our order total and the invoice must be our order number (SettleAsync), a repeat of the same transaction changes nothing
        var settled = await orders.SettleAsync(id, new PhonePaymentOutcome(status, r.PaidCents, r.Invoice.Trim(), string.IsNullOrWhiteSpace(r.TransactionId) ? null : r.TransactionId.Trim(), r.Code), ct);
        if (settled == SettleStatus.Paid) await PhonePaymentCompletion.AfterPaidAsync(attempt, store, states, sms, ct);
        return Signed(ctx, so.Value, time, new { ok = true, settle = settled.ToString() });
    }
}
