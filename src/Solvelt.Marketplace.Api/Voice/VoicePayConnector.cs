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
/// <summary>How many cards an account may save by phone (each is a credential at the processor).</summary>
public static class SavedCardLimits { public const int MaxPerAccount = 5; }

public static class PayConnectorEndpoints
{
    public const string ExpectPath = "/api/v1/voice/pay-connector/expect", ResultPath = "/api/v1/voice/pay-connector/result", CardPath = "/api/v1/voice/pay-connector/card";
    /// <summary>The attempt method of a card-saving step (V-16): amount 0, no order; the account is in the attempt's order field as <c>cs-{accountId}</c>.</summary>
    public const string SaveCardMethod = "save_card";
    private const string TimestampHeader = "X-Connector-Timestamp", SignatureHeader = "X-Connector-Signature";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapPayConnector(this IEndpointRouteBuilder app)
    {
        app.MapPost(ExpectPath, Expect);
        app.MapPost(ResultPath, Result);
        app.MapPost(CardPath, Card);
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
        var a = await db.VoicePaymentAttempts.AsNoTracking().Where(x => x.Id == id && (x.Method == PhonePaymentMethod.Keypad || x.Method == SaveCardMethod) && x.Status == "pending").Select(x => new { x.AmountCents, x.OrderNumber, x.Currency, x.Method }).FirstOrDefaultAsync(ct);
        return Signed(ctx, so.Value, time, a is null ? new { open = false } : a.Method == SaveCardMethod ? new { open = true, amountCents = 0L, invoice = "", currency = a.Currency, purpose = "save_card" } : new { open = true, amountCents = a.AmountCents, invoice = a.OrderNumber, currency = a.Currency, purpose = "sale" });
    }

    private sealed record CardReport(string? AttemptId, string? Status, string? Token, string? Brand, string? Last4, string? Exp, string? Code);

    /// <summary>
    /// The connector's report of a card it saved (V-16): the processor's token, brand, last four digits and expiry. Believed only for a PENDING card-saving attempt this server opened (it names the account); the token is
    /// stored for that account and nothing else (never a card number: this API never has one). A repeat of the same report changes nothing. Over the cap of saved cards the card is not kept.
    /// </summary>
    private static async Task<IResult> Card(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SolaOptions> so, MarketplaceDb db, VoiceStateStore states, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        if (!Enabled(vo.Value, so.Value)) return Results.NotFound();
        var (ok, body) = await ReadSignedAsync(ctx, so.Value, time, ct);
        if (!ok) return Results.StatusCode(401);
        CardReport? r;
        try { r = JsonSerializer.Deserialize<CardReport>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { return Results.StatusCode(400); }
        if (r is null || !Guid.TryParseExact(r.AttemptId, "N", out var id)) return Results.StatusCode(400);
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice.PayConnector");
        var attempt = await db.VoicePaymentAttempts.FirstOrDefaultAsync(x => x.Id == id && x.Method == SaveCardMethod, ct);
        if (attempt is null || !attempt.OrderNumber.StartsWith("cs-", StringComparison.Ordinal) || !Guid.TryParseExact(attempt.OrderNumber[3..], "N", out var accountId)) return Signed(ctx, so.Value, time, new { ok = false, settle = "unknown_attempt" });
        if (attempt.Status != "pending") return Signed(ctx, so.Value, time, new { ok = true, settle = attempt.Status });   // a repeat: the first word stands
        var now = time.GetUtcNow();
        var status = r.Status switch { "saved" => "saved", "declined" => "declined", "failed" => "failed", _ => "unknown" };
        if (status == "unknown") log.LogError("The pay connector could not confirm a card save (attempt {Attempt}): the card may have been tokenised at the processor. It needs a person.", attempt.Id);
        if (status == "saved")
        {
            var valid = !string.IsNullOrWhiteSpace(r.Token) && r.Token.Length <= 100 && r.Last4 is { Length: 4 } l4 && l4.All(char.IsDigit) && r.Exp is { Length: 4 } e && e.All(char.IsDigit) && int.Parse(e[..2]) is >= 1 and <= 12
                && r.Brand is { Length: > 0 and <= 20 };
            var count = await db.SavedCards.CountAsync(c => c.AccountId == accountId, ct);
            if (!valid) { status = "failed"; log.LogError("The pay connector reported a saved card in an unusable shape (attempt {Attempt}).", attempt.Id); }
            else if (count >= SavedCardLimits.MaxPerAccount) { status = "failed"; log.LogError("A card was tokenised for an account that already has {Max} saved cards (attempt {Attempt}); it was not kept.", SavedCardLimits.MaxPerAccount, attempt.Id); }
            else if (!await db.SavedCards.AnyAsync(c => c.AccountId == accountId && c.Token == r.Token, ct))
                db.SavedCards.Add(new SavedCard { Id = Guid.NewGuid(), AccountId = accountId, Token = r.Token!.Trim(), Brand = r.Brand!.Trim(), Last4 = r.Last4!, ExpMonth = r.Exp![..2], ExpYear = r.Exp[2..], CreatedAt = now });
        }
        attempt.Status = status; attempt.SettledAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return Signed(ctx, so.Value, time, new { ok = false, settle = "conflict" }); }   // the same report in parallel: the connector retries and sees the first word
        return Signed(ctx, so.Value, time, new { ok = true, settle = status });
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
