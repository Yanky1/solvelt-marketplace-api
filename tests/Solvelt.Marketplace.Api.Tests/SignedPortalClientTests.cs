using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public class SignedPortalClientTests
{
    private const string Secret = "unit-test-inbound-secret-0123456789abcdef";
    private static readonly Guid Seller = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Brand = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        public FakePortal Portal { get; }
        public SignedPortalClient Client { get; }
        public Rig(Action<PortalOptions>? configure = null, string portalSecret = Secret, FakeTimeProvider? portalClock = null)
        {
            Portal = new FakePortal(portalSecret, portalClock ?? Clock);
            var o = new PortalOptions { BaseUrl = "https://portal.test", InboundSecret = Secret, RetryBaseDelayMs = 0, MaxAttempts = 4 };
            configure?.Invoke(o);
            Client = new SignedPortalClient(new HttpClient(Portal) { BaseAddress = new Uri("https://portal.test"), Timeout = Timeout.InfiniteTimeSpan },
                Options.Create(o), Clock);
        }
    }

    private static CreateOrderRequest Order(string mid = "MP-10045") => new(mid, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), "USD",
        new PortalBuyer("Jane B.", "relay-8f3a@relay.solvelt.test"),
        new PortalAddress("Jane Buyer", "1 Main St", null, "Brooklyn", "NY", "11201", "US"),
        "Standard", "standard", null,
        [new OrderLineRequest("L-1", Seller, Brand, Guid.Parse("33333333-3333-3333-3333-333333333333"), null, 2, 1999, 0, 320)],
        [new ShippingChargeRequest(Seller, Brand, 599)]);

    private static PaymentEventRequest Capture(string id = "evt-1") => new(id, PaymentEventTypes.Captured,
        new PaymentCapturedData(PaymentKinds.Order, "MP-10045", null, null, "pi_123", "USD"));

    // ---------- signing end to end ----------
    [Fact]
    public async Task Post_is_signed_and_verified_by_the_fake_portal_over_the_exact_body_bytes()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.Created, "{\"created\":true,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.True(res.Value!.Created);
        var call = Assert.Single(rig.Portal.Calls);
        Assert.True(call.Verified);
        Assert.Equal("/api/internal/v1/orders", call.PathAndQuery);
        Assert.Equal(rig.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(), call.Timestamp);
        Assert.Contains("\"marketplaceOrderId\":\"MP-10045\"", call.BodyText);
    }

    [Fact]
    public async Task Get_is_signed_with_an_empty_body_and_the_encoded_query_exactly_as_sent()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.OK, "{\"durationMinutes\":60,\"priceCents\":5000,\"providers\":[],\"slots\":[]}");
        var svc = Guid.Parse("5c1e0000-0000-0000-0000-000000000001");
        var res = await rig.Client.GetServiceAvailabilityAsync(new ServiceAvailabilityQuery(svc,
            new DateTimeOffset(2026, 12, 9, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero), "customer", "11201", AddOns: ["deep", "a b"]));
        Assert.True(res.IsSuccess);
        var call = Assert.Single(rig.Portal.Calls);
        Assert.True(call.Verified);
        Assert.Empty(call.Body);
        Assert.Equal($"/api/internal/v1/services/{svc}/availability?from=2026-12-09T00%3A00%3A00Z&to=2026-12-10T00%3A00%3A00Z&location=customer&postalCode=11201&addOns=deep,a%20b", call.PathAndQuery);
    }

    [Fact]
    public async Task Every_attempt_has_a_fresh_nonce_and_the_same_body_bytes_and_all_are_accepted()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{\"code\":\"version_conflict\"}", r => r.Headers.RetryAfter = new(TimeSpan.Zero))
                  .Respond(HttpStatusCode.ServiceUnavailable, "{}")
                  .Respond(HttpStatusCode.Created, "{\"created\":true,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.Equal(3, res.Attempts);
        var calls = rig.Portal.Calls;
        Assert.All(calls, c => Assert.True(c.Verified));   // a reused nonce would be 401 replayed
        Assert.Equal(3, calls.Select(c => c.Nonce).Distinct().Count());
        Assert.Single(calls.Select(c => c.BodyText).Distinct());
    }

    [Fact]
    public async Task Retry_after_is_honoured()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(7)))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var task = rig.Client.CreateOrderAsync(Order());
        while (!task.IsCompleted) { rig.Clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
        Assert.True((await task).IsSuccess);
        var ts = rig.Portal.Calls.Select(c => long.Parse(c.Timestamp)).ToList();
        Assert.True(ts[1] - ts[0] >= 7, $"second attempt came after {ts[1] - ts[0]}s");
    }

    // ---------- retry policy ----------
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.Conflict, "concurrent_update")]
    [InlineData(HttpStatusCode.Conflict, "version_conflict")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    public async Task Retryable_answers_are_retried_for_ordinary_calls(HttpStatusCode status, string code)
    {
        var rig = new Rig();
        rig.Portal.Respond(status, $"{{\"code\":\"{code}\"}}").Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "bad_signature")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "too_large")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_order")]
    [InlineData(HttpStatusCode.BadRequest, "bad_request")]
    [InlineData(HttpStatusCode.Conflict, "something_else")]
    [InlineData(HttpStatusCode.NotFound, "order_not_found")]   // retryable only for payment events
    public async Task Other_answers_are_never_retried_for_ordinary_calls(HttpStatusCode status, string code)
    {
        var rig = new Rig();
        rig.Portal.Respond(status, $"{{\"code\":\"{code}\",\"message\":\"m\"}}").Respond(HttpStatusCode.OK, "{}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal((int)status, res.Status);
        Assert.Equal(1, res.Attempts);
        Assert.Single(rig.Portal.Calls);
        Assert.Equal(code, res.Error!.Code);
        Assert.False(res.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "order_not_found")]
    [InlineData(HttpStatusCode.NotFound, "booking_not_found")]
    [InlineData(HttpStatusCode.Conflict, "sale_not_recorded")]
    [InlineData(HttpStatusCode.Conflict, "concurrent_update")]
    public async Task Payment_events_retry_the_not_yet_arrived_answers(HttpStatusCode status, string code)
    {
        var rig = new Rig();
        rig.Portal.Respond(status, $"{{\"code\":\"{code}\"}}").Respond(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        var res = await rig.Client.SendPaymentEventAsync(Capture());
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Attempts);
        Assert.Equal("ok", res.Value!.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "exceeds_sale")]
    [InlineData(HttpStatusCode.Unauthorized, "stale")]
    [InlineData(HttpStatusCode.NotFound, "something_else")]
    public async Task Payment_events_do_not_retry_422_401_or_other_404(HttpStatusCode status, string code)
    {
        var rig = new Rig();
        rig.Portal.Respond(status, $"{{\"code\":\"{code}\"}}");
        var res = await rig.Client.SendPaymentEventAsync(Capture());
        Assert.Equal(1, res.Attempts);
        Assert.Equal(code, res.Error!.Code);
    }

    [Fact]
    public async Task Attempts_are_capped_and_the_last_answer_is_returned_flagged_retryable()
    {
        var rig = new Rig(o => o.MaxAttempts = 3);
        for (var i = 0; i < 5; i++) rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{\"code\":\"busy\"}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal(503, res.Status);
        Assert.Equal(3, res.Attempts);
        Assert.True(res.Retryable);
        Assert.Equal(3, rig.Portal.Calls.Count);
    }

    [Fact]
    public async Task Network_errors_are_retried_then_succeed()
    {
        var rig = new Rig();
        rig.Portal.RespondTransportError().Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Attempts);
    }

    [Fact]
    public async Task Network_errors_that_never_clear_throw_portal_unavailable()
    {
        var rig = new Rig(o => o.MaxAttempts = 2);
        rig.Portal.RespondTransportError().RespondTransportError();
        await Assert.ThrowsAsync<PortalUnavailableException>(() => rig.Client.CreateOrderAsync(Order()));
    }

    [Fact]
    public async Task A_hung_portal_hits_the_per_attempt_deadline_and_is_retried()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 1; o.MaxAttempts = 2; });
        rig.Portal.Respond(async_ => { Thread.Sleep(10); throw new TaskCanceledException("deadline"); })
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal(2, res.Attempts);
    }

    
    // ---------- the fake portal enforces the portal's rules (so every test exercises real verification) ----------
    [Fact]
    public async Task Client_clock_skew_beyond_300s_is_a_401_stale_and_is_not_retried()
    {
        var portalClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 10, 0, TimeSpan.Zero));   // portal is 10 min ahead of us
        var rig = new Rig(portalClock: portalClock);
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal(401, res.Status);
        Assert.Equal("stale", res.Error!.Code);
        Assert.Equal(1, res.Attempts);
    }

    [Fact]
    public async Task Wrong_secret_is_401_bad_signature_and_not_retried()
    {
        var rig = new Rig(portalSecret: "a-different-secret-entirely-0123456789ab");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal("bad_signature", res.Error!.Code);
        Assert.Equal(1, res.Attempts);
    }

    [Fact]
    public async Task Fake_portal_rejects_a_replayed_request_and_a_tampered_body()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var portal = new FakePortal(Secret, clock);
        var http = new HttpClient(portal) { BaseAddress = new Uri("https://portal.test") };
        var body = "{\"a\":1}"u8.ToArray();
        var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        const string path = "/api/internal/v1/orders";
        var sig = PortalSigning.Sign(Secret, PortalSigning.Canonical(ts, "n-1", "POST", path, body));

        HttpRequestMessage Req(byte[] b, string nonce, string signature)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(b) };
            r.Headers.Add("X-Solvelt-Timestamp", ts); r.Headers.Add("X-Solvelt-Nonce", nonce); r.Headers.Add("X-Solvelt-Signature", signature);
            return r;
        }

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(body, "n-1", sig))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(body, "n-1", sig))).StatusCode);   // replay
        var tampered = await http.SendAsync(Req("{\"a\":2}"u8.ToArray(), "n-2", sig));
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);                                          // body hash mismatch
        Assert.Contains("bad_signature", await tampered.Content.ReadAsStringAsync());
        var oversized = new byte[FakePortal.MaxBodyBytes + 1];
        var big = await http.SendAsync(Req(oversized, "n-3", PortalSigning.Sign(Secret, PortalSigning.Canonical(ts, "n-3", "POST", path, oversized))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }

    [Fact]
    public async Task Bodies_over_256KB_are_refused_before_anything_is_sent()
    {
        var rig = new Rig();
        var huge = Order() with { GiftMessage = new string('x', PortalSigning.MaxBodyBytes) };
        await Assert.ThrowsAsync<PortalRequestTooLargeException>(() => rig.Client.CreateOrderAsync(huge));
        Assert.Empty(rig.Portal.Calls);
    }

    [Fact]
    public async Task Unsafe_identifiers_cannot_reach_a_signed_path()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.RequestCancellationAsync("../orders", new CancelRequestBody("r1", "other")));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.ConfirmDeliveryAsync("MP-1?x=1", new DeliveryConfirmationBody()));
        Assert.Empty(rig.Portal.Calls);
    }

    [Fact]
    public async Task Not_configured_secret_fails_loudly_instead_of_sending_unsigned()
    {
        var rig = new Rig(o => o.InboundSecret = null);
        await Assert.ThrowsAsync<PortalNotConfiguredException>(() => rig.Client.GetShippingSettingsAsync(Seller, Brand));
        Assert.Empty(rig.Portal.Calls);
    }

    // ---------- contract tests per typed method (request shape + response parse) ----------
    [Fact]
    public async Task CreateOrder_sends_ids_not_skus_and_parses_the_201()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.Created, $$"""
            {"created":true,"orders":[{"sellerOrderId":"{{Guid.Empty}}","number":"SO-2026-000123","sellerId":"{{Seller}}","brandId":"{{Brand}}","status":"New","oversold":false,"totalCents":4917,"feeCents":400,"netCents":4517}]}
            """);
        var res = await rig.Client.CreateOrderAsync(Order());
        var o = Assert.Single(res.Value!.Orders);
        Assert.Equal("SO-2026-000123", o.Number);
        Assert.Equal(4517, o.NetCents);
        using var json = JsonDocument.Parse(rig.Portal.Calls[0].Body);
        var line = json.RootElement.GetProperty("lines")[0];
        Assert.False(line.TryGetProperty("sku", out _));
        Assert.Equal(1999, line.GetProperty("unitPriceCents").GetInt64());
        Assert.Equal("relay-8f3a@relay.solvelt.test", json.RootElement.GetProperty("buyer").GetProperty("relayEmail").GetString());
        Assert.Equal(599, json.RootElement.GetProperty("shipping")[0].GetProperty("amountCents").GetInt64());
    }

    [Fact]
    public async Task CreateOrder_422_invalid_order_exposes_the_line_errors()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.UnprocessableEntity, "{\"code\":\"invalid_order\",\"message\":\"bad\",\"errors\":[{\"index\":0,\"code\":\"product_inactive\",\"message\":\"gone\"}]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.False(res.IsSuccess);
        Assert.Equal("invalid_order", res.Error!.Code);
        var e = Assert.Single(res.Error.Errors!);
        Assert.Equal((0, "product_inactive"), (e.Index, e.Code));
    }

    [Fact]
    public async Task CreateOrder_200_created_false_is_success()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.False(res.Value!.Created);
    }

    [Fact]
    public async Task Buyer_events_use_the_documented_paths_and_bodies()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.OK, "{\"results\":[]}").Respond(HttpStatusCode.OK, "{}").Respond(HttpStatusCode.OK, "{}");
        await rig.Client.RequestCancellationAsync("MP-10045", new CancelRequestBody("req-1", "changed_mind", null, "SO-2026-000123"));
        await rig.Client.RequestReturnAsync("MP-10045", new ReturnRequestBody("ret-1", "damaged", null, null, [new ReturnLineRequest("L-1", 1)]));
        await rig.Client.ConfirmDeliveryAsync("MP-10045", new DeliveryConfirmationBody("SO-2026-000123"));
        var c = rig.Portal.Calls;
        Assert.Equal("/api/internal/v1/orders/MP-10045/cancel-requests", c[0].PathAndQuery);
        Assert.Contains("\"requestId\":\"req-1\"", c[0].BodyText);
        Assert.Equal("/api/internal/v1/orders/MP-10045/return-requests", c[1].PathAndQuery);
        Assert.Contains("\"returnId\":\"ret-1\"", c[1].BodyText);
        Assert.Equal("/api/internal/v1/orders/MP-10045/delivery-confirmations", c[2].PathAndQuery);
    }

    [Fact]
    public async Task Delivery_confirmation_never_carries_a_client_supplied_time()
    {
        var rig = new Rig();
        await rig.Client.ConfirmDeliveryAsync("MP-10045", new DeliveryConfirmationBody("SO-2026-000123", "1Z999"));
        var body = rig.Portal.Calls[0].BodyText;
        Assert.DoesNotContain("deliveredAt", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(typeof(DeliveryConfirmationBody).GetProperties().Select(p => p.Name).Order(), new[] { "SellerOrderNumber", "TrackingNumber" });
    }

    [Fact]
    public async Task ShippingSettings_get_parses_the_documented_shape()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.OK, $$"""
            {"sellerId":"{{Seller}}","brandId":"{{Brand}}","handlingDays":3,"shipsFrom":{"country":"US","region":"NY"},
             "profiles":[{"id":"{{Guid.Empty}}","name":"Default","isDefault":true,"currency":"USD","freeShippingThresholdCents":7500,
               "zones":[{"name":"Domestic","kind":"domestic","countries":["US"],
                 "speeds":[{"speed":"standard","enabled":true,"method":"flat","flatCents":500},
                           {"speed":"overnight","enabled":true,"method":"weight","baseCents":1500,"perKgCents":300}]}]}]}
            """);
        var res = await rig.Client.GetShippingSettingsAsync(Seller, Brand);
        Assert.Equal($"/api/internal/v1/shipping/settings?sellerId={Seller}&brandId={Brand}", rig.Portal.Calls[0].PathAndQuery);
        var speeds = res.Value!.Profiles[0].Zones[0].Speeds;
        Assert.Equal(500, speeds[0].FlatCents);
        Assert.Equal((1500, 300), (speeds[1].BaseCents, speeds[1].PerKgCents));
        Assert.Equal(7500, res.Value.Profiles[0].FreeShippingThresholdCents);
    }

    [Fact]
    public async Task LocalDeliveryEligibility_posts_and_parses_windows()
    {
        var rig = new Rig();
        var win = Guid.Parse("44444444-4444-4444-4444-444444444444");
        rig.Portal.Respond(HttpStatusCode.OK, $$"""
            {"eligible":true,"reason":null,"method":"local","feeCents":500,"zone":{"id":"{{Guid.Empty}}","name":"Brooklyn"},"timeZone":"America/New_York",
             "windows":[{"windowId":"{{win}}","date":"2026-11-04","start":"2026-11-04T17:00:00Z","end":"2026-11-04T19:00:00Z","remaining":2}]}
            """);
        var res = await rig.Client.CheckLocalDeliveryEligibilityAsync(new(Seller, Brand, 2000, new EligibilityAddress("11201", "US", 40.6928, -73.9903)));
        Assert.Equal("/api/internal/v1/local-delivery/eligibility", rig.Portal.Calls[0].PathAndQuery);
        Assert.Contains("\"subtotalCents\":2000", rig.Portal.Calls[0].BodyText);
        Assert.True(res.Value!.Eligible);
        Assert.Equal(win, res.Value.Windows![0].WindowId);
        Assert.Equal(2, res.Value.Windows[0].Remaining);
    }

    [Fact]
    public async Task ServiceAvailability_parses_slots_and_limits_the_window_to_62_days()
    {
        var rig = new Rig();
        var p = Guid.Parse("55555555-5555-5555-5555-555555555555");
        rig.Portal.Respond(HttpStatusCode.OK, $$"""
            {"durationMinutes":120,"priceCents":9000,"providers":[{"providerId":"{{p}}","timeZone":"America/New_York"}],
             "slots":[{"startAt":"2026-12-09T15:00:00Z","endAt":"2026-12-09T17:00:00Z","providerIds":["{{p}}"]}]}
            """);
        var from = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var res = await rig.Client.GetServiceAvailabilityAsync(new(Guid.NewGuid(), from, from.AddDays(62), "online"));
        Assert.Equal(120, res.Value!.DurationMinutes);
        Assert.Equal(p, res.Value.Slots[0].ProviderIds[0]);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.GetServiceAvailabilityAsync(new(Guid.NewGuid(), from, from.AddDays(63), "online")));
    }

    [Fact]
    public async Task Payment_events_carry_our_event_id_and_the_documented_data_for_each_type()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.OK, "{\"status\":\"duplicate\",\"result\":{}}");
        var dup = await rig.Client.SendPaymentEventAsync(Capture("evt-9f2"));
        Assert.Equal("duplicate", dup.Value!.Status);

        await rig.Client.SendPaymentEventAsync(new("evt-2", PaymentEventTypes.RefundProcessed, new RefundProcessedData("re_1", "MP-10045", "SO-2026-000123", null, 1999, 599, "USD", "cancelled")));
        await rig.Client.SendPaymentEventAsync(new("evt-3", PaymentEventTypes.DisputeCreated, new DisputeData("dp_1", "MP-10045", null, null, 4917, "needs_response", 1500)));
        var calls = rig.Portal.Calls;
        Assert.All(calls, c => Assert.Equal("/api/internal/v1/payment-events", c.PathAndQuery));
        using var d0 = JsonDocument.Parse(calls[0].Body);
        Assert.Equal("evt-9f2", d0.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("payment.captured", d0.RootElement.GetProperty("type").GetString());
        Assert.Equal("pi_123", d0.RootElement.GetProperty("data").GetProperty("paymentRef").GetString());
        using var d1 = JsonDocument.Parse(calls[1].Body);
        Assert.Equal("re_1", d1.RootElement.GetProperty("data").GetProperty("refundId").GetString());
        Assert.Equal(599, d1.RootElement.GetProperty("data").GetProperty("shippingCents").GetInt64());
        using var d2 = JsonDocument.Parse(calls[2].Body);
        Assert.Equal("dp_1", d2.RootElement.GetProperty("data").GetProperty("disputeId").GetString());
    }

    [Fact]
    public async Task Payment_event_ids_over_100_chars_are_refused_locally()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.SendPaymentEventAsync(Capture(new string('e', 101))));
        Assert.Empty(rig.Portal.Calls);
    }

    // ---------- DI wiring through the real app ----------
    [Fact]
    public async Task The_registered_client_in_the_real_app_signs_with_the_configured_secret_through_the_fake_portal()
    {
        using var f = new TestFactory();
        f.Portal.Respond(HttpStatusCode.OK, "{\"sellerId\":\"" + Seller + "\",\"brandId\":\"" + Brand + "\",\"handlingDays\":2,\"profiles\":[]}");
        using var scope = f.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ISignedPortalClient>();
        var res = await client.GetShippingSettingsAsync(Seller, Brand);
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Value!.HandlingDays);
        Assert.True(Assert.Single(f.Portal.Calls).Verified);
    }
}
