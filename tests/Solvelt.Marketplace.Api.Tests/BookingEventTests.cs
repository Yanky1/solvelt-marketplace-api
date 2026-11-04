using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 2: the booking.* receiver handlers (order independence, terminal states, matching on both ids, money fields, the meeting link).</summary>
public sealed class BookingEventTests : IDisposable
{
    private readonly AuthKit _k = new();
    private readonly FakeBookingPortal _portal;
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private string _jane = "";
    private int _n;

    public BookingEventTests() => _portal = new FakeBookingPortal(_k.F.Clock);
    public void Dispose() => _k.Dispose();

    private async Task<(Guid Id, string Mid, Guid PortalId)> NewHold(string location = "online", int hourOffset = 0)
    {
        if (_jane == "")
        {
            await CatalogKit.Sync(_k.F);
            _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{}"));
            _jane = await _k.VerifiedSession("jane@example.com");
        }
        var start = new DateTimeOffset(_k.F.Clock.GetUtcNow().Date.AddDays(7).AddHours(14 + hourOffset), TimeSpan.Zero);
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/holds") { Content = JsonContent.Create(new { listingId = Service, start, location, durationMinutes = 240 }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jane);
        r.Headers.Add("Idempotency-Key", "event-test-key-" + (++_n));
        var res = await _k.Http.SendAsync(r);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var key = "event-test-key-" + _n;
        var b = await _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(x => x.IdempotencyKey == key));
        return (b.Id, b.MarketplaceBookingId, b.PortalBookingId!.Value);
    }

    private object Ref(string mid, Guid portalId) => new { bookingId = portalId, number = "BK-2026-0001", marketplaceBookingId = mid };

    private async Task<(HttpStatusCode Status, string Json)> Send(string type, DateTimeOffset at, object data, string? id = null)
    {
        var body = Body(id ?? $"evt-{type}-{++_n}", type, at, data);
        return await Post(_k.Http, Request(_k.F, body));
    }

    private Task<Booking> Row(Guid id) => _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id));
    private static DateTimeOffset T(int minutes) => T0.AddMinutes(minutes);

    [Fact]
    public async Task Confirmed_reveals_the_meeting_link_to_the_buyer_only_on_the_detail_view_and_cancel_hides_it_again()
    {
        var (id, mid, pid) = await NewHold();
        Assert.Equal("applied", StatusOf((await Send("booking.confirmed", T(1), new { booking = Ref(mid, pid), status = "Confirmed", onlineInstructions = "https://meet.example/room-42", totalCents = 24000 })).Json));
        var detail = await (await _k.Get($"/api/v1/bookings/{id}", _jane)).Content.ReadAsStringAsync();
        Assert.Contains("https://meet.example/room-42", detail);
        Assert.Contains("\"status\":\"Confirmed\"", detail);
        Assert.DoesNotContain("room-42", await (await _k.Get("/api/v1/bookings", _jane)).Content.ReadAsStringAsync());   // never in a list
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("room-42"));
        await Send("booking.cancelled", T(2), new { booking = Ref(mid, pid), cancelledBy = "buyer", cancellationFeeCents = 0, refundDueCents = 24000 });
        Assert.DoesNotContain("room-42", await (await _k.Get($"/api/v1/bookings/{id}", _jane)).Content.ReadAsStringAsync());
        Assert.Null((await Row(id)).OnlineInstructions);
    }

    [Fact]
    public async Task Events_in_any_order_give_the_same_result_and_a_finished_booking_is_never_reopened()
    {
        var (id, mid, pid) = await NewHold();
        // Arrival order: cancelled (t3) first, then the older confirmed (t1) and rescheduled (t2).
        Assert.Equal("applied", StatusOf((await Send("booking.cancelled", T(3), new { booking = Ref(mid, pid), cancelledBy = "buyer", cancellationFeeCents = 5000, refundDueCents = 19000 })).Json));
        Assert.Equal("skipped", StatusOf((await Send("booking.confirmed", T(1), new { booking = Ref(mid, pid), onlineInstructions = "https://x.example/leak" })).Json));
        Assert.Equal("skipped", StatusOf((await Send("booking.rescheduled", T(2), new { booking = Ref(mid, pid), initiatedBy = "seller", requiresBuyerConsent = true })).Json));
        // ... and even a NEWER confirmation does not reopen it (absorbing terminal state, O-10).
        Assert.Equal("skipped", StatusOf((await Send("booking.confirmed", T(30), new { booking = Ref(mid, pid) })).Json));
        var b = await Row(id);
        Assert.Equal(BookingStatus.CancelledByBuyer, b.Status);
        Assert.Equal(5000, b.CancellationFeeCents); Assert.Equal(19000, b.RefundDueCents);
        Assert.Null(b.OnlineInstructions);
        Assert.False(b.RequiresBuyerConsent);
    }

    [Fact]
    public async Task Declined_and_expired_are_final()
    {
        var (a, midA, pidA) = await NewHold(hourOffset: 0);
        var (b, midB, pidB) = await NewHold(hourOffset: 1);
        await Send("booking.requested", T(1), new { booking = Ref(midA, pidA), approvalDeadline = T(600) });
        Assert.Equal(BookingStatus.Requested, (await Row(a)).Status);
        await Send("booking.declined", T(2), new { booking = Ref(midA, pidA), reason = "busy", auto = false });
        await Send("booking.confirmed", T(9), new { booking = Ref(midA, pidA) });
        Assert.Equal(BookingStatus.Declined, (await Row(a)).Status);
        await Send("booking.hold_expired", T(3), new { booking = Ref(midB, pidB) });
        await Send("booking.confirmed", T(9), new { booking = Ref(midB, pidB) });
        var rb = await Row(b);
        Assert.Equal(BookingStatus.Expired, rb.Status);
        var view = await (await _k.Get($"/api/v1/bookings/{b}", _jane)).Content.ReadAsStringAsync();
        Assert.Contains("\"holdExpired\":true", view);
    }

    [Fact]
    public async Task An_event_is_matched_on_both_our_id_and_the_portal_id_never_on_one_alone()
    {
        var (a, midA, pidA) = await NewHold(hourOffset: 0);
        var (b, midB, pidB) = await NewHold(hourOffset: 1);
        // right marketplace id, someone else's portal id: skipped, nothing changes
        Assert.Equal("skipped", StatusOf((await Send("booking.cancelled", T(1), new { booking = Ref(midA, pidB), cancelledBy = "seller" })).Json));
        // right portal id, wrong marketplace id (a different booking's): skipped as unknown or mismatching
        Assert.Equal("skipped", StatusOf((await Send("booking.cancelled", T(2), new { booking = Ref("BK-DOESNOTEXIST", pidA), cancelledBy = "seller" })).Json));
        Assert.Equal("skipped", StatusOf((await Send("booking.cancelled", T(3), new { booking = new { marketplaceBookingId = midA }, cancelledBy = "seller" })).Json));   // no portal id at all
        Assert.Equal(BookingStatus.Hold, (await Row(a)).Status);
        Assert.Equal(BookingStatus.Hold, (await Row(b)).Status);
        Assert.Equal("applied", StatusOf((await Send("booking.cancelled", T(4), new { booking = Ref(midA, pidA), cancelledBy = "seller" })).Json));
        Assert.Equal(BookingStatus.CancelledBySeller, (await Row(a)).Status);
        Assert.Equal(BookingStatus.Hold, (await Row(b)).Status);
    }

    [Fact]
    public async Task An_event_that_beats_the_hold_answer_is_parked_and_applied_when_the_hold_is_retried()
    {
        await NewHold();                         // sets up the portal, the buyer and the catalog
        _portal.LoseAnswer = true;
        var start = new DateTimeOffset(_k.F.Clock.GetUtcNow().Date.AddDays(8).AddHours(14), TimeSpan.Zero);
        object Hold() => new { listingId = Service, start, location = "online", durationMinutes = 240 };
        async Task<HttpResponseMessage> Send1()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/holds") { Content = JsonContent.Create(Hold()) };
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jane); r.Headers.Add("Idempotency-Key", "lost-answer-key");
            return await _k.Http.SendAsync(r);
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send1()).StatusCode);
        var pending = await _k.Db(db => db.Bookings.AsNoTracking().SingleAsync(b => b.IdempotencyKey == "lost-answer-key"));
        Assert.Equal(BookingStatus.HoldPending, pending.Status);
        var portalId = _portal.Find(pending.MarketplaceBookingId)!.BookingId;
        // the portal confirms (instant booking) before we ever saw the hold answer
        Assert.Equal("parked", StatusOf((await Send("booking.confirmed", T(1), new { booking = Ref(pending.MarketplaceBookingId, portalId), onlineInstructions = "https://meet.example/late" })).Json));
        _portal.LoseAnswer = false;
        var retry = await Send1();
        Assert.True(retry.StatusCode == HttpStatusCode.OK, string.Join("\n", _k.F.Logs.Lines.TakeLast(8)));
        var b = await Row(pending.Id);
        Assert.Equal(portalId, b.PortalBookingId);
        Assert.Equal(BookingStatus.Confirmed, b.Status);               // the parked confirmation was replayed after the id was stored
        Assert.Equal("https://meet.example/late", b.OnlineInstructions);
    }

    [Fact]
    public async Task Fees_and_refunds_are_the_portals_numbers_clamped_a_seller_cancel_has_no_fee_and_no_show_stores_its_fee()
    {
        var (a, midA, pidA) = await NewHold(hourOffset: 0);
        var (b, midB, pidB) = await NewHold(hourOffset: 1);
        await Send("booking.confirmed", T(1), new { booking = Ref(midA, pidA), totalCents = 24000 });
        await Send("booking.cancelled", T(2), new { booking = Ref(midA, pidA), cancelledBy = "seller", cancellationFeeCents = 9999, refundDueCents = 999999 });
        var ra = await Row(a);
        Assert.Equal(0, ra.CancellationFeeCents);                      // never a fee on a seller cancellation, whatever the event says
        Assert.Equal(24000, ra.RefundDueCents);                        // never more than what the booking cost
        await Send("booking.confirmed", T(1), new { booking = Ref(midB, pidB), totalCents = 24000 });
        await Send("booking.no_show", T(5), new { booking = Ref(midB, pidB), noShowFeeCents = 6000, refundDueCents = 18000 });
        var rb = await Row(b);
        Assert.Equal(BookingStatus.NoShow, rb.Status); Assert.Equal(6000, rb.NoShowFeeCents); Assert.Equal(18000, rb.RefundDueCents);
        // fractions and strings are not money
        var (c, midC, pidC) = await NewHold(hourOffset: 2);
        await Send("booking.cancelled", T(6), new { booking = Ref(midC, pidC), cancelledBy = "buyer", cancellationFeeCents = 12.5, refundDueCents = "100" });
        var rc = await Row(c);
        Assert.Equal(0, rc.CancellationFeeCents); Assert.Equal(0, rc.RefundDueCents);
    }

    [Fact]
    public async Task An_additional_charge_is_only_a_request_stored_for_the_buyer_to_answer()
    {
        var (id, mid, pid) = await NewHold();
        await Send("booking.confirmed", T(1), new { booking = Ref(mid, pid), totalCents = 24000 });
        await Send("booking.started", T(2), new { booking = Ref(mid, pid) });
        await Send("booking.additional_charge_requested", T(3), new { booking = Ref(mid, pid), amountCents = 3750, extraMinutes = 30, currency = "USD" });
        var b = await Row(id);
        Assert.Equal(BookingStatus.InProgress, b.Status);
        Assert.Equal(3750, b.AdditionalChargeCents); Assert.Equal("requested", b.ExtraChargeState);
        Assert.Equal(24000, b.TotalCents);                              // the booked price is never changed
        // a later, older status event cannot erase it, and a replay of an older request cannot change the amount
        await Send("booking.additional_charge_requested", T(1), new { booking = Ref(mid, pid), amountCents = 9999 });
        Assert.Equal(3750, (await Row(id)).AdditionalChargeCents);
        await Send("booking.completed", T(5), new { booking = Ref(mid, pid) });
        var done = await Row(id);
        Assert.Equal(BookingStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Reschedule_proposal_and_provider_facts_are_ordered_in_their_own_groups_and_a_seller_reschedule_asks_for_consent()
    {
        var (id, mid, pid) = await NewHold();
        await Send("booking.requested", T(1), new { booking = Ref(mid, pid), approvalDeadline = T(500) });
        var newStart = _k.F.Clock.GetUtcNow().AddDays(9);
        await Send("booking.time_proposed", T(2), new { booking = Ref(mid, pid), proposedStartAt = newStart });
        Assert.Equal(newStart, (await Row(id)).ProposedStartAt);
        await Send("booking.confirmed", T(3), new { booking = Ref(mid, pid) });
        Assert.Null((await Row(id)).ProposedStartAt);
        var moved = _k.F.Clock.GetUtcNow().AddDays(10);
        await Send("booking.rescheduled", T(4), new { booking = Ref(mid, pid), initiatedBy = "seller", requiresBuyerConsent = true, startAt = moved, endAt = moved.AddHours(4), previousStartAt = newStart });
        var b = await Row(id);
        Assert.Equal(BookingStatus.Rescheduled, b.Status); Assert.True(b.RequiresBuyerConsent); Assert.Equal(moved, b.StartAt);
        Assert.Equal(0, b.RescheduleCount);                             // a seller reschedule does not count against the buyer's limit
        var other = Guid.NewGuid();
        await Send("booking.provider_changed", T(5), new { booking = Ref(mid, pid), providerId = other });
        Assert.Equal(other, (await Row(id)).ProviderId);
    }

    [Fact]
    public async Task A_replayed_event_id_changes_nothing_and_an_unknown_booking_is_skipped()
    {
        var (id, mid, pid) = await NewHold();
        var first = await Send("booking.confirmed", T(1), new { booking = Ref(mid, pid) }, id: "dup-1");
        var again = await Send("booking.confirmed", T(1), new { booking = Ref(mid, pid) }, id: "dup-1");
        Assert.Equal("applied", StatusOf(first.Json));
        Assert.Contains("uplicate", again.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("skipped", StatusOf((await Send("booking.confirmed", T(2), new { booking = Ref("BK-UNKNOWN", Guid.NewGuid()) })).Json));
        Assert.Equal("skipped", StatusOf((await Send("booking.confirmed", T(2), new { })).Json));
    }
}
