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
    [InlineData(HttpStatusCode.NotFound, "booking_not_found")]
    [InlineData(HttpStatusCode.Conflict, "sale_not_recorded")] // retryable only for payment events
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

    // ---------- deadlines (review 2): run on the injected clock, against a portal that never answers ----------
    private static async Task WaitForCalls(FakePortal portal, int n)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (portal.Calls.Count < n)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Expected {n} call(s), saw {portal.Calls.Count}.");
            await Task.Delay(2);
        }
    }

    private static async Task Advance(Rig rig, int seconds)
    {
        await Task.Delay(20);   // let the client reach its await before the timer fires
        rig.Clock.Advance(TimeSpan.FromSeconds(seconds));
    }

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public void Defaults_are_30s_per_attempt_30s_in_total_and_5_attempts()
    {
        var o = new PortalOptions();
        Assert.Equal(30, o.AttemptTimeoutSeconds);
        Assert.Equal(30, o.TotalDeadlineSeconds);
        Assert.Equal(5, o.MaxAttempts);
    }

    [Fact]
    public async Task A_portal_that_never_answers_is_cut_off_at_the_total_deadline_on_the_injected_clock()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 30; o.TotalDeadlineSeconds = 30; o.MaxAttempts = 5; });   // the defaults, spelled out
        rig.Portal.RespondHang(5);
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 29);
        await Task.Delay(30);
        Assert.False(task.IsCompleted, "the call ended before the deadline");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.Single(rig.Portal.Calls);   // 30 s were spent, nothing left for a second attempt
    }

    [Fact]
    public async Task A_configured_attempt_timeout_above_30s_is_clamped_to_30s()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 120; o.TotalDeadlineSeconds = 60; o.MaxAttempts = 5; });
        rig.Portal.RespondHang(5);
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 29);
        await Task.Delay(30);
        Assert.Single(rig.Portal.Calls);   // still waiting on attempt 1
        rig.Clock.Advance(TimeSpan.FromSeconds(1));   // 30 s: attempt 1 is cancelled (120 would have run on) and attempt 2 starts
        await WaitForCalls(rig.Portal, 2);
        await Advance(rig, 30);
        await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.Equal(2, rig.Portal.Calls.Count);
    }

    [Fact]
    public async Task A_configured_total_deadline_above_60s_is_clamped_to_60s()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 30; o.TotalDeadlineSeconds = 3600; o.MaxAttempts = 10; });
        rig.Portal.RespondHang(10);
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 30);
        await WaitForCalls(rig.Portal, 2);
        await Advance(rig, 30);
        await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.Equal(2, rig.Portal.Calls.Count);   // 60 s in total, not 10 attempts
    }

    [Fact]
    public async Task The_last_attempt_is_bounded_by_what_is_left_of_the_total_deadline()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 30; o.TotalDeadlineSeconds = 45; o.MaxAttempts = 5; });
        rig.Portal.RespondHang(5);
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 30);                   // attempt 1 times out at 30 s; 15 s of budget remain
        await WaitForCalls(rig.Portal, 2);
        await Advance(rig, 15);                   // attempt 2 must end at 45 s, not run on to 60 s
        await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.Equal(2, rig.Portal.Calls.Count);
    }

    [Fact]
    public async Task Retrying_stops_when_the_next_backoff_would_exceed_the_total_deadline()
    {
        var rig = new Rig(o => { o.TotalDeadlineSeconds = 15; o.MaxAttempts = 5; });
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)))
                  .Respond(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var task = rig.Client.CreateOrderAsync(Order());
        while (!task.IsCompleted) { rig.Clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
        var res = await task;
        Assert.Equal(503, res.Status);
        Assert.Equal(2, res.Attempts);      // 0 s + 10 s wait fits in 15 s; a second 10 s wait (20 s) would not
        Assert.True(res.Retryable);
    }

    [Fact]
    public async Task A_503_without_retry_after_is_retried_within_the_deadline()
    {
        var rig = new Rig(o => { o.RetryBaseDelayMs = 0; });
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{}").Respond(HttpStatusCode.ServiceUnavailable, "{}").Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.True(res.IsSuccess);
        Assert.Equal(3, res.Attempts);
    }

    [Fact]
    public async Task A_429_is_surfaced_for_the_outbox_and_never_retried_by_the_client()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.TooManyRequests, "{\"code\":\"rate_limited\"}").Respond(HttpStatusCode.OK, "{}");
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal(429, res.Status);
        Assert.Equal(1, res.Attempts);
        Assert.True(res.Retryable);
        Assert.Single(rig.Portal.Calls);
    }

    [Fact]
    public async Task Retry_after_may_be_an_http_date()
    {
        var rig = new Rig();
        // The date is the first request's own signed timestamp plus 7 s, so it is exact (an HTTP date has whole seconds) and does not depend on when the clock is read.
        rig.Portal.Respond(c => FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(DateTimeOffset.FromUnixTimeSeconds(long.Parse(c.Timestamp)).AddSeconds(7))))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var task = rig.Client.CreateOrderAsync(Order());
        // The fake clock moves one second at a time and never past the date. A clock that kept running would make the retry's timestamp depend on how quickly the
        // continuation is scheduled (this test used to fail under load for that reason), so the retry now lands exactly on the date, or the test fails.
        DateTimeOffset? date = null;
        while (!task.IsCompleted)
        {
            if (rig.Portal.Calls.Count > 0)
            {
                date ??= DateTimeOffset.FromUnixTimeSeconds(long.Parse(rig.Portal.Calls[0].Timestamp)).AddSeconds(7);
                if (rig.Clock.GetUtcNow() < date) rig.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            await Task.Delay(5);
        }
        Assert.True((await task).IsSuccess);
        var ts = rig.Portal.Calls.Select(c => long.Parse(c.Timestamp)).ToList();
        Assert.Equal(2, ts.Count);
        Assert.Equal(7, ts[1] - ts[0]);          // waited for the date: not sooner (it would be 0) and not later (the clock is held at the date)
    }

    [Fact]
    public async Task A_retry_after_date_in_the_past_means_retry_now()
    {
        var rig = new Rig();
        rig.Portal.Respond(_ => FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(rig.Clock.GetUtcNow().AddSeconds(-100))))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await Within(rig.Client.CreateOrderAsync(Order()));   // would hang on an unadvanced clock if it waited
        Assert.Equal(2, res.Attempts);
    }

    [Fact]
    public async Task Retry_after_is_capped_at_30s()
    {
        var rig = new Rig(o => o.TotalDeadlineSeconds = 60);
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(600)))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var task = rig.Client.CreateOrderAsync(Order());
        // The clock stops at the cap (first timestamp + 30 s): a clock that kept running would make the second timestamp depend on scheduling speed.
        while (!task.IsCompleted)
        {
            if (rig.Portal.Calls.Count > 0 && rig.Clock.GetUtcNow().ToUnixTimeSeconds() < long.Parse(rig.Portal.Calls[0].Timestamp) + 30) rig.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        var res = await task;
        Assert.Equal(2, res.Attempts);
        var ts = rig.Portal.Calls.Select(c => long.Parse(c.Timestamp)).ToList();
        Assert.Equal(30, ts[1] - ts[0]);          // the 600 s Retry-After was cut to 30 s: not 600, and not shorter
    }

    [Fact]
    public async Task An_oversized_response_is_not_a_network_error_and_the_write_is_not_resent()
    {
        var rig = new Rig();
        rig.Portal.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[3 * 1024 * 1024]) })
                  .Respond(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<PortalResponseTooLargeException>(() => rig.Client.CreateOrderAsync(Order()));
        Assert.Single(rig.Portal.Calls);
    }

    [Fact]
    public async Task The_body_cap_is_exactly_262144_bytes()
    {
        const int cap = 262144;   // a literal on purpose: a test that reads the production constant cannot notice it changing
        var rig = new Rig();
        var baseLen = JsonSerializer.SerializeToUtf8Bytes(Order() with { GiftMessage = "" }, PortalJson.Options).Length;
        var atCap = Order() with { GiftMessage = new string('x', cap - baseLen) };
        var res = await rig.Client.CreateOrderAsync(atCap);
        Assert.True(res.IsSuccess);
        Assert.Equal(cap, rig.Portal.Calls[0].Body.Length);
        var over = Order() with { GiftMessage = new string('x', cap - baseLen + 1) };
        await Assert.ThrowsAsync<PortalRequestTooLargeException>(() => rig.Client.CreateOrderAsync(over));
        Assert.Single(rig.Portal.Calls);
    }

    // ---------- review 4: the backoff outlives the deadline; responses are disposed; the past-date clamp ----------
    [Fact]
    public async Task A_backoff_timer_that_fires_after_the_total_deadline_returns_the_last_answer_instead_of_crashing()
    {
        // The review's repro: total 15 s, a 503 with Retry-After 10, then a 200, and the clock jumps 16 s while the client waits.
        var rig = new Rig(o => { o.TotalDeadlineSeconds = 15; o.MaxAttempts = 5; });
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{\"code\":\"busy\"}", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 16);                                   // remaining is now negative when the timer fires
        var res = await Within(task);                             // no ArgumentOutOfRangeException from a timer built with a negative delay
        Assert.Equal((503, 1, true), (res.Status, res.Attempts, res.Retryable));
        Assert.Equal("busy", res.Error!.Code);
        Assert.Single(rig.Portal.Calls);                          // and no second attempt was started
    }

    [Fact]
    public async Task A_network_failure_whose_backoff_outlives_the_deadline_throws_portal_unavailable_with_the_cause()
    {
        var rig = new Rig(o => { o.TotalDeadlineSeconds = 1; o.RetryBaseDelayMs = 1000; o.MaxAttempts = 5; });
        rig.Portal.RespondTransportError().Respond(HttpStatusCode.OK, "{}");
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 5);
        var ex = await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Single(rig.Portal.Calls);
    }

    /// <summary>A response body whose reads we script. Built on StreamContent so the cancellation token reaches each read, as with a real network body.</summary>
    private sealed class ScriptedStream(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingContent(Stream body) : StreamContent(body)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public static TrackingContent Bytes(byte[] bytes) => new(new MemoryStream(bytes));
        public static TrackingContent Failing() => new(new ScriptedStream((_, _) => throw new IOException("connection reset")));
        public static TrackingContent Hanging() => new(new ScriptedStream(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return 0; }));
    }

    [Fact]
    public async Task The_response_is_disposed_when_the_body_is_oversized()
    {
        var rig = new Rig();
        var content = TrackingContent.Bytes(new byte[3 * 1024 * 1024]);
        rig.Portal.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await Assert.ThrowsAsync<PortalResponseTooLargeException>(() => rig.Client.CreateOrderAsync(Order()));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task The_response_is_disposed_when_reading_the_body_fails_and_on_every_retry()
    {
        var rig = new Rig();
        var broken = new[] { TrackingContent.Failing(), TrackingContent.Failing() };
        var ok = TrackingContent.Bytes("{\"created\":false,\"orders\":[]}"u8.ToArray());
        rig.Portal.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = broken[0] })
                  .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = broken[1] })
                  .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = ok });
        var res = await rig.Client.CreateOrderAsync(Order());
        Assert.Equal(3, res.Attempts);
        Assert.All(broken, b => Assert.True(b.Disposed));
        Assert.True(ok.Disposed);                                 // also on the success path
    }

    [Fact]
    public async Task The_response_is_disposed_when_the_attempt_times_out_while_reading_the_body()
    {
        var rig = new Rig(o => { o.AttemptTimeoutSeconds = 5; o.MaxAttempts = 1; });
        var content = TrackingContent.Hanging();   // headers arrive, the body never does
        rig.Portal.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var task = rig.Client.CreateOrderAsync(Order());
        await WaitForCalls(rig.Portal, 1);
        await Advance(rig, 5);
        await Assert.ThrowsAsync<PortalUnavailableException>(() => Within(task));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task A_retry_after_date_in_the_past_means_retry_now_even_when_a_backoff_is_configured()
    {
        // With a non-zero backoff base, an unclamped negative Retry-After would fall through to the exponential backoff (0.5 to 1 s on a clock
        // nobody advances), so this call would hang. "Already past" must mean "now".
        var rig = new Rig(o => o.RetryBaseDelayMs = 1000);
        rig.Portal.Respond(_ => FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.RetryAfter = new(rig.Clock.GetUtcNow().AddSeconds(-100))))
                  .Respond(HttpStatusCode.OK, "{\"created\":false,\"orders\":[]}");
        var res = await Within(rig.Client.CreateOrderAsync(Order()));
        Assert.Equal((true, 2), (res.IsSuccess, res.Attempts));
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
