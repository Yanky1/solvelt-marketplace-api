using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Saved cards by phone (decision V-16). This API never sees a card: it holds the processor's token, the brand, the last four digits and the expiry, which the pay connector reports over the signed channel.</summary>
internal static class SavedCardTalk
{
    public static string Spoken(SavedCard c) => $"{VoiceSpeech.Plain(c.Brand, 20)} card ending {VoiceSpeech.Digits(c.Last4)}, expiring {int.Parse(c.ExpMonth)} twenty {c.ExpYear}";
}

/// <summary>Reads the caller's saved cards (brand, last four, expiry) in order and remembers their ids for this call; the model names a position afterwards.</summary>
public sealed class ListSavedCardsTool(MarketplaceDb db) : IVoiceTool
{
    public string Name => "list_saved_cards";
    public JsonObject Definition => ToolKit.Def(Name, "Read out the caller's saved cards (brand, last four digits and expiry only). Only for a caller verified by text message in this call. Never ask for or say a full card number.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) || s.AccountId is not { } account) return ToolKit.Error("need_verification", "Saved cards belong to an account verified by text message in this call: offer identify_caller, or create_account for a guest.");
        var cards = await db.SavedCards.AsNoTracking().Where(c => c.AccountId == account).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        s.SetSavedCards(cards.Select(c => c.Id).ToList());
        var list = new JsonArray(cards.Select((c, i) => (JsonNode)new JsonObject { ["position"] = i + 1, ["card"] = SavedCardTalk.Spoken(c) }).ToArray());
        return ToolKit.Json(new JsonObject { ["count"] = cards.Count, ["cards"] = list, ["instruction"] = cards.Count == 0 ? "They have no saved card. Offer add_saved_card." : "Read them one by one with their position. Never say more than the brand, the last four digits and the expiry." });
    }
}

/// <summary>Starts the keypad step that saves a card: the model is off the line, SignalWire collects the card into OUR connector, which asks the processor for a token. Nothing is charged.</summary>
public sealed class AddSavedCardTool(PhonePayments payments, PhoneOrderService orders, MarketplaceDb db, IOptions<VoiceOptions> options) : IVoiceTool
{
    public string Name => "add_saved_card";
    public JsonObject Definition => ToolKit.Def(Name, "Save a card to the caller's account for later. Only for a caller verified by text message in this call. Say one short sentence that the card step is starting and they will type the card on the keypad (nothing is charged), then stop talking. Never ask for card details yourself.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) || s.AccountId is not { } account) return ToolKit.Error("need_verification", "Saving a card needs an account verified by text message in this call: offer identify_caller, or create_account for a guest.");
        if (payments.Provider is not ICardSavingProvider saver || !saver.CanSaveCards) return ToolKit.Error("cards_unavailable", "Saving a card is not available yet: offer the web site or a person.");
        if (s.CountCardSaveStart() > 2) return ToolKit.Error("too_many_attempts", "Too many tries to save a card in this call: offer to connect a person.");
        if (await db.SavedCards.CountAsync(c => c.AccountId == account, ct) >= SavedCardLimits.MaxPerAccount) return ToolKit.Error("too_many_cards", $"They already have {SavedCardLimits.MaxPerAccount} saved cards: offer to remove one first (list_saved_cards, then remove_saved_card).");
        var row = await orders.OpenAttemptAsync(ctx.CallId, "cs-" + account.ToString("N"), 0, PayConnectorEndpoints.SaveCardMethod, ct);
        var attemptId = row.Id.ToString("N");
        s.SetCardSave(new CardSaveStep(attemptId, saver.StartSaveCard(attemptId, options.Value.PublicBaseUrl!.TrimEnd('/') + VoiceEndpoints.KeypadPath)));
        s.SetTask("account");
        s.BeginKeypadStep("save_card");
        return new(new JsonObject { ["status"] = "card_step_starting", ["instruction"] = "Tell the caller in one short sentence that the card step is starting, that they will type the card on the keypad and that nothing will be charged. Do not say any card detail. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}

/// <summary>Removing a saved card: the server reads it back and the keypad yes removes it (our row; the processor's token is simply never used again).</summary>
public sealed class RemoveSavedCardTool(MarketplaceDb db) : IVoiceTool
{
    public string Name => "remove_saved_card";
    public JsonObject Definition => ToolKit.Def(Name, "Remove one of the caller's saved cards. Read the list first with list_saved_cards, then ask which one. Only for a caller verified by text message in this call. The server reads it back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop.",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "Position in the list just read out.") }, "position");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return refused;
        var pos = ToolKit.Int(args, "position", 0);
        if (pos < 1 || pos > s.SavedCardIds.Count) return ToolKit.Error("no_such_position", "Read the saved cards first with list_saved_cards, then ask which one.");
        var card = await db.SavedCards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == s.SavedCardIds[pos - 1] && c.AccountId == s.AccountId, ct);   // by account id: another buyer's card is simply not found
        if (card is null) return ToolKit.Error("not_available", "That card is not there any more: read the list again.");
        return AccountTalk.ReadingBack(s, new AccountChangeRequest("card_remove", $"I will remove your {SavedCardTalk.Spoken(card)} from your account." + AccountTalk.Ask, CardId: card.Id));
    }
}
