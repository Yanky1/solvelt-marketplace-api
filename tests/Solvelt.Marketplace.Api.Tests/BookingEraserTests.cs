using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Services;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Review finding: deleting an account must erase the booking PII (decision M4-18), keeping number, money and status.</summary>
public sealed class BookingEraserTests : BookingFlowBase
{
    [Fact]
    public async Task Deleting_the_account_redacts_the_bookings_personal_data_and_keeps_money_and_status()
    {
        await Ready();
        var b = await PaidBooking();
        var other = await PaidBooking(_joe, 1);
        await _k.Db(db =>
        {
            return db.Bookings.Where(x => x.Id == b.Id || x.Id == other.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AddrName, "Jane Brown").SetProperty(x => x.AddrLine1, "1 Main St").SetProperty(x => x.AddrCity, "Austin").SetProperty(x => x.AddrLat, 30.1).SetProperty(x => x.AddrLng, -97.7)
                .SetProperty(x => x.OnlineInstructions, "https://meet.example/abc"));
        });
        var before = await Row(b.Id);
        Assert.NotNull(before.Phone); Assert.NotNull(before.IntakeJson);

        var del = await Send(HttpMethod.Post, "/api/v1/account/delete", _jane, new { password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await Drain();

        var after = await Row(b.Id);
        Assert.Null(after.AddrName); Assert.Null(after.AddrLine1); Assert.Null(after.AddrCity); Assert.Null(after.AddrLat); Assert.Null(after.AddrLng);
        Assert.Null(after.Phone); Assert.Null(after.IntakeJson); Assert.Null(after.OnlineInstructions);
        Assert.NotNull(after.RedactedAt);
        Assert.Equal((before.Status, before.TotalCents, before.Currency, before.MarketplaceBookingId), (after.Status, after.TotalCents, after.Currency, after.MarketplaceBookingId));
        Assert.Equal(before.TotalCents, (await Pay(b.Id)).CapturedCents);                       // the money is kept
        // another buyer's booking is untouched
        var theirs = await Row(other.Id);
        Assert.Equal("Jane Brown", theirs.AddrName); Assert.NotNull(theirs.Phone); Assert.Null(theirs.RedactedAt);

        // idempotent: running it again changes nothing
        var stamp = after.RedactedAt;
        using (var scope = _k.F.Services.CreateScope())
            await new BookingsAccountEraser(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), _k.F.Clock).EraseAsync(after.AccountId, default);
        Assert.Equal(stamp, (await Row(b.Id)).RedactedAt);
    }
}
