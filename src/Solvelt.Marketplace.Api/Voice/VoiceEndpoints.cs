using System.Net.WebSockets;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

public static class VoiceEndpoints
{
    public const string InboundPath = "/api/v1/voice/signalwire/inbound", AfterStreamPath = "/api/v1/voice/signalwire/after-stream", KeypadPath = "/api/v1/voice/signalwire/keypad", SolaWebhookPath = "/api/v1/voice/sola/webhook", StreamPath = "/api/v1/voice/stream";

    public static IEndpointRouteBuilder MapVoice(this IEndpointRouteBuilder app)
    {
        app.MapPost(InboundPath, Inbound);
        app.MapPost(AfterStreamPath, AfterStream);
        app.MapPost(KeypadPath, Keypad);
        app.MapPost(SolaWebhookPath, SolaWebhook);
        app.MapPhoneEmail();
        app.MapPayConnector();
        app.Map(StreamPath, Stream);
        return app;
    }

    private static IResult Cxml(XElement response) => Results.Content(new XDocument(new XDeclaration("1.0", "UTF-8", null), response).ToString(), "text/xml");

    /// <summary>The signed URL is built from configuration, never from request headers (a forwarded Host must not decide what was signed).</summary>
    private static async Task<(bool Ok, IReadOnlyDictionary<string, string> Form)> VerifyAsync(HttpContext ctx, VoiceOptions v, SignalWireOptions s)
    {
        if (!ctx.Request.HasFormContentType) return (false, new Dictionary<string, string>());
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var fields = form.ToDictionary(k => k.Key, k => k.Value.ToString());
        var url = v.PublicBaseUrl!.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString;
        return (SignalWireSignature.Verify(s.SigningKey ?? "", url, fields, ctx.Request.Headers[SignalWireSignature.Header].ToString()), fields);
    }

    /// <summary>cXML that connects (or reconnects) the call to a stream: a fresh single-use token in the URL and as the bearer header, and a Redirect that runs when the stream ends.</summary>
    private static XElement[] StreamParts(VoiceOptions v, StreamTokens tokens, Guid callId)
    {
        var token = tokens.Issue(callId);
        var afterUrl = v.PublicBaseUrl!.TrimEnd('/') + AfterStreamPath;
        var wss = new UriBuilder(v.PublicBaseUrl!) { Scheme = new Uri(v.PublicBaseUrl!).Scheme == "https" ? "wss" : "ws", Path = StreamPath, Query = "token=" + Uri.EscapeDataString(token) };
        // authBearerToken: SignalWire sends it as "Authorization: Bearer" on the upgrade (documented); the same token is also in the URL. The Connect page documents no "action"
        // attribute (V-01), so the Redirect after the stream ends is what moves the call on.
        return [new XElement("Connect", new XElement("Stream", new XAttribute("url", wss.Uri.ToString()), new XAttribute("authBearerToken", token))), new XElement("Redirect", afterUrl)];
    }

    private static async Task<IResult> Inbound(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, VoiceStateStore states, StreamTokens tokens, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled) return Results.NotFound();
        var (ok, form) = await VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        if (!form.TryGetValue("CallSid", out var sid) || string.IsNullOrWhiteSpace(sid) || sid.Length > 64) return Results.BadRequest();
        form.TryGetValue("From", out var from);
        var call = await store.StartAsync(sid, from, ct);
        states.GetOrCreate(call.Id).SetCaller(PhoneNumbers.ToE164(from));   // the full number lives only in this call's memory
        return Cxml(new XElement("Response", new XElement("Say", VoiceScripts.Disclosure), StreamParts(v, tokens, call.Id)));
    }

    /// <summary>SignalWire calls this when the stream ends: a person was asked for and is reachable, so dial; otherwise hang up.</summary>
    private static async Task<IResult> AfterStream(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, VoiceStateStore states, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled) return Results.NotFound();
        var (ok, form) = await VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        var call = form.TryGetValue("CallSid", out var sid) ? await store.FindBySidAsync(sid, ct) : null;
        // the off-topic limit ended the AI part: the PLATFORM says the line (the model never does), then a person if one is reachable, otherwise goodbye (V2, V-11)
        if (call is not null && (call.Outcome == Data.VoiceOutcome.EndedOffTopic || states.Find(call.Id)?.OffTopicEnded == true))
            return Cxml(call.TransferRequestedAt is not null && !string.IsNullOrWhiteSpace(v.SupportNumber)
                ? new XElement("Response", new XElement("Say", VoiceScripts.OffTopicTransfer), new XElement("Dial", v.SupportNumber))
                : new XElement("Response", new XElement("Say", VoiceScripts.OffTopicGoodbye), new XElement("Hangup")));
        if (call?.TransferRequestedAt is not null && !string.IsNullOrWhiteSpace(v.SupportNumber))
            return Cxml(new XElement("Response", new XElement("Say", VoiceScripts.Connecting), new XElement("Dial", v.SupportNumber)));
        if (call is not null && states.Find(call.Id) is { } st)
        {
            if (st.KeypadStep == "sms_code") return Cxml(new XElement("Response", GatherCode(v, VoiceScripts.AskCode)));
            // the server speaks ITS OWN read-back (built from the fresh quote) and listens for yes / 1: the model is not on the line
            if (st.KeypadStep == "confirm" && st.ReadBackText is { } text) return Cxml(new XElement("Response", GatherConfirm(v, text)));
            // the payment provider's own instruction (the keypad step runs without the model; no card data touches this API or the model)
            if (st.KeypadStep == "confirm_cancel" && st.Cancel is { } cancel) return Cxml(new XElement("Response", GatherConfirm(v, cancel.Text)));
            if (st.KeypadStep == "confirm_account" && st.AccountChange is { } change) return Cxml(new XElement("Response", GatherConfirm(v, change.Text)));
            if (st.KeypadStep == "pay" && st.Payment?.Instruction is { } pay) return Cxml(new XElement("Response", new XElement(pay)));
        }
        return Cxml(new XElement("Response", new XElement("Hangup")));
    }

    /// <summary>The caller types the six-digit code on the keypad; SignalWire, not the model, collects the digits and posts them to us.</summary>
    private static XElement GatherCode(VoiceOptions v, string prompt)
        => new("Gather", new XAttribute("input", "dtmf"), new XAttribute("numDigits", PhoneCodeService.Digits), new XAttribute("timeout", 20), new XAttribute("finishOnKey", "#"),
            new XAttribute("actionOnEmptyResult", "true"), new XAttribute("method", "POST"), new XAttribute("action", v.PublicBaseUrl!.TrimEnd('/') + KeypadPath), new XElement("Say", prompt));

    private static XElement GatherConfirm(VoiceOptions v, string say)
        => new("Gather", new XAttribute("input", "dtmf speech"), new XAttribute("numDigits", 1), new XAttribute("timeout", 8), new XAttribute("speechTimeout", "auto"), new XAttribute("hints", "yes, no"),
            new XAttribute("actionOnEmptyResult", "true"), new XAttribute("method", "POST"), new XAttribute("action", v.PublicBaseUrl!.TrimEnd('/') + KeypadPath), new XElement("Say", say));

    /// <summary>The digits typed for an SMS code. Checked against the hash held for THIS call; never stored, logged or given to the model.</summary>
    /// <summary>
    /// Sola's account webhook for a payment made on the PaymentSITE page the caller was texted (method B). Signed with the webhook PIN over EVERY received parameter (<c>ck-signature</c>); an unsigned
    /// or wrongly signed notification is a 401 and nothing else happens. The body is never logged (it can hold a masked card number and a name). The invoice names our order; the amount, invoice and
    /// result are compared with the attempt the SERVER opened, so a customer who edited the amount in the link pays nothing we accept (V-07). Duplicates (<c>xRefNum</c>) and notifications for
    /// invoices we never asked about are acknowledged and ignored: Sola sends each one once and waits for any 2xx.
    /// </summary>
    private static async Task<IResult> SolaWebhook(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SolaOptions> sola, VoiceStateStore states, VoiceCallStore store, PhoneOrderService orders,
        Data.MarketplaceDb db, ISmsSender sms, CancellationToken ct)
    {
        if (!vo.Value.Enabled || vo.Value.PhonePaymentProvider != "sola") return Results.NotFound();
        if (!ctx.Request.HasFormContentType || ctx.Request.ContentLength is > 64 * 1024) return Results.StatusCode(400);
        var form = await ctx.Request.ReadFormAsync(ct);
        var pairs = form.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? ""))).ToList();
        if (!SolaSignature.Verify(sola.Value.WebhookPin, pairs, ctx.Request.Headers["ck-signature"].ToString())) return Results.StatusCode(401);
        var parsed = SolaNotification.Parse(pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase));
        if (parsed is null) return Results.Ok();
        var attempt = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.VoicePaymentAttempts.Where(a => a.OrderNumber == parsed.Invoice && a.Method == PhonePaymentMethod.TextLink && (a.Status == "pending" || a.Status == "paid")).OrderBy(a => a.Status == "pending" ? 0 : 1).ThenByDescending(a => a.CreatedAt), ct);   // a paid one too: a second sale for it is a duplicate charge that must be recorded
        if (attempt is null) return Results.Ok();
        var settled = await orders.SettleAsync(attempt.Id, parsed.Outcome, ct);
        if (settled == SettleStatus.Paid) await PhonePaymentCompletion.AfterPaidAsync(attempt, store, states, sms, ct);
        return Results.Ok();
    }

    private static async Task<IResult> Keypad(HttpContext ctx, IOptions<VoiceOptions> vo, IOptions<SignalWireOptions> so, VoiceCallStore store, VoiceStateStore states, PhoneCodeService codes,
        StreamTokens tokens, IServiceScopeFactory scopes, PhonePayments phonePayments, PhoneOrderService orders, ISmsSender sms, TimeProvider time, CancellationToken ct)
    {
        var v = vo.Value;
        if (!v.Enabled) return Results.NotFound();
        var (ok, form) = await VerifyAsync(ctx, v, so.Value);
        if (!ok) return Results.StatusCode(403);
        var call = form.TryGetValue("CallSid", out var sid) ? await store.FindBySidAsync(sid, ct) : null;
        var state = call is null || call.EndedAt is not null ? null : states.Find(call.Id);
        if (call is null || state is not { KeypadStep: "sms_code" or "confirm" or "pay" or "confirm_cancel" or "confirm_account" }) return Cxml(new XElement("Response", new XElement("Hangup")));
        form.TryGetValue("Digits", out var typed);
        if (state.KeypadStep == "confirm_cancel")
        {
            form.TryGetValue("SpeechResult", out var said);
            var answer = ReadBackAnswer.Parse(typed, said);
            if (answer == ConfirmChoice.Unclear && state.CountConfirmFailure() < 2) return Cxml(new XElement("Response", GatherConfirm(v, VoiceScripts.CancelRetry)));
            string outcome = "cancel_declined", say = VoiceScripts.CancelKept;
            if (answer == ConfirmChoice.Yes && state.Cancel is { } pending && state.AccountId is { } accountId)
            {
                // the ONLY path that sends a cancellation: the caller heard the server's read-back and said yes (or pressed 1); the existing buyer action applies its own rules
                var actions = ctx.RequestServices.GetRequiredService<Solvelt.Marketplace.Api.Fulfilment.BuyerOrderActions>();
                var principal = new Solvelt.Marketplace.Api.Auth.BuyerPrincipal(accountId, Guid.Empty, "", "", state.VerifiedAt ?? time.GetUtcNow());
                var cancelled = await actions.CancelAsync(principal, pending.OrderNumber, pending.SellerOrderNumber, pending.Reason, null, ct);
                var sent = cancelled.Status == 202;
                outcome = sent ? "cancel_sent" : "cancel_refused";
                say = sent ? VoiceScripts.CancelSent : VoiceScripts.CancelRefused;
            }
            state.SetCancel(null);
            state.SetKeypadOutcome(outcome);
            state.EndKeypadStep();
            return Cxml(new XElement("Response", new XElement("Say", say), StreamParts(v, tokens, call.Id)));
        }
        if (state.KeypadStep == "confirm_account")
        {
            form.TryGetValue("SpeechResult", out var heard);
            var reply = ReadBackAnswer.Parse(typed, heard);
            if (reply == ConfirmChoice.Unclear && state.CountConfirmFailure() < 2) return Cxml(new XElement("Response", GatherConfirm(v, VoiceScripts.AccountRetry)));
            string accountResult = "account_declined", line = VoiceScripts.AccountKept;
            if (reply == ConfirmChoice.Yes && state.AccountChange is { } pendingChange)
            {
                // the ONLY path that changes an account: the caller heard the server's read-back and said yes (or pressed 1); the existing account services apply their own rules
                using var scope = ctx.RequestServices.CreateScope();
                var applied = await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().ApplyAsync(state, pendingChange, ct);
                if (applied != AccountApply.Refused)
                {
                    var created = applied is AccountApply.Created or AccountApply.CreatedWithEmail or AccountApply.CreatedEmailNotSaved;
                    accountResult = created ? "account_created" : "account_changed";
                    line = applied switch { AccountApply.CreatedWithEmail => VoiceScripts.AccountCreatedEmail, AccountApply.CreatedEmailNotSaved => VoiceScripts.AccountCreatedNoEmail, AccountApply.EmailSent => VoiceScripts.EmailSent, AccountApply.Created => VoiceScripts.AccountCreated, _ => VoiceScripts.AccountSaved };
                    if (pendingChange.Kind is "phone" or "create" && state.VerifiedPhone is { } nowVerified) await store.SetVerifiedKeyAsync(call.Id, store.CallerKeyOf(nowVerified), ct);
                }
                else { accountResult = "account_refused"; line = VoiceScripts.AccountRefused; }
            }
            state.SetAccountChange(null);
            state.SetKeypadOutcome(accountResult);
            state.EndKeypadStep();
            return Cxml(new XElement("Response", new XElement("Say", line), StreamParts(v, tokens, call.Id)));
        }
        if (state.KeypadStep == "pay")
        {
            var attempt = state.Payment;
            var provider = phonePayments.Provider;
            var paid = false; var pending = false;
            var settledAlready = attempt is { Status: "paid" };   // the connector's signed report came first and PhonePaymentCompletion did everything (order placed, text sent)
            if (settledAlready) paid = true;
            else if (attempt is { Status: "pending" } && provider is not null)
            {
                var outcome = await provider.CompleteKeypadAsync(attempt.AttemptId, form, ct);
                // believed only after the server compares the provider's amount and invoice with its own order (and it is recorded once, whatever repeats)
                var settled = await orders.SettleAsync(Guid.ParseExact(attempt.AttemptId, "N"), outcome, ct);
                paid = settled is SettleStatus.Paid or SettleStatus.AlreadyPaid;
                pending = settled == SettleStatus.Pending;   // the connector's signed report is not in yet: the attempt stays open and a late report still places the order
                attempt.Status = paid ? "paid" : pending ? "pending" : settled == SettleStatus.Declined ? "declined" : "failed";
            }
            if (paid && !settledAlready)
            {
                state.SetOrderPlaced(attempt!.OrderNumber);
                await store.MarkOrderPlacedAsync(call.Id, ct);
                if ((state.VerifiedPhone ?? state.CallerNumber) is { } to) await sms.SendAsync(to, $"Solvelt: your order {attempt.OrderNumber} is placed and paid ({attempt.AmountCents / 100}.{attempt.AmountCents % 100:D2} USD). We will email you updates.", ct);
            }
            state.SetKeypadOutcome(paid ? "paid" : pending ? "pay_pending" : "pay_failed");
            state.EndKeypadStep();
            return Cxml(new XElement("Response", new XElement("Say", paid ? VoiceScripts.PayThanks : pending ? VoiceScripts.PayPending : VoiceScripts.PayFailed), StreamParts(v, tokens, call.Id)));
        }
        if (state.KeypadStep == "confirm")
        {
            form.TryGetValue("SpeechResult", out var spoken);
            var answer = ReadBackAnswer.Parse(typed, spoken);
            if (answer == ConfirmChoice.Unclear && state.CountConfirmFailure() < 2) return Cxml(new XElement("Response", GatherConfirm(v, VoiceScripts.ConfirmRetry)));
            var yes = answer == ConfirmChoice.Yes && state.ConfirmReadBack();
            state.SetKeypadOutcome(yes ? "confirmed" : answer == ConfirmChoice.Unclear ? "unclear" : "change_requested");
            state.EndKeypadStep();
            state.SetTask(yes ? "confirmed" : "order");
            return Cxml(new XElement("Response", new XElement("Say", yes ? VoiceScripts.ConfirmThanks : VoiceScripts.ConfirmChange), StreamParts(v, tokens, call.Id)));
        }
        var result = codes.Verify(state, typed);
        if (result == CodeResult.Wrong) return Cxml(new XElement("Response", GatherCode(v, VoiceScripts.WrongCode)));
        var challenge = state.Challenge;
        if (state.LastChallengePurpose == "phone_change")
        {
            // a code for a NEW number the signed-in account wants: it proves the number, it never changes who the caller is. Then the server reads the change back and waits for the yes.
            if (result == CodeResult.Ok && state.AccountChange is { Kind: "phone" } phoneChange && state.LastChallengePhone == phoneChange.Phone)
            {
                state.SetAccountChange(phoneChange with { PhoneProved = true });
                state.BeginKeypadStep("confirm_account");
                return Cxml(new XElement("Response", GatherConfirm(v, phoneChange.Text)));
            }
            state.SetAccountChange(null);
            state.SetKeypadOutcome("account_refused");
            state.SetChallenge(null);
            state.EndKeypadStep();
            return Cxml(new XElement("Response", new XElement("Say", VoiceScripts.CodeFailed), StreamParts(v, tokens, call.Id)));
        }
        if (result == CodeResult.Ok) await ApplyVerifiedAsync(state, scopes, store, call.Id, time, ct);
        else { state.SetKeypadOutcome("failed"); state.SetChallenge(null); }
        state.EndKeypadStep();
        return Cxml(new XElement("Response", new XElement("Say", result == CodeResult.Ok ? VoiceScripts.CodeOk : VoiceScripts.CodeFailed), StreamParts(v, tokens, call.Id)));
    }

    /// <summary>A right code proves the caller holds the number: the matching account (if exactly one) is now theirs for this call, otherwise they are a guest with a verified phone. A guest cart moves into the account.</summary>
    private static async Task ApplyVerifiedAsync(VoiceCallState state, IServiceScopeFactory scopes, VoiceCallStore store, Guid callId, TimeProvider time, CancellationToken ct)
    {
        var phone = state.LastChallengePhone;
        state.SetVerifiedPhone(phone);
        state.SetVerifiedAt(time.GetUtcNow());
        await store.SetVerifiedKeyAsync(callId, phone is null ? null : store.CallerKeyOf(phone), ct);
        state.SetKeypadOutcome("verified");
        if (state.LastChallengeAccount is { } accountId)
        {
            using var scope = scopes.CreateScope();
            var cart = scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Carts.CartService>();
            var db = scope.ServiceProvider.GetRequiredService<Data.MarketplaceDb>();
            var name = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.BuyerAccounts).Where(a => a.Id == accountId).Select(a => a.DisplayName), ct);
            if (state.CartToken is { } guestToken) await cart.MergeGuestAsync(accountId, guestToken, ct);   // the cart carries over into the account
            state.SetCartToken(null);
            state.Identify(CallerLevel.Verified, accountId, verifiedNow: true);
            state.SetFirstName(name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            state.SetCart("cart", (await cart.GetAsync(new Solvelt.Marketplace.Api.Carts.CartOwner(accountId, null), ct)).ItemCount);
        }
        else state.Identify(CallerLevel.Guest, null, verifiedNow: true);
        state.SetTask("shopping");
    }

    private static async Task Stream(HttpContext ctx, IOptions<VoiceOptions> vo, StreamTokens tokens, VoiceCallStore store, VoiceStateStore states, IRealtimeConnector connector, VoiceToolRunner tools,
        TimeProvider time, ILoggerFactory logs)
    {
        if (!vo.Value.Enabled) { ctx.Response.StatusCode = 404; return; }
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var auth = ctx.Request.Headers.Authorization.ToString();
        var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        var id = tokens.Use(!string.IsNullOrEmpty(bearer) ? bearer : ctx.Request.Query["token"].ToString());
        var call = id is { } g ? await store.FindAsync(g, ctx.RequestAborted) : null;
        if (call is null || call.EndedAt is not null) { ctx.Response.StatusCode = 401; return; }
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice");
        WebSocket upstream;
        try { upstream = await connector.ConnectAsync(call.Model, ctx.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Voice call {Call}: the model session could not be opened ({Type}).", call.Id, ex.GetType().Name);
            await store.EndAsync(call.Id, Data.VoiceOutcome.Abandoned, CancellationToken.None);
            ctx.Response.StatusCode = 503;
            return;
        }
        using var phone = await ctx.WebSockets.AcceptWebSocketAsync();
        log.LogInformation("Voice call {Call} started: caller {Caller}, model tier {Tier}.", call.Id, call.CallerMasked, call.Tier);
        var state = states.GetOrCreate(call.Id);
        state.Connected(time.GetUtcNow());
        var bridge = new RealtimeBridge(phone, upstream, call, state, vo, store, tools, time, log);
        await bridge.RunAsync(ctx.RequestAborted);
        if (!bridge.KeypadPause && !state.OffTopicEnded) states.Remove(call.Id);   // the call is over: nothing of it stays in memory (the flag of an off-topic end stays until the grace period, for the after-stream webhook)
        upstream.Dispose();
    }
}
