using System.Text.Json;
using Solvelt.Marketplace.Api.Services;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The review of web 7c6fe76..31d14e5, API side (decision M4-27): the listing's slug on a booking, and control characters in .ics text.</summary>
public sealed class BookingReviewFixes8Tests : BookingFlowBase
{
    [Fact]
    public async Task L8_a_booking_carries_the_slug_of_its_service_so_a_page_can_link_back_to_it()
    {
        await Ready();
        var b = await NewHold();
        var view = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}", _jane));
        Assert.Equal("deep-house-cleaning", view.GetProperty("listingSlug").GetString());
        var list = await Json(await Send(HttpMethod.Get, "/api/v1/bookings", _jane));
        Assert.Equal("deep-house-cleaning", list.GetProperty("bookings")[0].GetProperty("listingSlug").GetString());
    }

    [Fact]
    public void L6_calendar_text_loses_control_characters_but_keeps_line_breaks_as_escapes()
    {
        Assert.Equal("a\\nb", BookingCalendar.Escape("a\nb"));
        Assert.Equal("ab", BookingCalendar.Escape("a\u0001b"));
        Assert.Equal("ab", BookingCalendar.Escape("a\u007fb\u0000"));
        Assert.Equal("a\\;b\\,c", BookingCalendar.Escape("a;b,c"));
        Assert.Equal("tab\there", BookingCalendar.Escape("tab\there"));
        Assert.DoesNotContain(BookingCalendar.Escape("x\u0008\u000b\u000c\u001by"), c => c < 32 && c != '\t');
    }
}
