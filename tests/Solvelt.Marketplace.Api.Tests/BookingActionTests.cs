using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 4: what the buyer can do to a booking, and the money that follows from the portal's events.</summary>
public sealed class BookingActionTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();

    [Fact]
    public async Task Cancelling_an_unpaid_hold_is_free_idempotent_and_releases_the_slot_at_the_portal()
    {
        await Ready();
        var b = await NewHold();
        var r = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/cancel", _jane, new { reason = "changed my mind" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("CancelledByBuyer", (await Json(r)).GetProperty("booking").GetProperty("status").GetString());
        Assert.Equal(BookingStatus.CancelledByBuyer, (await Row(b.Id)).Status);
        var again = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/cancel", _jane, new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);                 // a repeated tap is the same cancellation
        Assert.Equal(1, _portal.ActionCalls);
        Assert.StartsWith("cancel-BK-", _portal.Holds.Single().RequestIds.Single());   // the request id is ours, derived from our booking id
        // the slot is free for someone else now
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/api/v1/bookings/holds", _joe, new { listingId = Service, start = Slot(), location = "online", durationMinutes = 240 }, "joe-hold-0001")).StatusCode);
    }

    [Fact]
    public async Task A_buyer_cancellation_with_a_fee_refunds_exactly_what_the_portal_says_is_due_and_never_the_fee()
    {
        await Ready();
        var b = await PaidBooking();
        Assert.Equal(BookingStatus.Confirmed, b.Status);
        _portal.CancelFeeCents = 6000;
        var r = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/cancel", _jane, new { reason = "can't make it" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var view = (await Json(r)).GetProperty("booking");
        Assert.Equal(6000, view.GetProperty("cancellationFeeCents").GetInt64());
        Assert.Empty(Stripe.Refunds);                                      // money follows the portal's event, not the request
        var evt = await PortalEvent("booking.cancelled", Now.AddMinutes(1), b, new { cancelledBy = "buyer", cancellationFeeCents = 6000, refundDueCents = 18000 });
        Assert.Equal("applied", StatusOf(evt.Json));
        await Drain();
        Assert.Equal(18000, Assert.Single(Stripe.Refunds).AmountCents);    // total 24000 minus the 6000 fee
        var pay = await Pay(b.Id);
        Assert.Equal(18000, pay.RefundedCents);
        var reported = Assert.Single(RefundEvents());
        Assert.Contains(b.MarketplaceBookingId, reported); Assert.Contains("18000", reported);
        Assert.DoesNotContain("marketplaceOrderId", reported);
        // the same fact again (another event id, same amounts) cannot refund twice
        await PortalEvent("booking.cancelled", Now.AddMinutes(2), b, new { cancelledBy = "buyer", cancellationFeeCents = 6000, refundDueCents = 18000 });
        await Drain();
        Assert.Single(Stripe.Refunds);
    }

    [Fact]
    public async Task A_seller_cancellation_refunds_everything_whatever_the_event_claims_about_fees_and_a_huge_refund_is_capped_at_the_payment()
    {
        await Ready();
        var seller = await PaidBooking();
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), seller, new { cancelledBy = "seller", cancellationFeeCents = 9999, refundDueCents = 5 });
        await Drain();
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);    // a seller cancel is free for the buyer: full amount, not the "5" the event carries
        var greedy = await PaidBooking(hours: 1);
        await PortalEvent("booking.no_show", Now.AddMinutes(3), greedy, new { noShowFeeCents = 0, refundDueCents = 99999999 });
        await Drain();
        Assert.Equal(new long[] { 24000, 24000 }, Stripe.Refunds.Select(r => r.AmountCents).ToArray());   // the second is clamped to the 24000 that was paid
    }

    [Fact]
    public async Task A_declined_request_to_book_returns_everything_that_was_paid()
    {
        await Ready();
        _portal.Instant = false;
        var b = await PaidBooking();
        Assert.Equal(BookingStatus.Requested, b.Status);
        Assert.Single(_portal.PaymentEvents);                              // the sale is known to the portal (a request is paid up front)
        await PortalEvent("booking.declined", Now.AddMinutes(5), b, new { reason = "busy", auto = false });
        await Drain();
        Assert.Equal(BookingStatus.Declined, (await Row(b.Id)).Status);
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Single(RefundEvents());
    }

    [Fact]
    public async Task A_no_show_refunds_total_minus_the_portals_fee()
    {
        await Ready();
        var b = await PaidBooking();
        await PortalEvent("booking.no_show", Now.AddMinutes(5), b, new { noShowFeeCents = 6000, refundDueCents = 18000 });
        await Drain();
        Assert.Equal(18000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Equal(6000, (await Row(b.Id)).NoShowFeeCents);
    }

    [Fact]
    public async Task Rescheduling_is_a_portal_call_with_a_request_id_we_derive_and_a_taken_time_is_a_conflict()
    {
        await Ready();
        var b = await PaidBooking();
        var target = Slot(2);
        var r = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/reschedule", _jane, new { newStart = target, priceCents = 1 }, "resched-key-0001");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var view = (await Json(r)).GetProperty("booking");
        Assert.Equal("Rescheduled", view.GetProperty("status").GetString());
        Assert.Equal(target, view.GetProperty("startAt").GetDateTimeOffset());
        Assert.Equal(24000, view.GetProperty("totalCents").GetInt64());
        Assert.Equal($"resched-{b.MarketplaceBookingId}-resched-key-0001", _portal.Holds.Single().RequestIds.Single());
        // someone else holds the next time: 409 slot_unavailable, nothing changes
        var other = await NewHold(_joe, hours: 4);
        var taken = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/reschedule", _jane, new { newStart = Slot(4) }, "resched-key-0002");
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("slot_unavailable", await Code(taken));
        Assert.Equal(target, (await Row(b.Id)).StartAt);
        _portal.RescheduleLimitReached = true;
        Assert.Equal("reschedule_limit", await Code(await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/reschedule", _jane, new { newStart = Slot(6) }, "resched-key-0003")));
        // validation and state
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/reschedule", _jane, new { newStart = Now.AddHours(-1) }, "resched-key-0004")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/reschedule", _jane, new { newStart = Slot(6) }, null)).StatusCode);   // needs a key
        var hold = await NewHold(hours: 8);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/bookings/{hold.Id}/reschedule", _jane, new { newStart = Slot(9) }, "resched-key-0005")).StatusCode);   // a hold is not rescheduled
        Assert.NotNull(other);
    }

    [Fact]
    public async Task The_providers_proposed_time_can_be_accepted_or_declined_only_when_there_is_one()
    {
        await Ready();
        _portal.Instant = false;
        var b = await PaidBooking();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/proposal", _jane, new { accept = true })).StatusCode);   // nothing proposed yet
        var proposed = Slot(3);
        _portal.Holds.Single().ProposedStart = proposed;
        await PortalEvent("booking.time_proposed", Now.AddMinutes(1), b, new { proposedStartAt = proposed });
        Assert.Equal(proposed, (await Row(b.Id)).ProposedStartAt);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/proposal", _jane, new { })).StatusCode);
        var ok = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/proposal", _jane, new { accept = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var row = await Row(b.Id);
        Assert.Equal(BookingStatus.Confirmed, row.Status); Assert.Equal(proposed, row.StartAt); Assert.Null(row.ProposedStartAt);
    }

    [Fact]
    public async Task Every_action_is_404_for_someone_elses_booking_and_401_without_a_session()
    {
        await Ready();
        var b = await PaidBooking();
        foreach (var (path, body) in new (string, object)[] { ("cancel", new { }), ("reschedule", new { newStart = Slot(2) }), ("proposal", new { accept = true }), ("acknowledge-reschedule", new { }) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/{path}", _joe, body, "other-key-0001")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/{path}", null, body, "anon-key-00001")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/cancel-preview", _joe)).StatusCode);
        Assert.Equal(0, _portal.ActionCalls);
        Assert.Equal(BookingStatus.Confirmed, (await Row(b.Id)).Status);
    }

    [Fact]
    public async Task The_fee_preview_is_unknown_until_the_portal_sends_a_policy_then_follows_its_rule_with_half_up_rounding()
    {
        await Ready();
        var unknown = await PaidBooking();
        var p0 = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{unknown.Id}/cancel-preview", _jane));
        Assert.False(p0.GetProperty("feeKnown").GetBoolean());             // we do not guess a fee
        Assert.Equal(JsonValueKind.Null, p0.GetProperty("feeCents").ValueKind);

        _portal.PriceCents = 10002;                                         // 25 % of 100.02 = 25.005: half-up gives 25.01
        _portal.PolicyForNext = new { cancelFreeHours = 24, cancelFeePercent = 25, noShowFeePercent = 50, maxReschedules = 2, approvalHours = 12 };
        var b = await PaidBooking(hours: 1);
        var early = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/cancel-preview", _jane));
        Assert.True(early.GetProperty("feeKnown").GetBoolean());
        Assert.True(early.GetProperty("withinFreeWindow").GetBoolean());
        Assert.Equal(0, early.GetProperty("feeCents").GetInt64());
        Assert.Equal(10002, early.GetProperty("refundCents").GetInt64());
        _k.F.Clock.Advance(TimeSpan.FromDays(6.5));                          // less than 24 h before the start
        var late = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/cancel-preview", _jane));
        Assert.False(late.GetProperty("withinFreeWindow").GetBoolean());
        Assert.Equal(2501, late.GetProperty("feeCents").GetInt64());
        Assert.Equal(10002 - 2501, late.GetProperty("refundCents").GetInt64());
        // an unpaid hold or a request is always free
        var hold = await NewHold(hours: 5);
        var free = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{hold.Id}/cancel-preview", _jane));
        Assert.True(free.GetProperty("feeKnown").GetBoolean()); Assert.Equal(0, free.GetProperty("feeCents").GetInt64());
    }

    [Fact]
    public async Task A_seller_reschedule_asks_for_consent_the_buyer_can_accept_it_and_cancelling_stays_free_in_the_preview()
    {
        await Ready();
        _portal.PolicyForNext = new { cancelFreeHours = 24, cancelFeePercent = 25, noShowFeePercent = 50, maxReschedules = 2, approvalHours = 12 };
        var b = await PaidBooking();
        var moved = Slot(3, days: 1);                                        // moved to within the paid-fee window
        await PortalEvent("booking.rescheduled", Now.AddMinutes(1), b, new { initiatedBy = "seller", requiresBuyerConsent = true, startAt = moved, endAt = moved.AddHours(4) });
        Assert.True((await Row(b.Id)).RequiresBuyerConsent);
        var preview = await Json(await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/cancel-preview", _jane));
        Assert.Equal(0, preview.GetProperty("feeCents").GetInt64());         // the buyer did not ask for the move: no fee
        var ack = await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/acknowledge-reschedule", _jane);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.False((await Row(b.Id)).RequiresBuyerConsent);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/acknowledge-reschedule", _jane)).StatusCode);
    }

    [Fact]
    public async Task The_reminder_mail_goes_once_to_the_buyer_for_a_live_booking_at_the_reminded_time_only()
    {
        await Ready();
        var b = await PaidBooking();
        var before = (await _k.Mail("jane@example.com")).Count;
        var r1 = await PortalEvent("booking.reminder", Now.AddMinutes(1), b, new { kind = "day_before", startAt = b.StartAt }, "rem-1");
        Assert.Equal("applied", StatusOf(r1.Json));
        await Drain();
        var mails = await _k.Mail("jane@example.com");
        Assert.Equal(before + 1, mails.Count);
        Assert.Contains("Reminder", mails.Last().Subject); Assert.Contains("/account/bookings", mails.Last().TextBody);
        Assert.Contains("/account/notifications", mails.Last().TextBody);                       // M5's notifier: preferences and unsubscribe come with the mail
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == "booking_reminder")));   // and the in-app entry
        Assert.DoesNotContain("meet.example", mails.Last().TextBody);
        await PortalEvent("booking.reminder", Now.AddMinutes(1), b, new { kind = "day_before", startAt = b.StartAt }, "rem-1");          // the same event again
        Assert.Equal("skipped", StatusOf((await PortalEvent("booking.reminder", Now.AddMinutes(2), b, new { kind = "hour_before", startAt = b.StartAt!.Value.AddHours(3) })).Json));   // an old time
        await Drain();
        Assert.Equal(before + 1, (await _k.Mail("jane@example.com")).Count);
        await PortalEvent("booking.cancelled", Now.AddMinutes(3), b, new { cancelledBy = "buyer", refundDueCents = 24000 });
        Assert.Equal("skipped", StatusOf((await PortalEvent("booking.reminder", Now.AddMinutes(4), b, new { kind = "hour_before", startAt = b.StartAt })).Json));   // a cancelled booking is not reminded
    }

    [Fact]
    public async Task The_buyer_is_notified_of_what_happened_to_them_not_of_what_they_just_did()
    {
        await Ready();
        var b = await PaidBooking();                                                           // booking.confirmed is not emitted by the fake portal here: send it
        await PortalEvent("booking.confirmed", Now.AddMinutes(1), b, new { totalCents = 24000 });
        await PortalEvent("booking.rescheduled", Now.AddMinutes(2), b, new { initiatedBy = "buyer", startAt = Slot(3), endAt = Slot(7) });       // the buyer's own move: no news
        await PortalEvent("booking.rescheduled", Now.AddMinutes(3), b, new { initiatedBy = "seller", requiresBuyerConsent = true, startAt = Slot(4), endAt = Slot(8) });
        await PortalEvent("booking.cancelled", Now.AddMinutes(4), b, new { cancelledBy = "seller", refundDueCents = 24000 });
        var kinds = (await _k.Db(db => db.Notifications.Select(n => n.Kind).ToListAsync())).Order().ToArray();
        Assert.Equal(new[] { "booking_cancelled", "booking_confirmed", "booking_rescheduled" }, kinds);
        // a repeated event id notifies once
        await PortalEvent("booking.confirmed", Now.AddMinutes(1), b, new { totalCents = 24000 }, "dup-notify");
        await PortalEvent("booking.confirmed", Now.AddMinutes(1), b, new { totalCents = 24000 }, "dup-notify");
        Assert.Equal(3, await _k.Db(db => db.Notifications.CountAsync()));
    }
}
