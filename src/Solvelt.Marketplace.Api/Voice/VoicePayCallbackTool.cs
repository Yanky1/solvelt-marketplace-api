using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// <c>schedule_pay_callback</c> (decision V-60): the caller ASKED to be called back to pay for an order that is placed and not yet paid. The model names the order and nothing else: the number that gets the call is the one
/// verified in this very call, chosen by the server. Everything else (is it theirs, is it unpaid, is there already a callback, how many today) is decided by <see cref="PayCallbackService"/>.
/// </summary>
public sealed class SchedulePayCallbackTool(PayCallbackService callbacks) : IVoiceTool
{
    public string Name => "schedule_pay_callback";
    public JsonObject Definition => ToolKit.Def(Name,
        "ONLY when the caller asks to be called back so they can pay for an order that is placed and not yet paid. Never offer it yourself. The platform rings only the number the caller verified in this call, takes the card on the keypad, and the offer lasts thirty minutes. Needs a caller verified by text message in this call.",
        new JsonObject { ["order_number"] = ToolKit.Prop("string", "The order number, only if the caller read it out. Leave it out to mean their newest unpaid order: the order lists do not show unpaid orders.") });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!callbacks.IsAvailable()) return ToolKit.Error("callback_unavailable", "Say that a call back is not available right now, and that they can pay on the web site or call again.");
        if (!OrderAccess.IsAccount(s) && !OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "A call back goes to a number verified by text message in this call: offer identify_caller first.");
        var given = ToolKit.Str(args, "order_number")?.Trim().ToUpperInvariant();
        if (given is { Length: > 40 } || (given is not null && !given.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return ToolKit.Error("which_order", "That did not sound like an order number: ask the caller to read it again, or leave it out for their newest unpaid order.");
        var (status, _) = await callbacks.ScheduleAsync(s, ctx.CallId, string.IsNullOrEmpty(given) ? null : given, ct);
        return status switch
        {
            ScheduleStatus.Scheduled => new(new JsonObject
            {
                ["status"] = "scheduled",
                ["instruction"] = "Tell the caller in one or two short sentences: we will call this number back in about a minute to take the payment on the keypad, and the offer lasts thirty minutes. Never ask for card details. Then say goodbye.",
            }.ToJsonString(), EndCall: true),
            ScheduleStatus.AlreadyScheduled => ToolKit.Error("already_scheduled", "Say a call back is already on its way for that order, and they can wait for it."),
            ScheduleStatus.TooMany => ToolKit.Error("too_many", "Say no more call backs can be arranged for this number today, and they can pay on the web site."),
            ScheduleStatus.NotPayable => ToolKit.Error("not_payable", "Say that order is not waiting for payment, so there is nothing to call back about."),
            ScheduleStatus.NotFound => ToolKit.Error("not_found", "Say you cannot find an unpaid order for this caller to call back about."),
            ScheduleStatus.NeedVerification => ToolKit.Error("need_verification", "A call back goes to a number verified by text message in this call: offer identify_caller first."),
            _ => ToolKit.Error("callback_unavailable", "Say that a call back is not available right now, and that they can pay on the web site or call again."),
        };
    }
}
