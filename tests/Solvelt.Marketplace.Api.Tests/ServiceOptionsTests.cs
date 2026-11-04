using System.Net;
using System.Text.Json;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4: the service options the widget needs (add-ons, intake form, providers, policy): cached, single-flight, bounded, and tolerant of a portal that has not shipped the endpoint.</summary>
public sealed class ServiceOptionsTests : IDisposable
{
    private readonly AuthKit _k = new();
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private static readonly Guid Product = CatalogKit.ListingId("challah-classic");
    public void Dispose() => _k.Dispose();
    private int Calls => _k.F.Portal.VerifiedCalls.Count(c => c.PathAndQuery.EndsWith("/options"));

    private async Task<(HttpStatusCode Status, JsonElement Json)> Get(Guid? id = null)
    {
        var r = await _k.Http.GetAsync($"/api/v1/services/{id ?? Service}/options");
        return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Fact]
    public async Task The_portals_document_is_cut_down_to_safe_bounded_values()
    {
        await CatalogKit.Sync(_k.F);
        var provider = Guid.NewGuid();
        _k.F.Portal.RespondAlways(_ => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            serviceId = Service, version = "v7", secretInternalNote = "do not leak",
            addOns = new object[] { new { id = "deep", name = "Deep clean", priceCents = 3000, extraMinutes = 60 }, new { id = "bad id!", name = "x", priceCents = 1, extraMinutes = 1 }, new { id = "neg", name = "Negative", priceCents = -5, extraMinutes = 0 } },
            intakeQuestions = new object[] { new { id = "pets", label = "Any pets?", kind = "yesNo", required = true }, new { id = "free", label = new string('x', 900), kind = "script", required = false }, new { id = "pick", label = "Pick", kind = "choice", required = false, choices = Enumerable.Range(0, 40).Select(i => "c" + i) } },
            providers = new object[] { new { providerId = provider, displayName = "Dana L.", internalEmail = "dana@portal.example" }, new { providerId = Guid.Empty, displayName = "Nobody" } },
            assignment = "buyerChoice", policy = new { cancelFreeHours = 24, cancelFeePercent = 250, noShowFeePercent = 50, maxReschedules = 2, approvalHours = 12 },
            travel = new { feeCents = 1500, serviceAreaPostalCodes = new[] { "11201", "11211" } },
        })));
        var (status, j) = await Get();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(j.GetProperty("available").GetBoolean());
        Assert.Equal("deep", Assert.Single(j.GetProperty("addOns").EnumerateArray()).GetProperty("id").GetString());     // bad id and negative price dropped
        var qs = j.GetProperty("intakeQuestions").EnumerateArray().ToList();
        Assert.Equal("yesNo", qs[0].GetProperty("kind").GetString());
        Assert.Equal("text", qs[1].GetProperty("kind").GetString());                                   // unknown kind becomes plain text
        Assert.Equal(300, qs[1].GetProperty("label").GetString()!.Length);                              // clipped
        Assert.Equal(20, qs[2].GetProperty("choices").GetArrayLength());
        Assert.Single(j.GetProperty("providers").EnumerateArray());
        Assert.Equal("buyerChoice", j.GetProperty("assignment").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("policy").GetProperty("cancelFeePercent").ValueKind);   // 250 % is not a percentage
        Assert.Equal(24, j.GetProperty("policy").GetProperty("cancelFreeHours").GetInt32());
        Assert.Equal(1500, j.GetProperty("travelFeeCents").GetInt64());
        var text = j.ToString();
        Assert.DoesNotContain("do not leak", text); Assert.DoesNotContain("dana@portal.example", text);
    }

    [Fact]
    public async Task A_portal_without_the_endpoint_gives_no_options_cached_for_a_minute_and_a_burst_is_one_call()
    {
        await CatalogKit.Sync(_k.F);
        var gate = new TaskCompletionSource();
        _k.F.Portal.RespondAsync(async (_, _) => { await gate.Task; return FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}"); });
        var burst = Enumerable.Range(0, 20).Select(_ => Get()).ToArray();
        await Task.Delay(300);
        gate.SetResult();
        var rs = await Task.WhenAll(burst);
        Assert.All(rs, r => { Assert.Equal(HttpStatusCode.OK, r.Status); Assert.False(r.Json.GetProperty("available").GetBoolean()); Assert.Empty(r.Json.GetProperty("addOns").EnumerateArray()); });
        Assert.Equal(1, Calls);
        _k.F.Clock.Advance(TimeSpan.FromSeconds(59));
        await Get();
        Assert.Equal(1, Calls);
        _k.F.Clock.Advance(TimeSpan.FromSeconds(2));
        await Get();
        Assert.Equal(2, Calls);
    }

    [Fact]
    public async Task A_real_document_is_cached_five_minutes_an_outage_is_a_503_that_is_not_cached_and_non_services_are_404()
    {
        await CatalogKit.Sync(_k.F);
        var down = true;
        _k.F.Portal.RespondAlways(_ => down ? FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakePortal.Json(HttpStatusCode.OK, "{\"addOns\":[{\"id\":\"a\",\"name\":\"A\",\"priceCents\":100,\"extraMinutes\":15}]}"));
        var first = await Get();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.Status);
        Assert.Equal("options_unavailable", first.Json.GetProperty("code").GetString());
        down = false;
        Assert.Equal(HttpStatusCode.OK, (await Get()).Status);
        var calls = Calls;
        _k.F.Clock.Advance(TimeSpan.FromMinutes(4));
        await Get();
        Assert.Equal(calls, Calls);                                   // still cached
        _k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        await Get();
        Assert.Equal(calls + 1, Calls);
        var before = Calls;
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Product)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Guid.NewGuid())).Status);
        Assert.Equal(before, Calls);
    }
}
