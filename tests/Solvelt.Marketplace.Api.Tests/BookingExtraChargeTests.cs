using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 4: additional charges are requests; the buyer's consent comes first, the amount is the portal's, and the cap holds.</summary>
public sealed class BookingExtraChargeTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();

    private async Task<Booking> Completed()
    {
        await Ready();
        var b = await PaidBooking();
        await PortalEvent("booking.started", Now.AddMinutes(1), b);
        await PortalEvent("booking.completed", Now.AddMinutes(2), b, new { actualMinutes = 270, billedMinutes = 270 });
        return b;
    }

    private Task RequestExtra(Booking b, long amount, int minute = 3) => PortalEvent("booking.additional_charge_requested", Now.AddMinutes(minute), b, new { amountCents = amount, extraMinutes = 30, currency = "USD" });
    private FakePaymentProvider.Intent ExtraIntent(string mid) => Stripe.Intents.Where(i => i.Metadata["marketplace_booking_id"] == mid && i.Metadata["kind"] == "booking_extra_charge").Single();
    private Task<HttpResponseMessage> Accept(Guid id, string? token = null, object? body = null) => Send(HttpMethod.Post, $"/api/v1/bookings/{id}/extra-charge/accept", token ?? _jane, body ?? new { });

    [Fact]
    public async Task Nothing_is_charged_until_the_buyer_accepts_then_one_intent_for_the_portals_amount_and_the_sale_is_reported_with_that_amount()
    {
        var b = await Completed();
        await RequestExtra(b, 3750);
        var before = await Row(b.Id);
        Assert.Equal("requested", before.ExtraChargeState); Assert.Equal(24000, before.TotalCents);
        Assert.Single(Stripe.Intents);                                      // only the booking's own intent: asking is not charging
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(b.Id, _joe)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/accept", null)).StatusCode);

        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Accept(b.Id, body: new { expectedAmountCents = 3750, amountCents = 1, amount = 1 })));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var intent = ExtraIntent(b.MarketplaceBookingId);                   // exactly one, for the portal's amount, whatever the client sent
        Assert.Equal(3750, intent.AmountCents);
        Assert.Equal("consented", (await Row(b.Id)).ExtraChargeState);

        Stripe.Capture(intent.Id);
        var ev = JsonSerializer.Serialize(new { id = "evt_x1", type = "payment_intent.succeeded", created = Now.ToUnixTimeSeconds(), data = new { @object = new { id = intent.Id, amount = 3750, amount_received = 3750, currency = "usd", metadata = new { marketplace_booking_id = b.MarketplaceBookingId, kind = "booking_extra_charge" } } } });
        Assert.True((await PostStripe(ev)).IsSuccessStatusCode);
        Assert.True((await PostStripe(ev)).IsSuccessStatusCode);            // Stripe repeats itself
        await Drain();
        var row = await Row(b.Id);
        Assert.Equal("paid", row.ExtraChargeState); Assert.Equal(24000, row.TotalCents); Assert.Equal(BookingStatus.Completed, row.Status);
        var captured = Assert.Single(_portal.PaymentEvents, e => e.Contains("booking_extra_charge"));
        Assert.Contains("\"amountCents\":3750", captured); Assert.Contains(b.MarketplaceBookingId, captured); Assert.Contains(intent.Id, captured);
        Assert.Empty(Stripe.Refunds);
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(b.Id)).StatusCode);   // already paid
        await RequestExtra(b, 9999, minute: 9);                                   // a paid amount is never replaced
        Assert.Equal(3750, (await Row(b.Id)).AdditionalChargeCents);
    }

    [Fact]
    public async Task Declining_charges_nothing_and_a_new_request_asks_again()
    {
        var b = await Completed();
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(b.Id)).StatusCode);   // nothing requested yet
        await RequestExtra(b, 3750);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/decline", _jane)).StatusCode);
        Assert.Equal("declined", (await Row(b.Id)).ExtraChargeState);
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(b.Id)).StatusCode);
        Assert.Single(Stripe.Intents);
        await RequestExtra(b, 5000, minute: 8);
        Assert.Equal("requested", (await Row(b.Id)).ExtraChargeState);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id, body: new { expectedAmountCents = 5000 })).StatusCode);
        Assert.Equal(5000, ExtraIntent(b.MarketplaceBookingId).AmountCents);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/decline", _joe)).StatusCode);
    }

    [Fact]
    public async Task A_payment_above_what_the_portal_now_allows_is_refunded_and_never_reported()
    {
        var b = await Completed();
        await RequestExtra(b, 3750);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id, body: new { expectedAmountCents = 3750 })).StatusCode);
        var intent = ExtraIntent(b.MarketplaceBookingId);
        await RequestExtra(b, 2000, minute: 8);                                   // the provider lowered the request after the buyer consented
        Stripe.Capture(intent.Id);
        await PostStripe(JsonSerializer.Serialize(new { id = "evt_x2", type = "payment_intent.succeeded", created = Now.ToUnixTimeSeconds(), data = new { @object = new { id = intent.Id, amount = 3750, amount_received = 3750, currency = "usd", metadata = new { marketplace_booking_id = b.MarketplaceBookingId, kind = "booking_extra_charge" } } } }));
        await Drain();
        Assert.Equal(3750, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.DoesNotContain(_portal.PaymentEvents, e => e.Contains("booking_extra_charge"));
        Assert.NotEqual("paid", (await Row(b.Id)).ExtraChargeState);
        // the new, lower request is a fresh consent and a fresh intent
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id, body: new { expectedAmountCents = 2000 })).StatusCode);
        Assert.Equal(2, Stripe.Intents.Count(i => i.Metadata["kind"] == "booking_extra_charge"));
    }

    [Fact]
    public async Task A_request_after_a_cancellation_or_no_show_is_ignored_and_never_chargeable()
    {
        await Ready();
        var b = await PaidBooking();
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), b, new { cancelledBy = "seller", refundDueCents = 24000 });
        var late = await PortalEvent("booking.additional_charge_requested", Now.AddMinutes(2), b, new { amountCents = 3750 });
        Assert.Equal("skipped", StatusOf(late.Json));
        Assert.Null((await Row(b.Id)).ExtraChargeState);
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(b.Id)).StatusCode);
    }

    [Fact]
    public async Task A_charge_below_stripes_minimum_is_refused_before_any_intent()
    {
        var b = await Completed();
        await RequestExtra(b, 30);
        var r = await Accept(b.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("amount_too_small", await Code(r));
        Assert.Single(Stripe.Intents);
    }

    [Fact]
    public async Task Consent_names_the_amount_the_buyer_saw_and_a_changed_request_is_refused_not_charged_under_the_old_label()
    {
        var b = await Completed();
        await RequestExtra(b, 3750);
        await RequestExtra(b, 5200, minute: 9);                                          // the provider asks for MORE before the buyer pressed the button
        var stale = await Accept(b.Id, body: new { expectedAmountCents = 3750 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("amount_changed", await Code(stale));
        Assert.DoesNotContain(Stripe.Intents, i => i.Metadata["kind"] == "booking_extra_charge");      // nothing was charged, nothing consented
        Assert.Equal("requested", (await Row(b.Id)).ExtraChargeState);
        var ok = await Accept(b.Id, body: new { expectedAmountCents = 5200 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(5200, (await Json(ok)).GetProperty("amountCents").GetInt64());
        Assert.Equal(5200, ExtraIntent(b.MarketplaceBookingId).AmountCents);
    }

    [Fact]
    public async Task M4_29_Consent_without_the_amount_the_buyer_saw_is_refused_and_nothing_is_consented_or_charged()
    {
        var b = await Completed();
        await RequestExtra(b, 3750);
        foreach (var body in new object[] { new { }, new { expectedAmountCents = (long?)null }, new { amountCents = 3750 } })
        {
            var r = await Accept(b.Id, body: body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("expectedAmountCents", out _));
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/bookings/{b.Id}/extra-charge/accept", _jane)).StatusCode);   // no body at all
        Assert.DoesNotContain(Stripe.Intents, i => i.Metadata["kind"] == "booking_extra_charge");
        Assert.Equal("requested", (await Row(b.Id)).ExtraChargeState);
        await RequestExtra(b, 5200, minute: 9);                                          // the raised amount is not consented by a client that names none
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Accept(b.Id)).StatusCode);
        Assert.Equal("requested", (await Row(b.Id)).ExtraChargeState);
        Assert.Equal(HttpStatusCode.OK, (await Accept(b.Id, body: new { expectedAmountCents = 5200 })).StatusCode);
    }
}
