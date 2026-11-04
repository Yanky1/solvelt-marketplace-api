using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal.Webhooks;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class WebhookSignatureTests
{
    // Independent vector (openssl): secret "test-secret", ts 1700000000, body {"a":1}, MAC over "1700000000.{\"a\":1}".
    private const string Vector = "v1=8cb2c3355fca388e9ac2caec004f4d5d7045d74937ab5faad61dc11682247a9f";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Fact]
    public void Outbound_signature_vector_is_over_timestamp_dot_raw_body()
        => Assert.Equal(Vector, WebhookSignature.Sign("test-secret", "1700000000", "{\"a\":1}"u8));

    [Fact]
    public void A_correct_signature_is_accepted_and_the_secret_matters()
    {
        Assert.True(WebhookSignature.Verify("test-secret", "1700000000", Vector, "{\"a\":1}"u8, Now).Ok);
        Assert.Equal("bad_signature", WebhookSignature.Verify("other-secret", "1700000000", Vector, "{\"a\":1}"u8, Now).Code);
        Assert.Equal("bad_signature", WebhookSignature.Verify("test-secret", "1700000000", Vector, "{\"a\":2}"u8, Now).Code);   // tampered body
        Assert.Equal("bad_signature", WebhookSignature.Verify("test-secret", "1700000001", Vector, "{\"a\":1}"u8, Now).Code);   // other timestamp
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(-300, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    public void The_timestamp_window_is_exactly_300_seconds(int skew, bool ok)
    {
        var ts = (1700000000 + skew).ToString();
        var r = WebhookSignature.Verify("test-secret", ts, WebhookSignature.Sign("test-secret", ts, "{}"u8), "{}"u8, Now);
        Assert.Equal(ok, r.Ok);
        if (!ok) Assert.Equal("stale", r.Code);
    }

    [Theory]
    [InlineData(null, "v1=x", "unsigned")]
    [InlineData("1700000000", null, "unsigned")]
    [InlineData("", "v1=x", "unsigned")]
    [InlineData("abc", "v1=x", "bad_timestamp")]
    [InlineData("-1", "v1=x", "bad_timestamp")]
    [InlineData("4102444801", "v1=x", "bad_timestamp")]
    [InlineData("99999999999999999999", "v1=x", "bad_timestamp")]
    public void Malformed_headers_are_rejected_with_a_code(string? ts, string? sig, string code)
        => Assert.Equal(code, WebhookSignature.Verify("test-secret", ts, sig, "{}"u8, Now).Code);

    [Fact]
    public void Signature_comparison_ignores_case_and_surrounding_whitespace()
        => Assert.True(WebhookSignature.Verify("test-secret", "1700000000", " " + Vector.ToUpperInvariant().Replace("V1=", "v1=") + " ", "{\"a\":1}"u8, Now).Ok);
}

public class PortalEventParsingTests
{
    [Theory]
    [InlineData("""{"id":"e1","type":"order.shipped","createdAt":"2026-11-04T12:00:00Z","data":{}}""", true)]
    [InlineData("""{"id":"e1","type":"order.shipped","createdAt":"2026-11-04T12:00:00+02:00"}""", true)]
    [InlineData("""{"type":"order.shipped","createdAt":"2026-11-04T12:00:00Z"}""", false)]
    [InlineData("""{"id":"","type":"x","createdAt":"2026-11-04T12:00:00Z"}""", false)]
    [InlineData("""{"id":"e1","createdAt":"2026-11-04T12:00:00Z"}""", false)]
    [InlineData("""{"id":"e1","type":"x"}""", false)]
    [InlineData("""{"id":"e1","type":"x","createdAt":"not a date"}""", false)]
    [InlineData("""{"id":5,"type":"x","createdAt":"2026-11-04T12:00:00Z"}""", false)]
    [InlineData("""[1,2]""", false)]
    [InlineData("not json", false)]
    public void Envelope_validation(string body, bool ok)
        => Assert.Equal(ok, PortalEvent.TryParse(Encoding.UTF8.GetBytes(body), out _, out _));

    [Fact]
    public void Offsets_are_normalised_to_utc_and_ids_are_length_capped()
    {
        Assert.True(PortalEvent.TryParse("""{"id":"e1","type":"x","createdAt":"2026-11-04T14:00:00+02:00"}"""u8, out var e, out _));
        Assert.Equal(TimeSpan.Zero, e!.CreatedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), e.CreatedAt);
        Assert.False(PortalEvent.TryParse(Encoding.UTF8.GetBytes($$"""{"id":"{{new string('i', 101)}}","type":"x","createdAt":"2026-11-04T12:00:00Z"}"""), out _, out _));
        Assert.True(PortalEvent.TryParse(Encoding.UTF8.GetBytes($$"""{"id":"{{new string('i', 100)}}","type":"x","createdAt":"2026-11-04T12:00:00Z"}"""), out _, out _));
    }

    [Fact]
    public void Order_and_booking_refs_need_both_the_marketplace_id_and_the_portal_id()
    {
        static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
        var soid = Guid.NewGuid();
        Assert.Equal(new OrderRef("MP-1", soid, "SO-1"), EventRefs.Order(J(new { order = new { sellerOrderId = soid, sellerOrderNumber = "SO-1", marketplaceOrderId = "MP-1" } })));
        Assert.Null(EventRefs.Order(J(new { order = new { marketplaceOrderId = "MP-1" } })));                      // no portal id
        Assert.Null(EventRefs.Order(J(new { order = new { sellerOrderId = soid } })));                             // no marketplace id
        Assert.Null(EventRefs.Order(J(new { trackingNumber = "1Z999" })));                                         // never by tracking number
        var bid = Guid.NewGuid();
        Assert.Equal(new BookingRef("BK-7", bid, "B-1"), EventRefs.Booking(J(new { booking = new { bookingId = bid, number = "B-1", marketplaceBookingId = "BK-7" } })));
        Assert.Null(EventRefs.Booking(J(new { booking = new { marketplaceBookingId = "BK-7" } })));
    }
}

public class WebhookReceiverTests
{
    private static TestFactory Factory(ScriptedHandler? handler = null, Dictionary<string, string?>? config = null)
        => new(config, handler is null ? null : s => s.AddSingleton<IProjectionHandler>(handler));

    private static int Rows(TestFactory f) => WithDb(f, db => db.PortalInboundEvents.Count());
    private static int Markers(TestFactory f) => WithDb(f, db => db.ProjectionStates.Count(s => s.AggregateType == "marker"));

    // ---------- verification before parsing, replay ----------
    [Fact]
    public async Task Unknown_types_are_stored_and_acknowledged_and_a_duplicate_does_nothing()
    {
        using var f = Factory();
        var c = f.CreateClient();
        var body = Body("evt-1", "some.future.type", T0, new { x = 1 });
        var first = await Post(c, Request(f, body));
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("stored", StatusOf(first.Json));
        var again = await Post(c, Request(f, body));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("duplicate", StatusOf(again.Json));
        var row = Assert.Single(WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToList()));
        Assert.Equal(("evt-1", "some.future.type", InboundStatus.Stored), (row.Id, row.Type, row.Status));
        Assert.Equal(T0, row.PortalCreatedAt);
        Assert.Equal(body, row.PayloadJson);
    }

    [Fact]
    public async Task The_signature_is_checked_before_the_body_is_parsed()
    {
        using var f = Factory();
        var c = f.CreateClient();
        // unparseable body + bad signature: 401 (never reaches the parser); unparseable body + good signature: 400
        var bad = await Post(c, Request(f, "not json", eventId: "e", type: "t", sig: "v1=00"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.Status);
        Assert.Equal("bad_signature", CodeOf(bad.Json));
        var good = await Post(c, Request(f, "not json", eventId: "e", type: "t"));
        Assert.Equal(HttpStatusCode.BadRequest, good.Status);
        Assert.Equal("invalid_envelope", CodeOf(good.Json));
        Assert.Equal(0, Rows(f));
    }

    [Fact]
    public async Task Rejections_have_codes_and_store_nothing()
    {
        using var f = Factory();
        var c = f.CreateClient();
        var body = Body("evt-1", "order.shipped", T0, Order("MP-1"));
        var now = f.Clock.GetUtcNow().ToUnixTimeSeconds();

        async Task<(HttpStatusCode, string)> Code(HttpRequestMessage r) { var x = await Post(c, r); return (x.Status, CodeOf(x.Json)); }
        Assert.Equal((HttpStatusCode.Unauthorized, "unsigned"), await Code(Request(f, body, signed: false)));
        Assert.Equal((HttpStatusCode.Unauthorized, "bad_timestamp"), await Code(Request(f, body, ts: "yesterday")));
        Assert.Equal((HttpStatusCode.Unauthorized, "stale"), await Code(Request(f, body, ts: (now - 301).ToString())));
        Assert.Equal((HttpStatusCode.Unauthorized, "stale"), await Code(Request(f, body, ts: (now + 301).ToString())));
        Assert.Equal((HttpStatusCode.Unauthorized, "bad_signature"), await Code(Request(f, body, secret: "not-the-webhook-secret-at-all-0123456789")));
        Assert.Equal((HttpStatusCode.Unauthorized, "bad_signature"), await Code(Request(f, body + " ", sig: Sign(Secret, now.ToString(), body))));   // tampered body
        Assert.Equal(0, Rows(f));
    }

    [Fact]
    public async Task The_portal_inbound_secret_and_the_web_forwarding_key_cannot_sign_webhooks()
    {
        using var f = Factory();
        var c = f.CreateClient();
        var body = Body("evt-1", "order.shipped", T0, Order("MP-1"));
        foreach (var other in new[] { DevDefaults.PortalInboundSecret, DevDefaults.WebForwardingKey })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, Request(f, body, secret: other))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, Request(f, body))).Status);   // sanity: the webhook secret works
    }

    [Fact]
    public async Task A_captured_request_replays_as_a_duplicate_inside_the_window_and_is_stale_after_it()
    {
        var h = new ScriptedHandler("order.shipped");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        var body = Body("evt-1", "order.shipped", T0, Order("MP-1"));
        var ts = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, body, ts: ts))).Json));
        for (var i = 0; i < 3; i++) Assert.Equal("duplicate", StatusOf((await Post(c, Request(f, body, ts: ts))).Json));
        Assert.Equal(1, h.Calls);
        Assert.Equal(1, Markers(f));
        f.Clock.Advance(TimeSpan.FromSeconds(301));
        var late = await Post(c, Request(f, body, ts: ts));
        Assert.Equal(HttpStatusCode.Unauthorized, late.Status);
        Assert.Equal("stale", CodeOf(late.Json));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task A_replay_with_a_different_event_id_header_cannot_bypass_the_duplicate_guard()
    {
        // The signature covers timestamp and body only, not the X-Solvelt-Event-Id header. A captured request replayed with a fresh
        // header id must not be applied a second time.
        var h = new ScriptedHandler("order.shipped");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        var body = Body("evt-1", "order.shipped", T0, Order("MP-1"));
        var ts = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, body, ts: ts))).Json));
        var forged = await Post(c, Request(f, body, eventId: "evt-1-but-fresh", ts: ts));
        Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
        Assert.Equal("event_id_mismatch", CodeOf(forged.Json));
        var wrongType = await Post(c, Request(f, Body("evt-2", "order.shipped", T0, Order("MP-1")), type: "order.cancelled"));
        Assert.Equal("event_type_mismatch", CodeOf(wrongType.Json));
        Assert.Equal(1, h.Calls);
        Assert.Equal(1, Rows(f));
    }

    [Fact]
    public async Task The_body_cap_is_exactly_256KB()
    {
        const int cap = 262144;   // a literal on purpose: a test that reads the production constant cannot notice it changing
        using var f = Factory();
        var c = f.CreateClient();
        var baseLen = Encoding.UTF8.GetByteCount(Body("evt-bigA", "big.type", T0, new { pad = "" }));
        var atCap = Body("evt-bigA", "big.type", T0, new { pad = new string('x', cap - baseLen) });
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(atCap));
        Assert.Equal(HttpStatusCode.OK, (await Post(c, Request(f, atCap))).Status);
        var over = Body("evt-bigB", "big.type", T0, new { pad = new string('x', cap - baseLen + 1) });
        Assert.Equal(cap + 1, Encoding.UTF8.GetByteCount(over));
        var res = await Post(c, Request(f, over));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.Status);
        Assert.Equal("too_large", CodeOf(res.Json));
        Assert.Equal(1, Rows(f));
    }

    [Fact]
    public async Task An_unconfigured_secret_answers_503_instead_of_accepting_anything()
    {
        using var f = Factory(config: new() { ["Portal:WebhookSecret"] = "" });
        var res = await Post(f.CreateClient(), Request(f, Body("e", "t", T0), secret: ""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.Status);
        Assert.Equal("webhook_not_configured", CodeOf(res.Json));
    }

    [Fact]
    public async Task Payloads_never_reach_the_logs()
    {
        using var f = Factory();
        await Post(f.CreateClient(), Request(f, Body("evt-log", "message.sent", T0, new { text = "SECRET-MESSAGE-TEXT jane.doe@example.com" })));
        Assert.Contains(f.Logs.Lines, l => l.Contains("message.sent"));   // the event type is logged, nothing else of it
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("SECRET-MESSAGE-TEXT") || l.Contains("jane.doe"));
    }

    // ---------- projections: order independence ----------
    [Fact]
    public async Task An_older_event_never_overwrites_newer_state()
    {
        var h = new ScriptedHandler("order.accepted", "order.held");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e-new", "order.held", T0.AddMinutes(10), Order("MP-1"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e-old", "order.accepted", T0.AddMinutes(5), Order("MP-1"))))).Json));
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e-newer", "order.accepted", T0.AddMinutes(11), Order("MP-1"))))).Json));
        var state = WithDb(f, db => db.ProjectionStates.AsNoTracking().Single(s => s.AggregateType == "order" && s.AggregateKey == "MP-1"));
        Assert.Equal(T0.AddMinutes(11), state.LastAppliedAt);
        Assert.Equal(2, Markers(f));
        Assert.Equal("skipped", WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "e-old").Status));   // stored, acked, not applied
    }

    [Fact]
    public async Task Terminal_states_are_never_reopened_by_an_older_or_newer_non_terminal_event()
    {
        var h = new ScriptedHandler("order.cancelled", "order.resumed", "order.delivered", "refund.issued", "order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e1", "order.cancelled", T0.AddMinutes(10), Order("MP-1"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e2", "order.accepted", T0.AddMinutes(1), Order("MP-1"))))).Json));    // older
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e3", "order.resumed", T0.AddMinutes(20), Order("MP-1"))))).Json));    // newer but non-terminal
        // a later terminal event is a legitimate progression (delivered, then refunded) and is applied; an older terminal one is not
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e4", "refund.issued", T0.AddMinutes(30), Order("MP-1"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e5", "order.delivered", T0.AddMinutes(12), Order("MP-1"))))).Json));
        var state = WithDb(f, db => db.ProjectionStates.AsNoTracking().Single(s => s.AggregateType == "order"));
        Assert.Equal(("refund.issued", T0.AddMinutes(30)), (state.TerminalState, state.LastAppliedAt));
    }

    [Fact]
    public async Task A_terminal_state_in_one_field_group_blocks_non_terminal_events_in_another()
    {
        using var f = new TestFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var store = scope.ServiceProvider.GetRequiredService<ProjectionStore>();
        Assert.True(await store.TryAdvanceAsync("order", "MP-9", "status", T0, "order.cancelled"));
        await db.SaveChangesAsync();
        Assert.False(await store.TryAdvanceAsync("order", "MP-9", "tracking", T0.AddHours(1)));       // new group, same aggregate
        Assert.True(await store.TryAdvanceAsync("order", "MP-9", "tracking", T0.AddHours(1), "order.delivered"));
    }

    [Fact]
    public async Task Events_with_equal_times_both_apply()
    {
        var h = new ScriptedHandler("order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("a", "order.accepted", T0, Order("MP-1"))))).Json));
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("b", "order.accepted", T0, Order("MP-1"))))).Json));
    }

    // ---------- parking ----------
    [Fact]
    public async Task Events_for_unknown_aggregates_are_parked_acked_and_applied_in_portal_order_when_the_aggregate_appears()
    {
        var h = new ScriptedHandler("order.shipped", "order.delivered");
        using var f = Factory(h);
        var c = f.CreateClient();
        // arrive out of order and before our ingest call returned
        Assert.Equal("parked", StatusOf((await Post(c, Request(f, Body("e-delivered", "order.delivered", T0.AddMinutes(20), Order("MP-1"))))).Json));
        Assert.Equal("parked", StatusOf((await Post(c, Request(f, Body("e-shipped", "order.shipped", T0.AddMinutes(10), Order("MP-1"))))).Json));
        Assert.Equal(0, Markers(f));
        Assert.All(WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToList()), e => Assert.Equal(("order", "MP-1"), (e.AggregateType, e.AggregateKey)));

        // while the aggregate is still unknown a replay re-parks and changes nothing
        using (var scope = f.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));
        Assert.Equal(0, Markers(f));

        h.KnownOrders.Add("MP-1");   // our ingest call returned
        using (var scope = f.Services.CreateScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));
        Assert.Equal(2, Markers(f));
        Assert.All(WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToList()), e => Assert.Equal(InboundStatus.Applied, e.Status));
        var state = WithDb(f, db => db.ProjectionStates.AsNoTracking().Single(s => s.AggregateType == "order"));
        Assert.Equal(("order.delivered", T0.AddMinutes(20)), (state.TerminalState, state.LastAppliedAt));
        using (var scope = f.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));   // nothing left to do
    }

    [Fact]
    public async Task Replay_applies_oldest_portal_time_first_so_a_terminal_event_cannot_be_overtaken()
    {
        var order = new List<string>();
        var h = new ScriptedHandler("t.a") { Script = c => { order.Add(c.Event.Id); return Task.FromResult(order.Count < 99 && known ? HandlerOutcome.Applied() : HandlerOutcome.Parked("order", "MP-1")); } };
        using var f = Factory(h);
        var client = f.CreateClient();
        foreach (var (id, min) in new[] { ("c", 30), ("a", 10), ("b", 20) })
            await Post(client, Request(f, Body(id, "t.a", T0.AddMinutes(min), Order("MP-1"))));
        order.Clear();
        known = true;
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1");
        Assert.Equal(["a", "b", "c"], order);
    }
    private static bool known;

    [Fact]
    public async Task The_sweep_replays_parked_events_across_aggregates()
    {
        var h = new ScriptedHandler("order.shipped");
        using var f = Factory(h);
        var c = f.CreateClient();
        await Post(c, Request(f, Body("e1", "order.shipped", T0, Order("MP-1"))));
        await Post(c, Request(f, Body("e2", "order.shipped", T0, Order("MP-2"))));
        h.KnownOrders.Add("MP-1"); h.KnownOrders.Add("MP-2");
        using var scope = f.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayAllParkedAsync());
        Assert.Equal(0, WithDb(f, db => db.PortalInboundEvents.Count(e => e.Status == InboundStatus.Parked)));
    }

    // ---------- failures roll everything back ----------
    [Fact]
    public async Task A_failing_handler_answers_5xx_and_rolls_back_the_event_row_and_its_changes_so_the_retry_applies_it()
    {
        var fail = true;
        var h = new ScriptedHandler("order.shipped")
        {
            Script = async c =>
            {
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = c.Event.Id, FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                await c.Db.SaveChangesAsync();   // the change is flushed inside the transaction ...
                if (fail) throw new InvalidOperationException("boom for jane.doe@example.com");
                return HandlerOutcome.Applied();
            },
        };
        using var f = Factory(h);
        var c = f.CreateClient();
        var body = Body("evt-1", "order.shipped", T0, Order("MP-1"));
        var res = await Post(c, Request(f, body));
        Assert.Equal(HttpStatusCode.InternalServerError, res.Status);
        Assert.Equal("internal_error", CodeOf(res.Json));
        Assert.DoesNotContain("jane.doe", res.Json);
        Assert.Equal(0, Rows(f));       // ... and rolled back with the event row
        Assert.Equal(0, Markers(f));
        fail = false;
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, body))).Json));   // the portal's retry
        Assert.Equal((1, 1), (Rows(f), Markers(f)));
    }

    // ---------- concurrency ----------
    [Fact]
    public async Task The_same_webhook_fired_in_parallel_is_applied_exactly_once()
    {
        var h = new ScriptedHandler("order.shipped");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        var body = Body("evt-race", "order.shipped", T0, Order("MP-1"));
        var ts = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => Post(c, Request(f, body, ts: ts)))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        var statuses = results.Select(r => StatusOf(r.Json)).ToList();
        Assert.Equal(1, statuses.Count(s => s == "applied"));
        Assert.Equal(23, statuses.Count(s => s == "duplicate"));
        Assert.Equal(1, Rows(f));
        Assert.Equal(1, Markers(f));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Different_events_for_one_aggregate_in_parallel_end_with_the_newest_state_once_the_portal_retries()
    {
        var h = new ScriptedHandler("order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        var ts = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        async Task<HttpStatusCode> DeliverWithRetries(int i)
        {
            var body = Body($"evt-{i}", "order.accepted", T0.AddMinutes(i), Order("MP-1"));
            for (var attempt = 0; ; attempt++)
            {
                var r = await Post(c, Request(f, body, ts: ts));
                if ((int)r.Status < 500 || attempt >= 12) return r.Status;   // the portal retries 5xx (up to 8 times)
                await Task.Delay(10);
            }
        }
        var codes = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Task.Run(() => DeliverWithRetries(i))));
        Assert.All(codes, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(12, Rows(f));
        var state = WithDb(f, db => db.ProjectionStates.AsNoTracking().Single(s => s.AggregateType == "order"));
        Assert.Equal(T0.AddMinutes(12), state.LastAppliedAt);   // never regressed by a late older event
    }

    [Fact]
    public async Task Concurrent_replayers_apply_a_parked_event_once()
    {
        // Each application writes a marker with a UNIQUE key, so a double application would show as two rows
        // (the version claim, not a key collision, is what must stop the second replayer).
        var known = false;
        var postgres = TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres";
        using var overlap = new Barrier(8);
        var h = new ScriptedHandler("order.shipped")
        {
            Script = async c =>
            {
                if (!Volatile.Read(ref known)) return HandlerOutcome.Parked("order", "MP-1");
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = Guid.NewGuid().ToString("N"), FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                // PostgreSQL (read committed) lets every replayer read the parked row; hold them until all have, so the version claim
                // is what decides. SQLite serialises writers on a file lock, so there the replayers cannot truly overlap.
                if (postgres) overlap.SignalAndWait(TimeSpan.FromSeconds(3));
                return HandlerOutcome.Applied();
            },
        };
        using var f = Factory(h);
        await Post(f.CreateClient(), Request(f, Body("evt-p", "order.shipped", T0, Order("MP-1"))));
        Volatile.Write(ref known, true);
        var applied = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1");
        })));
        Assert.Equal(1, applied.Sum());
        Assert.Equal(1, Markers(f));
        Assert.Equal(InboundStatus.Applied, WithDb(f, db => db.PortalInboundEvents.Single().Status));
    }

    // ---------- registry ----------
    [Fact]
    public void Two_handlers_for_one_event_type_fail_at_start_up()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectionRegistry([new ScriptedHandler("order.shipped"), new ScriptedHandler("order.shipped")]));
        Assert.Contains("order.shipped", ex.Message);
        var ok = new ProjectionRegistry([new ScriptedHandler("a", "b"), new ScriptedHandler("c")]);
        Assert.Equal(["a", "b", "c"], ok.RegisteredTypes.Order());
        Assert.Null(ok.Find("zzz"));
    }

    // ---------- retention ----------
    [Fact]
    public async Task Retention_purges_payloads_then_rows_but_never_parked_events()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        await Post(c, Request(f, Body("old-stored", "x.y", T0, new { text = "hello" })));
        await Post(c, Request(f, Body("old-parked", "x.z", T0)));
        WithDb(f, db => { db.PortalInboundEvents.Single(e => e.Id == "old-parked").Status = InboundStatus.Parked; return db.SaveChanges(); });
        f.Clock.Advance(TimeSpan.FromDays(29));
        await Post(c, Request(f, Body("recent", "x.y", f.Clock.GetUtcNow(), new { text = "fresh" })));

        async Task<(int, int)> Purge() { using var s = f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync(); }
        Assert.Equal((0, 0), await Purge());                       // 29 days: nothing yet
        f.Clock.Advance(TimeSpan.FromDays(2));                     // 31 days after the old rows, 2 after "recent"
        Assert.Equal((1, 0), await Purge());                       // old-stored payload dropped; parked keeps its payload
        var rows = WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToDictionary(e => e.Id));
        Assert.Null(rows["old-stored"].PayloadJson);
        Assert.NotNull(rows["old-stored"].PayloadPurgedAt);
        Assert.NotNull(rows["old-parked"].PayloadJson);
        Assert.NotNull(rows["recent"].PayloadJson);
        f.Clock.Advance(TimeSpan.FromDays(60));                    // 91 days after the old rows
        var (_, deleted) = await Purge();
        Assert.Equal(1, deleted);                                  // old-stored's id row is gone after 90 days; parked is never deleted
        Assert.Equal(["old-parked", "recent"], WithDb(f, db => db.PortalInboundEvents.Select(e => e.Id).ToList()).Order().ToList());
    }

    [Fact]
    public async Task Positions_and_proof_urls_are_dropped_after_24h_and_a_parked_one_expires()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        await Post(c, Request(f, Body("trk", "order.tracking_updated", T0, new { position = new { lat = 40.693, lng = -73.99 }, driverFirstName = "Sam" })));
        await Post(c, Request(f, Body("dlv", "shipment.delivered", T0, new { proof = new { photoUrls = new[] { "https://portal/proof/1?sig=x" } } })));
        await Post(c, Request(f, Body("msg", "message.sent", T0, new { text = "hello" })));
        await Post(c, Request(f, Body("trk-parked", "order.tracking_updated", T0, new { position = new { lat = 1.0, lng = 2.0 } })));
        WithDb(f, db => { db.PortalInboundEvents.Single(e => e.Id == "trk-parked").Status = InboundStatus.Parked; return db.SaveChanges(); });

        async Task Purge() { using var s = f.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync(); }
        f.Clock.Advance(TimeSpan.FromHours(23));
        await Purge();
        Assert.All(WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToList()), e => Assert.NotNull(e.PayloadJson));   // 23 h: nothing yet
        f.Clock.Advance(TimeSpan.FromHours(2));
        await Purge();
        var rows = WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToDictionary(e => e.Id));
        Assert.Null(rows["trk"].PayloadJson);
        Assert.Null(rows["dlv"].PayloadJson);
        Assert.NotNull(rows["msg"].PayloadJson);                   // ordinary events keep theirs for 30 days
        Assert.Null(rows["trk-parked"].PayloadJson);
        Assert.Equal(InboundStatus.Skipped, rows["trk-parked"].Status);
        Assert.Contains("expired", rows["trk-parked"].Note);
    }
}

