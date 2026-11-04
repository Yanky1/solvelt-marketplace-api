using System.Net;
using System.Text.Json;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 1: the availability proxy (cache, single-flight, validation, time zones).</summary>
public sealed class AvailabilityTests : IDisposable
{
    private readonly AuthKit _k = new();
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private static readonly Guid Product = CatalogKit.ListingId("challah-classic");
    private static readonly Guid Provider = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public void Dispose() => _k.Dispose();

    private int AvailabilityCalls => _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains("/availability"));

    // New York, 9-17 local: 14:00Z in winter, 13:00Z in summer. The portal sends UTC instants; the zone is for display.
    private static string Body(params string[] starts) => JsonSerializer.Serialize(new
    {
        durationMinutes = 240, priceCents = 24000,
        providers = new[] { new { providerId = Provider, timeZone = "America/New_York" } },
        slots = starts.Select(s => new { startAt = s, endAt = DateTimeOffset.Parse(s).AddHours(4), providerIds = new[] { Provider } }),
    });

    private async Task<(HttpStatusCode Status, JsonElement Json)> Get(string query, Guid? id = null)
    {
        var r = await _k.Http.GetAsync($"/api/v1/services/{id ?? Service}/availability?{query}");
        var s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, JsonDocument.Parse(s).RootElement.Clone());
    }

    private async Task Ready(Func<ReceivedCall, HttpResponseMessage>? portal = null)
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(portal ?? (_ => FakePortal.Json(HttpStatusCode.OK, Body("2026-12-09T14:00:00Z", "2026-12-09T18:00:00Z"))));
    }

    [Fact]
    public async Task Returns_the_portal_slots_with_the_provider_time_zone_unchanged()
    {
        await Ready();
        var (status, j) = await Get("location=customer&postalCode=11201&from=2026-12-09&to=2026-12-10");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("America/New_York", j.GetProperty("providers")[0].GetProperty("timeZone").GetString());
        Assert.Equal(2, j.GetProperty("slots").GetArrayLength());
        Assert.Equal(DateTimeOffset.Parse("2026-12-09T14:00:00Z"), j.GetProperty("slots")[0].GetProperty("startAt").GetDateTimeOffset());
        Assert.Equal(24000, j.GetProperty("priceCents").GetInt64());
    }

    [Fact]
    public async Task Summer_and_winter_slots_keep_their_utc_instants_and_are_sorted_and_deduplicated()
    {
        // 9:00 local is 13:00Z in July (EDT) and 14:00Z in December (EST). The proxy must not shift or re-zone anything, only sort and drop duplicates.
        await Ready(_ => FakePortal.Json(HttpStatusCode.OK, Body("2027-07-09T13:00:00Z", "2027-03-14T13:00:00Z", "2027-03-13T14:00:00Z", "2027-03-14T13:00:00Z")));
        _k.F.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.Zero));
        var (_, j) = await Get("location=seller&from=2027-03-13&to=2027-03-15");
        var starts = j.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startAt").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd HH:mm")).ToArray();
        Assert.Equal(["2027-03-13 14:00", "2027-03-14 13:00", "2027-07-09 13:00"], starts);   // spring-forward 2027-03-14: 9:00 local moves from 14:00Z to 13:00Z
    }

    [Fact]
    public async Task Parallel_identical_requests_make_one_portal_call_and_the_next_burst_within_30s_makes_none()
    {
        var gate = new TaskCompletionSource();
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAsync(async (_, _) => { await gate.Task; return FakePortal.Json(HttpStatusCode.OK, Body("2026-12-09T14:00:00Z")); });
        var url = "location=online&from=2026-12-09&to=2026-12-12";
        var burst = Enumerable.Range(0, 25).Select(_ => Get(url)).ToArray();
        await Task.Delay(300);                     // every request is now waiting on the one in-flight call
        gate.SetResult();
        var results = await Task.WhenAll(burst);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, AvailabilityCalls);

        _k.F.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Get(url)));
        Assert.Equal(1, AvailabilityCalls);        // cached

        _k.F.Clock.Advance(TimeSpan.FromSeconds(2));
        await Get(url);
        Assert.Equal(2, AvailabilityCalls);        // expired after 30 s
    }

    [Fact]
    public async Task Different_questions_are_different_cache_entries_and_equivalent_spellings_share_one()
    {
        await Ready();
        await Get("location=online&from=2026-12-09&to=2026-12-12");
        await Get("location=seller&from=2026-12-09&to=2026-12-12");                                    // other location
        await Get($"location=online&from=2026-12-09&to=2026-12-12&providerId={Provider}");              // other provider
        await Get("location=online&from=2026-12-09&to=2026-12-12&addOns=deep");                         // other add-on
        Assert.Equal(4, AvailabilityCalls);
        await Get("location=ONLINE&from=2026-12-09T17:30:00Z&to=2026-12-11T01:00:00Z");                 // same days after snapping
        await Get("location=online&from=2026-12-09&to=2026-12-12&addOns=");
        Assert.Equal(4, AvailabilityCalls);
        await Get("location=online&from=2026-12-09&to=2026-12-12&addOns=b,a");
        await Get("location=online&from=2026-12-09&to=2026-12-12&addOns=a,b,a");                        // add-on order and repeats do not matter
        Assert.Equal(5, AvailabilityCalls);
    }

    [Fact]
    public async Task A_failed_portal_answer_is_not_cached_and_the_buyer_sees_a_generic_retryable_error()
    {
        var down = true;
        await Ready(_ => down ? FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakePortal.Json(HttpStatusCode.OK, Body("2026-12-09T14:00:00Z")));
        var url = "location=online&from=2026-12-09&to=2026-12-12";
        var first = await Get(url);
        Assert.Contains("availability", first.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.Status);
        down = false;
        var second = await Get(url);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(1, second.Json.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task Portal_validation_errors_are_translated_and_not_cached()
    {
        await Ready(_ => FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"outside_service_area\",\"message\":\"internal detail 10.0.0.7\"}"));
        var (status, j) = await Get("location=customer&postalCode=99999&from=2026-12-09&to=2026-12-10");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("outside_service_area", j.GetProperty("code").GetString());
        Assert.DoesNotContain("10.0.0.7", j.ToString());
        await Get("location=customer&postalCode=99999&from=2026-12-09&to=2026-12-10");
        Assert.Equal(2, AvailabilityCalls);
    }

    [Fact]
    public async Task Products_unknown_ids_and_bad_input_never_reach_the_portal()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.NotFound, (await Get("location=online", Product)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("location=online", Guid.NewGuid())).Status);
        foreach (var bad in new[]
        {
            "", "location=moon", "location=online&from=nope", "location=online&from=2026-12-09&to=2026-12-08",
            "location=online&from=2026-12-01&to=2027-03-01",                    // > 62 days
            "location=customer&lat=91&lng=0", "location=customer&lat=1", "location=online&minutes=0", "location=online&minutes=99999",
            "location=online&providerId=x", "location=online&addOns=a;b", "location=online&postalCode=%3Cscript%3E",
        })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Get(bad)).Status);
        Assert.Equal(0, AvailabilityCalls);
    }

    [Fact]
    public async Task Exactly_62_days_is_allowed_and_a_start_in_the_past_is_moved_to_today()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2026-11-04&to=2027-01-05")).Status);   // 62 days
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2020-01-01&to=2026-11-06")).Status);
        var past = _k.F.Portal.VerifiedCalls.Last().PathAndQuery;
        Assert.Contains("from=2026-11-04T00%3A00%3A00Z", past);   // the fake clock says 2026-11-04
    }
}
