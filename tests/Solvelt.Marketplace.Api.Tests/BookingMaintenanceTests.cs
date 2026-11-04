using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Services;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 2: lost hold answers, overdue holds and the retention of booking personal data.</summary>
public sealed class BookingMaintenanceTests : IDisposable
{
    private readonly AuthKit _k = new();
    private readonly FakeBookingPortal _portal;
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private string _jane = "";
    private int _n;

    public BookingMaintenanceTests() => _portal = new FakeBookingPortal(_k.F.Clock);
    public void Dispose() => _k.Dispose();

    private async Task<HttpResponseMessage> Hold(string location = "customer", int hours = 0)
    {
        if (_jane == "")
        {
            await CatalogKit.Sync(_k.F);
            _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{}"));
            _jane = await _k.VerifiedSession("jane@example.com");
        }
        var start = new DateTimeOffset(_k.F.Clock.GetUtcNow().Date.AddDays(7).AddHours(14 + hours), TimeSpan.Zero);
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/holds")
        {
            Content = JsonContent.Create(new { listingId = Service, start, location, durationMinutes = 240, address = new { name = "Jane Brown", line1 = "1 Main St", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", lat = 40.69, lng = -73.99 }, phone = "+1 555 000 1111" }),
        };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jane);
        r.Headers.Add("Idempotency-Key", "maint-key-" + (++_n));
        return await _k.Http.SendAsync(r);
    }

    private async Task<BookingMaintenance.Result> Run()
    {
        using var scope = _k.F.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
    }

    private Task<Booking> Row(string key) => _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.IdempotencyKey == key));

    [Fact]
    public async Task A_hold_whose_answer_was_lost_is_found_at_the_portal_and_applied_without_the_buyer_retrying()
    {
        _portal.LoseAnswer = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Hold()).StatusCode);
        _portal.LoseAnswer = false;
        Assert.Equal(0, (await Run()).Resolved);                        // too early: the buyer's own retry gets a minute
        _k.F.Clock.Advance(TimeSpan.FromSeconds(90));
        var res = await Run();
        Assert.Equal(1, res.Resolved);
        var b = await Row("maint-key-1");
        Assert.Equal(BookingStatus.Hold, b.Status);
        Assert.Equal(24000, b.TotalCents);
        Assert.NotNull(b.PortalBookingId);
    }

    [Fact]
    public async Task A_hold_the_portal_never_saw_is_closed_as_failed_and_an_outage_leaves_it_alone()
    {
        _portal.Down = true;
        await Hold();
        _k.F.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(0, (await Run()).Failed);                          // outage: no conclusion
        Assert.Equal(BookingStatus.HoldPending, (await Row("maint-key-1")).Status);
        _portal.Down = false;                                           // the portal is back and never saw the hold (404)
        var res = await Run();
        Assert.Equal(1, res.Failed);
        var b = await Row("maint-key-1");
        Assert.Equal(BookingStatus.Failed, b.Status);
        Assert.Equal("not_found", b.FailureCode);
    }

    [Fact]
    public async Task An_overdue_hold_is_resynced_so_a_lost_expiry_event_cannot_keep_it_held_forever()
    {
        Assert.Equal(HttpStatusCode.Created, (await Hold()).StatusCode);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        _portal.Holds.Single().Status = "Expired";
        Assert.Equal(0, (await Run()).Resolved);                        // 1 minute past expiry is still inside the 2 minute grace
        _k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await Run()).Resolved);
        Assert.Equal(BookingStatus.Expired, (await Row("maint-key-1")).Status);
    }

    [Fact]
    public async Task Personal_data_is_wiped_30_days_after_a_booking_ended_and_a_day_after_a_failed_attempt()
    {
        Assert.Equal(HttpStatusCode.Created, (await Hold(hours: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Hold(hours: 1)).StatusCode);
        var failing = _portal.Holds.Count;
        _portal.FailNext = (HttpStatusCode.UnprocessableEntity, "outside_service_area");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Hold(hours: 2)).StatusCode);
        var ended = await Row("maint-key-1");
        await _k.Db(async db => { await db.Bookings.Where(b => b.Id == ended.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Completed).SetProperty(b => b.OnlineInstructions, "https://meet.example/x")); return 0; });
        var failedRow = await Row("maint-key-3");
        Assert.NotNull(failedRow.AddrLine1);

        // before the limits: nothing is wiped
        _k.F.Clock.Advance(TimeSpan.FromHours(23));
        await _k.Db(async db => { await db.Bookings.Where(b => b.Id == ended.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedAt, _k.F.Clock.GetUtcNow())); return 0; });
        await Run();
        Assert.NotNull((await Row("maint-key-3")).AddrLine1);          // a failed attempt keeps its data for a day
        _k.F.Clock.Advance(TimeSpan.FromHours(1.01));
        await Run();
        var f = await Row("maint-key-3");
        Assert.Null(f.AddrLine1); Assert.Null(f.Phone); Assert.NotNull(f.RedactedAt);
        Assert.NotNull((await Row("maint-key-1")).AddrLine1);          // the ended booking has 30 days
        _k.F.Clock.Advance(TimeSpan.FromDays(29));
        await Run();
        Assert.NotNull((await Row("maint-key-1")).AddrLine1);          // 29 days after it ended
        _k.F.Clock.Advance(TimeSpan.FromDays(1.1));
        await Run();
        var e = await Row("maint-key-1");
        Assert.Null(e.AddrLine1); Assert.Null(e.AddrLat); Assert.Null(e.AddrLng); Assert.Null(e.Phone); Assert.Null(e.OnlineInstructions); Assert.NotNull(e.RedactedAt);
        Assert.Equal(24000, e.TotalCents);                              // money stays
        var live = await Row("maint-key-2");                            // a hold that is still open (or expired by the clock but not ended by the portal) keeps its data
        Assert.Equal(BookingStatus.Hold, live.Status);
    }
}
