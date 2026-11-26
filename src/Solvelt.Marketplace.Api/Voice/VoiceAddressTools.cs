using System.Text.Json;
using System.Text.Json.Nodes;
using Solvelt.Marketplace.Api.Checkout;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>ZIP code to city and state for ONE call (decision V-63): the answer is kept in the call's memory only, so the same ZIP is asked about once per call and never across calls. A ZIP that does not exist is not kept.</summary>
public sealed class ZipResolver(IPostcodeLookup lookup)
{
    public async Task<ZipInfo?> ResolveAsync(VoiceCallState s, string zip5, CancellationToken ct)
    {
        if (s.Zip(zip5) is { } known) return known;
        var info = await lookup.FindAsync(zip5, ct);
        if (info is not null) s.RememberZip(zip5, info);
        return info;
    }
}

/// <summary>What the save-time check decided for the tools that take an address: either a refusal to give back to the model, or the address to read back (and a message for the model about why it differs).</summary>
public sealed record AddressGateResult(CheckoutAddress? Address, VoiceToolResult? Refusal, string? Message);

public static class AddressGate
{
    /// <summary>
    /// Runs the check (decision V-63). Incomplete and inconsistent addresses are refused (and the latter counted); a corrected one goes on with Google's city and state, to be read back and confirmed; after
    /// <see cref="AddressValidator.MaxAttempts"/> corrections the address as given goes on (recorded as <c>proceeded_after_retries</c>; never for an incomplete one). Nothing here saves anything: the caller's yes does.
    /// </summary>
    public static async Task<AddressGateResult> RunAsync(VoiceCallState s, CheckoutAddress address, ZipResolver zips, IPostcodeLookup postcode, ILogger log, CancellationToken ct)
    {
        var v = await AddressValidator.ValidateAsync(address, s.AddressRetries, postcode.IsConfigured, (zip, c) => zips.ResolveAsync(s, zip, c), ct);
        if (v.ProceededAfterRetries)
        {
            s.SetAddressProceeded();
            log.LogInformation("An address was taken as given after {Max} corrections (proceeded_after_retries).", AddressValidator.MaxAttempts);   // no address in the line
            return new(address, null, null);
        }
        switch (v.Status)
        {
            case AddressStatus.Incomplete:
                return new(null, ToolKit.Json(new JsonObject
                {
                    ["error"] = "incomplete_address", ["message"] = v.Message, ["missing"] = new JsonArray(v.Missing.Select(m => (JsonNode)new JsonObject { ["field"] = m.Field, ["problem"] = m.Problem }).ToArray()),
                    ["instruction"] = "Ask for what is still needed in one natural sentence. Never name a tool or a system.",
                }), null);
            case AddressStatus.Inconsistent:
                s.CountAddressRetry();
                return new(null, ToolKit.Json(new JsonObject { ["error"] = "inconsistent_address", ["message"] = v.Message, ["instruction"] = "Tell the caller what the message says in your own words, ask them to say the ZIP code again, and never say that two checks disagree." }), null);
            case AddressStatus.Corrected:
                s.CountAddressRetry();
                return new(v.Normalized, null, v.Message);
            default:
                return new(v.Normalized ?? address, null, null);
        }
    }
}

/// <summary>The one warning, once per process (the V-26 rule): without the Google key the ZIP check and the street search are off and addresses are taken the usual way.</summary>
public static class AddressLookupStartup
{
    private static int _warned;
    public static void WarnOnce(IPostcodeLookup lookup, bool voiceEnabled, ILogger log)
    {
        if (!voiceEnabled || lookup.IsConfigured || Interlocked.Exchange(ref _warned, 1) == 1) return;
        log.LogWarning("The street search and the ZIP code check on the telephone line are not available: Voice:Google:MapsApiKey is not set. Addresses are taken the usual way (the offline completeness check still works).");
    }
    internal static void Reset() => Interlocked.Exchange(ref _warned, 0);
}

/// <summary>
/// <c>find_street</c> (decision V-63), from the telephone system's own design: the caller gives a ZIP code and the whole or the first letters of their street; the answer is up to five street names near that ZIP AND the city and state
/// of the ZIP, so afterwards only the house number (and the apartment) is still needed. Offered only while the Google key is set; any failure says the street search is not available and the usual way of taking an address goes on.
/// </summary>
public sealed class FindStreetTool(ZipResolver zips, IPostcodeLookup postcode, IStreetSuggest streets, ZipViewportCache viewports, ILogger<FindStreetTool> log) : IConditionalVoiceTool
{
    public const int MaxPerCall = 8;
    public bool IsAvailable => postcode.IsConfigured && streets.IsConfigured;
    public string Name => "find_street";
    public JsonObject Definition => ToolKit.Def(Name,
        "Find the street the caller lives on. For an address: ask for the ZIP code first, then the street, like this: \"What's your street name? You can tell me the whole name, or just the first few letters and I'll find it for you.\" Pass whatever they give, whole or partial. It returns the matching streets AND the city and state of that ZIP code, so after that you only need the house number and the apartment, if any. Never ask for a city or state you were already given. Ask for what is still needed in one natural sentence. Never name a tool, a lookup, a vendor or a system, and never say that two checks disagree.",
        new JsonObject { ["zip"] = ToolKit.Prop("string", "The five digit ZIP code the caller gave."), ["partial_street"] = ToolKit.Prop("string", "The whole street name, or just the first few letters, as the caller said it.") }, "zip", "partial_street");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level == CallerLevel.Anonymous) return ToolKit.Error("need_identity", "Before taking an address, verify the caller with identify_caller (a guest is fine).");
        var zip = AddressShape.ZipFive(ToolKit.Str(args, "zip"));
        if (zip is null) return ToolKit.Error("bad_zip", "Ask the caller to say the five digit ZIP code again.");
        var partial = AddressHelp.Clean(ToolKit.Str(args, "partial_street"), 40);
        if (partial.Length == 0) return ToolKit.Error("need_street", "Ask for the street name, or just the first few letters of it.");
        if (s.CountStreetLookup() > MaxPerCall) return Unavailable();
        ZipInfo? info;
        IReadOnlyList<string> found;
        try
        {
            info = await zips.ResolveAsync(s, zip, ct);
            if (info is null) return ToolKit.Json(new JsonObject { ["error"] = "zip_not_found", ["zip"] = zip, ["message"] = $"ZIP code {VoiceSpeech.Digits(zip)} does not appear to exist.", ["instruction"] = "Ask the caller to check the ZIP code and say it again." });
            found = await streets.StreetsAsync(partial, info.Viewport ?? viewports.Get(zip), ct);
        }
        catch (LookupUnavailableException) { return Unavailable(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("A street search failed ({Type}).", ex.GetType().Name);   // the type only: never the words, the ZIP or the vendor's answer
            return Unavailable();
        }
        var place = $"{VoiceSpeech.Digits(zip)} is {AddressHelp.Clean(info.City, 60)}, {VoiceSpeech.State(info.State)}.";
        var result = new JsonObject { ["zip"] = zip, ["city"] = AddressHelp.Clean(info.City, 60), ["state"] = info.State, ["matches"] = new JsonArray(found.Select(f => (JsonNode)f).ToArray()) };
        result["instruction"] = found.Count == 0
            ? $"No streets near that ZIP code start with '{partial}'. ZIP code {place} Ask them to check the ZIP code or give a different part of the street name."
            : $"Read the first one or two names naturally and ask which is theirs. You now have the city and state; do not ask for them. Then you only need the house number and the apartment.";
        return ToolKit.Json(result);
    }

    private static VoiceToolResult Unavailable() => ToolKit.Error("street_lookup_unavailable", "Street lookup is not available just now: take the address the usual way with set_delivery_address, asking for the street, the city, the state and the ZIP code.");
}

/// <summary>
/// <c>check_address</c> (decision V-63): an advisory check that changes nothing, from the same rule (<see cref="AddressShape"/>) the save-time check uses. Needs no network, so it works whether or not the lookup is configured.
/// </summary>
public sealed class CheckAddressTool : IVoiceTool
{
    public string Name => "check_address";
    public JsonObject Definition => ToolKit.Def(Name,
        "Check whether what you have so far of the delivery address is complete. It only advises and changes nothing. A complete address has a house number, a street, a city, a state and a ZIP code of five digits. Use it before set_delivery_address when you are not sure you have everything.",
        new JsonObject
        {
            ["house_number"] = ToolKit.Prop("string", "The house number."), ["street"] = ToolKit.Prop("string", "The street name."), ["city"] = ToolKit.Prop("string", "The city."),
            ["state"] = ToolKit.Prop("string", "The state."), ["zip"] = ToolKit.Prop("string", "The ZIP code."),
        });

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var missing = AddressShape.Check(ToolKit.Str(args, "house_number"), ToolKit.Str(args, "street"), ToolKit.Str(args, "city"), ToolKit.Str(args, "state"), ToolKit.Str(args, "zip"));
        var r = new JsonObject { ["complete"] = missing.Count == 0 };
        if (missing.Count > 0)
        {
            r["missing"] = new JsonArray(missing.Select(m => (JsonNode)new JsonObject { ["field"] = m.Field, ["problem"] = m.Problem }).ToArray());
            r["instruction"] = "Ask for what is still needed in one natural sentence. Never name a tool or a system.";
        }
        else r["instruction"] = "Nothing is missing: take it with set_delivery_address.";
        return Task.FromResult(ToolKit.Json(r));
    }
}
