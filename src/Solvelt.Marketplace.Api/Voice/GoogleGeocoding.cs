using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// ZIP code viewports kept for seven days at most (decision V-63): a latitude and longitude box is the one piece of Google content the terms let us cache (up to 30 consecutive days); city and state are NOT kept across calls.
/// In memory only, bounded.
/// </summary>
public sealed class ZipViewportCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private const int MaxEntries = 5000;
    private readonly ConcurrentDictionary<string, (GeoViewport Box, DateTimeOffset At)> _items = new();

    public GeoViewport? Get(string zip5) => _items.TryGetValue(zip5, out var e) && time.GetUtcNow() - e.At < Lifetime ? e.Box : null;

    public void Put(string zip5, GeoViewport box)
    {
        var now = time.GetUtcNow();
        if (_items.Count >= MaxEntries) foreach (var kv in _items) if (now - kv.Value.At >= Lifetime) _items.TryRemove(kv.Key, out _);
        if (_items.Count < MaxEntries) _items[zip5] = (box, now);
    }

    public int Count => _items.Count;
}

/// <summary>
/// ZIP code to city, state and viewport with Google's Geocoding API (https://developers.google.com/maps/documentation/geocoding/requests-geocoding, read 2026-11-26): <c>GET https://maps.googleapis.com/maps/api/geocode/json?address=ZIP&amp;components=country:US&amp;key=KEY</c>.
/// THE KEY IS IN THE URL: the URL, the request and any exception message are never logged, and no exception from the HTTP stack leaves this class (an own exception without a message from the vendor is thrown instead); a refusal logs its
/// HTTP status only. The component filters are not repeated (that gives INVALID_REQUEST). <c>status</c>: OK and ZERO_RESULTS are real answers (ZERO_RESULTS = no such ZIP); every other status (OVER_DAILY_LIMIT,
/// OVER_QUERY_LIMIT, REQUEST_DENIED, INVALID_REQUEST, UNKNOWN_ERROR) is "unavailable", never "bad ZIP", and <c>error_message</c> is never logged or spoken. A 200 is not success by itself.
/// </summary>
public sealed class GoogleGeocoder(HttpClient http, IOptions<VoiceOptions> options, ZipViewportCache viewports, ILogger<GoogleGeocoder> log) : IPostcodeLookup
{
    private string Key => options.Value.Google.MapsApiKey?.Trim() ?? "";
    public bool IsConfigured => Key.Length > 0;

    public async Task<ZipInfo?> FindAsync(string zip5, CancellationToken ct)
    {
        if (!IsConfigured) throw new LookupUnavailableException();
        var url = "https://maps.googleapis.com/maps/api/geocode/json?address=" + Uri.EscapeDataString(zip5) + "&components=" + Uri.EscapeDataString("country:US") + "&key=" + Uri.EscapeDataString(Key);
        string body;
        try
        {
            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("The ZIP code check was refused ({Status}).", (int)res.StatusCode); throw new LookupUnavailableException(); }
            body = await res.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LookupUnavailableException) { throw; }
        catch (Exception ex) { log.LogWarning("The ZIP code check could not be reached ({Type}).", ex.GetType().Name); throw new LookupUnavailableException(); }   // the exception's own text can hold the URL, and the key is in it
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            if (status == "ZERO_RESULTS") return null;
            if (status != "OK") { log.LogWarning("The ZIP code check gave no answer (status {Status}).", Safe(status)); throw new LookupUnavailableException(); }
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
            var first = results[0];
            string? city = null, postalTown = null, sublocality = null, state = null;
            if (first.TryGetProperty("address_components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                {
                    if (!c.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array) continue;
                    var names = types.EnumerateArray().Select(x => x.GetString()).ToList();
                    if (names.Contains("locality")) city ??= Str(c, "long_name");
                    else if (names.Contains("postal_town")) postalTown ??= Str(c, "long_name");
                    else if (names.Contains("sublocality") || names.Contains("sublocality_level_1")) sublocality ??= Str(c, "long_name");
                    if (names.Contains("administrative_area_level_1")) state ??= Str(c, "short_name");
                }
            city ??= postalTown ?? sublocality;
            if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(state)) throw new LookupUnavailableException();   // an answer without a city or a state is not an answer
            GeoViewport? box = null;
            if (first.TryGetProperty("geometry", out var geo) && geo.TryGetProperty("viewport", out var vp) && vp.ValueKind == JsonValueKind.Object
                && Corner(vp, "southwest") is { } sw && Corner(vp, "northeast") is { } ne) box = new GeoViewport(sw.Lat, sw.Lng, ne.Lat, ne.Lng);
            if (box is not null) viewports.Put(zip5, box);
            return new ZipInfo(city.Trim(), state.Trim().ToUpperInvariant(), box);
        }
        catch (JsonException) { log.LogWarning("The ZIP code check gave an answer that could not be read."); throw new LookupUnavailableException(); }
    }

    private static string Safe(string? status) => status is "OVER_DAILY_LIMIT" or "OVER_QUERY_LIMIT" or "REQUEST_DENIED" or "INVALID_REQUEST" or "UNKNOWN_ERROR" ? status : "other";
    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static (double Lat, double Lng)? Corner(JsonElement e, string name)
        => e.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("lat", out var la) && la.ValueKind == JsonValueKind.Number && c.TryGetProperty("lng", out var lo) && lo.ValueKind == JsonValueKind.Number
            ? (la.GetDouble(), lo.GetDouble()) : null;
}

/// <summary>
/// Street names with Google's Places API (New) Autocomplete (https://developers.google.com/maps/documentation/places/web-service/place-autocomplete, .../place-types, read 2026-11-26): <c>POST https://places.googleapis.com/v1/places:autocomplete</c>,
/// header <c>X-Goog-Api-Key</c> (not in the URL), body <c>input</c>, <c>includedPrimaryTypes ["route"]</c> ("route" is a Table B type, filterable only by <c>includedPrimaryTypes</c>), <c>includedRegionCodes ["us"]</c>, <c>languageCode en</c> and,
/// when a viewport is known, a <c>locationRestriction</c> rectangle (low = southwest, high = northeast; both set, never empty; never together with a bias). With no viewport only the region is sent. NO session token: there is no Place Details
/// request here to end a session, so a token buys nothing (an incomplete session is billed per autocomplete request anyway). Up to five predictions; each name is <c>structuredFormat.mainText.text</c>, else <c>text.text</c> up to the first comma;
/// names are de-duplicated without regard to case. Nothing of the answer is logged or kept.
/// </summary>
public sealed class GoogleStreetSuggest(HttpClient http, IOptions<VoiceOptions> options, ILogger<GoogleStreetSuggest> log) : IStreetSuggest
{
    public const int MaxStreets = 5;
    private string Key => options.Value.Google.MapsApiKey?.Trim() ?? "";
    public bool IsConfigured => Key.Length > 0;

    public async Task<IReadOnlyList<string>> StreetsAsync(string partial, GeoViewport? viewport, CancellationToken ct)
    {
        if (!IsConfigured) throw new LookupUnavailableException();
        var body = new JsonObject { ["input"] = partial, ["includedPrimaryTypes"] = new JsonArray("route"), ["includedRegionCodes"] = new JsonArray("us"), ["languageCode"] = "en" };
        if (viewport is { } v)
            body["locationRestriction"] = new JsonObject { ["rectangle"] = new JsonObject
            {
                ["low"] = new JsonObject { ["latitude"] = v.SouthwestLat, ["longitude"] = v.SouthwestLng },
                ["high"] = new JsonObject { ["latitude"] = v.NortheastLat, ["longitude"] = v.NortheastLng },
            } };
        string text;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Goog-Api-Key", Key);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("The street search was refused ({Status}).", (int)res.StatusCode); throw new LookupUnavailableException(); }
            text = await res.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LookupUnavailableException) { throw; }
        catch (Exception ex) { log.LogWarning("The street search could not be reached ({Type}).", ex.GetType().Name); throw new LookupUnavailableException(); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var streets = new List<string>();
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("suggestions", out var list) || list.ValueKind != JsonValueKind.Array) return streets;
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("placePrediction", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                var name = Path(p, "structuredFormat", "mainText", "text");
                if (string.IsNullOrWhiteSpace(name)) { var full = Path(p, "text", "text"); name = full?.Split(',')[0]; }
                name = AddressHelp.Clean(name, 80);
                if (name.Length == 0 || streets.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) continue;
                streets.Add(name);
                if (streets.Count >= MaxStreets) break;
            }
            return streets;
        }
        catch (JsonException) { log.LogWarning("The street search gave an answer that could not be read."); throw new LookupUnavailableException(); }
    }

    private static string? Path(JsonElement e, params string[] names)
    {
        foreach (var n in names) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out e)) return null; }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}

/// <summary>Text from a vendor is untrusted: one line, no control characters, quotes or angle brackets, bounded.</summary>
public static class AddressHelp
{
    public static string Clean(string? text, int max = 100)
    {
        var sb = new StringBuilder();
        foreach (var ch in text ?? "") sb.Append(char.IsControl(ch) || ch is '"' or '\\' or '<' or '>' ? ' ' : ch);
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max].TrimEnd() : t;
    }
}
