using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;

namespace Solvelt.Marketplace.Api.Voice;

public sealed class SetGuestEmailTool : IVoiceTool
{
    public string Name => "set_guest_email";
    public JsonObject Definition => ToolKit.Def(Name, "Take a guest's email address as the caller spelled it, for the order link. Read it back letter by letter, then call confirm_guest_email when they agree.",
        new JsonObject { ["email"] = ToolKit.Prop("string", "The address as the caller spelled it.") }, "email");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.State.Level == CallerLevel.Verified) return Task.FromResult(ToolKit.Error("not_needed", "An account's own email is used: no need to ask."));
        var email = ToolKit.Str(args, "email") is { } raw ? AuthValidation.NormaliseEmail(raw) : null;
        if (AuthValidation.EmailProblem(email) is not null) return Task.FromResult(ToolKit.Error("bad_email", "That does not sound like an email address: ask them to spell it again, slowly."));
        ctx.State.SetGuestEmailDraft(email);
        return Task.FromResult(ToolKit.Json(new JsonObject { ["status"] = "read_back_needed", ["read_back"] = VoiceSpeech.Email(email!), ["instruction"] = "Read it back exactly as in read_back and ask if it is right. If they correct it, call set_guest_email again; when they agree, call confirm_guest_email." }));
    }
}

public sealed class ConfirmGuestEmailTool : IVoiceTool
{
    public string Name => "confirm_guest_email";
    public JsonObject Definition => ToolKit.Def(Name, "The caller agreed that the email address you read back is right.");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
        => Task.FromResult(ctx.State.ConfirmGuestEmail()
            ? ToolKit.Json(new JsonObject { ["status"] = "confirmed", ["instruction"] = "Say that is noted." })
            : ToolKit.Error("no_email", "There is no address to confirm: ask for it first."));
}

/// <summary>
/// Pays for the order the caller confirmed. The model chooses nothing but the method: the server places the order through the existing checkout at the amount it priced and read back, and the card
/// is entered by the caller on the keypad into the payment provider's own path while the model is off the line (or on the provider's page, from a text link). The model never sees a card.
/// </summary>
public sealed class PayForOrderTool(PhoneOrderService orders, PhonePayments payments, VoiceCallStore calls, ISmsSender sms, IOptions<VoiceOptions> options, Solvelt.Marketplace.Api.Data.MarketplaceDb db) : IVoiceTool
{
    public string Name => "pay_for_order";
    public JsonObject Definition => ToolKit.Def(Name, "After the caller confirmed the read-back: place the order and start the payment. Say one short sentence that the payment step is starting, then stop talking.",
        new JsonObject { ["method"] = ToolKit.Prop("string", "keypad (the caller types the card on the phone keypad, the default), text_link (a link by text message) or saved_card (one of their saved cards, for a verified account: read the list with list_saved_cards first and pass its position)."), ["position"] = ToolKit.Prop("integer", "For saved_card: the position in the list just read out.") });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        var provider = payments.Provider;
        if (provider is null) return ToolKit.Error("payments_unavailable", "Say that ordering by phone is not available yet, and offer the web site or a person.");
        var method = ToolKit.Str(args, "method") ?? PhonePaymentMethod.Keypad;
        if (method == PhonePaymentMethod.SavedCard)
        {
            // a saved card (V-16): the server reads back the card and the amount and only the caller's yes starts the charge; the model chooses nothing but the position of a card in the list the server read out
            if (provider is not ICardSavingProvider saver || !saver.CanSaveCards) return ToolKit.Error("method_unavailable", "Paying with a saved card is not available: offer the keypad or a text link.");
            if (!OrderAccess.IsAccount(s) || s.AccountId is not { } account) return ToolKit.Error("need_verification", "A saved card belongs to an account verified by text message in this call: offer identify_caller, or the keypad.");
            if (s.Confirmation != ConfirmationStatus.Confirmed || s.QuoteKey is null || s.QuoteTotalCents is not { } cents) return ToolKit.Error("not_confirmed", "The caller has not confirmed the order read-back: call read_back_order first.");
            if (s.OrderNumber is not null) return ToolKit.Error("already_placed", "The order is already placed and paid.");
            if (s.CountPaymentStart() > Math.Max(1, options.Value.MaxPaymentAttempts)) return ToolKit.Error("too_many_attempts", "Too many payment attempts in this call: offer to connect a person, or to order on the web site.");
            var pos = ToolKit.Int(args, "position", 0);
            if (pos < 1 || pos > s.SavedCardIds.Count) return ToolKit.Error("no_such_position", "Read the saved cards first with list_saved_cards, then ask which one.");
            var chosen = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.SavedCards), c => c.Id == s.SavedCardIds[pos - 1] && c.AccountId == account, ct);   // by account id: another buyer's card is simply not found
            if (chosen is null) return ToolKit.Error("not_available", "That card is not there any more: read the list again.");
            s.SetCardPay(new CardPayRequest(chosen.Id, $"I will charge your {SavedCardTalk.Spoken(chosen)} {VoiceSpeech.Money(cents)} for your order." + AccountTalk.Ask));
            s.SetTask("payment");
            s.BeginKeypadStep("confirm_card_pay");
            return new(new JsonObject { ["status"] = "reading_back", ["instruction"] = "Tell the caller in one short sentence that you will read the charge back now. Do not say it yourself. Then stop talking." }.ToJsonString(), EndCall: true);
        }
        if (!provider.Supports(method)) return ToolKit.Error("method_unavailable", "That way of paying is not available: offer the other one.");
        if (s.Confirmation != ConfirmationStatus.Confirmed || s.QuoteKey is null) return ToolKit.Error("not_confirmed", "The caller has not confirmed the order read-back: call read_back_order first.");
        if (s.OrderNumber is not null) return ToolKit.Error("already_placed", "The order is already placed and paid.");
        if (s.CountPaymentStart() > Math.Max(1, options.Value.MaxPaymentAttempts)) return ToolKit.Error("too_many_attempts", "Too many payment attempts in this call: offer to connect a person, or to order on the web site.");
        var call = await calls.FindAsync(ctx.CallId, ct);
        if (call is null) return ToolKit.Error("not_available", "Something went wrong: offer a person.");

        var (status, number, total, intent) = await orders.PlaceAsync(s, call, ct);
        switch (status)
        {
            case PlaceStatus.LimitReached: return ToolKit.Error("daily_limit", "This number has reached the daily limit for phone orders: suggest the web site.");
            case PlaceStatus.PriceChanged: s.OrderChanged(); return ToolKit.Error("price_changed", "The price changed since the read-back: call read_back_order again so the caller hears the new total.");
            case PlaceStatus.NotReady: return ToolKit.Error("not_ready", "Something is missing for the order (name, email, address or verification): check each and ask.");
            case PlaceStatus.Unavailable: return ToolKit.Error("order_unavailable", "The order could not be started right now: offer to try again in a moment.");
        }
        if (method == PhonePaymentMethod.TextLink && s.VerifiedPhone is null) return ToolKit.Error("need_verification", "A payment link goes to a verified phone: verify the caller first.");
        var row = await orders.OpenAttemptAsync(ctx.CallId, number!, total, method, ct);
        var attemptId = row.Id.ToString("N");
        var actionUrl = options.Value.PublicBaseUrl!.TrimEnd('/') + VoiceEndpoints.KeypadPath;
        PhonePaymentStart start;
        try { start = await provider.StartAsync(new PhonePaymentRequest(attemptId, number!, total, "USD", method, s.VerifiedPhone ?? s.CallerNumber), actionUrl, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolKit.Error("payments_unavailable", "The payment step could not start: offer to try again in a moment."); }
        var attempt = new PaymentAttempt { AttemptId = attemptId, OrderNumber = number!, IntentId = intent!, AmountCents = total, Method = method, Instruction = start.Instruction };
        s.SetPayment(attempt);
        s.SetTask("payment");
        if (method == PhonePaymentMethod.TextLink)
        {
            // the link goes by text to the verified phone: the caller pays on Sola's own page, and the call can go on while they do
            if (start.LinkUrl is null || !await sms.SendAsync(s.VerifiedPhone!, "Solvelt: pay for your order here: " + start.LinkUrl, ct)) return ToolKit.Error("sms_failed", "The text could not be sent: offer to try again.");
            return ToolKit.Json(new JsonObject { ["status"] = "link_sent", ["instruction"] = "Tell the caller a text with a payment link is on its way and they can pay on their phone while you stay on the line. Never ask for card details. When they say they are done, call check_payment." });
        }
        s.BeginKeypadStep("pay");
        return new(new JsonObject { ["status"] = "payment_starting", ["instruction"] = "Tell the caller in one short sentence that the payment step is starting and they will type their card on the keypad. Do not say any card detail or the total. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}

public sealed class CheckPaymentTool(PhoneOrderService orders) : IVoiceTool
{
    public string Name => "check_payment";
    public JsonObject Definition => ToolKit.Def(Name, "Say whether the order has been placed and paid.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        await orders.RefreshAsync(s, ct);   // a text-link payment is settled by the provider's notification, not by the call
        var o = new JsonObject
        {
            ["order_placed"] = s.OrderNumber is not null,
            ["order_number_spoken"] = s.OrderNumber is null ? null : VoiceSpeech.Spelled(s.OrderNumber),
            ["payment"] = s.Payment?.Status ?? "none",
            ["instruction"] = s.OrderNumber is not null ? "Tell them the order is placed, give the order number slowly, and say a text confirmation is on its way." : "The order is not paid yet: do not say it is placed.",
        };
        return ToolKit.Json(o);
    }
}
