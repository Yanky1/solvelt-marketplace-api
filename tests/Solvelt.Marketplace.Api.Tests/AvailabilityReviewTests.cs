using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Services;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The independent review of the availability proxy (eb27be3): the portal's per-service quota, extreme dates, suspended sellers, error caching headers, bad configuration, duplicate starts, and the
/// mutants that survived (every part of the question in the cache key, the 62-day cap, the shared call's cancellation, visibility, the per-address rate limit).
/// </summary>
public sealed class AvailabilityReviewTests : IDisposable
{
    private AuthKit _k;
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private static readonly Guid OtherService = CatalogKit.ListingId("window-cleaning");
    private static readonly Guid Product = CatalogKit.ListingId("challah-classic");
    private static readonly Guid Provider = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public AvailabilityReviewTests() => _k = new AuthKit();
    public void Dispose() => _k.Dispose();
    private void Restart(Dictionary<string, string?> config) { _k.Dispose(); _k = new AuthKit(config); }

    private int Calls(Guid? service = null) => _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains($"/services/{service ?? Service:D}/availability"));

    /// <summary>The portal's answer carries the window it was asked about, so a cache that forgets the window shows buyers the wrong days.</summary>
    private static HttpResponseMessage Echo(ReceivedCall c)
    {
        var q = System.Web.HttpUtility.ParseQueryString(c.PathAndQuery[(c.PathAndQuery.IndexOf('?') + 1)..]);
        var from = DateTimeOffset.Parse(q["from"]!);
        return FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            durationMinutes = 240, priceCents = 24000, providers = new[] { new { providerId = Provider, timeZone = "America/New_York" } },
            slots = new[] { new { startAt = from.AddHours(14), endAt = from.AddHours(18), providerIds = new[] { Provider } } },
        }));
    }

    private async Task Ready(Func<ReceivedCall, HttpResponseMessage>? portal = null)
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(portal ?? Echo);
    }

    private async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Raw)> Get(string query, Guid? id = null, CancellationToken ct = default)
    {
        var r = await _k.Http.GetAsync($"/api/v1/services/{id ?? Service}/availability?{query}", ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        return (r.StatusCode, s.Length > 0 ? JsonDocument.Parse(s).RootElement.Clone() : default, r);
    }

    [Fact]
    public async Task H1_the_marketplace_never_spends_more_than_its_own_share_of_the_portals_per_service_quota()
    {
        Restart(new() { ["Services:AvailabilityPortalCallsPerMinute"] = "5" });
        await Ready();
        // Ever-new queries (a new postal code each time) are cache misses. The sixth and later are refused here instead of reaching a portal that serves every shopper.
        var codes = Enumerable.Range(10000, 9).Select(i => i.ToString()).ToArray();
        var statuses = new List<HttpStatusCode>();
        foreach (var code in codes) statuses.Add((await Get($"location=customer&postalCode={code}&from=2026-12-09&to=2026-12-10")).Status);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.ServiceUnavailable));
        Assert.Equal(5, Calls());
        // a refusal is not cached, and the bucket refills continuously
        _k.F.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await Get($"location=customer&postalCode={codes[^1]}&from=2026-12-09&to=2026-12-10")).Status);
        // the quota is per service: another service is untouched
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2026-12-09&to=2026-12-10", OtherService)).Status);
        // ordinary shopping costs one token however many shoppers ask: cache hits and a joined in-flight call are free
        var before = Calls();
        var many = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Get("location=online&from=2026-12-11&to=2026-12-12")));
        Assert.All(many, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(before + 1, Calls());
    }

    [Fact]
    public async Task H1_coordinates_are_coarsened_so_probing_them_cannot_mint_cache_keys()
    {
        await Ready();
        for (var i = 0; i < 50; i++) await Get($"location=customer&lat=0.{i:D3}&lng=0.000&from=2026-12-09&to=2026-12-10");   // the reviewer's probe: lat 0.000 .. 0.049
        Assert.True(Calls() <= 6, $"50 probes of nearby coordinates made {Calls()} portal calls");
    }

    [Fact]
    public async Task M2_extreme_dates_are_a_422_not_a_500()
    {
        await Ready();
        foreach (var q in new[] { "from=9999-12-30", "to=9999-12-31T23:00:00Z", "from=9999-12-31T23:59:59Z&to=9999-12-31T23:59:59Z", "from=2026-12-09&to=2100-01-01" })
        {
            var (status, _, _) = await Get($"location=online&{q}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        }
        Assert.Equal(0, Calls());
        // a long-past start is simply moved to today
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=0001-01-01&to=2026-11-08")).Status);
    }

    [Fact]
    public async Task M3_a_suspended_sellers_service_is_not_found_everywhere_the_public_can_ask()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2026-12-09&to=2026-12-10")).Status);
        var seller = FixtureCatalogSource.Id("seller:hudson");
        await _k.Db(async db => { await db.CatalogSellers.Where(s => s.Id == seller).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false)); return 0; });
        _k.F.Clock.Advance(TimeSpan.FromMinutes(10));      // past every cache
        Assert.Equal(HttpStatusCode.NotFound, (await Get("location=online&from=2026-12-09&to=2026-12-10")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/services/{Service}/options")).StatusCode);
    }

    [Fact]
    public async Task L4_only_a_200_may_be_cached_and_the_staleness_budget_is_what_the_docs_say()
    {
        await Ready();
        var ok = await Get("location=online&from=2026-12-09&to=2026-12-10");
        Assert.Equal("public, max-age=15", ok.Raw.Headers.CacheControl!.ToString());
        foreach (var (query, id) in new (string, Guid)[] { ("location=moon", Service), ("location=online", Guid.NewGuid()), ("location=online", Product) })
        {
            var r = await Get(query, id);
            Assert.NotEqual(HttpStatusCode.OK, r.Status);
            Assert.Equal("no-store", r.Raw.Headers.CacheControl!.ToString());
        }
        _k.F.Portal.RespondAlways(_ => FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var down = await Get("location=online&from=2026-12-14&to=2026-12-15");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.Status);
        Assert.Equal("no-store", down.Raw.Headers.CacheControl!.ToString());
        var options = await _k.Http.GetAsync($"/api/v1/services/{Guid.NewGuid()}/options");
        Assert.Equal("no-store", options.Headers.CacheControl!.ToString());
        Assert.Contains("up to about 45 s", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/API.md")));
    }

    [Fact]
    public void L5_bad_configuration_is_refused_at_start_up_not_turned_into_500s()
    {
        static IReadOnlyList<string> Check(params (string, string)[] kv)
            => ServicesModule.ConfigProblems(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build());
        Assert.Empty(Check());
        Assert.Empty(Check(("Services:MaxWindowDays", "62"), ("Services:DefaultWindowDays", "30"), ("Services:AvailabilityCacheSeconds", "0"), ("Services:AvailabilityPortalCallsPerMinute", "600")));
        Assert.Single(Check(("Services:MaxWindowDays", "63")));
        Assert.Single(Check(("Services:MaxWindowDays", "0")));
        Assert.Single(Check(("Services:DefaultWindowDays", "0")));
        Assert.Single(Check(("Services:MaxWindowDays", "7"), ("Services:DefaultWindowDays", "14")));
        Assert.Single(Check(("Services:AvailabilityCacheSeconds", "31")));
        Assert.Single(Check(("Services:AvailabilityPortalCallsPerMinute", "601")));
        Assert.Single(Check(("Services:AvailabilityPortalCallsPerMinute", "0")));
        Assert.Single(Check(("Services:MaxWindowDays", "many")));
        // and the real validator, in every environment, carries them
        var problems = StartupValidator.Validate(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:MaxWindowDays"] = "120" }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" });
        Assert.Contains(problems, p => p.Contains("Services:MaxWindowDays"));
    }

    [Fact]
    public async Task L6_a_start_listed_twice_keeps_every_provider()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await Ready(_ => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            durationMinutes = 120, priceCents = 100, providers = new[] { new { providerId = a, timeZone = "UTC" }, new { providerId = b, timeZone = "UTC" } },
            slots = new object[]
            {
                new { startAt = "2026-12-09T14:00:00Z", endAt = "2026-12-09T16:00:00Z", providerIds = new[] { a } },
                new { startAt = "2026-12-09T14:00:00Z", endAt = "2026-12-09T16:00:00Z", providerIds = new[] { b, a } },
                new { startAt = "2026-12-09T14:00:00Z", endAt = "2026-12-09T17:00:00Z", providerIds = new[] { b } },
            },
        })));
        var (_, j, _) = await Get("location=online&from=2026-12-09&to=2026-12-10");
        var slots = j.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(2, slots.Count);                                              // two different ends stay two slots
        Assert.Equal(2, slots[0].GetProperty("providerIds").GetArrayLength());     // the same start and end merges its providers
        Assert.Equal(1, slots[1].GetProperty("providerIds").GetArrayLength());
    }

    [Fact]
    public async Task L7_every_part_of_the_question_is_part_of_the_cache_key_and_equivalent_spellings_share_one()
    {
        await Ready();
        const string baseQ = "location=customer&postalCode=11201&lat=40.69&lng=-73.99&minutes=120&from=2026-12-09&to=2026-12-12";
        var first = await Get(baseQ);
        Assert.Equal(1, Calls());
        var firstStart = first.Json.GetProperty("slots")[0].GetProperty("startAt").GetDateTimeOffset();
        // the WINDOW: a different `to` (and `from`) must be a different question with different days in the answer
        var wider = await Get(baseQ.Replace("to=2026-12-12", "to=2026-12-20"));
        Assert.Equal(2, Calls());
        Assert.Equal(firstStart, wider.Json.GetProperty("slots")[0].GetProperty("startAt").GetDateTimeOffset());   // same from: same first day
        var later = await Get(baseQ.Replace("from=2026-12-09", "from=2026-12-10"));
        Assert.Equal(3, Calls());
        Assert.Equal(firstStart.AddDays(1), later.Json.GetProperty("slots")[0].GetProperty("startAt").GetDateTimeOffset());   // the cache did not serve the 9th for a question about the 10th
        // every other dimension
        await Get(baseQ.Replace("postalCode=11201", "postalCode=11211")); Assert.Equal(4, Calls());
        await Get(baseQ.Replace("lat=40.69", "lat=40.72")); Assert.Equal(5, Calls());
        await Get(baseQ.Replace("lng=-73.99", "lng=-73.95")); Assert.Equal(6, Calls());
        await Get(baseQ.Replace("minutes=120", "minutes=180")); Assert.Equal(7, Calls());
        await Get(baseQ.Replace("location=customer", "location=seller")); Assert.Equal(8, Calls());
        await Get(baseQ + $"&providerId={Provider}"); Assert.Equal(9, Calls());
        await Get(baseQ + "&addOns=deep"); Assert.Equal(10, Calls());
        // equivalent spellings do NOT cost another call
        await Get(baseQ.Replace("lat=40.69", "lat=40.694"));                     // within the 2-decimal coarsening
        await Get(baseQ.Replace("from=2026-12-09", "from=2026-12-09T20:15:00Z"));  // same day
        await Get(baseQ + "&addOns=");
        Assert.Equal(10, Calls());
        // the postal code is upper-cased: UK-style codes differ only in case
        var mixed = "location=customer&postalCode=sw1a%201aa&from=2026-12-09&to=2026-12-10";
        await Get(mixed); var n = Calls();
        await Get(mixed.Replace("sw1a%201aa", "SW1A%201AA"));
        Assert.Equal(n, Calls());
    }

    [Fact]
    public async Task L7_the_cap_is_exactly_62_days()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2026-11-04&to=2027-01-05")).Status);                    // 62 days
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Get("location=online&from=2026-11-04&to=2027-01-06")).Status);  // 63 days
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Get("location=online&from=2026-11-05&to=2027-01-07")).Status);
    }

    [Fact]
    public async Task L7_the_first_callers_disconnect_does_not_cancel_the_call_the_others_are_waiting_for()
    {
        var gate = new TaskCompletionSource();
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAsync(async (c, _) => { await gate.Task; return Echo(c); });
        using var cts = new CancellationTokenSource();
        var q = "location=online&from=2026-12-09&to=2026-12-12";
        var first = Get(q, ct: cts.Token);
        var second = Get(q);
        await Task.Delay(300);
        cts.Cancel();                                                              // the first shopper closes the tab
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await second).Status);
        Assert.Equal(1, Calls());
    }

    [Fact]
    public async Task L7_non_services_and_the_per_address_rate_limit_are_enforced()
    {
        Restart(new() { ["RateLimits:PublicPerMinute"] = "3" });
        await Ready();
        Assert.Equal(HttpStatusCode.NotFound, (await Get("location=online", Product)).Status);                   // a real product listing, not a service
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await Get("location=online&from=2026-12-09&to=2026-12-10")).Status);
        var limited = await Get("location=online&from=2026-12-09&to=2026-12-10");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
    }
}
