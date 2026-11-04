using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Independent review of the one-pending-order work (F1-F5, decision M3-25): lost cancels, in-flight payments, orphan intents, purge safety.</summary>
public sealed class PendingHardeningTests : OrderFlowBase
{
    private async Task<int> Expire() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<OrderMaintenance>().ExpirePendingAsync(); }
    private async Task<int> Purge() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<OrderMaintenance>().PurgeAbandonedAsync(); }
    private Task<string?> CancelRowStatus(string intent) => _k.Db(db => db.OutboxMessages.Where(m => m.DedupeKey == "cancel:" + intent).Select(m => m.Status).SingleOrDefaultAsync());

    private async Task<HttpResponseMessage> RawCheckout(string gift)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout") { Content = await QuoteHashes.WithHashAsync(_k.Http, _session, new { address = Address(), giftMessage = gift }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        return await _k.Http.SendAsync(r);
    }

    // ---- F1: a cancel is only "done" when the intent is verifiably closed ----

    [Fact]
    public async Task A_cancel_refused_because_the_intent_is_in_flight_is_retried_not_delivered_and_is_delivered_once_the_intent_is_paid()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        Stripe.SetProcessing(intent);                                    // a delayed payment started after our last look
        Assert.Equal(1, await Expire());
        await Drain();
        Assert.Equal("pending", await CancelRowStatus(intent));          // refused by Stripe (unexpected state), intent still processing: NOT done
        Assert.False(Stripe.Intents.Single().Cancelled);
        Stripe.Capture(intent);                                          // the money arrived
        _k.F.Clock.Advance(TimeSpan.FromMinutes(10));
        await Drain();
        Assert.Equal("delivered", await CancelRowStatus(intent));        // verifiably succeeded: the webhook refunds it, nothing left to cancel
        _ = (number, total);
    }

    [Fact]
    public async Task A_cancel_refused_for_any_other_reason_is_dead_not_delivered_and_the_order_is_never_purged_while_its_cancel_is_not_delivered()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, _) = await Checkout();
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        Stripe.RejectCancelWith = "api_key_expired";                     // a rotated key: Stripe would never accept this cancel as given
        Assert.Equal(1, await Expire());
        await Drain();
        Assert.Equal("dead", await CancelRowStatus(intent));
        _k.F.Clock.Advance(TimeSpan.FromDays(100));
        Assert.Equal(0, await Purge());                                  // kept: a person must look at the dead cancel
        Assert.Equal("expired", (await Order(number)).Status);
    }

    [Fact]
    public async Task A_payment_on_an_intent_we_created_whose_order_is_gone_is_refunded_by_intent_id_not_kept()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(1, await Expire());
        await Drain();                                                   // cancel delivered
        _k.F.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, await Purge());
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync(o => o.Number == number)));
        Stripe.Capture(intent);                                          // a late payment still lands
        var body = Event("payment_intent.succeeded", intent, number, total, id: "evt_late");
        _k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(body)).StatusCode);
        await Drain();
        Assert.Equal(total, Assert.Single(Stripe.Refunds).AmountCents);
        await PostStripe(Event("payment_intent.succeeded", intent, number, total, id: "evt_late2"));   // a second event for the same payment refunds nothing more
        await Drain();
        Assert.Single(Stripe.Refunds);
    }

    // ---- F2: expiry closes first, reads the intent afterwards, queues the cancel atomically ----

    [Fact]
    public async Task An_intent_stored_after_the_expiry_listed_the_order_is_still_cancelled()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        Stripe.FailNext = 1;                                             // the first attempt cannot create its intent: pending, no intent
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RawCheckout("a")).StatusCode);
        var number = await _k.Db(db => db.Orders.Select(o => o.Number).SingleAsync());
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        // the buyer's retry stores an intent; with the old code expiry had already read "no intent" and would close the order without a cancel
        var retry = await RawCheckout("a");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var intent = (await Order(number)).PaymentIntentId!;
        Assert.Equal(1, await Expire());
        await Drain();
        Assert.True(Stripe.Intents.Single(i => i.Id == intent).Cancelled);
    }

    // ---- F3: money in flight is never closed under the buyer ----

    [Fact]
    public async Task A_different_checkout_of_a_cart_whose_payment_is_processing_is_refused_and_nothing_is_cancelled()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var first = await Checkout(new { address = Address(), giftMessage = "one" });
        Stripe.SetProcessing(first.Intent);                              // Stripe says processing (we have not heard the webhook yet)
        var res = await RawCheckout("two");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("checkout_in_progress", (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Equal("pending_payment", (await Order(first.Number)).Status);
        await Drain();
        Assert.False(Stripe.Intents.Single().Cancelled);
    }

    [Fact]
    public async Task A_processing_event_protects_the_order_from_expiry_and_new_checkouts_and_the_success_or_failure_moves_it_on()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Stripe.SetProcessing(intent);
        Assert.Equal(HttpStatusCode.OK, (await PostStripe(Event("payment_intent.processing", intent, number, total))).StatusCode);
        Assert.Equal("payment_processing", (await Order(number)).Status);
        _k.F.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, await Expire());                                 // not expired: the money may still arrive
        Assert.Equal(HttpStatusCode.Conflict, (await RawCheckout("other")).StatusCode);
        await Drain();
        Assert.False(Stripe.Intents.Single().Cancelled);
        // it fails: payable again
        await PostStripe(Event("payment_intent.payment_failed", intent, number, total, extra: new { last_payment_error = new { code = "payment_intent_authentication_failure" } }));
        Assert.Equal("pending_payment", (await Order(number)).Status);
        // ... and a later processing + success pays it
        await PostStripe(Event("payment_intent.processing", intent, number, total));
        await Pay(number, intent, total);
        Assert.Equal("paid", (await Order(number)).Status);
    }

    [Fact]
    public async Task Intents_are_created_for_cards_only()
    {
        // the form body sent to Stripe: no automatic_payment_methods (it would enable ACH/SEPA, which stay "processing" for days)
        var h = new CapturingHandler();
        var provider = new StripeHttpProvider(new HttpClient(h), Microsoft.Extensions.Options.Options.Create(new StripeOptions { SecretKey = "sk_test_abc", BaseUrl = "https://stripe.test/" }));
        await provider.CreateIntentAsync(1000, "usd", "pi:MP-1", new Dictionary<string, string>(), default);
        Assert.Contains("payment_method_types%5B%5D=card", h.Body);
        Assert.DoesNotContain("automatic_payment_methods", h.Body);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"pi_1\",\"client_secret\":\"s\",\"status\":\"requires_payment_method\"}") };
        }
    }

    // ---- F4: an intent whose order never stored it is cancelled too ----

    [Fact]
    public async Task An_intent_created_but_never_stored_is_cancelled_when_its_order_is_closed()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        _k.F.Services.GetRequiredService<CheckoutHooks>().AfterIntentCreated = () => throw new InvalidOperationException("crash between create and store");
        try { await RawCheckout("x"); } catch { /* the request died: that is the point */ }
        _k.F.Services.GetRequiredService<CheckoutHooks>().AfterIntentCreated = null;
        var order = await _k.Db(db => db.Orders.AsNoTracking().SingleAsync());
        Assert.Null(order.PaymentIntentId);
        Assert.Single(Stripe.Intents);                                   // created at Stripe, unknown to us
        Assert.False(Stripe.Intents.Single().Cancelled);
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(1, await Expire());
        await Drain();
        Assert.True(Stripe.Intents.Single().Cancelled);                  // found again by its idempotency key and cancelled
        Assert.Single(Stripe.Intents);                                   // no second intent was left behind
    }

    // ---- F5: the order of supersede's steps, caught deterministically ----

    [Fact]
    public async Task An_intent_stored_between_supersedes_listing_and_its_status_change_is_still_cancelled()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        Stripe.FailNext = 1;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RawCheckout("one")).StatusCode);   // pending, no intent
        var first = await _k.Db(db => db.Orders.AsNoTracking().SingleAsync());
        // the other request's intent is stored right after supersede has listed the order (it saw "no intent") and before it changes the status
        _k.F.Services.GetRequiredService<CheckoutHooks>().BeforeSupersedeUpdate = async db =>
        {
            _k.F.Services.GetRequiredService<CheckoutHooks>().BeforeSupersedeUpdate = null;
            await db.Orders.Where(o => o.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentIntentId, "pi_stored_late"));
        };
        Assert.Equal(HttpStatusCode.OK, (await RawCheckout("two")).StatusCode);
        Assert.Equal("superseded", (await Order(first.Number)).Status);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == "cancel:pi_stored_late")));   // read AFTER the status change, so it is seen and cancelled
    }
}
