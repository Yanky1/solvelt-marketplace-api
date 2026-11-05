using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Services;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Deleting an account erases booking PII (decisions M4-18 and O-23): finished bookings at once, open ones only when they finish; number, money and status stay.</summary>
public sealed class BookingEraserTests : BookingFlowBase
{
    private async Task Run()
    {
        using var scope = _k.F.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
    }

    [Fact]
    public async Task An_upcoming_booking_keeps_its_data_after_the_account_is_deleted_and_is_wiped_by_the_sweep_once_it_finishes()
    {
        await Ready();
        var b = await PaidBooking();
        var finished = await PaidBooking(_jane, 1);
        var other = await PaidBooking(_joe, 2);
        await _k.Db(db => db.Bookings.Where(x => x.Id == b.Id || x.Id == finished.Id || x.Id == other.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.AddrName, "Jane Brown").SetProperty(x => x.AddrLine1, "1 Main St").SetProperty(x => x.AddrCity, "Austin").SetProperty(x => x.AddrLat, 30.1).SetProperty(x => x.AddrLng, -97.7)
            .SetProperty(x => x.OnlineInstructions, "https://meet.example/abc")));
        await _k.Db(db => db.Bookings.Where(x => x.Id == finished.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, BookingStatus.Completed)));
        var before = await Row(b.Id);
        Assert.NotNull(before.Phone); Assert.NotNull(before.IntakeJson);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/v1/account/delete", _jane, new { password = AuthKit.Password })).StatusCode);
        await Drain();

        // the finished booking is wiped at once, money and status kept
        var done = await Row(finished.Id);
        Assert.Null(done.AddrName); Assert.Null(done.AddrLine1); Assert.Null(done.Phone); Assert.Null(done.IntakeJson); Assert.Null(done.OnlineInstructions); Assert.NotNull(done.RedactedAt);
        Assert.Equal((BookingStatus.Completed, finished.TotalCents, finished.MarketplaceBookingId), (done.Status, done.TotalCents, done.MarketplaceBookingId));
        Assert.Equal(finished.TotalCents, (await Pay(finished.Id)).CapturedCents);
        // the upcoming one is still identified: the provider has a job to do
        var open = await Row(b.Id);
        Assert.Equal("Jane Brown", open.AddrName); Assert.Equal("1 Main St", open.AddrLine1); Assert.NotNull(open.Phone); Assert.NotNull(open.IntakeJson); Assert.Null(open.RedactedAt);
        await Run();                                                                       // the sweep leaves an open booking alone too
        Assert.Equal("Jane Brown", (await Row(b.Id)).AddrName);

        // it finishes: the sweep wipes it, without waiting for the retention days
        await _k.Db(db => db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, BookingStatus.Completed)));
        await Run();
        var after = await Row(b.Id);
        Assert.Null(after.AddrName); Assert.Null(after.AddrLine1); Assert.Null(after.AddrCity); Assert.Null(after.AddrLat); Assert.Null(after.AddrLng);
        Assert.Null(after.Phone); Assert.Null(after.IntakeJson); Assert.Null(after.OnlineInstructions); Assert.NotNull(after.RedactedAt);
        Assert.Equal((before.TotalCents, before.Currency, before.MarketplaceBookingId), (after.TotalCents, after.Currency, after.MarketplaceBookingId));
        Assert.Equal(before.TotalCents, (await Pay(b.Id)).CapturedCents);                  // the money is kept
        // another buyer's booking is untouched
        var theirs = await Row(other.Id);
        Assert.Equal("Jane Brown", theirs.AddrName); Assert.NotNull(theirs.Phone); Assert.Null(theirs.RedactedAt);

        // idempotent: running it again changes nothing
        var stamp = after.RedactedAt;
        await Run();
        using (var scope = _k.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BookingsAccountEraser>().EraseAsync(after.AccountId, default);
        Assert.Equal(stamp, (await Row(b.Id)).RedactedAt);
    }

    [Fact]
    public async Task The_sweep_does_not_touch_the_bookings_of_an_account_that_is_not_deleted()
    {
        await Ready();
        var b = await PaidBooking();
        await _k.Db(db => db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AddrName, "Jane Brown").SetProperty(x => x.Status, BookingStatus.Completed)));
        await Run();
        Assert.Equal("Jane Brown", (await Row(b.Id)).AddrName);                              // finished, but the buyer is still here: the 30-day retention applies, not the sweep
    }
}
