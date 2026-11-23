using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Setting or changing the phone PIN (decision V-18): needs an account verified in this call AND a FRESH text message code, then the server takes the PIN on the keypad itself (the model is off the line and never hears it).</summary>
public sealed class SetMyPinTool(PhoneCodeService codes, PhonePinService pins) : IVoiceTool
{
    public string Name => "set_my_pin";
    public JsonObject Definition => ToolKit.Def(Name, "Set or change the caller's phone PIN, a second factor for signing in by phone. Only for a caller verified by text message in this call. Texts a NEW code to their number and moves the call to the keypad: they type the code, then choose and confirm the PIN on the keypad. Never ask for the PIN or say any digit of it.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        // the unlock path: a caller whose account PIN is LOCKED is only a guest with a verified phone, and may set a NEW PIN after a fresh code (nothing else unlocks it)
        Guid? unlock = s.PinFor is { } locked && s.VerifiedPhone is not null && await pins.IsLockedAsync(locked, ct) ? locked : null;
        if (unlock is null && AccountTalk.Gate(s) is { } refused) return refused;
        var sent = await codes.SendAsync(s, s.VerifiedPhone!, "pin_change", unlock ?? s.AccountId, ct);
        if (sent == SendResult.TooMany) return ToolKit.Error("too_many_codes", "No more codes can be sent right now. Offer the web site or a person.");
        if (sent == SendResult.Failed) return ToolKit.Error("sms_failed", "The text could not be sent. Offer to try once more, or a person.");
        s.SetPin(new PinSetup("set", null, unlock));
        s.SetTask("account");
        s.BeginKeypadStep("sms_code");
        return new(new JsonObject { ["status"] = "code_sent", ["instruction"] = "Tell the caller, in one or two short sentences: you are texting a new six digit code to their number now, and after you stop talking the automated line will ask them to SAY the code, and then they will choose their PIN on the keypad. Do not ask for the code yourself, never repeat it, and do not ask for the PIN. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}

/// <summary>Removing the PIN: the same fresh code first, then the server reads the removal back and the keypad yes removes it.</summary>
public sealed class RemoveMyPinTool(PhoneCodeService codes, PhonePinService pins) : IVoiceTool
{
    public string Name => "remove_my_pin";
    public JsonObject Definition => ToolKit.Def(Name, "Remove the caller's phone PIN. Only for a caller verified by text message in this call who has a PIN. Texts a NEW code and moves the call to the keypad; the server then reads the removal back and asks for a yes.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return refused;
        if (!await pins.HasPinAsync(s.AccountId!.Value, ct)) return ToolKit.Error("no_pin", "This account has no PIN.");
        var sent = await codes.SendAsync(s, s.VerifiedPhone!, "pin_change", s.AccountId, ct);
        if (sent == SendResult.TooMany) return ToolKit.Error("too_many_codes", "No more codes can be sent right now. Offer the web site or a person.");
        if (sent == SendResult.Failed) return ToolKit.Error("sms_failed", "The text could not be sent. Offer to try once more, or a person.");
        s.SetPin(new PinSetup("remove", null));
        s.SetTask("account");
        s.BeginKeypadStep("sms_code");
        return new(new JsonObject { ["status"] = "code_sent", ["instruction"] = "Tell the caller, in one or two short sentences: you are texting a new six digit code to their number now, and after you stop talking the automated line will ask them to SAY the code. Do not ask for the code yourself and never repeat it. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}
