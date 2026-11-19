using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What the connector answered to a saved-card sale: <see cref="Reached"/> false when it could not be reached or its answer was not authentic (the outcome is then UNKNOWN: the sale may have happened).</summary>
public sealed record ConnectorSaleResult(bool Reached, string? ChargeId, string? ErrorCode);

public interface IPayConnectorClient
{
    /// <summary>Asks the connector for ONE sale with a saved token for the attempt. Never retried by this API: the connector is idempotent per attempt and an unclear answer is for a person.</summary>
    Task<ConnectorSaleResult> ChargeTokenAsync(string attemptId, string token, CancellationToken ct);
}

/// <summary>The signed call to the connector's <c>/charge-token</c> (V-16): <c>X-Connector-Timestamp</c> + <c>X-Connector-Signature</c> (HMAC-SHA256 of <c>{ts}.{body}</c>), five-minute window, the answer signed the same way. The token goes ONLY in this body.</summary>
public sealed class PayConnectorClient(HttpClient http, IOptions<SolaOptions> options, TimeProvider time) : IPayConnectorClient
{
    public async Task<ConnectorSaleResult> ChargeTokenAsync(string attemptId, string token, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.KeypadEnabled || string.IsNullOrEmpty(o.PayConnectorKey)) return new ConnectorSaleResult(false, null, "not_configured");
        try
        {
            var body = JsonSerializer.Serialize(new { attemptId, token });
            var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
            var origin = new Uri(o.PayConnectorUrl!).GetLeftPart(UriPartial.Authority);
            using var req = new HttpRequestMessage(HttpMethod.Post, origin + "/charge-token") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Connector-Timestamp", ts);
            req.Headers.Add("X-Connector-Signature", PayConnectorEndpoints.Sign(o.PayConnectorKey, ts, body));
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            var rts = res.Headers.TryGetValues("X-Connector-Timestamp", out var t) ? t.FirstOrDefault() : null;
            var rsig = res.Headers.TryGetValues("X-Connector-Signature", out var s) ? s.FirstOrDefault() : null;
            var authentic = long.TryParse(rts, out var rtsn) && Math.Abs((time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(rtsn)).TotalSeconds) <= 300 && rsig is not null
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(PayConnectorEndpoints.Sign(o.PayConnectorKey, rts!, text)), Encoding.ASCII.GetBytes(rsig.Trim()));
            if (!authentic || (int)res.StatusCode != 200) return new ConnectorSaleResult(false, null, "unclear");
            using var d = JsonDocument.Parse(text);
            string? S(string n) => d.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new ConnectorSaleResult(true, S("chargeId"), S("errorCode"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException) { return new ConnectorSaleResult(false, null, "unclear"); }
    }
}

public enum SavedCardPayResult { Paid, Pending, Failed }

/// <summary>
/// Paying for the confirmed order with a SAVED card (decision V-16), after the caller heard the server's read-back (card, amount) and said yes. The order is placed through the same path as every payment and priced by the SERVER; a
/// <c>saved_card</c> attempt is opened for exactly that total; the token is decrypted only HERE, for this one sale, and goes only to the connector over the signed channel; the connector asks this API what to charge (amount and invoice
/// from the order) and reports its result, which settles through the SAME amount-and-invoice check as every payment. An unclear answer is never retried and a second saved-card attempt for an order whose last one is still unclear is refused.
/// </summary>
public sealed class SavedCardPayment(PhoneOrderService orders, MarketplaceDb db, SavedCardVault vault, IPayConnectorClient connector, ILogger<SavedCardPayment> log)
{
    public async Task<SavedCardPayResult> PayAsync(VoiceCallState s, VoiceCall call, Guid cardId, CancellationToken ct)
    {
        if (s.AccountId is not { } account || !OrderAccess.IsAccount(s) || s.Confirmation != ConfirmationStatus.Confirmed || s.QuoteKey is null || s.OrderNumber is not null) return SavedCardPayResult.Failed;
        var card = await db.SavedCards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId && c.AccountId == account, ct);   // by account id: another account's card is simply not found
        if (card is null) return SavedCardPayResult.Failed;
        var (status, number, total, intent) = await orders.PlaceAsync(s, call, ct);
        if (status != PlaceStatus.Placed) { if (status == PlaceStatus.PriceChanged) s.OrderChanged(); return SavedCardPayResult.Failed; }
        // a previous saved-card attempt for this order that never got a clear answer: the card may already have been charged, so it is not charged again behind it
        if (await db.VoicePaymentAttempts.AnyAsync(a => a.OrderNumber == number && a.Method == PhonePaymentMethod.SavedCard && a.Status == "pending", ct)) return SavedCardPayResult.Pending;
        var token = vault.Unprotect(card.Token, account);
        if (token is null) { log.LogError("A saved card could not be decrypted (card {Card}); nothing was charged. It needs a person.", card.Id); return SavedCardPayResult.Failed; }
        var row = await orders.OpenAttemptAsync(call.Id, number!, total, PhonePaymentMethod.SavedCard, ct);
        var attemptId = row.Id.ToString("N");
        s.SetPayment(new PaymentAttempt { AttemptId = attemptId, OrderNumber = number!, IntentId = intent!, AmountCents = total, Method = PhonePaymentMethod.SavedCard });
        s.SetTask("payment");
        var sale = await connector.ChargeTokenAsync(attemptId, token, ct);
        if (!sale.Reached) log.LogError("The pay connector gave no clear answer to the saved-card sale for order {Number} (attempt {Attempt}): the card may have been charged. It needs a person.", number, attemptId);
        // the connector's signed report already settled the attempt (it reports before it answers); read what the server decided
        db.ChangeTracker.Clear();
        var settled = await db.VoicePaymentAttempts.AsNoTracking().Where(a => a.Id == row.Id).Select(a => a.Status).FirstAsync(ct);
        return settled switch { "paid" => SavedCardPayResult.Paid, "pending" => SavedCardPayResult.Pending, _ => SavedCardPayResult.Failed };
    }
}
