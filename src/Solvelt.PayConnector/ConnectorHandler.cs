using System.Globalization;

namespace Solvelt.PayConnector;

/// <summary>The answer to SignalWire: an HTTP status and, for a business result, the body SignalWire documents (<c>charge_id</c>, <c>error_code</c>, <c>error_message</c>). Messages are fixed words of ours, never Sola's text.</summary>
public sealed record ConnectorReply(int Status, string? ChargeId, string? ErrorCode, string? ErrorMessage)
{
    public static ConnectorReply Paid(string chargeId) => new(200, chargeId, null, null);
    public static ConnectorReply Refused(string code, string message) => new(200, null, code, message);
    public static ConnectorReply Http(int status) => new(status, null, null, null);
}

/// <summary>
/// One card payment through the connector, in this order, and each step fails closed: https only; body size; the Basic credentials; the SignalWire signature; a strict parse and the card checks;
/// what to charge from the MARKETPLACE API's server state (the amount in the request must equal it, never the other way round); the per-attempt and amount limits; the ledger (idempotent on the
/// <c>transaction_id</c>, one paid charge per attempt); ONE sale at Sola (never retried); then the result is reported to the marketplace API and answered to SignalWire. Nothing the caller typed
/// except the card digits matters, and those go nowhere but into the one request to Sola.
/// </summary>
public sealed class ConnectorHandler(Microsoft.Extensions.Options.IOptions<ConnectorOptions> options, IMarketplaceApi api, ISolaGateway sola, ChargeLedger ledger, TimeProvider time, ILogger<ConnectorHandler> log)
{
    public const int MaxBodyBytes = 4096;

    public async Task<ConnectorReply> HandleAsync(bool isHttps, string? authorization, string? signature, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var o = options.Value;
        if (o.RequireHttps && !isHttps) return ConnectorReply.Http(403);
        if (body.Length is 0 or > MaxBodyBytes) return ConnectorReply.Http(400);
        // both checks run, whatever the first says: one answer, no hint which failed
        var basicOk = SignalWireRequest.BasicOk(o.BasicUser, o.BasicPassword, authorization);
        var signatureOk = SignalWireRequest.SignatureOk(o.SignalWireSigningKey, o.PublicUrl!, body.Span, signature);
        if (!basicOk || !signatureOk) { log.LogWarning("A connector request failed authentication and was refused."); return ConnectorReply.Http(401); }

        using var card = ConnectorRequest.TryParse(body.Span);
        if (card is null) { log.LogWarning("A connector request was malformed and was refused."); return ConnectorReply.Http(400); }

        // what to charge comes from the marketplace API's server state; SignalWire's chargeAmount (OUR <Pay> attribute) must equal it
        var expect = await api.ExpectAsync(card.AttemptId, ct);
        if (expect is null) return ConnectorReply.Refused("attempt_not_open", "This payment cannot be taken.");
        if (card.ChargeAmount != SolaGateway.Dollars(expect.AmountCents) || !string.Equals(expect.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("Connector request for attempt {Attempt} asked for another amount than the order's ({Asked} vs {Expected}); nothing was charged.", card.AttemptId, card.ChargeAmount, SolaGateway.Dollars(expect.AmountCents));
            return ConnectorReply.Refused("amount_mismatch", "This payment cannot be taken.");
        }
        if (expect.AmountCents < 1 || expect.AmountCents > o.MaxChargeCents)
        {
            log.LogError("Attempt {Attempt}: {Cents} cents is outside the connector's limit; nothing was charged.", card.AttemptId, expect.AmountCents);
            return ConnectorReply.Refused("over_limit", "This payment cannot be taken.");
        }

        var begin = ledger.Begin(card.TransactionId, card.AttemptId, expect.Invoice, expect.AmountCents, o.MaxRequestsPerAttempt, time.GetUtcNow());
        switch (begin.Kind)
        {
            case BeginKind.Replay: return Replay(begin.Row!);
            case BeginKind.AttemptAlreadyPaid: return ConnectorReply.Refused("already_paid", "This payment was already taken.");
            case BeginKind.TooManyRequests: return ConnectorReply.Refused("too_many_attempts", "Too many tries for this payment.");
        }

        var result = await sola.SaleAsync(new SolaSale(expect.Invoice, expect.AmountCents, card.PostalCode), card, ct);
        string state; ConnectorReply reply; string status;
        switch (result.Outcome)
        {
            case SolaOutcome.Approved when result.AuthorisedCents == expect.AmountCents:
                state = "approved"; status = "paid"; reply = ConnectorReply.Paid(result.RefNum!); break;
            case SolaOutcome.Approved:
                // the gateway authorised another amount than asked for: money moved that does not match the order. Reported as it was (the API records a mismatch for a person); SignalWire is not told "paid".
                log.LogError("Attempt {Attempt}: Sola authorised {Authorised} cents where {Expected} were asked (reference {Ref}). It needs a person.", card.AttemptId, result.AuthorisedCents, expect.AmountCents, result.RefNum);
                state = "unknown"; status = "paid"; reply = ConnectorReply.Refused("amount_mismatch", "This payment could not be completed."); break;
            case SolaOutcome.Declined:
                state = "declined"; status = "declined"; reply = ConnectorReply.Refused("declined", "The card was declined."); break;
            case SolaOutcome.Error:
                state = "error"; status = "failed"; reply = ConnectorReply.Refused("gateway_error", "The payment could not be processed."); break;
            default:
                log.LogError("Attempt {Attempt}: the answer to the sale is unknown ({Code}); the card may have been charged. It needs a person (transaction {Tx}).", card.AttemptId, result.ErrorCode, card.TransactionId);
                state = "unknown"; status = "unknown"; reply = ConnectorReply.Refused("gateway_unknown", "The payment could not be confirmed."); break;
        }
        ledger.Finish(card.TransactionId, state, result.RefNum, result.ErrorCode, state is "approved" or "unknown" && result.AuthorisedCents is { } a ? a : null);
        var report = new ChargeReport(card.AttemptId, expect.Invoice, status, result.AuthorisedCents ?? 0, result.RefNum, result.ErrorCode);
        if (await ReportWithRetriesAsync(report, ct)) ledger.MarkReported(card.TransactionId);   // otherwise the background service keeps trying: the result is not lost
        return reply;
    }

    private async Task<bool> ReportWithRetriesAsync(ChargeReport report, CancellationToken ct)
    {
        for (var i = 0; i < 3; i++)
        {
            if (await api.ReportAsync(report, ct)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (i + 1)), time, ct);
        }
        return false;
    }

    /// <summary>A repeated <c>transaction_id</c>: the stored answer, never a second sale.</summary>
    private static ConnectorReply Replay(LedgerRow row) => row.State switch
    {
        "approved" => ConnectorReply.Paid(row.ChargeId ?? ""),
        "declined" => ConnectorReply.Refused("declined", "The card was declined."),
        "processing" => ConnectorReply.Refused("in_progress", "This payment is being processed."),
        "error" => ConnectorReply.Refused("gateway_error", "The payment could not be processed."),
        _ => ConnectorReply.Refused("gateway_unknown", "The payment could not be confirmed."),
    };

    /// <summary>The report of a finished ledger row, for the background retry.</summary>
    public static ChargeReport ReportOf(LedgerRow r) => new(r.AttemptId, r.Invoice, r.State switch { "approved" => "paid", "declined" => "declined", "error" => "failed", _ => r.ChargeId is not null ? "paid" : "unknown" }, r.State is "approved" or "unknown" ? r.Cents : 0, r.ChargeId, r.ErrorCode);
}

/// <summary>Keeps trying to tell the marketplace API about results it has not received (it was down at the time): a charge is never left unrecorded.</summary>
public sealed class ReportRetryService(ChargeLedger ledger, IMarketplaceApi api, ILogger<ReportRetryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var row in ledger.Unreported())
                    if (await api.ReportAsync(ConnectorHandler.ReportOf(row), stop)) ledger.MarkReported(row.TransactionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Reporting results to the marketplace API failed ({Type}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { }
        }
    }
}
