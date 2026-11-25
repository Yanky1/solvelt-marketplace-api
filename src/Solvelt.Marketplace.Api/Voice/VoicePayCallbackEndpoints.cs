using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The three webhooks of a pay callback (decision V-60): what SignalWire fetches when the person answers, the answer to the question, and the call's end. Each one is refused unless SignalWire's own signature is
/// right AND the signed callback token in the query is genuine, unexpired and names a callback in the right state; nothing here is believed on SignalWire's word alone. A person who is not confirmed (a machine, a wrong
/// number, no clear yes) hears one neutral line and nothing about any order. The card is taken by the same <c>&lt;Pay&gt;</c> step and the same connector as on an inbound call: no card data ever reaches this API.
/// </summary>
public static class VoicePayCallbackEndpoints
{
    public static IEndpointRouteBuilder MapPayCallback(this IEndpointRouteBuilder app)
    {
        app.MapPost(PayCallbackService.AnswerPath, Answer);
        app.MapPost(PayCallbackService.ConfirmPath, Confirm);
        app.MapPost(PayCallbackService.StatusPath, Status);
        return app;
    }

    private static IResult Neutral() => VoiceEndpoints.Cxml(new XElement("Response", new XElement("Say", VoiceScripts.CallbackNeutral), new XElement("Hangup")));
    private static IResult SayAndHangUp(string say) => VoiceEndpoints.Cxml(new XElement("Response", new XElement("Say", say), new XElement("Hangup")));

    private sealed record Opened(VoicePayCallback Row, IReadOnlyDictionary<string, string> Form, string Token);

    /// <summary>The checks every one of the three share. A refusal is returned as the answer; nothing is said about why.</summary>
    private static async Task<(Opened? Ok, IResult? Refusal)> OpenAsync(HttpContext ctx, VoiceOptions v, SignalWireOptions s, PayCallbackTokens tokens, PayCallbackService svc, TimeProvider time, CancellationToken ct)
    {
        if (!v.Enabled || !v.PayCallback.Enabled) return (null, Results.NotFound());
        var (ok, form) = await VoiceEndpoints.VerifyAsync(ctx, v, s);
        if (!ok) return (null, Results.StatusCode(403));
        var token = ctx.Request.Query["t"].ToString();
        if (tokens.Validate(token, time.GetUtcNow()) is not { } id) return (null, Results.StatusCode(403));
        var row = await svc.FindAsync(id, ct);
        return row is null ? (null, Neutral()) : (new Opened(row, form, token), null);
    }

    private static string Url(VoiceOptions v, string path, string token) => v.PublicBaseUrl!.TrimEnd('/') + path + "?t=" + Uri.EscapeDataString(token);

    private static XElement Ask(VoiceOptions v, string token, string say)
        => new("Gather", new XAttribute("input", "speech"), new XAttribute("language", v.SignalWireSpeech.Language), new XAttribute("timeout", 8), new XAttribute("speechTimeout", "auto"), new XAttribute("hints", "yes, no"),
            new XAttribute("actionOnEmptyResult", "true"), new XAttribute("method", "POST"), new XAttribute("action", Url(v, PayCallbackService.ConfirmPath, token)), new XElement("Say", say));

    /// <summary>The person picked up. Direction and the number called are checked against the callback; the callback moves to answered exactly once.</summary>
    private static async Task<IResult> Answer(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, PayCallbackTokens tokens, PayCallbackService svc, VoiceCallStore store, VoiceStateStore states, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var v = vo.Value;
        var (opened, refusal) = await OpenAsync(ctx, v, so.Value, tokens, svc, time, ct);
        if (refusal is not null) return refusal;
        var (row, form, token) = (opened!.Row, opened.Form, opened.Token);
        if (row.Status != PayCallbackStatus.Dialing) return Neutral();   // a repeat of the fetch, or a callback that is no longer waiting: nothing starts twice
        if (!form.TryGetValue("CallSid", out var sid) || string.IsNullOrWhiteSpace(sid) || sid.Length > 64 || (row.CallSid is not null && row.CallSid != sid)) return Neutral();
        form.TryGetValue("Direction", out var direction); form.TryGetValue("To", out var to); form.TryGetValue("AnsweredBy", out var answeredBy);
        if (!string.Equals(direction, "outbound-api", StringComparison.OrdinalIgnoreCase)) return Neutral();   // only a call WE launched through the API
        if (time.GetUtcNow() > row.ExpiresAt) { await svc.MoveAsync(row.Id, [PayCallbackStatus.Dialing], "expired", "time_ran_out", ct); return Neutral(); }
        if (PhoneNumbers.ToE164(to) != row.Phone) { await svc.MoveAsync(row.Id, [PayCallbackStatus.Dialing], "wrong_number", "called_number_differs", ct); return Neutral(); }   // the order's own number is the only one that counts
        if (!await svc.MoveAsync(row.Id, [PayCallbackStatus.Dialing], PayCallbackStatus.Answered, "answered", ct, sid)) return Neutral();
        if (answeredBy?.Contains("machine", StringComparison.OrdinalIgnoreCase) == true)
        {
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Answered], "machine", "answering_machine", ct);
            return Neutral();
        }
        var call = await store.StartAsync(sid, row.Phone, ct);
        var state = states.GetOrCreate(call.Id);
        state.SetCaller(row.Phone);
        state.SetPayCallback(row.Id);
        state.SetTask("payment");
        await db.VoicePayCallbacks.Where(c => c.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.CallId, call.Id), ct);
        return VoiceEndpoints.Cxml(new XElement("Response", Ask(v, token, VoiceScripts.CallbackAsk)));
    }

    /// <summary>The answer to "is now a good time?". Only a clear yes goes on to the payment step.</summary>
    private static async Task<IResult> Confirm(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, PayCallbackTokens tokens, PayCallbackService svc, VoiceCallStore store, VoiceStateStore states,
        PhonePayments phonePayments, PhoneOrderService orders, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var v = vo.Value;
        var (opened, refusal) = await OpenAsync(ctx, v, so.Value, tokens, svc, time, ct);
        if (refusal is not null) return refusal;
        var (row, form, token) = (opened!.Row, opened.Form, opened.Token);
        if (row.Status != PayCallbackStatus.Answered) return Neutral();
        if (!form.TryGetValue("CallSid", out var sid) || sid != row.CallSid) return Neutral();
        var call = await store.FindBySidAsync(sid, ct);
        var state = call is null || call.EndedAt is not null ? null : states.Find(call.Id);
        if (call is null || state is null || state.PayCallbackId != row.Id) return Neutral();
        if (time.GetUtcNow() > svc.GraceEnd(row)) { await svc.MoveAsync(row.Id, [PayCallbackStatus.Answered], "expired", "time_ran_out", ct); return Neutral(); }   // answered inside the window: the yes may come within the grace
        form.TryGetValue("Digits", out var typed); form.TryGetValue("SpeechResult", out var spoken);
        var answer = ReadBackAnswer.Parse(typed, spoken);
        if (answer == ConfirmChoice.Unclear)
        {
            if (state.CountConfirmFailure() < 2) return VoiceEndpoints.Cxml(new XElement("Response", Ask(v, token, VoiceScripts.CallbackRetry)));
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Answered], "unconfirmed", "no_clear_yes", ct);
            return Neutral();   // a person we could not confirm hears nothing about an order
        }
        if (answer == ConfirmChoice.No)
        {
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Answered], "declined", "said_no", ct);
            return SayAndHangUp(VoiceScripts.CallbackNotNow);
        }
        if (!await svc.MoveAsync(row.Id, [PayCallbackStatus.Answered], PayCallbackStatus.Paying, "said_yes", ct)) return Neutral();
        var order = await db.Orders.AsNoTracking().Where(o => o.Number == row.OrderNumber).Select(o => new { o.Status, o.TotalCents, o.PaymentIntentId }).FirstOrDefaultAsync(ct);
        var provider = phonePayments.Provider;
        if (order is null || order.Status != OrderStatus.PendingPayment || order.TotalCents <= 0 || order.PaymentIntentId is null)
        {
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Paying], "not_payable", "order_not_waiting", ct);
            return SayAndHangUp(VoiceScripts.CallbackNotPayable);
        }
        if (provider is null || !provider.Supports(PhonePaymentMethod.Keypad))
        {
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Paying], "failed", "no_provider", ct);
            return SayAndHangUp(VoiceScripts.CallbackTrouble);
        }
        try
        {
            var attemptRow = await orders.OpenAttemptAsync(call.Id, row.OrderNumber, order.TotalCents, PhonePaymentMethod.Keypad, ct);
            var attemptId = attemptRow.Id.ToString("N");
            var start = await provider.StartAsync(new PhonePaymentRequest(attemptId, row.OrderNumber, order.TotalCents, "USD", PhonePaymentMethod.Keypad, row.Phone), v.PublicBaseUrl!.TrimEnd('/') + VoiceEndpoints.KeypadPath, ct);
            if (start.Instruction is null) throw new InvalidOperationException("The provider gave no keypad step.");
            state.SetPayment(new PaymentAttempt { AttemptId = attemptId, OrderNumber = row.OrderNumber, IntentId = order.PaymentIntentId, AmountCents = order.TotalCents, Method = PhonePaymentMethod.Keypad, Instruction = start.Instruction });
            state.BeginKeypadStep("pay");
            await db.VoicePayCallbacks.Where(c => c.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.AttemptId, attemptRow.Id), ct);
            return VoiceEndpoints.Cxml(new XElement("Response", new XElement("Say", VoiceScripts.CallbackPayIntro(order.TotalCents)), new XElement(start.Instruction)));   // the provider's own step, as on an inbound call
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await svc.MoveAsync(row.Id, [PayCallbackStatus.Paying], "failed", "payment_step_not_started", ct);
            return SayAndHangUp(VoiceScripts.CallbackTrouble);
        }
    }

    /// <summary>The call ended (the <c>completed</c> event, the default). The callback gets its final word: paid when the ORDER is paid, otherwise what happened to the call.</summary>
    private static async Task<IResult> Status(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, PayCallbackTokens tokens, PayCallbackService svc, VoiceCallStore store, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        var v = vo.Value;
        var (opened, refusal) = await OpenAsync(ctx, v, so.Value, tokens, svc, time, ct);
        if (refusal is not null) return refusal;
        var (row, form) = (opened!.Row, opened.Form);
        if (form.TryGetValue("CallSid", out var sid) && row.CallSid is not null && sid != row.CallSid) return Results.Ok();
        if (!PayCallbackStatus.IsActive(row.Status)) return Results.Ok();
        form.TryGetValue("CallStatus", out var callStatus);
        var paid = await db.Orders.AsNoTracking().AnyAsync(o => o.Number == row.OrderNumber && o.Status == OrderStatus.Paid, ct);
        string status, why;
        if (paid) { status = "paid"; why = "order_is_paid"; }
        else if (row.Status is PayCallbackStatus.Scheduled or PayCallbackStatus.Dialing)
            (status, why) = callStatus switch { "busy" => ("busy", "line_busy"), "no-answer" => ("no_answer", "nobody_answered"), "failed" or "canceled" => ("failed", "call_failed"), _ => ("no_answer", "ended_before_answer") };
        else if (row.Status == PayCallbackStatus.Answered) (status, why) = ("unconfirmed", "hung_up_before_yes");
        else (status, why) = ("ended_unpaid", "call_ended_without_payment");
        await svc.MoveAsync(row.Id, PayCallbackStatus.Active, status, why, ct);
        if (row.CallId is { } callId) await store.EndAsync(callId, paid ? VoiceOutcome.OrderPlaced : VoiceOutcome.Abandoned, CancellationToken.None);
        return Results.Ok();
    }
}
