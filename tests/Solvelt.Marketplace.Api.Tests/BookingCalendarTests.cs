using System.Net;
using System.Text;
using Solvelt.Marketplace.Api.Services;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4: add-to-calendar (.ics) for the buyer's own booking, with no address and no meeting link.</summary>
public sealed class BookingCalendarTests : BookingFlowBase
{
    private Task<HttpResponseMessage> Ics(Guid id, string? token = null) => Send(HttpMethod.Get, $"/api/v1/bookings/{id}/calendar.ics", token ?? _jane);

    [Fact]
    public async Task The_calendar_file_has_the_time_the_title_and_a_link_but_never_the_address_or_the_meeting_link()
    {
        await Ready();
        _portal.OnlineInstructions = "https://meet.example/secret-room";
        var b = await PaidBooking();
        var r = await Ics(b.Id);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/calendar", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition!.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", text); Assert.EndsWith("END:VCALENDAR\r\n", text);
        Assert.Contains($"DTSTART:{b.StartAt!.Value.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}", text);
        Assert.Contains("STATUS:CONFIRMED", text);
        Assert.Contains("SUMMARY:Deep House Cleaning", text);
        Assert.Contains("LOCATION:Online", text);
        Assert.Contains($"/account/bookings/{b.Id}", text);
        Assert.DoesNotContain("secret-room", text);
        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
    }

    [Fact]
    public async Task Only_the_owner_gets_one_and_only_for_a_live_booking_a_request_is_tentative()
    {
        await Ready();
        var b = await PaidBooking();
        Assert.Equal(HttpStatusCode.NotFound, (await Ics(b.Id, _joe)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/calendar.ics", null)).StatusCode);
        var hold = await NewHold(hours: 3);
        Assert.Equal(HttpStatusCode.NotFound, (await Ics(hold.Id)).StatusCode);                  // a hold is not a booking yet
        _portal.Instant = false;
        var req = await PaidBooking(hours: 5);
        Assert.Contains("STATUS:TENTATIVE", await (await Ics(req.Id)).Content.ReadAsStringAsync());
        await PortalEvent("booking.cancelled", _k.F.Clock.GetUtcNow().AddMinutes(1), b, new { cancelledBy = "buyer", refundDueCents = 24000 });
        Assert.Equal(HttpStatusCode.NotFound, (await Ics(b.Id)).StatusCode);
    }

    [Fact]
    public void Text_is_escaped_and_long_lines_are_folded_on_character_boundaries()
    {
        Assert.Equal("a\\;b\\,c\\\\d\\ne", BookingCalendar.Escape("a;b,c\\d\ne"));
        var folded = BookingCalendar.Fold("SUMMARY:" + new string('é', 80));
        foreach (var line in folded.Split("\r\n")) Assert.True(Encoding.UTF8.GetByteCount(line) <= 75);
        Assert.Equal("SUMMARY:" + new string('é', 80), folded.Replace("\r\n ", ""));     // unfolding gives the original back
    }
}
