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
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>What a booking flow test needs: the app, a stateful fake portal, fake Stripe, two buyers, holds, checkout, signed Stripe and portal events, and the outbox.</summary>
public abstract class BookingFlowBase : IDisposable
{
    protected readonly AuthKit _k = new();
    protected readonly FakeBookingPortal _portal;
    protected static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    protected string _jane = "", _joe = "";
    protected int _n, _ev;

    protected BookingFlowBase() => _portal = new FakeBookingPortal(_k.F.Clock);
    public void Dispose() => _k.Dispose();
    protected FakePaymentProvider Stripe => _k.F.Services.GetRequiredService<FakePaymentProvider>();

    protected async Task Ready()
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{}"));
        _jane = await _k.VerifiedSession("jane@example.com");
        _joe = await _k.VerifiedSession("joe@example.com", name: "Joe Green");
    }

    protected HttpRequestMessage Req(HttpMethod m, string url, string token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key != null) r.Headers.Add("Idempotency-Key", key);
        return r;
    }

    protected Task<HttpResponseMessage> Send(HttpMethod m, string url, string? token, object? body = null, string? key = null)
        => _k.Http.SendAsync(token is null ? new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) } : Req(m, url, token, body, key));

    protected DateTimeOffset Slot(int hours = 0, int days = 7) => new(_k.F.Clock.GetUtcNow().Date.AddDays(days).AddHours(14 + hours), TimeSpan.Zero);

    protected async Task<Booking> NewHold(string? token = null, int hours = 0, string location = "online")
    {
        var key = "flow-key-" + (++_n);
        var res = await _k.Http.SendAsync(Req(HttpMethod.Post, "/api/v1/bookings/holds", token ?? _jane, new { listingId = Service, start = Slot(hours), location, durationMinutes = 240 }, key));
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return await _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.IdempotencyKey == key));
    }

    protected Task<HttpResponseMessage> Checkout(Guid id, string? token = null)
        => Send(HttpMethod.Post, $"/api/v1/bookings/{id}/checkout", token ?? _jane, new { intake = new[] { new { id = "pets", answer = "no" } }, phone = "+1 555 000 1111" });

    protected string StripeEvent(string type, string intentId, string mid, long amount, string currency = "usd", string? id = null)
        => JsonSerializer.Serialize(new { id = id ?? $"evt_b{++_ev:D4}", type, created = _k.F.Clock.GetUtcNow().ToUnixTimeSeconds(),
            data = new { @object = new { id = intentId, amount, amount_received = amount, currency, metadata = new { marketplace_booking_id = mid, kind = "booking" } } } });

    protected async Task<HttpResponseMessage> PostStripe(string body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhooks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("Stripe-Signature", StripeSignature.Sign(body, DevDefaults.StripeWebhookSecret, _k.F.Clock.GetUtcNow()));
        return await _k.Http.SendAsync(r);
    }

    protected async Task Drain() { while (await _k.Dispatch() > 0) { } }
    protected Task<Booking> Row(Guid id) => _k.Db(db => db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id));
    protected Task<BookingPayment> Pay(Guid bookingId) => _k.Db(db => db.BookingPayments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId && p.Kind == "booking"));
    protected FakePaymentProvider.Intent IntentOf(string mid) => Stripe.Intents.Single(i => i.Metadata["marketplace_booking_id"] == mid && i.Metadata["kind"] == "booking");

    /// <summary>Hold, checkout, Stripe success, outbox: the booking ends Confirmed (or Requested when the portal is not instant).</summary>
    protected async Task<Booking> PaidBooking(string? token = null, int hours = 0)
    {
        var b = await NewHold(token, hours);
        var res = await Checkout(b.Id, token);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        Assert.True((await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, intent.AmountCents))).IsSuccessStatusCode);
        await Drain();
        return await Row(b.Id);
    }

    protected object Ref(Booking b) => new { bookingId = b.PortalBookingId, number = "BK-2026-0001", marketplaceBookingId = b.MarketplaceBookingId };

    protected async Task<(HttpStatusCode Status, string Json)> PortalEvent(string type, DateTimeOffset at, Booking b, object? extra = null, string? id = null)
    {
        var data = new Dictionary<string, object?> { ["booking"] = Ref(b) };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return await Post(_k.Http, Request(_k.F, Body(id ?? $"evt-{type}-{++_n}", type, at, data)));
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    protected static async Task<string> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("code").GetString()!;
    protected IEnumerable<string> RefundEvents() => _portal.PaymentEvents.Where(e => e.Contains("refund.processed"));
}
