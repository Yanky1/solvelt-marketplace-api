using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The Google adapter (decision V-62), against a recording handler built from the facts in docs/agent-briefs/google-places-facts.md. The answer shapes are the documented ones for autocomplete; the details sub-fields are a SHAPE ASSUMED (to be checked on the first real call).</summary>
public class GoogleAddressLookupTests
{
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

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };

    private const string AutocompleteAnswer = """
        {"suggestions":[
          {"placePrediction":{"place":"places/ChIJmazel","placeId":"ChIJmazel","text":{"text":"12 Mazel Lane, Brooklyn, NY, USA","matches":[{"endOffset":3}]},"structuredFormat":{"mainText":{"text":"12 Mazel Lane","matches":[]},"secondaryText":{"text":"Brooklyn, NY, USA"}},"types":["street_address"]}},
          {"queryPrediction":{"text":{"text":"12 mazel lane restaurants"}}},
          {"placePrediction":{"place":"places/ChIJoak","placeId":"ChIJoak","text":{"text":"14 Oak Avenue"},"structuredFormat":{"mainText":{"text":"14 Oak Avenue"}}}},
          {"placePrediction":{"place":"places/ChIJpine","text":{"text":"16 Pine Street, Brooklyn"}}},
          {"placePrediction":{"place":"places/ChIJelm","placeId":"ChIJelm","structuredFormat":{"mainText":{"text":"18 Elm Street"}}}}
        ]}
        """;

    private const string DetailsAnswer = """
        {"formattedAddress":"12 Mazel Ln, Brooklyn, NY 11201, USA",
         "addressComponents":[{"longText":"12","shortText":"12","types":["street_number"]},{"longText":"Mazel Lane","shortText":"Mazel Ln","types":["route"]},{"longText":"Brooklyn","shortText":"Brooklyn","types":["sublocality_level_1","sublocality"]},{"longText":"New York","shortText":"New York","types":["locality"]},{"longText":"New York","shortText":"NY","types":["administrative_area_level_1"]},{"longText":"11201","shortText":"11201","types":["postal_code"]},{"longText":"United States","shortText":"US","types":["country"]}],
         "postalAddress":{"regionCode":"US","postalCode":"11201-1234","administrativeArea":"NY","locality":"Brooklyn","addressLines":["12 Mazel Ln"]}}
        """;

    private static (GoogleAddressLookup L, Recorder R, ListLogger Log, FakeTimeProvider T, AddressLookupState State) Make(Func<HttpRequestMessage, string, HttpResponseMessage>? answer = null, string key = "KEY-123", bool attribution = true)
    {
        var rec = new Recorder(answer ?? ((r, _) => r.Method == HttpMethod.Post ? Json(AutocompleteAnswer) : Json(DetailsAnswer)));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var state = new AddressLookupState(time);
        var log = new ListLogger();
        var opts = Options.Create(new VoiceOptions { Google = new GoogleVoiceOptions { MapsApiKey = key, SpokenAttribution = attribution } });
        return (new GoogleAddressLookup(new HttpClient(rec), opts, state, new ListLoggerOf<GoogleAddressLookup>(log)), rec, log, time, state);
    }

    private sealed class ListLoggerOf<T>(ListLogger inner) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static bool IsV4Uuid(string? s) => Guid.TryParse(s, out _) && s!.Length == 36 && s[14] == '4';

    [Fact]
    public async Task Autocomplete_is_the_documented_request_a_post_with_the_key_in_a_header_a_region_and_a_session_token_and_nothing_else()
    {
        var (l, rec, _, _, _) = Make();
        var found = await l.SuggestAsync(new AddressQuery("12 mazel", "11201", "call-1"), CancellationToken.None);
        var (req, body) = Assert.Single(rec.Seen);
        Assert.Equal((HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete"), (req.Method, req.RequestUri!.ToString()));
        Assert.Equal("KEY-123", Assert.Single(req.Headers.GetValues("X-Goog-Api-Key")));
        Assert.False(req.Headers.Contains("X-Goog-FieldMask"));                                                        // optional here: not sent
        Assert.DoesNotContain("KEY-123", req.RequestUri.ToString());                                                    // the key never travels in a URL
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        var j = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(new[] { "includedRegionCodes", "input", "languageCode", "regionCode", "sessionToken" }, j.Select(p => p.Key).Order().ToArray());   // no includedPrimaryTypes (types NOT STATED), no location bias or restriction
        Assert.Equal("12 mazel 11201", j["input"]!.GetValue<string>());
        Assert.Equal(["us"], j["includedRegionCodes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(("us", "en"), (j["regionCode"]!.GetValue<string>(), j["languageCode"]!.GetValue<string>()));
        Assert.True(IsV4Uuid(j["sessionToken"]!.GetValue<string>()));
        Assert.DoesNotContain("call-1", body);                                                                          // our call key never reaches Google
        Assert.Equal(3, found.Count);                                                                                   // a query prediction is skipped; at most three addresses
        Assert.Equal(("ChIJmazel", "12 Mazel Lane", "Brooklyn, NY, USA"), (found[0].Id, found[0].Preview.Line1, found[0].Secondary));
        Assert.Equal(("ChIJoak", "14 Oak Avenue", null), (found[1].Id, found[1].Preview.Line1, found[1].Secondary));   // the secondary text may be missing
        Assert.Equal(("ChIJpine", "16 Pine Street, Brooklyn"), (found[2].Id, found[2].Preview.Line1));                  // only "place": the id is taken from it, the text from the full text
    }

    [Fact]
    public async Task One_session_token_per_capture_attempt_the_details_request_ends_it_and_an_idle_one_is_replaced()
    {
        var (l, rec, _, time, state) = Make();
        string Token(int i) => JsonNode.Parse(rec.Seen[i].Body)!["sessionToken"]!.GetValue<string>();
        await l.SuggestAsync(new AddressQuery("12 maz", null, "a"), CancellationToken.None);
        await l.SuggestAsync(new AddressQuery("12 mazel", null, "a"), CancellationToken.None);
        await l.SuggestAsync(new AddressQuery("12 maz", null, "b"), CancellationToken.None);
        Assert.Equal(Token(0), Token(1));                                                                               // the same attempt: the same token
        Assert.NotEqual(Token(0), Token(2));                                                                            // another call: its own
        await l.ResolveAsync("ChIJmazel", "a", CancellationToken.None);
        var details = rec.Seen[3].Req;
        Assert.Equal(Token(0), System.Web.HttpUtility.ParseQueryString(details.RequestUri!.Query)["sessionToken"]);     // the Details request carries it and ENDS the session
        await l.SuggestAsync(new AddressQuery("14 oak", null, "a"), CancellationToken.None);
        Assert.NotEqual(Token(0), Token(4));                                                                            // a fresh token after a Details call
        Assert.True(IsV4Uuid(Token(4)));
        time.Advance(TimeSpan.FromMinutes(11));
        await l.SuggestAsync(new AddressQuery("14 oak", null, "a"), CancellationToken.None);
        Assert.NotEqual(Token(4), Token(5));                                                                            // an abandoned attempt's token is not reused after ten idle minutes
        Assert.True(state.Open >= 1);
        await l.SuggestAsync(new AddressQuery("x street", null, null), CancellationToken.None);
        Assert.NotEqual(Token(5), Token(6));                                                                            // no key: a token of its own each time
    }

    [Fact]
    public async Task Details_is_the_documented_request_with_the_required_field_mask_of_essentials_only_and_maps_the_address()
    {
        var (l, rec, _, _, _) = Make();
        await l.SuggestAsync(new AddressQuery("12 maz", null, "a"), CancellationToken.None);
        var parts = await l.ResolveAsync("ChIJmazel", "a", CancellationToken.None);
        var req = rec.Seen[1].Req;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://places.googleapis.com/v1/places/ChIJmazel", req.RequestUri!.GetLeftPart(UriPartial.Path));
        var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        Assert.Equal(("en", "us"), (q["languageCode"], q["regionCode"]));
        Assert.True(IsV4Uuid(q["sessionToken"]));
        Assert.Equal("KEY-123", Assert.Single(req.Headers.GetValues("X-Goog-Api-Key")));
        var mask = Assert.Single(req.Headers.GetValues("X-Goog-FieldMask"));
        Assert.Equal("addressComponents,formattedAddress,postalAddress", mask);                                         // required, no spaces
        Assert.DoesNotContain("displayName", mask); Assert.DoesNotContain("location", mask);                            // nothing dearer than Essentials, no location
        Assert.DoesNotContain("KEY-123", req.RequestUri.ToString());
        Assert.Equal(new AddressParts("12 Mazel Lane", "Brooklyn", "NY", "11201", "US"), parts);                       // 5-digit ZIP, the street from the components
        Assert.Null(await l.ResolveAsync("bad id/with?chars", "a", CancellationToken.None));                           // a malformed id is never sent
        Assert.Equal(2, rec.Seen.Count);
    }

    [Fact]
    public void The_details_mapping_is_defensive_an_incomplete_or_foreign_address_is_null_and_the_usual_flow_takes_over()
    {
        AddressParts? Map(string json) { using var d = JsonDocument.Parse(json); return GoogleAddressLookup.ToParts(d.RootElement.Clone()); }
        Assert.Equal(new AddressParts("5 Elm St", "Albany", "NY", "12207", "US"), Map("""{"postalAddress":{"regionCode":"US","postalCode":"12207","administrativeArea":"NY","locality":"Albany","addressLines":["5 Elm St"]}}"""));   // the postal address alone is enough
        Assert.Equal(new AddressParts("7 Oak Ave", "Troy", "NY", "12180", "US"), Map("""{"addressComponents":[{"longText":"7","types":["street_number"]},{"longText":"Oak Ave","types":["route"]},{"longText":"Troy","types":["locality"]},{"shortText":"NY","longText":"New York","types":["administrative_area_level_1"]},{"longText":"12180","types":["postal_code"]}]}"""));   // so are the components
        Assert.Null(Map("""{"postalAddress":{"regionCode":"US","postalCode":"12207","administrativeArea":"NY","locality":"Albany"}}"""));                  // no street
        Assert.Null(Map("""{"postalAddress":{"regionCode":"US","administrativeArea":"NY","locality":"Albany","addressLines":["5 Elm St"]}}"""));            // no ZIP
        Assert.Null(Map("""{"postalAddress":{"regionCode":"GB","postalCode":"SW1A 1AA","administrativeArea":"ENG","locality":"London","addressLines":["1 Mall"]}}"""));   // not a US address
        Assert.Null(Map("""{"postalAddress":{"regionCode":"US","postalCode":"122","administrativeArea":"NY","locality":"Albany","addressLines":["5 Elm St"]}}"""));  // a ZIP too short
        Assert.Null(Map("[]")); Assert.Null(Map("{}")); Assert.Null(Map("""{"addressComponents":"nope","postalAddress":5}"""));
    }

    [Fact]
    public async Task A_refusal_is_an_exception_with_the_status_only_and_neither_the_key_nor_any_body_or_address_reaches_a_log()
    {
        var (l, _, log, _, _) = Make((_, _) => Json("""{"error":{"code":403,"message":"The 12 Mazel Lane request is denied for key KEY-123"}}""", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => l.SuggestAsync(new AddressQuery("12 Mazel Lane", "11201", "a"), CancellationToken.None));
        Assert.Equal("status 403", ex.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => l.ResolveAsync("ChIJmazel", "a", CancellationToken.None));
        var lines = string.Join("\n", log.Lines);
        Assert.Contains("403", lines);
        foreach (var secret in new[] { "KEY-123", "Mazel", "denied", "11201" }) Assert.DoesNotContain(secret, lines);
    }

    [Fact]
    public async Task The_first_real_answers_are_logged_by_field_names_only_never_values_and_only_three_times()
    {
        var (l, _, log, _, _) = Make();
        for (var i = 0; i < 5; i++) await l.ResolveAsync("ChIJmazel", "s" + i, CancellationToken.None);
        var lines = log.Lines.Where(x => x.Contains("fields (names only)")).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains("addressComponents", lines[0]); Assert.Contains("postalAddress", lines[0]); Assert.Contains("longText", lines[0]); Assert.Contains("locality", lines[0]);
        foreach (var value in new[] { "Mazel", "Brooklyn", "11201", "12 Mazel" }) Assert.DoesNotContain(value, string.Join("\n", log.Lines));
    }

    [Fact]
    public void The_lookup_is_configured_only_with_a_key_and_the_spoken_credit_follows_its_setting()
    {
        Assert.True(Make().L.IsConfigured); Assert.False(Make(key: "").L.IsConfigured); Assert.False(Make(key: "  ").L.IsConfigured);
        Assert.Equal("According to Google Maps", Make().L.Attribution); Assert.Null(Make(attribution: false).L.Attribution);
        Assert.Contains("MapsApiKey", Make(key: "").L.UnavailableReason);
        Assert.Null(Make().L.UnavailableReason);
    }

    [Fact]
    public async Task With_a_key_the_app_offers_the_two_tools_and_without_one_it_does_not()
    {
        using var withKey = new TestFactory(VoiceFactory.Config(new() { ["Voice:Google:MapsApiKey"] = "test-key" }));
        var names = withKey.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("suggest_address", names); Assert.Contains("use_suggested_address", names);
        Assert.IsType<GoogleAddressLookup>(withKey.Services.GetRequiredService<IAddressLookup>());
        using var without = new TestFactory(VoiceFactory.Config());
        Assert.DoesNotContain("suggest_address", without.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()));
        Assert.False(without.Services.GetRequiredService<IAddressLookup>().IsConfigured);
        await Task.CompletedTask;
    }
}
