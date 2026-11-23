using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Runs the EXISTING buyer order handlers in process, as the buyer the call has proved (an account verified by SMS code in this call) or, for a guest, through the order-access link the server mints
/// for an order that OUR records tie to the caller's verified number. The same ownership rules and the same shapes as the web (another buyer's order is a 404; a claimed guest order shows only its
/// number and status), nothing re-implemented.
/// </summary>
public sealed class InProcessOrders(IServiceProvider sp)
{
    private async Task<JsonDocument?> RunAsync(Func<HttpContext, Task<IResult>> handler, Action<HttpContext>? setup, CancellationToken ct)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        setup?.Invoke(ctx);
        ctx.Response.Body = new MemoryStream();
        var result = await handler(ctx);
        await result.ExecuteAsync(ctx);
        if (ctx.Response.StatusCode != 200) return null;
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body, cancellationToken: ct);
    }

    private static void AsBuyer(HttpContext ctx, Guid accountId, DateTimeOffset at) => AuthEndpoints.SetBuyer(ctx, new BuyerPrincipal(accountId, Guid.Empty, "", "", at));

    public Task<JsonDocument?> AccountListAsync(Guid accountId, DateTimeOffset at, int limit, CancellationToken ct)
        => RunAsync(c => OrderEndpoints.List(c, sp.GetRequiredService<MarketplaceDb>(), limit, null, null, ct), c => AsBuyer(c, accountId, at), ct);

    public Task<JsonDocument?> AccountDetailAsync(Guid accountId, DateTimeOffset at, string number, CancellationToken ct)
        => RunAsync(c => OrderEndpoints.Detail(number, c, sp.GetRequiredService<MarketplaceDb>(), sp.GetRequiredService<TimeProvider>(), ct), c => AsBuyer(c, accountId, at), ct);

    public async Task<JsonDocument?> GuestDetailAsync(string number, CancellationToken ct)
    {
        var db = sp.GetRequiredService<MarketplaceDb>();
        var id = await db.Orders.AsNoTracking().Where(o => o.Number == number).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        var (token, _) = sp.GetRequiredService<GuestLinks>().Make(id.Value);
        return await RunAsync(c => GuestOrderEndpoints.Get(number, c, db, sp.GetRequiredService<GuestLinks>(), ct), c => c.Request.Headers[Solvelt.Marketplace.Api.Checkout.CheckoutEndpoints.GuestOrderHeader] = token, ct);
    }
}

/// <summary>Order facts as a person says them: statuses in plain words, dates as weekdays, at most three groups. Built from the handlers' own JSON.</summary>
public static class OrderSpeech
{
    public static string StatusWords(string? status) => status switch
    {
        "processing" => "being processed", "new" => "received by the seller", "accepted" => "accepted by the seller", "on_hold" => "on hold",
        "partially_shipped" => "partly shipped", "shipped" => "on its way", "delivered" => "delivered", "cancelled" => "cancelled", "refunded" => "refunded",
        "placed" => "placed", "paid" => "paid", _ => "being processed",
    };

    private static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string When(JsonElement order, string name, DateOnly today)
        => Str(order, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? VoiceSpeech.Date(DateOnly.FromDateTime(d.UtcDateTime), today) : "";

    /// <summary>"Order M P, ..., placed Tuesday the third. From Mazel Bakery: on its way with UPS, expected Friday the twentieth."</summary>
    public static string Describe(JsonElement order, DateOnly today)
    {
        var number = Str(order, "number") ?? "";
        var parts = new List<string> { $"Order {VoiceSpeech.Spelled(number)}" + (When(order, "createdAt", today) is { Length: > 0 } w ? ", placed " + w : "") + "." };
        if (order.TryGetProperty("claimed", out var cl) && cl.ValueKind == JsonValueKind.True) { parts.Add("It is " + StatusWords(Str(order, "status")) + ". More detail is in the account it belongs to."); return string.Join(" ", parts); }
        if (!order.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return string.Join(" ", parts);
        foreach (var g in groups.EnumerateArray().Take(3))
        {
            var seller = VoiceSpeech.Plain(Str(g, "sellerName"), 40);
            var sentence = $"From {seller}: {StatusWords(Str(g, "status"))}";
            if (g.TryGetProperty("shipments", out var ships) && ships.ValueKind == JsonValueKind.Array && ships.EnumerateArray().FirstOrDefault(s => !(s.TryGetProperty("voided", out var v) && v.ValueKind == JsonValueKind.True)) is { ValueKind: JsonValueKind.Object } sh)
            {
                if (Str(sh, "carrier") is { Length: > 0 } c) sentence += " with " + VoiceSpeech.Plain(c, 30);
                if (Str(sh, "deliveredAt") is not null) sentence += ", " + When(sh, "deliveredAt", today) is { Length: > 0 } dd ? ", delivered " + dd : "";
                else if (When(sh, "eta", today) is { Length: > 0 } eta) sentence += ", expected " + eta;
                if (Str(sh, "trackingNumber") is { Length: >= 4 } t) sentence += ", tracking number ending " + VoiceSpeech.Digits(t[^4..]);
            }
            if (g.TryGetProperty("cancelRequest", out var cr) && cr.ValueKind == JsonValueKind.Object) sentence += $"; a cancellation request is {Str(cr, "status")?.Replace('_', ' ')}";
            parts.Add(sentence + ".");
        }
        return string.Join(" ", parts);
    }
}

internal static class OrderAccess
{
    public static bool IsAccount(VoiceCallState s) => s.Level == CallerLevel.Verified && s.VerifiedInThisCall && s.AccountId is not null;
    public static bool IsGuest(VoiceCallState s) => s.Level == CallerLevel.Guest && s.VerifiedInThisCall && s.VerifiedPhone is not null;

    /// <summary>The order the caller means: a position in the list just read out, or a number they gave. The server decides whether it is theirs (it asks the handler that knows).</summary>
    public static string? Pick(VoiceCallState s, JsonElement args)
    {
        var pos = ToolKit.Int(args, "position", 0);
        if (pos >= 1 && pos <= s.ListedOrders.Count) return s.ListedOrders[pos - 1];
        var given = ToolKit.Str(args, "order_number")?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(given) || given.Length > 40 || !given.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? null : given;
    }

    public static async Task<JsonDocument?> DetailAsync(VoiceCallState s, InProcessOrders orders, VoiceCallStore calls, string number, TimeProvider time, CancellationToken ct)
    {
        if (IsAccount(s)) return await orders.AccountDetailAsync(s.AccountId!.Value, s.VerifiedAt ?? time.GetUtcNow(), number, ct);
        if (IsGuest(s) && calls.CallerKeyOf(s.VerifiedPhone) is { } key && (await calls.PhoneOrdersAsync(key, 20, ct)).Contains(number)) return await orders.GuestDetailAsync(number, ct);
        return null;   // not theirs: the same answer as a number that does not exist
    }
}

public sealed class ListMyOrdersTool(InProcessOrders orders, VoiceCallStore calls, TimeProvider time) : IVoiceTool
{
    public string Name => "list_my_orders";
    public JsonObject Definition => ToolKit.Def(Name, "Read out the caller's most recent orders (at most three). Needs a caller verified by SMS code in this call: an account, or a guest who ordered by phone.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) && !OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "Orders need the text-message code first: offer identify_caller.");
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var numbers = new List<string>();
        var arr = new JsonArray();
        if (OrderAccess.IsAccount(s))
        {
            using var list = await orders.AccountListAsync(s.AccountId!.Value, s.VerifiedAt ?? time.GetUtcNow(), 3, ct);
            if (list is null) return ToolKit.Error("unavailable", "Say the orders could not be read right now.");
            foreach (var o in list.RootElement.GetProperty("orders").EnumerateArray().Take(3))
            {
                numbers.Add(o.GetProperty("number").GetString()!);
                arr.Add(new JsonObject { ["position"] = numbers.Count, ["order_number_spoken"] = VoiceSpeech.Spelled(numbers[^1]), ["placed"] = OrderSpeech.When(o, "createdAt", today), ["total_spoken"] = VoiceSpeech.Money(o.GetProperty("totalCents").GetInt64()),
                    ["first_item"] = VoiceSpeech.Plain(o.GetProperty("groups").EnumerateArray().Select(g => g.TryGetProperty("firstItem", out var f) ? f.GetString() : null).FirstOrDefault(x => x is not null), 60) });
            }
        }
        else
        {
            foreach (var n in await calls.PhoneOrdersAsync(calls.CallerKeyOf(s.VerifiedPhone)!, 3, ct))
            {
                using var d = await orders.GuestDetailAsync(n, ct);
                if (d is null) continue;
                numbers.Add(n);
                arr.Add(new JsonObject { ["position"] = numbers.Count, ["order_number_spoken"] = VoiceSpeech.Spelled(n), ["placed"] = OrderSpeech.When(d.RootElement, "createdAt", today) });
            }
        }
        s.SetListedOrders(numbers);
        s.SetTask("orders");
        return ToolKit.Json(new JsonObject { ["found"] = numbers.Count, ["orders"] = arr, ["instruction"] = numbers.Count == 0 ? "There are no orders to show." : "Read the order positions with their dates, then ask which one they want to hear about." });
    }
}

public sealed class OrderStatusTool(InProcessOrders orders, VoiceCallStore calls, TimeProvider time) : IVoiceTool
{
    public string Name => "order_status";
    public JsonObject Definition => ToolKit.Def(Name, "Say where one of the caller's orders is. Give the position from list_my_orders, or the order number they read out.",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "Position in the list just read out."), ["order_number"] = ToolKit.Prop("string", "The order number, if they said it.") });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) && !OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "Orders need the text-message code first: offer identify_caller.");
        if (OrderAccess.Pick(s, args) is not { } number) return ToolKit.Error("which_order", "Ask which order: read their orders with list_my_orders.");
        using var d = await OrderAccess.DetailAsync(s, orders, calls, number, time, ct);
        if (d is null) return ToolKit.Error("not_found", "Say you cannot find that order for this caller, and offer to read their orders.");
        s.SetTask("orders");
        return ToolKit.Json(new JsonObject { ["status_spoken"] = OrderSpeech.Describe(d.RootElement, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)), ["instruction"] = "Say it as given, in one or two sentences, then ask if they need anything else." });
    }
}

/// <summary>A text with the order's number and where it is (no address, no items), to the verified phone only. A few per call.</summary>
public sealed class TextOrderSummaryTool(InProcessOrders orders, VoiceCallStore calls, ISmsSender sms, TimeProvider time) : IVoiceTool
{
    public string Name => "text_order_summary";
    public JsonObject Definition => ToolKit.Def(Name, "Text the caller a short summary of one order (its number and status) to their verified phone.",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "Position in the list just read out."), ["order_number"] = ToolKit.Prop("string", "The order number, if they said it.") });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) && !OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "Orders need the text-message code first.");
        if (OrderAccess.Pick(s, args) is not { } number) return ToolKit.Error("which_order", "Ask which order.");
        if (s.CountOrderText() > 3) return ToolKit.Error("too_many_texts", "Enough texts in this call: say so.");
        using var d = await OrderAccess.DetailAsync(s, orders, calls, number, time, ct);
        if (d is null) return ToolKit.Error("not_found", "Say you cannot find that order for this caller.");
        var r = d.RootElement;
        var status = r.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array ? string.Join("; ", g.EnumerateArray().Take(3).Select(x => (x.TryGetProperty("sellerName", out var n) ? n.GetString() : "") + ": " + OrderSpeech.StatusWords(x.TryGetProperty("status", out var st) ? st.GetString() : null))) : OrderSpeech.StatusWords(r.TryGetProperty("status", out var o) ? o.GetString() : null);
        var total = r.TryGetProperty("totalCents", out var t) && t.TryGetInt64(out var cents) ? $" Total {SolaNotification.Dollars(cents)} USD." : "";
        if (!await sms.SendAsync(s.VerifiedPhone!, $"Solvelt order {number}: {status}.{total}", ct)) return ToolKit.Error("sms_failed", "The text could not be sent: say so.");
        return ToolKit.Json(new JsonObject { ["status"] = "sent", ["instruction"] = "Say the text is on its way." });
    }
}

/// <summary>
/// Asks to cancel an order: ACCOUNT callers verified in this call only (the existing cancel rules and ownership apply: another buyer's order is not found; an order that can no longer be cancelled is
/// refused by the same check as on the web). The model proposes; the SERVER reads the cancellation back and the call moves to the keypad step where the caller says yes or presses 1 (same machine as
/// the order read-back): only that answer sends the request to the seller. A request, not a cancellation: the seller decides.
/// </summary>
public sealed class CancelOrderTool(InProcessOrders orders, TimeProvider time) : IVoiceTool
{
    public string Name => "cancel_order";
    public JsonObject Definition => ToolKit.Def(Name, "Start a cancellation request for one of the caller's orders. The server reads it back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop talking.",
        new JsonObject
        {
            ["position"] = ToolKit.Prop("integer", "Position in the list just read out."), ["order_number"] = ToolKit.Prop("string", "The order number, if they said it."),
            ["seller"] = ToolKit.Prop("string", "The seller's name, only when the order has more than one seller and the caller named one."),
            ["reason"] = ToolKit.Prop("string", "One of: " + string.Join(", ", BuyerOrderActions.CancelReasons) + "."),
        }, "reason");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s)) return ToolKit.Error("not_allowed", s.Level == CallerLevel.Guest ? "Cancelling needs an account verified by phone: offer the link in their order email, or a person." : "Cancelling needs the text-message code first: offer identify_caller.");
        var reason = ToolKit.Str(args, "reason");
        if (reason is null || !BuyerOrderActions.CancelReasons.Contains(reason)) return ToolKit.Error("bad_reason", "Ask why, and pick the closest of: " + string.Join(", ", BuyerOrderActions.CancelReasons) + ".");
        if (OrderAccess.Pick(s, args) is not { } number) return ToolKit.Error("which_order", "Ask which order: read their orders with list_my_orders.");
        if (s.CountCancelStart() > 3) return ToolKit.Error("too_many_attempts", "Too many cancellation attempts in this call: offer a person.");
        using var d = await orders.AccountDetailAsync(s.AccountId!.Value, s.VerifiedAt ?? time.GetUtcNow(), number, ct);
        if (d is null) return ToolKit.Error("not_found", "Say you cannot find that order for this caller.");
        var groups = d.RootElement.GetProperty("groups").EnumerateArray().ToList();
        var seller = ToolKit.Str(args, "seller");
        var chosen = groups.Count == 1 ? groups[0] : groups.FirstOrDefault(g => seller is not null && string.Equals(g.GetProperty("sellerName").GetString(), seller, StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind != JsonValueKind.Object) return ToolKit.Json(new JsonObject { ["error"] = "which_seller", ["sellers"] = new JsonArray(groups.Select(g => (JsonNode)VoiceSpeech.Plain(g.GetProperty("sellerName").GetString(), 40)).ToArray()), ["instruction"] = "This order has several sellers: ask which one to cancel." });
        if (chosen.TryGetProperty("cancelRequest", out var open) && open.ValueKind == JsonValueKind.Object && open.TryGetProperty("status", out var openStatus) && openStatus.GetString() is "pending" or "requested")
            return ToolKit.Error("already_requested", "Say a cancellation request for that order is already waiting for the seller, and they will get an email with the answer.");
        if (!chosen.GetProperty("canCancel").GetBoolean())
            return ToolKit.Error("not_cancellable", $"Say that part of the order is {OrderSpeech.StatusWords(chosen.GetProperty("status").GetString())} and can no longer be cancelled; offer a return or a person.");
        var items = string.Join(", ", chosen.GetProperty("lines").EnumerateArray().Take(4).Select(l => $"{VoiceSpeech.Number(l.GetProperty("quantity").GetInt32())} {VoiceSpeech.Plain(l.GetProperty("title").GetString(), 60)}"));
        var text = $"You asked to cancel order {VoiceSpeech.Spelled(number)}, the part from {VoiceSpeech.Plain(chosen.GetProperty("sellerName").GetString(), 40)}: {items}. "
            + "This sends a cancellation request to the seller, who decides; you will get an email with the answer. To send the request, say yes. To keep the order, say no.";
        s.SetCancel(new PendingCancel(number, chosen.TryGetProperty("sellerOrderNumber", out var so) ? so.GetString() : null, reason, text));
        s.SetTask("cancellation");
        s.BeginKeypadStep("confirm_cancel");
        return new(new JsonObject { ["status"] = "reading_back", ["instruction"] = "Tell the caller in one short sentence that you will read the cancellation back now. Do not say it yourself. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}
