using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Services;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The independent review of a1c53d2..0c24759 (booking money): every case below was reproduced by the reviewer's probe on 0c24759 and fails there.
/// H1 confirm racing hold expiry, H2 an extra charge paid twice, M1 terminal states set from portal answers, M2 refunds of an unreported sale, L1, L5.
/// </summary>
public sealed class BookingMoneyFixTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();

    private async Task<(Booking Booking, FakePaymentProvider.Intent Intent)> PaidNotConfirmed()
    {
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        Assert.True((await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, intent.AmountCents))).IsSuccessStatusCode);
        return (b, intent);
    }

    [Fact]
    public async Task H1_a_confirm_that_waited_out_the_hold_never_confirms_charges_or_reports_the_closed_booking()
    {
        await Ready();
        var (b, _) = await PaidNotConfirmed();
        _portal.ConfirmDown = true;
        await Drain();                                                      // the confirm retries against an outage
        var callsDuring = _portal.ConfirmCalls;
        await PortalEvent("booking.hold_expired", Now.AddMinutes(11), b);   // the hold lapses: the row is Expired
        Assert.Equal(BookingStatus.Expired, (await Row(b.Id)).Status);
        _portal.ConfirmDown = false;
        _k.F.Clock.Advance(TimeSpan.FromHours(2));
        await Drain();
        Assert.Equal(callsDuring, _portal.ConfirmCalls);                    // not even asked to confirm a booking we show as over
        var pay = await Pay(b.Id);
        Assert.Equal(BookingPaymentStatus.Void, pay.Status);
        Assert.False(pay.ReportedToPortal);
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(_portal.PaymentEvents);
        Assert.Equal(BookingStatus.Expired, (await Row(b.Id)).Status);
    }

    [Fact]
    public async Task H1_a_portal_that_confirms_while_we_close_the_booking_is_cancelled_there_and_the_payment_is_refunded_unreported()
    {
        await Ready();
        var (b, _) = await PaidNotConfirmed();
        // The portal confirms, and before its answer reaches us the hold-expired event is applied on our side.
        _portal.OnConfirmed = () => Assert.Equal("applied", StatusOf(PortalEvent("booking.hold_expired", Now.AddMinutes(11), b).GetAwaiter().GetResult().Json));
        await Drain();
        var row = await Row(b.Id);
        Assert.Equal(BookingStatus.Expired, row.Status);
        var pay = await Pay(b.Id);
        Assert.Equal(BookingPaymentStatus.Void, pay.Status);
        Assert.False(pay.ReportedToPortal);
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(_portal.PaymentEvents);                                // never a sale for a booking we show as Expired
        Assert.Equal("Confirmed", _portal.Find(b.MarketplaceBookingId)!.Status.Replace("CancelledByBuyer", "Confirmed"));   // it was confirmed over there ...
        await Drain();
        Assert.Contains(_portal.Holds.Single().RequestIds, r => r.StartsWith("cancel-late-"));   // ... and is cancelled there now
    }

    [Fact]
    public async Task M1_a_finished_booking_set_by_a_portal_answer_is_not_reopened_by_a_stale_event_and_never_shows_the_link()
    {
        await Ready();
        _portal.OnlineInstructions = "https://meet.example/secret-room";
        var b = await PaidBooking();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/cancel", _jane, new { })).StatusCode);   // CancelledByBuyer from the portal's ANSWER
        Assert.Equal(BookingStatus.CancelledByBuyer, (await Row(b.Id)).Status);
        var stale = await PortalEvent("booking.confirmed", Now.AddHours(1), b, new { onlineInstructions = "https://meet.example/secret-room" });
        Assert.Equal("skipped", StatusOf(stale.Json));
        var row = await Row(b.Id);
        Assert.Equal(BookingStatus.CancelledByBuyer, row.Status);
        Assert.Null(row.OnlineInstructions);
        Assert.DoesNotContain("secret-room", await (await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}", _jane)).Content.ReadAsStringAsync());
        // the cancellation event itself still does its work (the refund)
        Assert.Equal("applied", StatusOf((await PortalEvent("booking.cancelled", Now.AddHours(2), b, new { cancelledBy = "buyer", cancellationFeeCents = 0, refundDueCents = 24000 })).Json));
        await Drain();
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
    }

    [Fact]
    public async Task M2_a_cancellation_before_the_sale_was_reported_voids_and_refunds_in_full_and_reports_no_refund()
    {
        await Ready();
        var (b, _) = await PaidNotConfirmed();
        _portal.ConfirmDown = true;
        await Drain();                                                      // captured, the confirm is still retrying: the portal has no sale
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/cancel", _jane, new { })).StatusCode);
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), b, new { cancelledBy = "buyer", cancellationFeeCents = 0, refundDueCents = 24000 });
        _portal.ConfirmDown = false;
        _k.F.Clock.Advance(TimeSpan.FromHours(2));
        await Drain();
        var pay = await Pay(b.Id);
        Assert.Equal(BookingPaymentStatus.Void, pay.Status);
        Assert.Equal(24000, pay.RefundedCents);
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(_portal.PaymentEvents);                                // neither payment.captured nor refund.processed: the portal never recorded a sale
    }

    private async Task<Booking> Completed()
    {
        await Ready();
        var b = await PaidBooking();
        await PortalEvent("booking.started", Now.AddMinutes(1), b);
        await PortalEvent("booking.completed", Now.AddMinutes(2), b);
        return b;
    }

    private Task Request(Booking b, long amount, int minute) => PortalEvent("booking.additional_charge_requested", Now.AddMinutes(minute), b, new { amountCents = amount, extraMinutes = 30, currency = "USD" });
    private Task<HttpResponseMessage> Accept(Guid id) => Send(HttpMethod.Post, $"/api/v1/bookings/{id}/extra-charge/accept", _jane, new { });
    private List<FakePaymentProvider.Intent> Extras(string mid) => Stripe.Intents.Where(i => i.Metadata["marketplace_booking_id"] == mid && i.Metadata["kind"] == "booking_extra_charge").OrderBy(i => i.AmountCents).ToList();

    private Task PayExtra(FakePaymentProvider.Intent i, string mid)
    {
        Stripe.Capture(i.Id);
        return PostStripe(JsonSerializer.Serialize(new { id = $"evt_x{++_ev}", type = "payment_intent.succeeded", created = Now.ToUnixTimeSeconds(), data = new { @object = new { id = i.Id, amount = i.AmountCents, amount_received = i.AmountCents, currency = "usd", metadata = new { marketplace_booking_id = mid, kind = "booking_extra_charge" } } } }));
    }

    [Fact]
    public async Task H2_an_extra_charge_is_kept_once_a_superseded_intent_that_still_gets_paid_is_refunded_in_full()
    {
        var b = await Completed();
        await Request(b, 3000, 3);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id)).StatusCode);          // intent A for 30.00
        await Request(b, 4000, 9);                                                 // the provider changes the request: a new consent is needed
        Assert.Equal("requested", (await Row(b.Id)).ExtraChargeState);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id)).StatusCode);          // intent B for 40.00
        var (a, bb) = (Extras(b.MarketplaceBookingId)[0], Extras(b.MarketplaceBookingId)[1]);
        Assert.Equal((3000, 4000), (a.AmountCents, bb.AmountCents));
        await PayExtra(a, b.MarketplaceBookingId);                                 // the old page is still open and gets paid
        await PayExtra(bb, b.MarketplaceBookingId);
        await Drain();
        Assert.Equal("paid", (await Row(b.Id)).ExtraChargeState);
        var captured = Assert.Single(_portal.PaymentEvents, e => e.Contains("booking_extra_charge"));
        Assert.Contains("\"amountCents\":4000", captured);
        Assert.Equal(3000, Assert.Single(Stripe.Refunds).AmountCents);              // the superseded payment is returned, not kept
        Assert.Equal(1000 * 0 + 4000, await _k.Db(db => db.BookingPayments.Where(p => p.BookingId == b.Id && p.Kind == "booking_extra_charge" && p.Status == BookingPaymentStatus.Captured).SumAsync(p => p.CapturedCents)));
    }

    [Fact]
    public async Task H2_declining_closes_the_open_intent_and_a_payment_that_still_lands_is_refunded()
    {
        var b = await Completed();
        await Request(b, 3000, 3);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/decline", _jane)).StatusCode);
        var intent = Extras(b.MarketplaceBookingId).Single();
        await PayExtra(intent, b.MarketplaceBookingId);                            // paid from a page that was open before the decline
        await Drain();
        Assert.Equal(3000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.DoesNotContain(_portal.PaymentEvents, e => e.Contains("booking_extra_charge"));
        Assert.Equal("declined", (await Row(b.Id)).ExtraChargeState);
    }

    [Fact]
    public async Task H2_a_changed_request_and_a_decline_cancel_the_open_intents_at_stripe()
    {
        var b = await Completed();
        await Request(b, 3000, 3);
        await Accept(b.Id);
        await Request(b, 4000, 9);
        await Drain();
        Assert.True(Extras(b.MarketplaceBookingId).Single().Cancelled);            // intent A is no longer payable
        await Accept(b.Id);
        await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/decline", _jane);
        await Drain();
        Assert.All(Extras(b.MarketplaceBookingId), i => Assert.True(i.Cancelled));
    }

    [Fact]
    public async Task L1_the_losing_buyers_retry_with_the_same_key_is_the_same_409_never_a_200_for_a_hold_that_does_not_exist()
    {
        await Ready();
        _portal.FailNext = (HttpStatusCode.Conflict, "slot_unavailable");
        var key = "loser-key-0001";
        var body = new { listingId = Service, start = Slot(), location = "online", durationMinutes = 240 };
        var first = await Send(HttpMethod.Post, "/api/v1/bookings/holds", _jane, body, key);
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        var retry = await Send(HttpMethod.Post, "/api/v1/bookings/holds", _jane, body, key);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("slot_unavailable", await Code(retry));
    }

    [Fact]
    public async Task L5_the_intent_behind_a_lapsed_hold_is_cancelled()
    {
        await Ready();
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        await PortalEvent("booking.hold_expired", Now.AddMinutes(11), b);
        await Drain();
        Assert.True(intent.Cancelled);
    }

    private async Task<string> Secret(HttpResponseMessage r) => (await Json(r)).GetProperty("payment").GetProperty("clientSecret").GetString()!;

    [Fact]
    public async Task L4_a_late_checkout_reuses_the_stored_intent_instead_of_opening_a_second_one()
    {
        await Ready();
        var b = await NewHold();
        var first = await Checkout(b.Id);
        Assert.True(first.IsSuccessStatusCode);
        Stripe.ForgetIdempotencyKeys();                                     // Stripe forgets keys after 24 hours
        var again = await Checkout(b.Id);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await Secret(first), await Secret(again));
        Assert.Single(Stripe.Intents);
    }

    [Fact]
    public async Task L4_a_late_extra_charge_accept_reuses_the_stored_intent_and_a_new_request_after_a_decline_gets_a_fresh_one()
    {
        var b = await Completed();
        await Request(b, 3000, 3);
        var first = await Accept(b.Id);
        Stripe.ForgetIdempotencyKeys();
        var again = await Accept(b.Id);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await Json(first)).GetProperty("payment").GetProperty("clientSecret").GetString(), (await Json(again)).GetProperty("payment").GetProperty("clientSecret").GetString());
        Assert.Single(Extras(b.MarketplaceBookingId));
        // decline closes it; the provider asks again for the SAME amount: a new consent, a new intent (never the cancelled one)
        await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/decline", _jane);
        await Drain();
        await Request(b, 3000, 9);
        var third = await Accept(b.Id);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var intents = Extras(b.MarketplaceBookingId);
        Assert.Equal(2, intents.Count);
        Assert.Equal(1, intents.Count(i => !i.Cancelled));
    }

    [Fact]
    public async Task L4_two_intents_made_in_parallel_leave_exactly_one_stored_and_payable_and_every_answer_names_it()
    {
        await Ready();
        var b = await NewHold();
        Stripe.ForgetKeysAfterCreate = true;                                // every parallel create is a different intent
        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Checkout(b.Id)));
        Stripe.ForgetKeysAfterCreate = false;
        var stored = (await Pay(b.Id)).IntentId!;
        var storedSecret = Stripe.Intents.Single(i => i.Id == stored).ClientSecret;
        foreach (var r in all)
            if (r.StatusCode == HttpStatusCode.OK) Assert.Equal(storedSecret, await Secret(r));      // nobody is told to pay an intent we do not match
            else Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        await Drain();
        Assert.Equal(1, Stripe.Intents.Count(i => !i.Cancelled));            // the extra ones were cancelled
        Assert.False(Stripe.Intents.Single(i => i.Id == stored).Cancelled);
    }

    [Fact]
    public async Task L4_a_second_checkout_that_finishes_inside_the_first_wins_and_the_first_intent_is_cancelled_not_stored_over_it()
    {
        await Ready();
        var b = await NewHold();
        var hooks = _k.F.Services.GetRequiredService<BookingHooks>();
        HttpResponseMessage? inner = null;
        // The first checkout has created its intent and not stored it yet; a second checkout (its idempotency key already forgotten) completes in between.
        hooks.AfterIntentCreated = async () => { Stripe.ForgetKeysAfterCreate = true; inner = await Checkout(b.Id); Stripe.ForgetKeysAfterCreate = false; };
        Stripe.ForgetKeysAfterCreate = true;
        var outer = await Checkout(b.Id);
        Stripe.ForgetKeysAfterCreate = false;
        Assert.Equal(HttpStatusCode.OK, inner!.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outer.StatusCode);       // the first one lost: it is not told to pay an intent we do not match
        var stored = (await Pay(b.Id)).IntentId!;
        Assert.Equal(Stripe.Intents.Single(i => i.Id == stored).ClientSecret, await Secret(inner));
        await Drain();
        Assert.Equal(1, Stripe.Intents.Count(i => !i.Cancelled));
        Assert.False(Stripe.Intents.Single(i => i.Id == stored).Cancelled);
    }

    private Task<HttpResponseMessage> StripeObject(string type, object obj, long? created = null)
        => PostStripe(JsonSerializer.Serialize(new { id = $"evt_m{++_ev}", type, created = created ?? Now.ToUnixTimeSeconds(), data = new { @object = obj } }));

    private async Task<BookingPayment> PaymentRow(Guid bookingId) => await Pay(bookingId);

    [Fact]
    public async Task L2_a_dashboard_refund_is_recorded_once_even_while_one_of_ours_is_only_reserved_and_counts_against_the_cap()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        // our cancellation refund of 18000 is reserved but has not reached Stripe yet
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), b, new { cancelledBy = "buyer", cancellationFeeCents = 6000, refundDueCents = 18000 });
        Assert.Equal(18000, (await Pay(b.Id)).RefundedCents);
        // somebody refunds 5000 in the Stripe dashboard: Stripe's cumulative amount_refunded is 5000
        Assert.True((await StripeObject("charge.refunded", new { payment_intent = intent.Id, amount_refunded = 5000 })).IsSuccessStatusCode);
        var pay = await Pay(b.Id);
        Assert.Equal(5000, pay.ExternalRefundedCents);
        Assert.Equal(23000, pay.RefundedCents);
        Assert.True((await StripeObject("charge.refunded", new { payment_intent = intent.Id, amount_refunded = 5000 })).IsSuccessStatusCode);   // the same fact again
        Assert.Equal(5000, (await Pay(b.Id)).ExternalRefundedCents);
        // only 1000 of the 24000 is left to refund
        using var scope = _k.F.Services.CreateScope();
        var refunds = scope.ServiceProvider.GetRequiredService<BookingRefundService>();
        Assert.Equal(BookingReserveOutcome.ExceedsCaptured, await refunds.ReserveAsync(pay.Id, 1500, "extra-1", "test", false, CancellationToken.None));
        Assert.Equal(BookingReserveOutcome.Reserved, await refunds.ReserveAsync(pay.Id, 1000, "extra-2", "test", false, CancellationToken.None));
    }

    [Fact]
    public async Task L3_a_dispute_on_a_booking_payment_reaches_the_portal_ordered_and_holds_back_refunds_while_open()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        var t0 = Now.ToUnixTimeSeconds();
        Assert.True((await StripeObject("charge.dispute.created", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "needs_response", reason = "fraudulent" }, t0 + 10)).IsSuccessStatusCode);
        await Drain();
        var sent = Assert.Single(_portal.PaymentEvents, e => e.Contains("dispute.created"));
        Assert.Contains(b.MarketplaceBookingId, sent); Assert.Contains("\"disputeId\":\"dp_1\"", sent); Assert.DoesNotContain("marketplaceOrderId", sent);
        var pay = await Pay(b.Id);
        Assert.Equal(24000, pay.DisputedCents);
        using (var scope = _k.F.Services.CreateScope())   // the chargeback already took everything: nothing is left to refund
            Assert.Equal(BookingReserveOutcome.ExceedsCaptured, await scope.ServiceProvider.GetRequiredService<BookingRefundService>().ReserveAsync(pay.Id, 1, "after-chargeback", "test", false, CancellationToken.None));
        // an older update arriving late is dropped; the win releases the money
        Assert.True((await StripeObject("charge.dispute.updated", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "under_review" }, t0 + 5)).IsSuccessStatusCode);
        Assert.True((await StripeObject("charge.dispute.closed", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "won" }, t0 + 20)).IsSuccessStatusCode);
        await Drain();
        Assert.Equal(0, (await Pay(b.Id)).DisputedCents);
        Assert.Equal(1, _portal.PaymentEvents.Count(e => e.Contains("dispute.closed")));
        Assert.DoesNotContain(_portal.PaymentEvents, e => e.Contains("under_review"));
        // nothing follows a closed dispute
        await StripeObject("charge.dispute.updated", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "needs_response" }, t0 + 30);
        await Drain();
        Assert.Equal(0, (await Pay(b.Id)).DisputedCents);
        using var s2 = _k.F.Services.CreateScope();
        Assert.Equal(BookingReserveOutcome.Reserved, await s2.ServiceProvider.GetRequiredService<BookingRefundService>().ReserveAsync((await Pay(b.Id)).Id, 100, "after-win", "test", false, CancellationToken.None));
    }
}
