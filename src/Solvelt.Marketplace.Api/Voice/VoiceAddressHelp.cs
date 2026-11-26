using System.Text.Json;
using System.Text.Json.Nodes;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>The parts of an address as a lookup gives them back. Any part can be empty in a suggestion; a resolved address has the street, city, state and ZIP.</summary>
public sealed record AddressParts(string Line1, string City, string Region, string PostalCode, string Country = "US");

/// <summary>One suggestion: an opaque id that only the lookup that made it understands (never given to the model) and what is known about the address so far.</summary>
public sealed record AddressSuggestion(string Id, AddressParts Preview);

/// <summary>What the caller said so far, and the ZIP if they gave one. Nothing else about the caller (no name, number, account or order) is ever part of a lookup.</summary>
public sealed record AddressQuery(string Text, string? PostalCode);

/// <summary>
/// Address help for the telephone line (decision V-61), independent of any vendor: suggest addresses from a few spoken words and resolve a chosen suggestion to its parts. An adapter must send nothing but the
/// <see cref="AddressQuery"/>, must bound its wait, and must NEVER write the query, the suggestions or the vendor's answer to a log or a diagnostic: addresses are personal data. A failure is an exception of any
/// type; the tools turn it into "address help is not available just now" and the usual way of taking an address goes on.
/// </summary>
public interface IAddressLookup
{
    /// <summary>False when the lookup cannot be used (no key, or no adapter yet). The two tools are then not offered to the model at all and the existing address flow is unchanged.</summary>
    bool IsConfigured { get; }
    /// <summary>Why it is not configured, for the one start-up warning. Never holds a key.</summary>
    string? UnavailableReason { get; }
    Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(AddressQuery query, CancellationToken ct);
    Task<AddressParts?> ResolveAsync(string suggestionId, CancellationToken ct);
}

/// <summary>The default: no lookup. Address help is off and the line takes addresses the usual way.</summary>
public sealed class NotConfiguredAddressLookup : IAddressLookup
{
    public bool IsConfigured => false;
    public string? UnavailableReason => "Voice:Google:MapsApiKey is not set";
    public Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(AddressQuery query, CancellationToken ct) => throw new InvalidOperationException("Address help is not configured.");
    public Task<AddressParts?> ResolveAsync(string suggestionId, CancellationToken ct) => throw new InvalidOperationException("Address help is not configured.");
}

/// <summary>
/// The Google adapter, NOT BUILT YET: awaiting vendor facts. The request and response shapes, endpoints, field names, session rules, billing and terms of use (what may be shown or stored) are to be taken from Google's
/// own documentation, which could not be read when this was written (developers.google.com was refused by the network), and are not written from memory. Until then a key being set changes nothing: the lookup reports
/// itself unavailable and says why in the one start-up warning.
/// </summary>
public sealed class GoogleAddressLookup : IAddressLookup
{
    public bool IsConfigured => false;
    public string? UnavailableReason => "Voice:Google:MapsApiKey is set but the Google adapter is not built yet (awaiting vendor facts)";
    public Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(AddressQuery query, CancellationToken ct) => throw new NotSupportedException("Awaiting vendor facts.");
    public Task<AddressParts?> ResolveAsync(string suggestionId, CancellationToken ct) => throw new NotSupportedException("Awaiting vendor facts.");
}

/// <summary>The one warning, once per process (the V-26 rule: a missing piece makes a feature unavailable with one warning, never a start-up failure).</summary>
public static class AddressHelpStartup
{
    private static int _warned;
    public static void WarnOnce(IAddressLookup lookup, bool voiceEnabled, ILogger log)
    {
        if (!voiceEnabled || lookup.IsConfigured || Interlocked.Exchange(ref _warned, 1) == 1) return;
        log.LogWarning("Address help on the telephone line is not available: {Reason}. Addresses are taken the usual way.", lookup.UnavailableReason ?? "not configured");
    }
    /// <summary>For tests only: lets the warning be seen again.</summary>
    internal static void Reset() => Interlocked.Exchange(ref _warned, 0);
}

public static class AddressHelp
{
    public const int MaxSuggestions = 3, MaxQueryChars = 80, MaxLookupsPerCall = 5, MaxPartChars = 100;

    /// <summary>Text from a vendor is untrusted: one line, no control characters or quotes, bounded.</summary>
    public static string Clean(string? text, int max = MaxPartChars)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text ?? "") sb.Append(char.IsControl(ch) || ch is '"' or '\\' or '<' or '>' ? ' ' : ch);
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    public static AddressParts Clean(AddressParts p) => new(Clean(p.Line1), Clean(p.City, 60), Clean(p.Region, 2).ToUpperInvariant(), new string(Clean(p.PostalCode, 10).Where(c => char.IsAsciiDigit(c) || c == '-').ToArray()), Clean(p.Country, 2).ToUpperInvariant());

    /// <summary>How a suggestion is said aloud: the same speech rules as every other address read-back (numbers as words, the state by name).</summary>
    public static string Spoken(AddressParts p) => VoiceSpeech.Address(new CheckoutAddress(null, p.Line1, null, p.City, p.Region, p.PostalCode, p.Country, null));

    public static string? CleanPostal(string? postal)
    {
        var digits = new string((postal ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 5 or 9 ? digits[..5] : null;
    }
}

/// <summary>
/// <c>suggest_address</c> (decision V-61): from the few words the caller said (or the letters of the street they spelled) and their ZIP if they gave one, up to three real addresses are read back, one at a time. Offered
/// only while an address lookup is configured. It changes nothing: no address is taken, saved or confirmed by it.
/// </summary>
public sealed class SuggestAddressTool(IAddressLookup lookup, ILogger<SuggestAddressTool> log) : IConditionalVoiceTool
{
    public bool IsAvailable => lookup.IsConfigured;
    public string Name => "suggest_address";
    public JsonObject Definition => ToolKit.Def(Name,
        "Use ONLY when the caller is not sure how to give their delivery address, or the address you took was refused, or they ask for help with the street. From the few words they said (the house number and the start of the street, or the street name if they spelled it, joined into one word) and their ZIP code, it finds up to three real addresses. Read them to the caller ONE AT A TIME and ask if it is right; the caller must say yes. It does not take, save or confirm anything. If none is right, or the caller says no, take the address the usual way with set_delivery_address.",
        new JsonObject { ["text"] = ToolKit.Prop("string", "What the caller said of the address so far, for example the house number and the first letters of the street. Letters the caller spelled go together as one word."), ["postal_code"] = ToolKit.Prop("string", "The ZIP code, if the caller gave one.") }, "text");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level == CallerLevel.Anonymous) return ToolKit.Error("need_identity", "Before taking an address, verify the caller with identify_caller (a guest is fine).");
        var text = AddressHelp.Clean(ToolKit.Str(args, "text"), AddressHelp.MaxQueryChars);
        if (text.Length < 3) return ToolKit.Error("too_short", "Ask the caller for a little more of the address: the house number and the start of the street.");
        if (s.CountAddressLookup() > AddressHelp.MaxLookupsPerCall) return Fallback("too_many", "That is enough tries: take the address the usual way with set_delivery_address.");
        IReadOnlyList<AddressSuggestion> found;
        try { found = await lookup.SuggestAsync(new AddressQuery(text, AddressHelp.CleanPostal(ToolKit.Str(args, "postal_code"))), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("An address lookup failed ({Type}).", ex.GetType().Name);   // the type only: never the query, never the vendor's words
            return Fallback("address_help_failed", "Address help is not available just now: take the address the usual way with set_delivery_address.");
        }
        var kept = found.Where(f => !string.IsNullOrWhiteSpace(f.Id) && f.Id.Length <= 400).Take(AddressHelp.MaxSuggestions).Select(f => new AddressSuggestion(f.Id, AddressHelp.Clean(f.Preview))).Where(f => f.Preview.Line1.Length > 0).ToList();
        s.SetAddressSuggestions(kept);
        if (kept.Count == 0) return ToolKit.Json(new JsonObject { ["status"] = "none_found", ["instruction"] = "Say you could not find it, and take the address the usual way with set_delivery_address." });
        var arr = new JsonArray();
        for (var i = 0; i < kept.Count; i++) arr.Add(new JsonObject { ["position"] = i + 1, ["spoken"] = AddressHelp.Spoken(kept[i].Preview) });
        return ToolKit.Json(new JsonObject
        {
            ["status"] = "found", ["suggestions"] = arr,
            ["instruction"] = "Read the first one only, exactly as in spoken, and ask if that is the address. If the caller says no, read the next. When the caller says yes, call use_suggested_address with its position (and the apartment if they have one). If none is right, take the address the usual way with set_delivery_address. Never say an address that is not in this list.",
        });
    }

    private static VoiceToolResult Fallback(string code, string instruction) => ToolKit.Error(code, instruction);
}

/// <summary>
/// <c>use_suggested_address</c> (decision V-61): the caller said yes to one of the suggestions just read out. The lookup gives its full parts and they go through EXACTLY the same checks as an address the caller spelled,
/// and are held UNCONFIRMED: the caller still hears the whole address read back and must agree (<c>confirm_address</c>) before anything depends on it. An address the caller did not agree to is never kept as confirmed.
/// </summary>
public sealed class UseSuggestedAddressTool(IAddressLookup lookup, MarketplaceDb db, ILogger<UseSuggestedAddressTool> log) : IConditionalVoiceTool
{
    public bool IsAvailable => lookup.IsConfigured;
    public string Name => "use_suggested_address";
    public JsonObject Definition => ToolKit.Def(Name,
        "The caller said yes to one of the addresses you just read out with suggest_address. The server then reads the whole address back to be confirmed (call confirm_address only when they agree to that read-back).",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "Position of the address the caller said yes to."), ["apartment"] = ToolKit.Prop("string", "Apartment, suite or floor, if any."), ["recipient_name"] = ToolKit.Prop("string", "Only if the delivery is for someone other than the caller.") }, "position");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        var suggestions = s.AddressSuggestions;
        var pos = ToolKit.Int(args, "position", 0);
        if (pos < 1 || pos > suggestions.Count) return ToolKit.Error("no_such_position", "Read the suggestions first with suggest_address, then use the one the caller said yes to.");
        AddressParts? parts;
        try { parts = await lookup.ResolveAsync(suggestions[pos - 1].Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("An address lookup failed ({Type}).", ex.GetType().Name);
            return ToolKit.Error("address_help_failed", "Address help is not available just now: take the address the usual way with set_delivery_address.");
        }
        if (parts is null) return ToolKit.Error("address_help_failed", "That address could not be completed: take it the usual way with set_delivery_address.");
        var clean = AddressHelp.Clean(parts);
        s.SetAddressSuggestions([]);   // a suggestion is used once
        return await SetDeliveryAddressTool.TakeAsync(s, db, clean.Line1, ToolKit.Str(args, "apartment"), clean.City, clean.Region, clean.PostalCode, clean.Country, ToolKit.Str(args, "recipient_name"), ct);
    }
}
