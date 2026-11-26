using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Address suggestions from Google's Places API (New) (decision V-62). Everything here follows the facts the orchestrator read on 2026-11-26 and wrote to <c>docs/agent-briefs/google-places-facts.md</c>; what that file marks
/// NOT STATED is not in this code. Legacy Places cannot be enabled for new projects, so only the New API is used.
/// <list type="bullet">
/// <item>Autocomplete: <c>POST https://places.googleapis.com/v1/places:autocomplete</c>, JSON body (<c>input</c>, <c>includedRegionCodes</c>, <c>regionCode</c>, <c>languageCode</c>, <c>sessionToken</c>), header <c>X-Goog-Api-Key</c>. No <c>includedPrimaryTypes</c>: which types mean a street address is not stated. A region is always sent (otherwise Google biases by OUR server's IP).</item>
/// <item>Place Details: <c>GET https://places.googleapis.com/v1/places/{placeId}?sessionToken=..&amp;languageCode=en&amp;regionCode=us</c> with the REQUIRED <c>X-Goog-FieldMask</c>. Only Essentials fields that are used are asked for (<c>addressComponents,formattedAddress,postalAddress</c>), never <c>displayName</c> (a dearer tier), never the location (not needed, and its storage is limited by the terms).</item>
/// <item>Session tokens: version 4 UUIDs, one per capture attempt in a call, ended by the Details request; the call id never goes to Google. Google's session lifetime is NOT STATED, so a token idle for ten minutes (our own bound) is replaced.</item>
/// <item>The key travels only in the header, never in a URL; no request, answer or text is logged; a refusal is logged by its HTTP status only.</item>
/// <item>The exact sub-field names of <c>addressComponents</c> and <c>postalAddress</c> were NOT FETCHED: they are read defensively and the mapping is a SHAPE ASSUMED, to be verified on the first real call (VOICE.md section 19). The first answers are logged by field NAMES only, never values.</item>
/// </list>
/// </summary>
public sealed class GoogleAddressLookup(HttpClient http, IOptions<VoiceOptions> options, AddressLookupState state, ILogger<GoogleAddressLookup> log) : IAddressLookup
{
    private const string Host = "https://places.googleapis.com";
    private string Key => options.Value.Google.MapsApiKey?.Trim() ?? "";
    public bool IsConfigured => Key.Length > 0;
    public string? UnavailableReason => IsConfigured ? null : "Voice:Google:MapsApiKey is not set";
    public string? Attribution => options.Value.Google.SpokenAttribution ? "According to Google Maps" : null;

    public async Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(AddressQuery query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["input"] = string.IsNullOrWhiteSpace(query.PostalCode) ? query.Text : query.Text + " " + query.PostalCode,
            ["includedRegionCodes"] = new JsonArray("us"),
            ["regionCode"] = "us",
            ["languageCode"] = "en",
            ["sessionToken"] = state.TokenFor(query.Session),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Host + "/v1/places:autocomplete") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Goog-Api-Key", Key);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) { log.LogWarning("The address lookup was refused ({Status}).", (int)res.StatusCode); throw new HttpRequestException("status " + (int)res.StatusCode); }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var found = new List<AddressSuggestion>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("suggestions", out var list) || list.ValueKind != JsonValueKind.Array) return found;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("placePrediction", out var p) || p.ValueKind != JsonValueKind.Object) continue;   // a query prediction is not an address
            var id = Str(p, "placeId") ?? Str(p, "place")?.Replace("places/", "", StringComparison.Ordinal);
            if (id is null || id.Length is 0 or > 300 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) continue;
            var main = Path(p, "structuredFormat", "mainText", "text") ?? Path(p, "text", "text");
            if (string.IsNullOrWhiteSpace(main)) continue;
            found.Add(new AddressSuggestion(id, new AddressParts(main, "", "", ""), Path(p, "structuredFormat", "secondaryText", "text")));   // the secondary text may be missing
            if (found.Count >= AddressHelp.MaxSuggestions) break;
        }
        return found;
    }

    public async Task<AddressParts?> ResolveAsync(string suggestionId, string? session, CancellationToken ct)
    {
        if (suggestionId.Length is 0 or > 300 || !suggestionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        var token = state.End(session);   // the Details request ENDS the session; the next attempt gets a fresh token
        var url = $"{Host}/v1/places/{Uri.EscapeDataString(suggestionId)}?languageCode=en&regionCode=us" + (token is null ? "" : "&sessionToken=" + Uri.EscapeDataString(token));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Goog-Api-Key", Key);
        req.Headers.Add("X-Goog-FieldMask", "addressComponents,formattedAddress,postalAddress");   // required: there is no default list; no spaces allowed
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) { log.LogWarning("The address lookup was refused ({Status}).", (int)res.StatusCode); throw new HttpRequestException("status " + (int)res.StatusCode); }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        LogFieldNames(doc.RootElement);
        return ToParts(doc.RootElement);
    }

    /// <summary>SHAPE ASSUMED (see the class comment): street from the components (or the first address line), city, state and ZIP from the postal address or the components. Anything missing gives null and the usual flow takes over.</summary>
    internal static AddressParts? ToParts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? Component(params string[] types)
        {
            if (!root.TryGetProperty("addressComponents", out var comps) || comps.ValueKind != JsonValueKind.Array) return null;
            foreach (var c in comps.EnumerateArray())
            {
                if (!c.TryGetProperty("types", out var t) || t.ValueKind != JsonValueKind.Array) continue;
                var names = t.EnumerateArray().Select(x => x.GetString()).ToList();
                if (types.Any(names.Contains)) return Str(c, "longText") ?? Str(c, "shortText");
            }
            return null;
        }
        string? ComponentShort(string type)
        {
            if (!root.TryGetProperty("addressComponents", out var comps) || comps.ValueKind != JsonValueKind.Array) return null;
            foreach (var c in comps.EnumerateArray())
                if (c.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.GetString() == type)) return Str(c, "shortText") ?? Str(c, "longText");
            return null;
        }
        JsonElement postal = default;
        var hasPostal = root.TryGetProperty("postalAddress", out postal) && postal.ValueKind == JsonValueKind.Object;
        var number = Component("street_number"); var route = Component("route");
        var line1 = !string.IsNullOrWhiteSpace(route) ? (string.IsNullOrWhiteSpace(number) ? route : number + " " + route)
            : hasPostal && postal.TryGetProperty("addressLines", out var lines) && lines.ValueKind == JsonValueKind.Array ? lines.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;
        var city = (hasPostal ? Str(postal, "locality") : null) ?? Component("locality", "postal_town", "sublocality_level_1", "sublocality");
        var region = (hasPostal ? Str(postal, "administrativeArea") : null) ?? ComponentShort("administrative_area_level_1");
        var zip = (hasPostal ? Str(postal, "postalCode") : null) ?? Component("postal_code");
        var country = ((hasPostal ? Str(postal, "regionCode") : null) ?? ComponentShort("country") ?? "US").ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(line1) || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(zip) || country != "US") return null;
        var digits = new string(zip.Where(char.IsAsciiDigit).ToArray());
        return digits.Length < 5 ? null : new AddressParts(line1, city, region.Trim().ToUpperInvariant(), digits[..5], "US");
    }

    /// <summary>The first real answers: which fields came back (NAMES only, never a value), so the assumed shape can be checked against the truth.</summary>
    private void LogFieldNames(JsonElement root)
    {
        if (!state.ShouldLogFieldNames() || root.ValueKind != JsonValueKind.Object) return;
        static IEnumerable<string> Names(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => p.Name) : [];
        var first = root.TryGetProperty("addressComponents", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 ? c[0] : default;
        log.LogInformation("Address lookup details fields (names only): root [{Root}], postalAddress [{Postal}], addressComponents item [{Component}].",
            string.Join(",", Names(root)), root.TryGetProperty("postalAddress", out var pa) ? string.Join(",", Names(pa)) : "", string.Join(",", Names(first)));
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Path(JsonElement e, params string[] names)
    {
        foreach (var n in names[..^1]) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out e)) return null; }
        return Str(e, names[^1]);
    }
}

/// <summary>
/// What the Google lookup must remember between requests, as a singleton (the HTTP client is created per use): the token of each capture attempt (a version 4 UUID, one per attempt in a call, ended by the Details request,
/// replaced after ten idle minutes because Google's session lifetime is NOT STATED) and how many times the field names have been logged.
/// </summary>
public sealed class AddressLookupState(TimeProvider time)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Last)> _sessions = new();
    private int _namesLogged;

    public string TokenFor(string? session)
    {
        var now = time.GetUtcNow();
        if (session is null) return Guid.NewGuid().ToString("D");
        if (_sessions.Count > 1000) foreach (var kv in _sessions) if (now - kv.Value.Last > Idle) _sessions.TryRemove(kv.Key, out _);
        return _sessions.AddOrUpdate(session, _ => (Guid.NewGuid().ToString("D"), now), (_, old) => now - old.Last > Idle ? (Guid.NewGuid().ToString("D"), now) : (old.Token, now)).Token;
    }

    /// <summary>The token to end the attempt with (null when there was none); the next attempt starts a new one.</summary>
    public string? End(string? session) => session is not null && _sessions.TryRemove(session, out var e) ? e.Token : null;

    public int Open => _sessions.Count;
    public bool ShouldLogFieldNames() => Interlocked.Increment(ref _namesLogged) <= 3;
}
