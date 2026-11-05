using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Portal C11 + C12 (portal main f482df0, N7): the options document as the portal really sends it, the provider rule, and the policy that rides on booking events (decision M4-24).</summary>
public sealed class PortalOptionsC12Tests : BookingFlowBase
{
    private static readonly Guid Pv = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static string Doc(string assignment = "buyerChoice") => JsonSerializer.Serialize(new
    {
        serviceId = Service, version = 1840,                                          // a NUMBER, as the portal sends it
        addOns = new object[] { new { id = "deep", name = "Deep clean", priceCents = 2500, extraMinutes = 30 } },     // no description
        intakeQuestions = new object[] { new { id = "pets", label = "Any pets?", kind = "yesNo", required = false }, new { id = "size", label = "Home size", kind = "choice", required = true, choices = new[] { "S", "M", "L" } } },
        providers = new object[] { new { providerId = Pv, displayName = "Jane D." } },                                  // no photoImageId
        assignment, policy = new { cancelFreeHours = 24, cancelFeePercent = 50, noShowFeePercent = 100, maxReschedules = 2, approvalHours = (int?)null },
        travel = new { feeCents = 1000, serviceAreaPostalCodes = new[] { "11201", "112*" }, serviceRadiusKm = 10 },
    });

    private Task<HttpResponseMessage> Hold(object? provider, int n = 1)
        => Send(HttpMethod.Post, "/api/v1/bookings/holds", _jane, new { listingId = Service, start = Slot(n), location = "online", durationMinutes = 240, providerId = provider }, "c12-key-" + n + "-" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public async Task The_options_document_with_a_numeric_version_no_descriptions_and_no_photo_is_read()
    {
        await Ready();
        _portal.OptionsJson = Doc();
        var r = await Send(HttpMethod.Get, $"/api/v1/services/{Service}/options", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = (await Json(r));
        Assert.True(j.GetProperty("available").GetBoolean());
        Assert.Equal("1840", j.GetProperty("version").GetString());
        Assert.Equal("deep", j.GetProperty("addOns")[0].GetProperty("id").GetString());
        Assert.Equal(new[] { "yesNo", "choice" }, j.GetProperty("intakeQuestions").EnumerateArray().Select(q => q.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("Jane D.", j.GetProperty("providers")[0].GetProperty("displayName").GetString());
        Assert.Equal("buyerChoice", j.GetProperty("assignment").GetString());
        Assert.Equal(24, j.GetProperty("policy").GetProperty("cancelFreeHours").GetInt32());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("policy").GetProperty("approvalHours").ValueKind);
    }

    [Fact]
    public async Task A_provider_may_be_chosen_only_when_the_service_lets_the_buyer_choose_and_only_from_the_listed_ones()
    {
        await Ready();
        _portal.OptionsJson = Doc("leastBusy");
        var no = await Hold(Pv);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, no.StatusCode);                  // the portal assigns: the marketplace must not send a providerId
        Assert.Equal(0, _portal.HoldCalls);
        _portal.OptionsJson = Doc("buyerChoice");
        _k.F.Clock.Advance(TimeSpan.FromMinutes(6));                                      // the options are cached for 5 minutes
        var stranger = await Hold(Guid.NewGuid(), 2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);            // not an eligible provider of this service
        Assert.Equal(0, _portal.HoldCalls);
        var ok = await Hold(Pv, 3);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Contains(Pv.ToString(), _portal.HoldBodies.Single());                       // and then it IS sent
    }

    [Fact]
    public async Task A_hold_without_a_chosen_provider_needs_no_options_at_all()
    {
        await Ready();                                                                    // the portal has no options endpoint (404)
        Assert.Equal(HttpStatusCode.Created, (await Hold(null)).StatusCode);
    }

    [Fact]
    public async Task The_policy_a_booking_event_carries_is_stored_on_the_booking()
    {
        await Ready();
        var b = await PaidBooking();
        await PortalEvent("booking.reminder", Now.AddMinutes(1), b, new { kind = "day_before", startAt = b.StartAt, policy = new { cancelFreeHours = 48, cancelFeePercent = 25, noShowFeePercent = 100, maxReschedules = 1, approvalHours = (int?)null } });
        await Drain();
        var row = await Row(b.Id);
        Assert.Equal((48, 25, 100, 1), (row.PolicyCancelFreeHours, row.PolicyCancelFeePercent, row.PolicyNoShowFeePercent, row.PolicyMaxReschedules));
        var view = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}", _jane));                      // and the buyer's booking shows it
        Assert.Equal(48, view.GetProperty("policy").GetProperty("cancelFreeHours").GetInt32());
        Assert.Equal(25, view.GetProperty("policy").GetProperty("cancelFeePercent").GetInt32());
    }

    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();
}
