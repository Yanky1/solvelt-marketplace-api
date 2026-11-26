using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The two Google adapters of the street search (decision V-63), against a recording handler built from the facts in docs/agent-briefs/google-places-facts.md.</summary>
public class GoogleGeocodingTests
{
    private const string Key = "KEY-SECRET-123";

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private sealed class ListLoggerOf<T>(ListLogger inner) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };
    private static IOptions<VoiceOptions> Opts(string key = Key) => Options.Create(new VoiceOptions { Google = new GoogleVoiceOptions { MapsApiKey = key } });

    private const string Albany = """
        {"status":"OK","results":[{"address_components":[{"long_name":"12207","short_name":"12207","types":["postal_code"]},{"long_name":"Albany","short_name":"Albany","types":["locality","political"]},{"long_name":"New York","short_name":"NY","types":["administrative_area_level_1","political"]},{"long_name":"United States","short_name":"US","types":["country","political"]}],
          "geometry":{"viewport":{"northeast":{"lat":42.67,"lng":-73.74},"southwest":{"lat":42.64,"lng":-73.77}}}}]}
        """;

    private static (GoogleGeocoder G, Recorder R, ListLogger Log, ZipViewportCache Cache, FakeTimeProvider Time) Geo(Func<HttpRequestMessage, string, HttpResponseMessage> answer, string key = Key)
    {
        var rec = new Recorder(answer);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var cache = new ZipViewportCache(time);
        var log = new ListLogger();
        return (new GoogleGeocoder(new HttpClient(rec), Opts(key), cache, new ListLoggerOf<GoogleGeocoder>(log)), rec, log, cache, time);
    }

    [Fact]
    public async Task Geocoding_is_the_documented_get_with_the_zip_the_country_component_once_and_the_key()
    {
        var (g, rec, _, cache, _) = Geo((_, _) => Json(Albany));
        var info = await g.FindAsync("12207", CancellationToken.None);
        var req = Assert.Single(rec.Seen).Req;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://maps.googleapis.com/maps/api/geocode/json", req.RequestUri!.GetLeftPart(UriPartial.Path));
        var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        Assert.Equal(["address", "components", "key"], q.AllKeys.Order());                                            // nothing else, and the component filters are not repeated
        Assert.Equal(("12207", "country:US", Key), (q["address"], q["components"], q["key"]));
        Assert.Equal(new ZipInfo("Albany", "NY", new GeoViewport(42.64, -73.77, 42.67, -73.74)), info);              // city = locality (long name), state = the short name, the viewport corners
        Assert.Equal(new GeoViewport(42.64, -73.77, 42.67, -73.74), cache.Get("12207"));                              // the viewport is cached
    }

    [Theory]
    [InlineData("""{"status":"OK","results":[{"address_components":[{"long_name":"London","short_name":"London","types":["postal_town"]},{"long_name":"NY","short_name":"NY","types":["administrative_area_level_1"]}]}]}""", "London")]
    [InlineData("""{"status":"OK","results":[{"address_components":[{"long_name":"Brooklyn","short_name":"Brooklyn","types":["sublocality","sublocality_level_1"]},{"long_name":"New York","short_name":"ny","types":["administrative_area_level_1"]}]}]}""", "Brooklyn")]
    public async Task The_city_is_the_locality_else_the_postal_town_else_the_sublocality_and_a_missing_viewport_is_fine(string json, string city)
    {
        var (g, _, _, cache, _) = Geo((_, _) => Json(json));
        var info = await g.FindAsync("11201", CancellationToken.None);
        Assert.Equal((city, "NY"), (info!.City, info.State));
        Assert.Null(info.Viewport); Assert.Null(cache.Get("11201"));
    }

    [Fact]
    public async Task Zero_results_is_a_real_answer_no_such_zip_and_every_other_status_is_unavailable_never_a_bad_zip()
    {
        Assert.Null(await Geo((_, _) => Json("""{"status":"ZERO_RESULTS","results":[]}""")).G.FindAsync("00001", CancellationToken.None));
        foreach (var status in new[] { "OVER_DAILY_LIMIT", "OVER_QUERY_LIMIT", "REQUEST_DENIED", "INVALID_REQUEST", "UNKNOWN_ERROR", "SOMETHING_NEW" })
        {
            var (g, _, log, _, _) = Geo((_, _) => Json($$"""{"status":"{{status}}","error_message":"The provided API key {{Key}} is invalid for 12207"}"""));
            await Assert.ThrowsAsync<LookupUnavailableException>(() => g.FindAsync("12207", CancellationToken.None));
            Assert.DoesNotContain("invalid for", string.Join("\n", log.Lines)); Assert.DoesNotContain(Key, string.Join("\n", log.Lines));   // error_message is never logged
        }
        foreach (var bad in new[] { "not json", "{}", "[]", """{"status":"OK","results":[]}""", """{"status":"OK","results":[{"address_components":[]}]}""", """{"status":"OK","results":[{"address_components":[{"long_name":"X","short_name":"X","types":["locality"]}]}]}""" })
        {
            var (g, _, _, _, _) = Geo((_, _) => Json(bad));
            try { var r = await g.FindAsync("12207", CancellationToken.None); Assert.Null(r); }                       // an empty result list is "no such ZIP"
            catch (LookupUnavailableException) { }                                                                     // everything else that is not an answer is unavailable
        }
        var (g500, _, _, _, _) = Geo((_, _) => Json("{}", HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<LookupUnavailableException>(() => g500.FindAsync("12207", CancellationToken.None));   // a 200 is not success by itself, and neither is anything else
        await Assert.ThrowsAsync<LookupUnavailableException>(() => Geo((_, _) => Json(Albany), key: "").G.FindAsync("12207", CancellationToken.None));
    }

    [Fact]
    public async Task The_key_is_in_the_url_so_the_url_the_request_and_the_exception_text_never_reach_a_log_or_an_exception()
    {
        var (g, _, log, _, _) = Geo((req, _) => throw new HttpRequestException($"Name or service not known ({req.RequestUri})"));
        var ex = await Assert.ThrowsAsync<LookupUnavailableException>(() => g.FindAsync("12207", CancellationToken.None));
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.Null(ex.InnerException);
        var (g2, _, log2, _, _) = Geo((_, _) => Json("{}", HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<LookupUnavailableException>(() => g2.FindAsync("12207", CancellationToken.None));
        foreach (var lines in new[] { string.Join("\n", log.Lines), string.Join("\n", log2.Lines) })
        {
            Assert.DoesNotContain(Key, lines); Assert.DoesNotContain("googleapis", lines); Assert.DoesNotContain("12207", lines);
        }
        Assert.Contains("HttpRequestException", string.Join("\n", log.Lines));                                         // only the type
        Assert.Contains("403", string.Join("\n", log2.Lines));                                                         // or the status
    }

    [Fact]
    public void Viewports_are_kept_seven_days_in_memory_and_then_gone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var cache = new ZipViewportCache(time);
        cache.Put("12207", new GeoViewport(1, 2, 3, 4));
        time.Advance(TimeSpan.FromDays(6.9));
        Assert.NotNull(cache.Get("12207"));
        time.Advance(TimeSpan.FromDays(0.2));
        Assert.Null(cache.Get("12207"));
        Assert.Equal(TimeSpan.FromDays(7), ZipViewportCache.Lifetime);
    }

    private static (GoogleStreetSuggest S, Recorder R, ListLogger Log) Streets(Func<HttpRequestMessage, string, HttpResponseMessage> answer, string key = Key)
    {
        var rec = new Recorder(answer);
        var log = new ListLogger();
        return (new GoogleStreetSuggest(new HttpClient(rec), Opts(key), new ListLoggerOf<GoogleStreetSuggest>(log)), rec, log);
    }

    private static string Prediction(string? main, string? full = null)
        => "{\"placePrediction\":{\"place\":\"places/X\",\"placeId\":\"X\"" + (full is null ? "" : $",\"text\":{{\"text\":\"{full}\"}}") + (main is null ? "" : $",\"structuredFormat\":{{\"mainText\":{{\"text\":\"{main}\"}}}}") + "}}";

    [Fact]
    public async Task Street_autocomplete_is_the_documented_post_with_route_the_region_a_viewport_restriction_and_no_session_token()
    {
        var (s, rec, _) = Streets((_, _) => Json("{\"suggestions\":[" + Prediction("Mazel Lane") + "]}"));
        await s.StreetsAsync("maz", new GeoViewport(42.64, -73.77, 42.67, -73.74), CancellationToken.None);
        var (req, body) = Assert.Single(rec.Seen);
        Assert.Equal((HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete"), (req.Method, req.RequestUri!.ToString()));
        Assert.Equal(Key, Assert.Single(req.Headers.GetValues("X-Goog-Api-Key")));
        Assert.DoesNotContain(Key, req.RequestUri.ToString());                                                         // this key is NOT in the URL
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        var j = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(new[] { "includedPrimaryTypes", "includedRegionCodes", "input", "languageCode", "locationRestriction" }, j.Select(p => p.Key).Order().ToArray());   // no sessionToken, no locationBias
        Assert.Equal("maz", j["input"]!.GetValue<string>());
        Assert.Equal(["route"], j["includedPrimaryTypes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["us"], j["includedRegionCodes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("en", j["languageCode"]!.GetValue<string>());
        var rect = j["locationRestriction"]!["rectangle"]!;
        Assert.Equal((42.64, -73.77), (rect["low"]!["latitude"]!.GetValue<double>(), rect["low"]!["longitude"]!.GetValue<double>()));     // low = southwest
        Assert.Equal((42.67, -73.74), (rect["high"]!["latitude"]!.GetValue<double>(), rect["high"]!["longitude"]!.GetValue<double>()));    // high = northeast
    }

    [Fact]
    public async Task With_no_viewport_only_the_region_is_sent_never_an_empty_rectangle_and_never_a_bias()
    {
        var (s, rec, _) = Streets((_, _) => Json("{}"));
        Assert.Empty(await s.StreetsAsync("maz", null, CancellationToken.None));
        var j = JsonNode.Parse(rec.Seen[0].Body)!.AsObject();
        Assert.Equal(new[] { "includedPrimaryTypes", "includedRegionCodes", "input", "languageCode" }, j.Select(p => p.Key).Order().ToArray());
    }

    [Fact]
    public async Task Up_to_five_street_names_from_the_main_text_else_the_text_before_the_first_comma_without_duplicates()
    {
        var items = string.Join(",", new[]
        {
            Prediction("Mazel Lane", "Mazel Lane, Albany, NY, USA"), Prediction(null, "Maple Avenue, Albany, NY, USA"), Prediction("MAZEL LANE"), Prediction("Main Street"), "{\"queryPrediction\":{\"text\":{\"text\":\"maz\"}}}",
            Prediction("Mason Road"), Prediction("Maple Court"), Prediction("Marsh Way"), Prediction("Third Street"),
        });
        var (s, _, _) = Streets((_, _) => Json("{\"suggestions\":[" + items + "]}"));
        Assert.Equal(["Mazel Lane", "Maple Avenue", "Main Street", "Mason Road", "Maple Court"], await s.StreetsAsync("ma", null, CancellationToken.None));   // five, case-insensitive duplicates and query predictions out
    }

    [Fact]
    public async Task A_street_search_failure_is_unavailable_and_neither_the_words_nor_an_answer_reach_a_log()
    {
        foreach (var answer in new Func<HttpRequestMessage, string, HttpResponseMessage>[] { (_, _) => Json("{\"error\":{\"message\":\"bad Mazel request\"}}", HttpStatusCode.BadRequest), (_, _) => Json("not json"), (_, _) => throw new HttpRequestException("Mazel unreachable") })
        {
            var (s, _, log) = Streets(answer);
            await Assert.ThrowsAsync<LookupUnavailableException>(() => s.StreetsAsync("mazel", null, CancellationToken.None));
            var lines = string.Join("\n", log.Lines);
            Assert.DoesNotContain("Mazel", lines, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(Key, lines);
        }
        await Assert.ThrowsAsync<LookupUnavailableException>(() => Streets((_, _) => Json("{}"), key: "").S.StreetsAsync("maz", null, CancellationToken.None));
        Assert.False(Streets((_, _) => Json("{}"), key: " ").S.IsConfigured);
        Assert.Equal("Mazel Lane b", AddressHelp.Clean("Mazel \"Lane\"\n<b>", 80));                                         // vendor text is cleaned
    }
}
