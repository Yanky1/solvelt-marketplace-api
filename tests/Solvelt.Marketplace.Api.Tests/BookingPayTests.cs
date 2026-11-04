using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Services;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 3: paying for a booking (one intent per booking, hold-then-pay-then-confirm, refunds for what cannot be honoured, payment.captured for bookings).</summary>
public sealed class BookingPayTests : IDisposable
{
    private readonly AuthKit _k = new();
    private readonly FakeBookingPortal _portal;
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private string _jane = "", _joe = "";
    private int _n, _ev;

    public BookingPayTests() => _portal = new FakeBookingPortal(_k.F.Clock);
    public void Dispose() => _k.Dispose();
    private FakePaymentProvider Stripe => _k.F.Services.GetRequiredService<FakePaymentProvider>();

    private async Task Ready()
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{}"));
        _jane = await _k.VerifiedSession("jane@example.com");
        _joe = await _k.VerifiedSession("joe@example.com", name: "Joe Green");
    }

    private HttpRequestMessage Req(HttpMethod m, string url, string token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private async Task<Booking> NewHold(string? token = null, int hours = 0, string location = "online")
    {
        var start = new DateTimeOffset(_k.F.Clock.GetUtcNow().Date.AddDays(7).AddHours(14 + hours), TimeSpan.Zero);
        var r = Req(HttpMethod.Post, "/api/v1/bookings/holds", token ?? _jane, new { listingId = Service, start, location, durationMinutes = 240 });
        r.Headers.Add("Idempotency-Key", "pay-test-key-" + (++_n));
        var res = await _k.Http.SendAsync(r);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var key = "pay-test-key-" + _n;
        return await _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.IdempotencyKey == key));
    }

    private Task<HttpResponseMessage> Checkout(Guid id, object? body = null, string? token = null)
        => _k.Http.SendAsync(Req(HttpMethod.Post, $"/api/v1/bookings/{id}/checkout", token ?? _jane, body ?? new { intake = new[] { new { id = "pets", answer = "no" } }, phone = "+1 555 000 1111" }));

    private string StripeEvent(string type, string intentId, string mid, long amount, string currency = "usd", string? id = null)
        => JsonSerializer.Serialize(new { id = id ?? $"evt_b{++_ev:D4}", type, created = _k.F.Clock.GetUtcNow().ToUnixTimeSeconds(),
            data = new { @object = new { id = intentId, amount, amount_received = amount, currency, metadata = new { marketplace_booking_id = mid, kind = "booking" } } } });

    private async Task<HttpResponseMessage> PostStripe(string body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhooks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("Stripe-Signature", StripeSignature.Sign(body, DevDefaults.StripeWebhookSecret, _k.F.Clock.GetUtcNow()));
        return await _k.Http.SendAsync(r);
    }

    private async Task Drain() { while (await _k.Dispatch() > 0) { } }
    private Task<Booking> Row(Guid id) => _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id));
    private Task<BookingPayment> Pay(Guid bookingId) => _k.Db(db => db.BookingPayments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId));
    private FakePaymentProvider.Intent IntentOf(string mid) => Stripe.Intents.Single(i => i.Metadata.TryGetValue("marketplace_booking_id", out var m) && m == mid);

    private async Task<(Booking Booking, string Intent)> Paid(Booking b, long? amount = null)
    {
        var res = await Checkout(b.Id);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        var ok = await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, amount ?? intent.AmountCents));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        return (b, intent.Id);
    }

    [Fact]
    public async Task Checkout_creates_one_intent_for_the_portals_total_and_ignores_any_amount_the_client_sends()
    {
        await Ready();
        var b = await NewHold();
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Checkout(b.Id, new { amountCents = 1, totalCents = 1, intake = new[] { new { id = "pets", answer = "no" } } })));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var intent = Assert.Single(Stripe.Intents);
        Assert.Equal(24000, intent.AmountCents);
        Assert.Equal(b.MarketplaceBookingId, intent.Metadata["marketplace_booking_id"]);
        Assert.False(intent.Metadata.ContainsKey("marketplace_order_id"));
        Assert.Equal(1, await _k.Db(db => db.BookingPayments.CountAsync()));
        var secrets = new HashSet<string>();
        foreach (var r in all) secrets.Add(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("payment").GetProperty("clientSecret").GetString()!);
        Assert.Single(secrets);                                        // everyone got the same intent
        Assert.NotNull((await Row(b.Id)).IntakeJson);
    }

    [Fact]
    public async Task Paying_confirms_at_the_portal_then_reports_the_sale_exactly_once_even_when_stripe_repeats_itself()
    {
        await Ready();
        var (b, intent) = await Paid(await NewHold());
        Assert.Equal(0, _portal.ConfirmCalls);                         // nothing is confirmed before the outbox runs
        await Drain();
        Assert.Equal(1, _portal.ConfirmCalls);
        var body = _portal.ConfirmBodies.Single();
        Assert.Contains("relay-", body); Assert.DoesNotContain("jane@example.com", body); Assert.Contains("\"pets\"", body);
        var row = await Row(b.Id);
        Assert.Equal(BookingStatus.Confirmed, row.Status);
        var captured = Assert.Single(_portal.PaymentEvents);
        using var doc = JsonDocument.Parse(captured);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("payment.captured", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("booking", data.GetProperty("kind").GetString());
        Assert.Equal(b.MarketplaceBookingId, data.GetProperty("marketplaceBookingId").GetString());
        Assert.Equal(intent, data.GetProperty("paymentRef").GetString());
        Assert.False(data.TryGetProperty("marketplaceOrderId", out _));
        // the same Stripe event again, and a NEW event id for the same intent: nothing happens twice
        var dup = StripeEvent("payment_intent.succeeded", intent, b.MarketplaceBookingId, 24000, id: "evt_same");
        await PostStripe(dup); await PostStripe(dup);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent, b.MarketplaceBookingId, 24000));
        await Drain();
        Assert.Equal(1, _portal.ConfirmCalls);
        Assert.Single(_portal.PaymentEvents);
        Assert.Equal(BookingPaymentStatus.Captured, (await Pay(b.Id)).Status);
        Assert.Equal(0, await _k.Db(db => db.BookingRefunds.CountAsync()));
    }

    [Fact]
    public async Task A_portal_outage_after_the_payment_is_retried_and_the_sale_is_reported_once_the_booking_is_confirmed()
    {
        await Ready();
        var (b, _) = await Paid(await NewHold());
        _portal.ConfirmDown = true;
        await Drain();
        Assert.Equal(BookingStatus.Hold, (await Row(b.Id)).Status);
        Assert.Empty(_portal.PaymentEvents);                           // no sale is reported for a booking the portal has not confirmed
        Assert.Empty(Stripe.Refunds);                                  // and an outage is not a reason to refund
        _portal.ConfirmDown = false;
        _k.F.Clock.Advance(TimeSpan.FromHours(1));
        await Drain();
        Assert.Equal(BookingStatus.Confirmed, (await Row(b.Id)).Status);
        Assert.Single(_portal.PaymentEvents);
    }

    [Fact]
    public async Task A_payment_after_the_hold_lapsed_and_the_slot_was_taken_is_refunded_in_full_never_captured_and_never_reported()
    {
        await Ready();
        var b = await NewHold();
        var res = await Checkout(b.Id);
        Assert.True(res.IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(12));                  // the buyer pays after the hold ran out
        _portal.ConfirmFailNext = (HttpStatusCode.Conflict, "slot_unavailable");
        Stripe.Capture(intent.Id);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, 24000));
        await Drain();
        var row = await Row(b.Id);
        Assert.Equal(BookingStatus.Expired, row.Status);
        var pay = await Pay(b.Id);
        Assert.Equal(BookingPaymentStatus.Void, pay.Status);
        Assert.Equal(24000, pay.RefundedCents);
        var refund = Assert.Single(Stripe.Refunds);
        Assert.Equal(24000, refund.AmountCents);
        Assert.Empty(_portal.PaymentEvents);                           // not a sale, so no payment.captured and no refund.processed either
        var mail = (await _k.Mail("jane@example.com")).Last();
        Assert.Contains("refund", mail.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_payment_for_a_booking_that_is_already_over_is_refunded_without_asking_the_portal()
    {
        await Ready();
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        var portalId = (await Row(b.Id)).PortalBookingId!.Value;
        var evt = Body("evt-exp-1", "booking.hold_expired", _k.F.Clock.GetUtcNow(), new { booking = new { bookingId = portalId, number = "BK-1", marketplaceBookingId = b.MarketplaceBookingId } });
        Assert.Equal("applied", StatusOf((await Post(_k.Http, Request(_k.F, evt))).Json));
        Stripe.Capture(intent.Id);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, 24000));
        await Drain();
        Assert.Equal(0, _portal.ConfirmCalls);
        Assert.Equal(BookingPaymentStatus.Void, (await Pay(b.Id)).Status);
        Assert.Equal(24000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(_portal.PaymentEvents);
        Assert.Equal(BookingStatus.Expired, (await Row(b.Id)).Status);
    }

    [Fact]
    public async Task An_amount_that_is_not_the_one_we_priced_is_refunded_and_the_booking_is_not_confirmed()
    {
        await Ready();
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, 100));       // Stripe says 1.00, we priced 240.00
        await Drain();
        var pay = await Pay(b.Id);
        Assert.Equal(BookingPaymentStatus.Void, pay.Status); Assert.Equal("amount_mismatch", pay.FailureCode);
        Assert.Equal(0, _portal.ConfirmCalls);
        Assert.Equal(100, pay.RefundedCents);                          // what was actually received is what goes back
        Assert.Equal(BookingStatus.Hold, (await Row(b.Id)).Status);
    }

    [Fact]
    public async Task Checkout_rules_ownership_expiry_validation_and_no_second_payment()
    {
        await Ready();
        var b = await NewHold();
        Assert.Equal(HttpStatusCode.NotFound, (await Checkout(b.Id, token: _joe)).StatusCode);                    // another buyer's booking
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.PostAsJsonAsync($"/api/v1/bookings/{b.Id}/checkout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Checkout(b.Id, new { intake = Enumerable.Range(0, 31).Select(i => new { id = "q" + i, answer = "x" }) })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Checkout(b.Id, new { phone = "<script>" })).StatusCode);
        Assert.Empty(Stripe.Intents);                                                                             // nothing was created for a refused request
        var (_, _) = await Paid(b);
        await Drain();
        var again = await Checkout(b.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_paid", JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var lapsed = await NewHold(hours: 1);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        var late = await Checkout(lapsed.Id);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("hold_expired", JsonDocument.Parse(await late.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refunds_can_never_exceed_what_was_captured_and_one_key_is_one_refund_even_in_parallel()
    {
        await Ready();
        var (b, _) = await Paid(await NewHold());
        await Drain();
        var pay = await Pay(b.Id);
        async Task<BookingReserveOutcome> Reserve(long amount, string key)
        {
            using var scope = _k.F.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BookingRefundService>().ReserveAsync(pay.Id, amount, key, "test", true, CancellationToken.None);
        }
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Reserve(10000, "same-key")));
        Assert.Equal(1, parallel.Count(o => o == BookingReserveOutcome.Reserved));
        Assert.Equal(5, parallel.Count(o => o is BookingReserveOutcome.Duplicate));   // a duplicate is "already done", never an error
        Assert.Equal(BookingReserveOutcome.Reserved, await Reserve(14000, "k2"));       // 10000 + 14000 = the whole 24000
        var racers = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Reserve(1, "over-" + i)));
        Assert.All(racers, o => Assert.Equal(BookingReserveOutcome.ExceedsCaptured, o));
        Assert.Equal(24000, (await Pay(b.Id)).RefundedCents);
        await Drain();
        Assert.Equal(24000, Stripe.Refunds.Sum(r => r.AmountCents));
        Assert.Equal(2, _portal.PaymentEvents.Count(e => e.Contains("refund.processed")));   // reported with the booking id
        Assert.All(_portal.PaymentEvents.Where(e => e.Contains("refund.processed")), e => Assert.Contains(b.MarketplaceBookingId, e));
    }
}
