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
    public void A_handler_matches_a_stored_order_only_when_both_ids_are_equal_to_the_stored_ones()
    {
        var stored = Guid.NewGuid();
        var mine = new OrderRef("MP-1", stored, "SO-1");
        Assert.True(EventRefs.Matches(mine, "MP-1", stored));
        Assert.False(EventRefs.Matches(mine with { SellerOrderId = Guid.NewGuid() }, "MP-1", stored));     // same marketplace id, another seller's order
        Assert.False(EventRefs.Matches(mine with { MarketplaceOrderId = "MP-2" }, "MP-1", stored));        // same portal id, another marketplace order
        Assert.False(EventRefs.Matches(mine with { MarketplaceOrderId = "mp-1" }, "MP-1", stored));        // ids are case sensitive
        Assert.False(EventRefs.Matches((OrderRef?)null, "MP-1", stored));
        var booking = new BookingRef("BK-1", stored, "B-1");
        Assert.True(EventRefs.Matches(booking, "BK-1", stored));
        Assert.False(EventRefs.Matches(booking with { BookingId = Guid.NewGuid() }, "BK-1", stored));
        Assert.False(EventRefs.Matches(booking with { MarketplaceBookingId = "BK-2" }, "BK-1", stored));
        Assert.False(EventRefs.Matches((BookingRef?)null, "BK-1", stored));
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
    {
        // The scripted stand-in replaces any production handler for the same event types (the framework tests exercise the framework, not the M3 order handlers).
        var f = new TestFactory(config, handler is null ? null : s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IProjectionHandler) && d.ImplementationType is { } t && t != typeof(ScriptedHandler)
                         && (t == typeof(Solvelt.Marketplace.Api.Checkout.PortalRefundHandler)) && handler.EventTypes.Intersect(new Solvelt.Marketplace.Api.Checkout.PortalRefundHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger<Solvelt.Marketplace.Api.Checkout.PortalRefundHandler>.Instance).EventTypes).Any()).ToList())
                s.Remove(d);
            s.AddSingleton<IProjectionHandler>(handler);
        });
        f.Clock.Advance(TimeSpan.FromHours(3));   // "now" is T0 + 3 h, so events dated T0 + minutes are in the past (future-dated events are clamped, see below)
        return f;
    }

    private static int Rows(TestFactory f) => WithDb(f, db => db.PortalInboundEvents.Count());
    private static int Markers(TestFactory f) => WithDb(f, db => db.ProjectionStates.Count(s => s.AggregateType == "marker"));
    private static ProjectionState GroupRow(TestFactory f, string key, string group) => WithDb(f, db => db.ProjectionStates.AsNoTracking().Single(s => s.AggregateType == "order" && s.AggregateKey == key && s.FieldGroup == group));

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
        var state = GroupRow(f, "MP-1", "status");
        Assert.Equal(T0.AddMinutes(11), state.LastAppliedAt);
        Assert.Equal(2, Markers(f));
        Assert.Equal("skipped", WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "e-old").Status));   // stored, acked, not applied
    }

    [Fact]
    public async Task Terminal_states_are_never_reopened_and_cancelled_is_absorbing()
    {
        var h = new ScriptedHandler("order.cancelled", "order.resumed", "order.delivered", "refund.issued", "order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e1", "order.cancelled", T0.AddMinutes(10), Order("MP-1"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e2", "order.accepted", T0.AddMinutes(1), Order("MP-1"))))).Json));    // older, non-terminal
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e3", "order.resumed", T0.AddMinutes(20), Order("MP-1"))))).Json));    // newer but non-terminal
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e4", "order.delivered", T0.AddMinutes(30), Order("MP-1"))))).Json));  // cancelled -> delivered is not a legal step (O-10)
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e5", "refund.issued", T0.AddMinutes(40), Order("MP-1"))))).Json));   // cancelled and refunded are absorbing
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e6", "order.cancelled", T0.AddMinutes(50), Order("MP-1"))))).Json));  // the same terminal state again is fine
        var terminal = GroupRow(f, "MP-1", ProjectionStore.TerminalGroup);
        Assert.Equal((TerminalStates.Cancelled, T0.AddMinutes(50)), (terminal.TerminalState, terminal.LastAppliedAt));
    }

    [Fact]
    public async Task Delivered_may_become_refunded_but_not_cancelled_and_an_older_terminal_event_is_ignored()
    {
        var h = new ScriptedHandler("order.cancelled", "order.delivered", "refund.issued");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e1", "order.delivered", T0.AddMinutes(10), Order("MP-1"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e2", "order.cancelled", T0.AddMinutes(20), Order("MP-1"))))).Json));  // delivered -> cancelled is not legal
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e3", "refund.issued", T0.AddMinutes(5), Order("MP-1"))))).Json));    // legal step, but older than delivered
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e4", "refund.issued", T0.AddMinutes(30), Order("MP-1"))))).Json));   // delivered -> refunded
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e5", "refund.issued", T0.AddMinutes(40), Order("MP-1"))))).Json));   // a second (partial) refund
        Assert.Equal(TerminalStates.Refunded, GroupRow(f, "MP-1", ProjectionStore.TerminalGroup).TerminalState);
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e6", "order.delivered", T0.AddMinutes(50), Order("MP-1"))))).Json)); // refunded is absorbing
    }

    [Theory]
    [InlineData("order", "cancelled", "cancelled", true)]
    [InlineData("order", "cancelled", "refunded", false)]
    [InlineData("order", "cancelled", "delivered", false)]
    [InlineData("order", "cancelled", "completed", false)]
    [InlineData("order", "refunded", "refunded", true)]
    [InlineData("order", "refunded", "cancelled", false)]
    [InlineData("order", "refunded", "delivered", false)]
    [InlineData("order", "delivered", "cancelled", false)]
    [InlineData("order", "delivered", "completed", true)]
    [InlineData("order", "delivered", "refunded", true)]
    [InlineData("order", "completed", "refunded", true)]       // goodwill refund after the return window
    [InlineData("order", "completed", "cancelled", false)]
    [InlineData("order", "completed", "delivered", false)]
    [InlineData("booking", "completed", "refunded", false)]    // an aggregate type with no rules is absorbing
    [InlineData("booking", "completed", "completed", true)]
    public void The_terminal_step_table_follows_the_portal_state_machine(string aggregate, string from, string to, bool allowed)
        => Assert.Equal(allowed, new TerminalStateRules().Allows(aggregate, from, to));

    [Fact]
    public void Other_modules_add_their_own_legal_steps()
    {
        var rules = new TerminalStateRules().Allow("booking", "completed", "refunded");
        Assert.True(rules.Allows("booking", "completed", "refunded"));
        Assert.False(rules.Allows("booking", "refunded", "completed"));
    }

    [Fact]
    public async Task A_terminal_state_blocks_every_field_group_even_one_that_already_has_a_row()
    {
        // The review's repro: tracking@T0, then the order is cancelled, then tracking@T0+1h must NOT apply (the group already had a row).
        using var f = new TestFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var store = scope.ServiceProvider.GetRequiredService<ProjectionStore>();
        Assert.True(await store.TryAdvanceAsync("order", "MP-9", "tracking", "e1", T0));
        Assert.True(await store.TryAdvanceAsync("order", "MP-9", "status", "e2", T0.AddMinutes(1), TerminalStates.Cancelled));
        await db.SaveChangesAsync();
        Assert.False(await store.TryAdvanceAsync("order", "MP-9", "tracking", "e3", T0.AddHours(1)));
        Assert.False(await store.TryAdvanceAsync("order", "MP-9", "address", "e4", T0.AddHours(1)));   // and a group with no row yet
        Assert.True(await store.IsTerminalAsync("order", "MP-9"));
        Assert.Equal(TerminalStates.Cancelled, await store.TerminalStateAsync("order", "MP-9"));
        Assert.False(await store.IsTerminalAsync("order", "MP-10"));
        Assert.Throws<ArgumentException>(() => store.TryAdvanceAsync("order", "MP-9", ProjectionStore.TerminalGroup, "e5", T0).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task A_terminal_event_older_than_the_current_terminal_state_is_ignored_even_from_another_field_group()
    {
        // Only the terminal row can decide this: the "status" group has never seen an event, so its own ordering has nothing to compare against.
        using var f = new TestFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var store = scope.ServiceProvider.GetRequiredService<ProjectionStore>();
        Assert.True(await store.TryAdvanceAsync("order", "MP-5", "tracking", "e1", T0.AddMinutes(10), TerminalStates.Delivered));
        await db.SaveChangesAsync();
        Assert.False(await store.TryAdvanceAsync("order", "MP-5", "status", "e2", T0.AddMinutes(5), TerminalStates.Refunded));    // a legal step, but older than delivered
        Assert.True(await store.TryAdvanceAsync("order", "MP-5", "status", "e3", T0.AddMinutes(20), TerminalStates.Refunded));   // the same step, later
        await db.SaveChangesAsync();
        Assert.Equal(TerminalStates.Refunded, await store.TerminalStateAsync("order", "MP-5"));
    }

    [Fact]
    public async Task Events_with_equal_times_are_ordered_by_event_id_whatever_order_they_arrive_in()
    {
        var h = new ScriptedHandler("order.accepted");
        h.KnownOrders.Add("MP-1"); h.KnownOrders.Add("MP-2");
        using var f = Factory(h);
        var c = f.CreateClient();
        // aggregate 1: a1 then b1 (same time): both apply
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("a1", "order.accepted", T0, Order("MP-1"))))).Json));
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("b1", "order.accepted", T0, Order("MP-1"))))).Json));
        // aggregate 2: the same two events the other way round: b2 first, then a2 is the older one
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("b2", "order.accepted", T0, Order("MP-2"))))).Json));
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("a2", "order.accepted", T0, Order("MP-2"))))).Json));
        // either way the aggregate ends on the event with the larger id
        Assert.Equal("b1", GroupRow(f, "MP-1", "status").LastEventId);
        Assert.Equal("b2", GroupRow(f, "MP-2", "status").LastEventId);
    }

    [Fact]
    public async Task Times_are_compared_at_the_stored_microsecond_precision_with_the_event_id_as_tie_breaker()
    {
        // 100 ns apart: PostgreSQL stores microseconds, so two events inside one microsecond must not be ordered by the lost digits.
        var h = new ScriptedHandler("order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("a", "order.accepted", T0.AddTicks(4), Order("MP-1"))))).Json));
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("z", "order.accepted", T0.AddTicks(2), Order("MP-1"))))).Json));   // same microsecond: id decides, z > a
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("m", "order.accepted", T0.AddTicks(3), Order("MP-1"))))).Json));   // m < z
        Assert.Equal(("z", T0), (GroupRow(f, "MP-1", "status").LastEventId, GroupRow(f, "MP-1", "status").LastAppliedAt));
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
        var terminal = GroupRow(f, "MP-1", ProjectionStore.TerminalGroup);
        Assert.Equal((TerminalStates.Delivered, T0.AddMinutes(20)), (terminal.TerminalState, terminal.LastAppliedAt));
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
        var state = GroupRow(f, "MP-1", "status");
        Assert.Equal(T0.AddMinutes(12), state.LastAppliedAt);   // never regressed by a late older event
    }

    [Fact]
    public async Task Concurrent_replayers_neither_throw_nor_apply_an_event_twice_even_when_the_handler_creates_the_first_state_row()
    {
        // The review's repro: on PostgreSQL the losers of a race to create the first projection_states row got a unique violation, which escaped as an
        // exception and abandoned the rest of the aggregate's parked list. Each application also writes a marker with a UNIQUE key, so a double
        // application would show as two rows (the claim, not a key collision, must stop the second replayer).
        var known = false;
        var h = new ScriptedHandler("order.shipped")
        {
            Script = async c =>
            {
                if (!Volatile.Read(ref known)) return HandlerOutcome.Parked("order", "MP-1");
                if (!await c.AdvanceAsync("order", "MP-1", "status", ct: default)) return HandlerOutcome.Skipped("older");
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = Guid.NewGuid().ToString("N"), FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                return HandlerOutcome.Applied();
            },
        };
        using var f = Factory(h);
        var client = f.CreateClient();
        foreach (var (id, min) in new[] { ("p1", 1), ("p2", 2), ("p3", 3) })
            await Post(client, Request(f, Body(id, "order.shipped", T0.AddMinutes(min), Order("MP-1"))));
        Assert.Equal(3, WithDb(f, db => db.PortalInboundEvents.Count(e => e.Status == InboundStatus.Parked)));
        Volatile.Write(ref known, true);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            try { return (Ok: true, Count: await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1")); }
            catch (Exception) { return (Ok: false, Count: 0); }
        })));
        Assert.All(results, r => Assert.True(r.Ok, "a replayer threw instead of treating a lost race as a skipped claim"));
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1");   // whatever lost a race is picked up
        var rows = WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToList());
        Assert.DoesNotContain(rows, e => e.Status == InboundStatus.Parked);                          // nothing abandoned
        Assert.Equal(rows.Count(e => e.Status == InboundStatus.Applied), Markers(f));                // one application per applied event, never two
        Assert.Equal(T0.AddMinutes(3), GroupRow(f, "MP-1", "status").LastAppliedAt);                  // and the newest event won
    }

    [Fact]
    public async Task A_replay_that_loses_the_race_to_create_the_first_state_row_against_a_live_event_leaves_its_event_parked()
    {
        // On PostgreSQL (read committed) a replay and a live webhook for the same aggregate can both find no state row and both insert it; the
        // second committer gets a unique violation. For the replay that must be a lost race (its event stays parked), never an exception that
        // abandons the rest of the list. SQLite serialises every writer on a file lock, so the race exists on PostgreSQL only (CI runs this there).
        if (TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) != "postgres") return;
        var known = false;
        var replayHasRead = new TaskCompletionSource();
        var liveHasCommitted = new TaskCompletionSource();
        var h = new ScriptedHandler("order.shipped", "order.accepted")
        {
            Script = async c =>
            {
                if (c.Event.Type == "order.accepted")                                      // the live event: creates and commits the state row
                {
                    await c.AdvanceAsync("order", "MP-1", "status");
                    return HandlerOutcome.Applied();
                }
                if (!Volatile.Read(ref known)) return HandlerOutcome.Parked("order", "MP-1");
                await c.AdvanceAsync("order", "MP-1", "status");                           // the replay: reads "no row", its insert is pending ...
                replayHasRead.TrySetResult();
                await liveHasCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));          // ... until the live event has committed its own row
                return HandlerOutcome.Applied();                                           // its INSERT now violates the key
            },
        };
        using var f = Factory(h);
        var client = f.CreateClient();
        await Post(client, Request(f, Body("p1", "order.shipped", T0.AddMinutes(1), Order("MP-1"))));
        Volatile.Write(ref known, true);

        var replay = Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1");
        });
        await replayHasRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var live = await Post(client, Request(f, Body("live-1", "order.accepted", T0.AddMinutes(2), Order("MP-1"))));
        Assert.Equal(HttpStatusCode.OK, live.Status);
        liveHasCommitted.SetResult();
        var applied = await replay.WaitAsync(TimeSpan.FromSeconds(15));                   // must not throw
        Assert.Equal(0, applied);
        Assert.Equal(InboundStatus.Parked, WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "p1").Status));   // lost the race: still parked, nothing half-applied
        Assert.Equal(0, Markers(f));
        // the next pass sees the committed row and decides normally (p1 is older than the live event, so it is skipped, no longer parked)
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1");
        Assert.NotEqual(InboundStatus.Parked, WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "p1").Status));
    }

    [Fact]
    public async Task A_parked_event_is_claimed_before_it_is_applied_so_a_second_claim_gets_nothing()
    {
        var h = new ScriptedHandler("order.shipped");
        using var f = Factory(h);
        await Post(f.CreateClient(), Request(f, Body("p1", "order.shipped", T0, Order("MP-1"))));
        h.KnownOrders.Add("MP-1");
        // somebody else has already claimed it (its version moved on): our replay must skip it without applying anything
        WithDb(f, db => db.PortalInboundEvents.ExecuteUpdate(s => s.SetProperty(e => e.Status, InboundStatus.Applied)));
        using var scope = f.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));
        Assert.Equal(0, Markers(f));
    }

    // ---------- the sweep serves everyone ----------
    [Fact]
    public async Task Events_that_never_resolve_cannot_starve_the_sweep()
    {
        var h = new ScriptedHandler("order.shipped");
        h.KnownOrders.Add("MP-OK");
        using var f = Factory(h);
        var c = f.CreateClient();
        // 205 aggregates that will never appear, then one that can resolve, received last
        for (var i = 0; i < 205; i++)
        {
            await Post(c, Request(f, Body($"never-{i:D3}", "order.shipped", T0, Order($"MP-NEVER-{i:D3}"))));
            f.Clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        h.KnownOrders.Remove("MP-OK");
        await Post(c, Request(f, Body("resolvable", "order.shipped", T0, Order("MP-OK"))));
        h.KnownOrders.Add("MP-OK");
        Assert.Equal(206, WithDb(f, db => db.PortalInboundEvents.Count(e => e.Status == InboundStatus.Parked)));

        async Task<int> Sweep() { using var s = f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayAllParkedAsync(limit: 200); }
        Assert.Equal(0, await Sweep());                                                              // the first 200 aggregates by age are the hopeless ones
        Assert.Equal(200, WithDb(f, db => db.PortalInboundEvents.Count(e => e.LastReplayAt != null)));   // they are stamped as tried
        Assert.Equal(1, await Sweep());                                                              // the never-tried ones go first now: the resolvable event is served
        Assert.Equal(InboundStatus.Applied, WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "resolvable").Status));
        Assert.Equal(205, WithDb(f, db => db.PortalInboundEvents.Count(e => e.Status == InboundStatus.Parked)));
    }

    // ---------- future-dated events ----------
    [Theory]
    [InlineData(-3600, -3600)]     // the past is never changed
    [InlineData(0, 0)]
    [InlineData(299, 299)]
    [InlineData(300, 300)]         // exactly the tolerance: unchanged
    [InlineData(301, 300)]         // one second beyond: clamped to receipt + 5 minutes
    [InlineData(86_400, 300)]
    public void The_ordering_time_is_never_later_than_receipt_plus_five_minutes_and_the_past_is_untouched(int offsetSeconds, int expectedSeconds)
    {
        var received = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(received.AddSeconds(expectedSeconds), PortalEvent.EffectiveTime(received.AddSeconds(offsetSeconds), received));
    }

    [Fact]
    public async Task A_future_dated_event_cannot_freeze_its_aggregate()
    {
        // The review's repro: createdAt in 2100 was applied and then every real event was skipped for ever.
        var h = new ScriptedHandler("order.accepted");
        h.KnownOrders.Add("MP-1");
        using var f = Factory(h);
        var c = f.CreateClient();
        var received = f.Clock.GetUtcNow();
        var future = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e-future", "order.accepted", future, Order("MP-1"))))).Json));
        var row = WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "e-future"));
        Assert.Equal(future, row.PortalCreatedAt);                                                    // the raw value is kept
        Assert.Equal(received.AddMinutes(5), row.OrderingAt);                                          // ordering uses receipt + 5 minutes
        Assert.Equal(received.AddMinutes(5), GroupRow(f, "MP-1", "status").LastAppliedAt);
        Assert.Contains(f.Logs.Lines, l => l.Contains("in the future") && !l.Contains("2100"));        // clamping is logged, without payload values
        // within the five minutes a real event is still older than the clamped one ...
        Assert.Equal("skipped", StatusOf((await Post(c, Request(f, Body("e-now", "order.accepted", received, Order("MP-1"))))).Json));
        // ... but not for ever: ten minutes later a real event applies
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("applied", StatusOf((await Post(c, Request(f, Body("e-later", "order.accepted", f.Clock.GetUtcNow(), Order("MP-1"))))).Json));
        Assert.Equal(f.Clock.GetUtcNow(), GroupRow(f, "MP-1", "status").LastAppliedAt);
    }

    [Fact]
    public async Task A_parked_future_dated_event_keeps_the_ordering_time_it_was_given_when_it_was_received()
    {
        var h = new ScriptedHandler("order.shipped");
        using var f = Factory(h);
        var received = f.Clock.GetUtcNow();
        await Post(f.CreateClient(), Request(f, Body("p-future", "order.shipped", new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), Order("MP-1"))));
        f.Clock.Advance(TimeSpan.FromDays(3));                                                        // replayed much later: the ceiling must not move with the clock
        h.KnownOrders.Add("MP-1");
        using var scope = f.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));
        Assert.Equal(received.AddMinutes(5), WithDb(f, db => db.PortalInboundEvents.Single().OrderingAt));
        Assert.Equal(received.AddMinutes(5), GroupRow(f, "MP-1", "status").LastAppliedAt);
    }

    // ---------- body cap without a Content-Length ----------
    [Fact]
    public async Task The_body_cap_also_holds_for_chunked_requests_without_a_content_length()
    {
        const int cap = 262144;
        using var f = Factory();
        var c = f.CreateClient();
        var baseLen = Encoding.UTF8.GetByteCount(Body("chunkA", "big.type", T0, new { pad = "" }));
        var atCap = Body("chunkA", "big.type", T0, new { pad = new string('x', cap - baseLen) });
        var atCapRequest = Request(f, atCap, chunked: true);
        Assert.Null(atCapRequest.Content!.Headers.ContentLength);                                      // really chunked
        Assert.Equal(HttpStatusCode.OK, (await Post(c, atCapRequest)).Status);
        var over = Body("chunkB", "big.type", T0, new { pad = new string('x', cap - baseLen + 1) });
        Assert.Equal(cap + 1, Encoding.UTF8.GetByteCount(over));
        var res = await Post(c, Request(f, over, chunked: true));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.Status);                                // NB: the in-process test server declares a length, so this alone does not prove the in-read cap; the real-socket test does (ReplayFollowUpTests)
        Assert.Equal("too_large", CodeOf(res.Json));
        var huge = Body("chunkC", "big.type", T0, new { pad = new string('x', 10 * 1024 * 1024) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(c, Request(f, huge, chunked: true))).Status);
        Assert.Equal(1, Rows(f));
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
    public async Task Retention_purges_payloads_then_rows_and_a_parked_event_expires_after_30_days()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        await Post(c, Request(f, Body("old-stored", "x.y", T0, new { text = "hello" })));
        await Post(c, Request(f, Body("old-parked", "message.sent", T0, new { text = "call me on jane.doe@example.com" })));
        WithDb(f, db => { var e = db.PortalInboundEvents.Single(x => x.Id == "old-parked"); e.Status = InboundStatus.Parked; e.AggregateType = "order"; e.AggregateKey = "MP-1"; return db.SaveChanges(); });
        f.Clock.Advance(TimeSpan.FromDays(29));
        await Post(c, Request(f, Body("recent", "x.y", f.Clock.GetUtcNow(), new { text = "fresh" })));

        async Task<PurgeResult> Purge() { using var s = f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync(); }
        var day29 = await Purge();
        Assert.Equal((0, 0, 0), (day29.PayloadsPurged, day29.RowsDeleted, day29.ParkedExpired));
        Assert.Equal(1, day29.ParkedAging);                        // parked for 29 days: reported, not yet expired
        Assert.NotNull(WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "old-parked").PayloadJson));
        f.Clock.Advance(TimeSpan.FromDays(2));                     // 31 days after the old rows, 2 after "recent"
        var day31 = await Purge();
        Assert.Equal((1, 0, 1), (day31.PayloadsPurged, day31.RowsDeleted, day31.ParkedExpired));   // old-stored's payload dropped; the parked one expired
        var rows = WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToDictionary(e => e.Id));
        Assert.Null(rows["old-stored"].PayloadJson);
        Assert.NotNull(rows["old-stored"].PayloadPurgedAt);
        Assert.Equal(InboundStatus.Skipped, rows["old-parked"].Status);
        Assert.Null(rows["old-parked"].PayloadJson);               // the message text is gone with it
        Assert.NotNull(rows["old-parked"].PayloadPurgedAt);
        Assert.Contains("expired", rows["old-parked"].Note);
        Assert.NotNull(rows["recent"].PayloadJson);
        f.Clock.Advance(TimeSpan.FromDays(60));                    // 91 days after the old rows
        var day91 = await Purge();
        Assert.Equal(2, day91.RowsDeleted);                        // both old id rows are gone after 90 days (the expired one included)
        Assert.Equal(["recent"], WithDb(f, db => db.PortalInboundEvents.Select(e => e.Id).ToList()));
    }

    [Fact]
    public async Task A_parked_event_that_has_not_yet_expired_keeps_its_payload_and_can_still_be_applied()
    {
        var h = new ScriptedHandler("order.shipped");
        using var f = Factory(h);
        await Post(f.CreateClient(), Request(f, Body("p1", "order.shipped", T0, Order("MP-1"))));
        f.Clock.Advance(TimeSpan.FromDays(29));
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync();
        h.KnownOrders.Add("MP-1");
        using (var scope = f.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1"));
        Assert.Equal(InboundStatus.Applied, WithDb(f, db => db.PortalInboundEvents.Single().Status));
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

