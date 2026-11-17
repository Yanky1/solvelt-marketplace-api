using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using QuoteStatus = Solvelt.Marketplace.Api.Checkout.QuoteStatus;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What a spoken or keypad answer to the read-back means. A "no" of any kind beats a "yes" in the same breath; anything unclear is unclear, never a yes.</summary>
public enum ConfirmChoice { Yes, No, Unclear }

public static class ReadBackAnswer
{
    private static readonly HashSet<string> Yes = ["yes", "yeah", "yep", "yup", "correct", "right", "confirm", "confirmed", "affirmative"], No = ["no", "nope", "not", "wrong", "change", "cancel", "wait", "stop", "dont", "don't", "back", "incorrect", "different"];

    public static ConfirmChoice Parse(string? digits, string? speech)
    {
        var d = (digits ?? "").Trim();
        if (d == "1") return ConfirmChoice.Yes;
        if (d == "2") return ConfirmChoice.No;
        if (d.Length > 0) return ConfirmChoice.Unclear;
        var words = (speech ?? "").ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 6) return ConfirmChoice.Unclear;
        if (words.Any(No.Contains)) return ConfirmChoice.No;
        return words.Any(Yes.Contains) ? ConfirmChoice.Yes : ConfirmChoice.Unclear;
    }
}

/// <summary>The server's own words for the order: what the caller hears before saying yes. Built from the fresh quote, never from anything the model said.</summary>
public static class OrderReadBack
{
    public static string Build(QuoteView q, CheckoutAddress address, DateOnly today)
    {
        var parts = new List<string> { "Here is your order." };
        var lines = q.Groups.SelectMany(g => g.Lines).ToList();
        var said = lines.Take(5).Select(l => $"{VoiceSpeech.Number(l.Quantity)} {VoiceSpeech.Plain(l.Title, 70)}{(string.IsNullOrWhiteSpace(l.VariantName) || l.VariantName == "Default" ? "" : ", " + VoiceSpeech.Plain(l.VariantName, 40))}");
        parts.Add(string.Join(". ", said) + ".");
        if (lines.Count > 5) parts.Add($"And {VoiceSpeech.Number(lines.Count - 5)} more.");
        parts.Add("Delivery to " + VoiceSpeech.Address(address) + ".");
        foreach (var g in q.Groups)
        {
            var sel = g.Selected;
            if (sel is null) continue;
            var how = sel.Kind == "local" ? "Local delivery" : VoiceSpeech.Plain(sel.Speed, 20) + " shipping";
            var when = sel.Kind == "carrier" && g.ShippingOptions.FirstOrDefault(o => o.Speed == sel.Speed) is { } o ? VoiceSpeech.DateRange(o.Estimate.Earliest, o.Estimate.Latest, today) : sel.Date is { } d && DateOnly.TryParse(d, CultureInfo.InvariantCulture, out var day) ? VoiceSpeech.Date(day, today) : "";
            parts.Add($"{(q.Groups.Count > 1 ? VoiceSpeech.Plain(g.SellerName, 40) + ": " : "")}{how}, {(sel.ShippingCents == 0 ? "free" : VoiceSpeech.Money(sel.ShippingCents))}{(when.Length > 0 ? ", arriving " + when : "")}.");
        }
        parts.Add($"Items {VoiceSpeech.Money(q.ItemsCents)}" + (q.ShippingCents > 0 ? $", shipping {VoiceSpeech.Money(q.ShippingCents)}" : "") + (q.TaxCents > 0 ? $", tax {VoiceSpeech.Money(q.TaxCents)}" : "") + ".");
        parts.Add($"Your total is {VoiceSpeech.Money(q.TotalCents)}.");
        parts.Add(VoiceScripts.ConfirmQuestion);
        return string.Join(" ", parts);
    }
}

internal static class CheckoutAccess
{
    public static CartOwner Owner(VoiceCallState s) => CartAccess.Owner(s);

    /// <summary>The recipient name: the account's or the guest's own, or one the caller gives for a gift.</summary>
    public static async Task<string?> NameAsync(VoiceCallState s, MarketplaceDb db, string? given, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(given)) return given.Trim();
        if (s.Level == CallerLevel.Verified && s.AccountId is { } id) return await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == id).Select(a => a.DisplayName).FirstOrDefaultAsync(ct);
        return s.GuestName;
    }

    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string> errors) => errors.Select(e => e.Key switch
    {
        "address.name" => "the name", "address.line1" => "the street address", "address.city" => "the city", "address.postalCode" => "the postal code", "address.country" => "the country", "address.phone" => "the phone number",
        _ => "something in the address",
    }).Distinct().ToList();

    public static string AddressLabel(CheckoutAddress a) => "in " + VoiceSpeech.Plain(a.City, 40);

    public static QuoteRequest Request(VoiceCallState s) => new(new QuoteAddress(s.DeliveryAddress!.Country!.ToUpperInvariant(), s.DeliveryAddress.Region?.ToUpperInvariant(), s.DeliveryAddress.PostalCode, s.DeliveryAddress.Lat, s.DeliveryAddress.Lng), s.Selections);

    public static JsonObject? Precondition(VoiceCallState s)
    {
        if (s.CartItemCount == 0) return new JsonObject { ["error"] = "cart_empty", ["instruction"] = "The cart is empty: help them find something first." };
        if (s.DeliveryAddress is null) return new JsonObject { ["error"] = "need_address", ["instruction"] = "Ask where to deliver: a saved address, or a new one." };
        if (!s.AddressConfirmed) return new JsonObject { ["error"] = "address_not_confirmed", ["instruction"] = "The caller has not confirmed the address you read back: ask, then call confirm_address." };
        return null;
    }

    public static JsonObject? QuoteProblem(QuoteStatus status, string? detail) => status switch
    {
        QuoteStatus.Ok => null,
        QuoteStatus.InvalidAddress => new JsonObject { ["error"] = "invalid_address", ["instruction"] = "The address cannot be delivered to as given: ask for the state and postal code again." },
        QuoteStatus.EmptyOrUnavailableCart => new JsonObject { ["error"] = "cart_not_ready", ["instruction"] = "Something in the cart is no longer available: read the cart and fix it." },
        QuoteStatus.InvalidSelection => new JsonObject { ["error"] = "invalid_selection", ["instruction"] = "That delivery choice is no longer available: read the delivery options again." },
        QuoteStatus.TotalTooLarge => new JsonObject { ["error"] = "order_limit", ["instruction"] = "The order is too large for a phone order: suggest splitting it or ordering on the web site." },
        QuoteStatus.DemoCart => new JsonObject { ["error"] = "not_orderable", ["instruction"] = "This cart cannot be ordered." },
        _ => new JsonObject { ["error"] = "delivery_unavailable", ["instruction"] = "Delivery options are not available right now: offer to try again in a moment." },
    };
}

public sealed class ListSavedAddressesTool(AccountService accounts) : IVoiceTool
{
    public string Name => "list_saved_addresses";
    public JsonObject Definition => ToolKit.Def(Name, "Read out the caller's saved delivery addresses (at most three). Only for a caller verified by SMS code in this call.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level != CallerLevel.Verified || !s.VerifiedInThisCall || s.AccountId is not { } id) return ToolKit.Error("need_verification", "Saved addresses need the text-message code first: offer identify_caller, or take a new address.");
        var all = (await accounts.ListAddressesAsync(id, ct)).OrderByDescending(a => a.IsDefault).ToList();
        s.SetSavedAddresses(all.Select(a => a.Id).ToList());
        if (all.Count == 0) return ToolKit.Json(new JsonObject { ["found"] = 0, ["instruction"] = "They have no saved address: take a new one." });
        var arr = new JsonArray();
        for (var n = 0; n < Math.Min(3, all.Count); n++)
            arr.Add(new JsonObject { ["position"] = n + 1, ["address_spoken"] = VoiceSpeech.Street(VoiceSpeech.Plain(all[n].Line1, 80)) + ", " + VoiceSpeech.Plain(all[n].City, 40), ["is_default"] = all[n].IsDefault });
        return ToolKit.Json(new JsonObject { ["found"] = all.Count, ["addresses"] = arr, ["instruction"] = "Read at most these, street and city only, and ask which one." });
    }
}

public sealed class UseSavedAddressTool(AccountService accounts) : IVoiceTool
{
    public string Name => "use_saved_address";
    public JsonObject Definition => ToolKit.Def(Name, "Use one of the saved addresses just read out as the delivery address.", new JsonObject { ["position"] = ToolKit.Prop("integer", "Position in the list read out.") }, "position");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level != CallerLevel.Verified || !s.VerifiedInThisCall || s.AccountId is not { } id) return ToolKit.Error("need_verification", "Saved addresses need the text-message code first.");
        var pos = ToolKit.Int(args, "position", 0);
        if (pos < 1 || pos > s.SavedAddressIds.Count) return ToolKit.Error("no_such_position", "Read the saved addresses again.");
        var a = await accounts.GetAddressAsync(id, s.SavedAddressIds[pos - 1], ct);   // by account id: another buyer's address is simply not found
        if (a is null) return ToolKit.Error("not_available", "That address is not there any more: read the list again.");
        var addr = new CheckoutAddress(a.FullName, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country, a.Phone ?? s.VerifiedPhone, a.Lat, a.Lng);
        if (CheckoutService.Validate(addr, null).Count > 0) return ToolKit.Error("invalid_address", "That saved address is incomplete: take a new one.");
        s.SetDeliveryAddress(addr, confirmed: true, CheckoutAccess.AddressLabel(addr));
        s.SetTask("delivery");
        return ToolKit.Json(new JsonObject { ["status"] = "set", ["to"] = VoiceSpeech.Street(VoiceSpeech.Plain(a.Line1, 80)) + ", " + VoiceSpeech.Plain(a.City, 40), ["instruction"] = "Say the address is set, in one short sentence." });
    }
}

public sealed class SetDeliveryAddressTool(MarketplaceDb db) : IVoiceTool
{
    public string Name => "set_delivery_address";
    public JsonObject Definition => ToolKit.Def(Name, "Take a new delivery address as the caller spelled it. The server validates it and gives you the text to read back; then call confirm_address once the caller agrees.",
        new JsonObject
        {
            ["street"] = ToolKit.Prop("string", "House number and street."), ["apartment"] = ToolKit.Prop("string", "Apartment, suite or floor, if any."), ["city"] = ToolKit.Prop("string", "City."),
            ["state"] = ToolKit.Prop("string", "Two-letter state code, for example NY."), ["postal_code"] = ToolKit.Prop("string", "ZIP or postal code."), ["country"] = ToolKit.Prop("string", "Two-letter country code; US if the caller did not say."),
            ["recipient_name"] = ToolKit.Prop("string", "Only if the delivery is for someone other than the caller."),
        }, "street", "city", "state", "postal_code");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level == CallerLevel.Anonymous) return ToolKit.Error("need_identity", "Before taking an address, verify the caller with identify_caller (a guest is fine).");
        var name = await CheckoutAccess.NameAsync(s, db, ToolKit.Str(args, "recipient_name"), ct);
        if (name is null) return ToolKit.Error("need_name", "Ask the caller's name and record it with set_guest_name first.");
        var addr = new CheckoutAddress(name, ToolKit.Str(args, "street"), ToolKit.Str(args, "apartment"), ToolKit.Str(args, "city"), ToolKit.Str(args, "state")?.Trim().ToUpperInvariant(),
            ToolKit.Str(args, "postal_code"), (ToolKit.Str(args, "country") ?? "US").Trim().ToUpperInvariant(), s.VerifiedPhone);
        var errors = CheckoutService.Validate(addr, null);
        if (errors.Count == 0 && addr.Country == "US" && !UsStates.Codes.Contains(addr.Region ?? "")) errors = new Dictionary<string, string> { ["address.region"] = "state" };
        if (errors.Count > 0) return ToolKit.Json(new JsonObject { ["error"] = "invalid_address", ["problems"] = new JsonArray(CheckoutAccess.Problems(errors).Select(p => (JsonNode)p).ToArray()), ["instruction"] = "Ask the caller again for the parts that are wrong, one at a time." });
        s.SetDeliveryAddress(addr, confirmed: false, CheckoutAccess.AddressLabel(addr));
        s.SetTask("delivery");
        return ToolKit.Json(new JsonObject { ["status"] = "read_back_needed", ["read_back"] = VoiceSpeech.Address(addr), ["instruction"] = "Read the address back exactly as in read_back and ask if it is right. If they correct anything, call set_delivery_address again with the whole corrected address. When they say it is right, call confirm_address." });
    }
}

public sealed class ConfirmAddressTool : IVoiceTool
{
    public string Name => "confirm_address";
    public JsonObject Definition => ToolKit.Def(Name, "The caller agreed that the delivery address you read back is right.");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.State.DeliveryAddress is null) return Task.FromResult(ToolKit.Error("need_address", "There is no address to confirm: take one first."));
        ctx.State.ConfirmAddress();
        return Task.FromResult(ToolKit.Json(new JsonObject { ["status"] = "confirmed", ["instruction"] = "Say that is noted and move on to delivery options." }));
    }
}

public sealed class GetDeliveryOptionsTool(QuoteService quotes, TimeProvider time) : IVoiceTool
{
    public string Name => "get_delivery_options";
    public JsonObject Definition => ToolKit.Def(Name, "Read out how the order can be delivered and what each option costs (at most three per seller). Needs the cart and a confirmed address.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (CheckoutAccess.Precondition(s) is { } refusal) return new(refusal.ToJsonString());
        var (status, view, detail) = await quotes.QuoteAsync(CheckoutAccess.Owner(s), CheckoutAccess.Request(s), ct);
        if (CheckoutAccess.QuoteProblem(status, detail) is { } problem) return new(problem.ToJsonString());
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var choices = new List<DeliveryChoice>();
        var groups = new JsonArray();
        foreach (var g in view!.Groups)
        {
            var options = new List<DeliveryOption>();
            foreach (var o in g.ShippingOptions.Take(3))
                options.Add(new("carrier", o.Speed, null, null, o.PriceCents, $"{VoiceSpeech.Plain(o.Speed, 20)} shipping, {(o.PriceCents == 0 ? "free" : VoiceSpeech.Money(o.PriceCents))}, arriving {VoiceSpeech.DateRange(o.Estimate.Earliest, o.Estimate.Latest, today)}"));
            if (g.Local is { Eligible: true, FeeCents: { } fee } local)
                foreach (var w in local.Windows.Where(w => w.Remaining > 0).Take(Math.Max(0, 3 - options.Count)))
                    options.Add(new("local", null, w.WindowId, w.Date, fee, $"local delivery, {(fee == 0 ? "free" : VoiceSpeech.Money(fee))}, {(DateOnly.TryParse(w.Date, CultureInfo.InvariantCulture, out var d) ? VoiceSpeech.Date(d, today) : "")}"));
            choices.Add(new DeliveryChoice(g.SellerId, g.BrandId, g.SellerName, options));
            var arr = new JsonArray();
            for (var n = 0; n < options.Count; n++) arr.Add(new JsonObject { ["position"] = n + 1, ["option"] = options[n].Spoken });
            groups.Add(new JsonObject { ["group_position"] = groups.Count + 1, ["seller"] = VoiceSpeech.Plain(g.SellerName, 40), ["options"] = arr, ["currently_selected"] = g.Selected is null ? null : (g.Selected.Kind == "local" ? "local delivery" : VoiceSpeech.Plain(g.Selected.Speed, 20)) });
        }
        s.SetChoices(choices);
        s.SetTask("delivery");
        return ToolKit.Json(new JsonObject
        {
            ["groups"] = groups,
            ["instruction"] = "Read at most three options per group with their prices and dates. The cheapest is already selected: ask whether they want that or another, and call choose_delivery only if they pick a different one.",
        });
    }
}

public sealed class ChooseDeliveryTool : IVoiceTool
{
    public string Name => "choose_delivery";
    public JsonObject Definition => ToolKit.Def(Name, "Select a delivery option from the list just read out.",
        new JsonObject { ["option_position"] = ToolKit.Prop("integer", "Position of the option."), ["group_position"] = ToolKit.Prop("integer", "Position of the seller group when there is more than one; 1 otherwise.") }, "option_position");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        var gp = ToolKit.Int(args, "group_position", 1);
        var op = ToolKit.Int(args, "option_position", 0);
        if (gp < 1 || gp > s.Choices.Count) return Task.FromResult(ToolKit.Error("no_options", "Read the delivery options first with get_delivery_options."));
        var group = s.Choices[gp - 1];
        if (op < 1 || op > group.Options.Count) return Task.FromResult(ToolKit.Error("no_such_position", "Read the options again and ask which one."));
        var o = group.Options[op - 1];
        s.SetSelection(new GroupSelection(group.SellerId, group.BrandId, o.Kind == "carrier" ? o.Speed : null, o.WindowId, o.Date), o.Spoken);
        return Task.FromResult(ToolKit.Json(new JsonObject { ["status"] = "selected", ["option"] = o.Spoken, ["instruction"] = "Say it is selected in one short sentence. The total comes with the read-back." }));
    }
}

/// <summary>
/// The server prices the order from the cart, the address and the delivery choice, and then READS IT BACK ITSELF: the model only says that it is about to, and the call moves to the keypad
/// step in which the platform speaks the server's text and collects "yes" or 1. The model's own words never stand in for the read-back, and nothing but that answer confirms the order.
/// </summary>
public sealed class ReadBackOrderTool(QuoteService quotes, IOptions<VoiceOptions> options, TimeProvider time) : IVoiceTool
{
    public string Name => "read_back_order";
    public JsonObject Definition => ToolKit.Def(Name, "When the cart, the address and the delivery are settled: the server reads the whole order and its total back to the caller and asks for a yes. Call it, say one short sentence that you are reading the order back, then stop talking.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (CheckoutAccess.Precondition(s) is { } refusal) return new(refusal.ToJsonString());
        var (status, view, detail) = await quotes.QuoteAsync(CheckoutAccess.Owner(s), CheckoutAccess.Request(s), ct);
        if (CheckoutAccess.QuoteProblem(status, detail) is { } problem) return new(problem.ToJsonString());
        if (!view!.CanPay) return ToolKit.Error("delivery_unavailable", "Part of the order cannot be delivered there: offer another address or to remove that item.");
        var o = options.Value;
        if (view.TotalCents > o.MaxOrderAmount * 100L) return ToolKit.Error("order_limit", $"A phone order can be at most {VoiceSpeech.Number(o.MaxOrderAmount)} dollars. Say the order is over the limit and suggest removing something or ordering on the web site.");
        if (view.Groups.SelectMany(g => g.Lines).Sum(l => l.Quantity) > o.MaxItems) return ToolKit.Error("order_limit", "The order has too many items for a phone order.");
        s.SetQuoteView(view);
        s.BeginReadBack(OrderReadBack.Build(view, s.DeliveryAddress!, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)));
        s.SetTask("confirmation");
        s.BeginKeypadStep("confirm");
        return new(new JsonObject { ["status"] = "reading_back", ["instruction"] = "Tell the caller in one short sentence that you will read the order back now. Do not say any item, price or total yourself. Then stop talking." }.ToJsonString(), EndCall: true);
    }
}
