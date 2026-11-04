using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 2: holds (idempotency, one winner per slot, portal-decided amounts, ownership, privacy).</summary>
public sealed class BookingHoldTests : IDisposable
{
    private readonly AuthKit _k = new();
    private readonly FakeBookingPortal _portal;
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private string _jane = "", _joe = "";

    public BookingHoldTests() => _portal = new FakeBookingPortal(_k.F.Clock);
    public void Dispose() => _k.Dispose();

    private async Task Ready()
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}"));
        _jane = await _k.VerifiedSession("jane@example.com", name: "Jane Brown");
        _joe = await _k.VerifiedSession("joe@example.com", name: "Joe Green");
    }

    private DateTimeOffset Slot(int hours = 0) => _k.F.Clock.GetUtcNow().Date.AddDays(7).AddHours(14 + hours) is var d ? new DateTimeOffset(d, TimeSpan.Zero) : default;

    private object Body(DateTimeOffset? start = null, string location = "online", object? extra = null) => new
    {
        listingId = Service, start = start ?? Slot(), location, durationMinutes = 240, addOnIds = new[] { "deep" },
        address = location == "customer" ? new { name = "Jane Brown", line1 = "1 Main St", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", lat = 40.69, lng = -73.99 } : null,
        extra,
    };

    private async Task<HttpResponseMessage> Hold(string token, object body, string? key = "key-00000001")
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/holds") { Content = JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key != null) r.Headers.Add("Idempotency-Key", key);
        return await _k.Http.SendAsync(r);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    private Task<int> Rows(Func<IQueryable<Booking>, IQueryable<Booking>> q) => _k.Db(db => q(db.Bookings.AsNoTracking()).CountAsync());

    [Fact]
    public async Task A_hold_returns_the_portals_amounts_and_expiry_and_sends_no_price_to_the_portal()
    {
        await Ready();
        // The client tries to dictate the price, the total and the end time: none of that has a field on the request, and none reaches the portal.
        var r = await Hold(_jane, new { listingId = Service, start = Slot(), location = "online", durationMinutes = 240, priceCents = 1, totalCents = 1, endAt = Slot(9), addOnIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = (await Json(r)).GetProperty("booking");
        Assert.Equal("Hold", b.GetProperty("status").GetString());
        Assert.Equal(24000, b.GetProperty("totalCents").GetInt64());
        Assert.Equal(24000, b.GetProperty("priceCents").GetInt64());
        Assert.Equal(Slot().AddMinutes(240), b.GetProperty("endAt").GetDateTimeOffset());
        Assert.Equal(_k.F.Clock.GetUtcNow().AddMinutes(10), b.GetProperty("holdExpiresAt").GetDateTimeOffset());
        var sent = _portal.Holds.Single().BodyText;
        Assert.DoesNotContain("priceCents", sent); Assert.DoesNotContain("totalCents", sent); Assert.DoesNotContain("endAt", sent);
        Assert.DoesNotContain("jane@example.com", sent);          // the buyer is the relay identity, never the real address
        Assert.Contains("relay-", sent); Assert.Contains("Jane B.", sent);
    }

    [Fact]
    public async Task The_same_idempotency_key_is_one_booking_and_one_portal_booking_even_when_sent_in_parallel()
    {
        await Ready();
        var all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Hold(_jane, Body())));
        Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(1, all.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await Rows(q => q));
        Assert.Single(_portal.Holds);
        var ids = new HashSet<Guid>();
        foreach (var r in all) ids.Add((await Json(r)).GetProperty("booking").GetProperty("id").GetGuid());
        Assert.Single(ids);
        var again = await Hold(_jane, Body());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await Json(again)).GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task The_same_key_for_a_different_request_is_refused_not_answered_with_the_old_hold()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.Created, (await Hold(_jane, Body())).StatusCode);
        var r = await Hold(_jane, Body(Slot(1)));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(r)).GetProperty("code").GetString());
        Assert.Single(_portal.Holds);
    }

    [Fact]
    public async Task Two_buyers_holding_the_same_slot_in_parallel_exactly_one_wins_and_the_loser_has_nothing_held()
    {
        await Ready();
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Hold(i % 2 == 0 ? _jane : _joe, Body(), "race-key-" + i)));
        // Per buyer, three requests with three keys: the winner buyer's first request gets the slot; everything else is 409 slot_unavailable.
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(rs.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single(_portal.Holds);
        Assert.Equal(1, await Rows(q => q.Where(b => b.Status == BookingStatus.Hold)));
        Assert.Equal(5, await Rows(q => q.Where(b => b.Status == BookingStatus.Failed)));
        // Failed attempts are not bookings: neither buyer's list shows them, and a failed one can't be fetched.
        foreach (var t in new[] { _jane, _joe })
        {
            var list = await _k.Get("/api/v1/bookings", t);
            Assert.True((await Json(list)).GetProperty("bookings").GetArrayLength() <= 1);
        }
        var failedId = await _k.Db(db => db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Failed).Select(b => b.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Get($"/api/v1/bookings/{failedId}", _jane)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Get($"/api/v1/bookings/{failedId}", _joe)).StatusCode);
    }

    [Fact]
    public async Task Another_buyers_booking_is_404_and_anonymous_is_401()
    {
        await Ready();
        var id = (await Json(await Hold(_jane, Body()))).GetProperty("booking").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _k.Get($"/api/v1/bookings/{id}", _jane)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Get($"/api/v1/bookings/{id}", _joe)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get($"/api/v1/bookings/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.PostAsJsonAsync("/api/v1/bookings/holds", Body())).StatusCode);
        Assert.Empty((await Json(await _k.Get("/api/v1/bookings", _joe))).GetProperty("bookings").EnumerateArray());
        var res = await _k.Get($"/api/v1/bookings/{id}", _jane);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_portal_outage_leaves_the_hold_pending_and_the_retry_resumes_with_the_same_portal_id()
    {
        await Ready();
        _portal.Down = true;
        var r = await Hold(_jane, Body());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("booking_unavailable", (await Json(r)).GetProperty("code").GetString());
        var pending = await _k.Db(db => db.Bookings.AsNoTracking().SingleAsync());
        Assert.Equal(BookingStatus.HoldPending, pending.Status);
        _portal.Down = false;
        var ok = await Hold(_jane, Body());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);                     // the row already existed
        var held = await _k.Db(db => db.Bookings.AsNoTracking().SingleAsync());
        Assert.Equal(BookingStatus.Hold, held.Status);
        Assert.Equal(pending.MarketplaceBookingId, held.MarketplaceBookingId);
        Assert.Equal(pending.MarketplaceBookingId, _portal.Holds.Single().Mid);
        Assert.NotNull(held.PortalBookingId);
    }

    [Fact]
    public async Task Validation_and_portal_refusals_have_buyer_safe_answers()
    {
        await Ready();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Hold(_jane, Body(), key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Hold(_jane, Body(), key: "short")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Hold(_jane, Body(_k.F.Clock.GetUtcNow().AddHours(-1)), "key-past-0001")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Hold(_jane, new { listingId = Service, start = Slot(), location = "customer" }, "key-noaddr-1")).StatusCode);   // at-home needs an address
        Assert.Equal(HttpStatusCode.NotFound, (await Hold(_jane, new { listingId = CatalogKit.ListingId("challah-classic"), start = Slot(), location = "online" }, "key-product-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Hold(_jane, new { listingId = Guid.NewGuid(), start = Slot(), location = "online" }, "key-nolist-01")).StatusCode);
        Assert.Empty(_portal.Holds);

        _portal.FailNext = (HttpStatusCode.UnprocessableEntity, "outside_service_area");
        var r = await Hold(_jane, Body(location: "customer"), "key-area-0001");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("outside_service_area", (await Json(r)).GetProperty("code").GetString());
        // a failed attempt can be tried again with a NEW key (the old key stays failed: it answers the same, never a surprise success)
        Assert.Equal(HttpStatusCode.Created, (await Hold(_jane, Body(location: "customer"), "key-area-0002")).StatusCode);
    }

    [Fact]
    public async Task At_most_three_open_holds_per_buyer_and_the_meeting_link_is_never_shown_or_kept_for_a_hold()
    {
        await Ready();
        _portal.OnlineInstructions = "https://meet.example/secret-room";
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await Hold(_jane, Body(Slot(i)), "many-key-" + i)).StatusCode);
        var fourth = await Hold(_jane, Body(Slot(3)), "many-key-3");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fourth.StatusCode);
        Assert.Equal("too_many_holds", (await Json(fourth)).GetProperty("code").GetString());
        var id = await _k.Db(db => db.Bookings.AsNoTracking().Select(b => b.Id).FirstAsync());
        var body = await (await _k.Get($"/api/v1/bookings/{id}", _jane)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-room", body);
        Assert.Empty(await _k.Db(db => db.Bookings.AsNoTracking().Where(b => b.OnlineInstructions != null).ToListAsync()));
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("secret-room"));
        // after the hold times out the buyer may hold again
        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Created, (await Hold(_jane, Body(Slot(4)), "many-key-4")).StatusCode);
    }
}
