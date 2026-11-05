using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>"Download my data" (M5's exporter seam), M4's section (decision M4-28): the buyer's own bookings, payments, refunds and quote requests, and nothing of the provider's, the portal's or Stripe's.</summary>
public sealed class BookingsDataExportTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();

    [Fact]
    public async Task The_export_has_the_buyers_bookings_money_policy_address_refunds_and_quotes_and_nothing_internal()
    {
        await Ready();
        var mine = await PaidBooking();
        var theirs = await PaidBooking(_joe, 1);
        await _k.Db(db => db.Bookings.Where(x => x.Id == mine.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AddrName, "Jane Brown").SetProperty(x => x.AddrLine1, "1 Main St").SetProperty(x => x.AddrCity, "Austin").SetProperty(x => x.AddrCountry, "US")));
        await _k.Db(db => db.Bookings.Where(x => x.Id == theirs.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AddrLine1, "99 Joe Street")));
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), mine, new { cancelledBy = "buyer", cancellationFeeCents = 6000, refundDueCents = 18000, policy = new { cancelFreeHours = 24, cancelFeePercent = 25, noShowFeePercent = 100, maxReschedules = 2, approvalHours = (int?)null } });
        await Drain();
        var account = await _k.Db(db => db.BuyerAccounts.AsNoTracking().Where(a => a.Email == "jane@example.com").Select(a => a.Id).SingleAsync());
        await _k.Db(async db =>
        {
            db.Quotes.Add(new BuyerQuote { Id = Guid.NewGuid(), MarketplaceQuoteId = "mq-export-1", AccountId = account, IdempotencyKey = "export-key-0001", RequestHash = "secret-hash", ListingId = Service, Status = QuoteStatus.Quoted, Location = "online",
                Details = "Please bring a ladder", Phone = "+1 555 123 4567", Currency = "USD", PriceCents = 40000, DurationMinutes = 180, Note = "Bring your own tap", CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
            return 0;
        });

        var res = await Send(HttpMethod.Post, "/api/v1/account/data-export", _jane);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition!.ToString());
        var text = await res.Content.ReadAsStringAsync();
        var section = JsonDocument.Parse(text).RootElement.GetProperty("sections").GetProperty("bookings");
        var b = Assert.Single(section.GetProperty("bookings").EnumerateArray());
        Assert.Equal(mine.Number, b.GetProperty("number").GetString());
        Assert.Equal("Deep House Cleaning", b.GetProperty("service").GetString());
        Assert.Equal(BookingStatus.CancelledByBuyer, b.GetProperty("status").GetString());
        Assert.Equal(24000, b.GetProperty("money").GetProperty("totalCents").GetInt64());
        Assert.Equal(6000, b.GetProperty("money").GetProperty("cancellationFeeCents").GetInt64());
        Assert.Equal(25, b.GetProperty("policy").GetProperty("cancelFeePercent").GetInt32());
        Assert.Equal("1 Main St", b.GetProperty("address").GetProperty("line1").GetString());
        Assert.False(string.IsNullOrEmpty(b.GetProperty("phone").GetString()));
        Assert.Equal(JsonValueKind.Array, b.GetProperty("intakeAnswers").ValueKind);
        var pay = Assert.Single(b.GetProperty("payments").EnumerateArray());
        Assert.Equal("captured", pay.GetProperty("status").GetString());
        Assert.Equal(18000, Assert.Single(b.GetProperty("refunds").EnumerateArray()).GetProperty("amountCents").GetInt64());
        var q = Assert.Single(section.GetProperty("quoteRequests").EnumerateArray());
        Assert.Equal("Please bring a ladder", q.GetProperty("details").GetString());
        Assert.Equal(40000, q.GetProperty("priceCents").GetInt64());
        Assert.Equal("Bring your own tap", q.GetProperty("providerNote").GetString());

        // nothing that belongs to somebody else or to our plumbing
        Assert.DoesNotContain("99 Joe Street", text);                                     // another buyer's booking (the fake portal numbers every booking alike, so the address tells them apart)
        Assert.DoesNotContain(mine.MarketplaceBookingId, text); Assert.DoesNotContain("mq-export-1", text);       // our own ids for the portal
        Assert.DoesNotContain("secret-hash", text); Assert.DoesNotContain("export-key-0001", text);
        Assert.DoesNotContain("pi_fake", text); Assert.DoesNotContain("re_fake", text); Assert.DoesNotContain("clientSecret", text);   // Stripe
        if (mine.ProviderId is { } provider) Assert.DoesNotContain(provider.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_buyer_without_bookings_gets_an_empty_section_and_a_failed_hold_attempt_is_not_in_it()
    {
        await Ready();
        var res = await Send(HttpMethod.Post, "/api/v1/account/data-export", _joe);
        var section = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("sections").GetProperty("bookings");
        Assert.Equal(0, section.GetProperty("bookings").GetArrayLength());
        Assert.Equal(0, section.GetProperty("quoteRequests").GetArrayLength());
        _portal.FailNext = (HttpStatusCode.Conflict, "slot_unavailable");
        await NewHoldAttempt();
        var again = JsonDocument.Parse(await (await Send(HttpMethod.Post, "/api/v1/account/data-export", _joe)).Content.ReadAsStringAsync()).RootElement.GetProperty("sections").GetProperty("bookings");
        Assert.Equal(0, again.GetProperty("bookings").GetArrayLength());                  // a refused attempt was never a booking
    }

    private async Task NewHoldAttempt()
        => await Send(HttpMethod.Post, "/api/v1/bookings/holds", _joe, new { listingId = Service, start = Slot(3), location = "online", durationMinutes = 240 }, "export-fail-key-1");
}
