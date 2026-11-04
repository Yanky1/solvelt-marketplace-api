using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Scripted handler for the type "test.msg": each call is answered by the next function (the last one repeats).</summary>
public sealed class ScriptedOutboxHandler : IOutboxHandler
{
    private readonly List<Func<OutboxMessage, Task<OutboxResult>>> _script = [];
    private int _calls;
    public List<string> Seen { get; } = [];
    public int Calls => _calls;
    public IReadOnlyCollection<string> Types { get; } = ["test.msg"];

    public ScriptedOutboxHandler Then(Func<OutboxMessage, Task<OutboxResult>> f) { _script.Add(f); return this; }
    public ScriptedOutboxHandler Then(OutboxResult r) => Then(_ => Task.FromResult(r));

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls) - 1;
        lock (Seen) Seen.Add(message.Payload ?? "");
        var f = _script[Math.Min(n, _script.Count - 1)];
        return await f(message);
    }
}

public class OutboxTests
{
    private static TestFactory With(ScriptedOutboxHandler h, Dictionary<string, string?>? config = null)
        => new(config, s => s.AddScoped<IOutboxHandler>(_ => h));

    private static async Task<Guid> Enqueue(TestFactory f, string type = "test.msg", object? payload = null, string? key = null, TimeSpan? delay = null)
    {
        using var scope = f.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<OutboxWriter>().Enqueue(type, payload ?? new { n = 1 }, key, delay);
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        return m.Id;
    }

    private static async Task<int> Dispatch(TestFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
    }

    private static async Task<OutboxMessage> Load(TestFactory f, Guid id)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id);
    }

    [Fact]
    public async Task A_due_message_is_delivered_once()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = With(h);
        var id = await Enqueue(f);
        Assert.Equal(1, await Dispatch(f));
        Assert.Equal(0, await Dispatch(f));
        var m = await Load(f, id);
        Assert.Equal("delivered", m.Status);
        Assert.Equal(1, m.Attempts);
        Assert.Equal(f.Clock.GetUtcNow(), m.DeliveredAt);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task A_message_that_is_not_due_yet_waits()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = With(h);
        var id = await Enqueue(f, delay: TimeSpan.FromSeconds(60));
        f.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await Dispatch(f));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Dispatch(f));
        Assert.Equal("delivered", (await Load(f, id)).Status);
    }

    [Fact]
    public async Task Failures_back_off_30_seconds_doubling_and_die_at_the_eighth()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Retry("boom"));
        using var f = With(h);
        var id = await Enqueue(f);
        var expected = new[] { 30, 60, 120, 240, 480, 960, 1920 };
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var before = f.Clock.GetUtcNow();
            Assert.Equal(1, await Dispatch(f));
            var m = await Load(f, id);
            Assert.Equal("pending", m.Status);
            Assert.Equal(attempt, m.Attempts);
            Assert.Equal(before.AddSeconds(expected[attempt - 1]), m.NextAttemptAt);
            Assert.Equal("boom", m.LastError);
            f.Clock.Advance(TimeSpan.FromSeconds(expected[attempt - 1] - 1));
            Assert.Equal(0, await Dispatch(f));   // one second early: still waiting
            f.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(1, await Dispatch(f));
        var dead = await Load(f, id);
        Assert.Equal("dead", dead.Status);
        Assert.Equal(8, dead.Attempts);
        Assert.Equal(f.Clock.GetUtcNow(), dead.DeadAt);
        Assert.Equal(0, await Dispatch(f));
        Assert.Equal(8, h.Calls);
    }

    [Fact]
    public async Task Backoff_is_capped_at_an_hour()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Retry("boom"));
        using var f = With(h, new() { ["Outbox:MaxAttempts"] = "20" });
        var id = await Enqueue(f);
        for (var i = 0; i < 7; i++) { await Dispatch(f); f.Clock.Advance(TimeSpan.FromHours(2)); }
        var before = f.Clock.GetUtcNow();
        await Dispatch(f);   // the 8th failure: 30 x 2^7 = 3840, capped
        Assert.Equal(before.AddSeconds(3600), (await Load(f, id)).NextAttemptAt);
    }

    [Fact]
    public async Task Retry_after_can_lengthen_the_wait_but_never_shorten_it_or_pass_the_cap()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Retry("a", TimeSpan.FromSeconds(5))).Then(OutboxResult.Retry("b", TimeSpan.FromSeconds(500))).Then(OutboxResult.Retry("c", TimeSpan.FromDays(3)));
        using var f = With(h);
        var id = await Enqueue(f);
        var t = f.Clock.GetUtcNow();
        await Dispatch(f);
        Assert.Equal(t.AddSeconds(30), (await Load(f, id)).NextAttemptAt);      // 5 s asked, 30 s backoff stands
        f.Clock.Advance(TimeSpan.FromSeconds(30)); t = f.Clock.GetUtcNow();
        await Dispatch(f);
        Assert.Equal(t.AddSeconds(500), (await Load(f, id)).NextAttemptAt);     // 500 s > 60 s backoff
        f.Clock.Advance(TimeSpan.FromSeconds(500)); t = f.Clock.GetUtcNow();
        await Dispatch(f);
        Assert.Equal(t.AddSeconds(3600), (await Load(f, id)).NextAttemptAt);    // capped
    }

    [Fact]
    public async Task A_handler_can_dead_letter_at_once()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Dead("nope"));
        using var f = With(h);
        var id = await Enqueue(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("dead", m.Status);
        Assert.Equal(1, m.Attempts);
        Assert.Equal("nope", m.LastError);
        Assert.NotNull(m.DeadAt);
    }

    [Fact]
    public async Task A_throwing_handler_is_retried_and_only_the_exception_type_is_stored()
    {
        var h = new ScriptedOutboxHandler().Then(_ => throw new InvalidOperationException("secret-token-123 for jane@example.com"));
        using var f = With(h);
        var id = await Enqueue(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("pending", m.Status);
        Assert.Equal("InvalidOperationException", m.LastError);
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("secret-token-123") || l.Contains("jane@example.com"));
    }

    [Fact]
    public async Task An_unknown_type_is_retried_not_lost()
    {
        using var f = With(new ScriptedOutboxHandler().Then(OutboxResult.Delivered()));
        var id = await Enqueue(f, type: "no.such.type");
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("pending", m.Status);
        Assert.Equal("no_handler", m.LastError);
    }

    [Fact]
    public async Task Messages_are_delivered_oldest_first()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = With(h);
        await Enqueue(f, payload: new { n = "first" });
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Enqueue(f, payload: new { n = "second" });
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Enqueue(f, payload: new { n = "third" });
        await Dispatch(f);
        Assert.Equal(["{\"n\":\"first\"}", "{\"n\":\"second\"}", "{\"n\":\"third\"}"], h.Seen);
    }

    [Fact]
    public async Task Concurrent_dispatchers_deliver_each_message_exactly_once()
    {
        var h = new ScriptedOutboxHandler().Then(async _ => { await Task.Delay(50); return OutboxResult.Delivered(); });
        using var f = With(h);
        for (var i = 0; i < 12; i++) await Enqueue(f, payload: new { n = i });
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Dispatch(f))));
        Assert.Equal(12, h.Calls);
        Assert.Equal(12, h.Seen.Distinct().Count());
        using var scope = f.Services.CreateScope();
        Assert.Equal(12, await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.CountAsync(m => m.Status == "delivered" && m.Attempts == 1));
    }

    [Fact]
    public async Task A_live_lease_blocks_other_dispatchers_and_an_expired_one_is_taken_over()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = With(h);
        var id = await Enqueue(f);
        // Simulate a dispatcher that claimed the message and then died: a lease 120 s ahead, version bumped.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var until = f.Clock.GetUtcNow().AddSeconds(120);
            await db.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, (DateTimeOffset?)until).SetProperty(m => m.Version, 1));
        }
        f.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await Dispatch(f));
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await Dispatch(f));
        Assert.Equal("delivered", (await Load(f, id)).Status);
    }

    [Fact]
    public async Task A_result_that_arrives_after_the_lease_was_lost_is_ignored()
    {
        TestFactory? factory = null;
        int early = -1, takeover = -1;   // asserted after the dispatch: an assertion inside a handler would be swallowed as a handler failure
        var h = new ScriptedOutboxHandler();
        // First call: stalls past its lease, lets a second dispatcher take over and deliver, then reports a failure.
        h.Then(async _ =>
        {
            factory!.Clock.Advance(TimeSpan.FromSeconds(119));
            early = await Dispatch(factory);          // the 120 s lease still protects the message
            factory.Clock.Advance(TimeSpan.FromSeconds(2));
            takeover = await Dispatch(factory);
            return OutboxResult.Retry("late");
        }).Then(OutboxResult.Delivered());
        using var f = With(h);
        factory = f;
        var id = await Enqueue(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal((0, 1), (early, takeover));
        Assert.Equal(2, h.Calls);
        Assert.Equal("delivered", m.Status);
        Assert.Equal(1, m.Attempts);
        Assert.Null(m.LastError);
    }

    [Fact]
    public async Task A_late_delivered_result_cannot_overwrite_a_newer_claims_outcome()
    {
        TestFactory? factory = null;
        var h = new ScriptedOutboxHandler();
        h.Then(async _ =>
        {
            factory!.Clock.Advance(TimeSpan.FromSeconds(121));
            await Dispatch(factory);                   // the newer claim fails and schedules a retry
            return OutboxResult.Delivered();           // the stale claim says delivered
        }).Then(OutboxResult.Retry("newer"));
        using var f = With(h);
        factory = f;
        var id = await Enqueue(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal(("pending", 1, "newer"), (m.Status, m.Attempts, m.LastError));
        Assert.Null(m.DeliveredAt);
    }

    [Fact]
    public async Task The_same_dedupe_key_cannot_be_queued_twice()
    {
        using var f = With(new ScriptedOutboxHandler().Then(OutboxResult.Delivered()));
        await Enqueue(f, key: "k1");
        await Assert.ThrowsAsync<DbUpdateException>(() => Enqueue(f, key: "k1"));
        using var scope = f.Services.CreateScope();
        var w = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        Assert.True(await w.ExistsAsync("k1"));
        Assert.False(await w.ExistsAsync("k2"));
    }

    [Fact]
    public async Task Enqueue_saves_nothing_by_itself_and_commits_or_rolls_back_with_the_callers_unit()
    {
        using var f = With(new ScriptedOutboxHandler().Then(OutboxResult.Delivered()));
        await Enqueue(f, key: "taken");
        using (var scope = f.Services.CreateScope())
        {
            var w = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
            w.Enqueue("test.msg", new { n = 1 }, "unsaved");
        }   // never saved
        using (var scope = f.Services.CreateScope())
        {
            var w = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
            w.Enqueue("test.msg", new { n = 2 }, "rolled-back");
            w.Enqueue("test.msg", new { n = 3 }, "taken");   // violates the unique key: the whole save fails
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync());
        }
        using var check = f.Services.CreateScope();
        var keys = await check.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.Select(m => m.DedupeKey).ToListAsync();
        Assert.Equal(["taken"], keys);
    }

    // ---- email ----

    [Fact]
    public async Task An_email_is_sent_through_the_sender_and_the_sink_keeps_it()
    {
        using var f = new TestFactory();
        using (var scope = f.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueueEmail(new EmailMessage("jane@example.com", "Hello", "Body text"));
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        Assert.Equal(1, await Dispatch(f));
        var sink = (DevEmailSink)f.Services.GetRequiredService<IEmailSender>();
        var sent = Assert.Single(sink.Sent);
        Assert.Equal(("jane@example.com", "Hello", "Body text"), (sent.To, sent.Subject, sent.TextBody));
    }

    [Fact]
    public async Task A_failing_email_provider_leaves_the_mail_pending_and_stores_only_the_exception_type()
    {
        using var f = new TestFactory(services: s => s.AddSingleton<IEmailSender, FailingEmailSender>());
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            id = scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueueEmail(new EmailMessage("jane@example.com", "Hello", "Body")).Id;
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal(("pending", "InvalidOperationException"), (m.Status, m.LastError));
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("jane@example.com"));
    }

    [Fact]
    public async Task An_email_send_that_never_answers_is_cut_off_inside_the_lease_and_retried()
    {
        using var f = new TestFactory(new() { ["Email:SendTimeoutSeconds"] = "1" }, s => s.AddSingleton<IEmailSender, HangingEmailSender>());
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            id = scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueueEmail(new EmailMessage("jane@example.com", "Hello", "Body")).Id;
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Dispatch(f);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 10);
        var m = await Load(f, id);
        Assert.Equal(("pending", "TimeoutException"), (m.Status, m.LastError));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"to\":\"\",\"subject\":\"s\",\"textBody\":\"b\"}")]
    public async Task A_malformed_email_payload_is_dead_lettered(string payload)
    {
        using var f = new TestFactory();
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var m = scope.ServiceProvider.GetRequiredService<OutboxWriter>().Enqueue("email.send", new { });
            m.Payload = payload;
            id = m.Id;
            await db.SaveChangesAsync();
        }
        await Dispatch(f);
        var row = await Load(f, id);
        Assert.Equal("dead", row.Status);
        Assert.Equal("invalid_payload", row.LastError);
    }

    // ---- payment events ----

    private static async Task<Guid> EnqueuePayment(TestFactory f, string eventId = "evt-1")
    {
        using var scope = f.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueuePaymentEvent(eventId, PaymentEventTypes.Captured,
            new PaymentCapturedData(PaymentKinds.Order, "MP-1", null, "SO-1", "pi_123", "usd", 1000));
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        return m.Id;
    }

    [Fact]
    public async Task A_payment_event_is_signed_and_sent_to_the_portal_with_our_event_id()
    {
        using var f = new TestFactory();
        f.Portal.Respond(HttpStatusCode.OK, "{\"status\":\"recorded\"}");
        var id = await EnqueuePayment(f);
        await Dispatch(f);
        Assert.Equal("delivered", (await Load(f, id)).Status);
        var call = Assert.Single(f.Portal.VerifiedCalls);
        Assert.Equal("/api/internal/v1/payment-events", call.PathAndQuery);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("evt-1", body.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("payment.captured", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("pi_123", body.RootElement.GetProperty("data").GetProperty("paymentRef").GetString());
        Assert.Equal("SO-1", body.RootElement.GetProperty("data").GetProperty("sellerOrderNumber").GetString());
    }

    [Fact]
    public async Task A_payment_event_is_queued_once_per_event_id()
    {
        using var f = new TestFactory();
        await EnqueuePayment(f, "evt-dup");
        await Assert.ThrowsAsync<DbUpdateException>(() => EnqueuePayment(f, "evt-dup"));
        using var scope = f.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<OutboxWriter>().ExistsAsync("payment:evt-dup"));
    }

    [Fact]
    public async Task A_payment_event_the_portal_refuses_for_good_is_dead_lettered_with_its_code()
    {
        using var f = new TestFactory();
        for (var i = 0; i < 3; i++) f.Portal.Respond(HttpStatusCode.UnprocessableEntity, "{\"code\":\"exceeds_sale\",\"message\":\"x\"}");
        var id = await EnqueuePayment(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("dead", m.Status);
        Assert.Equal("portal_422_exceeds_sale", m.LastError);
        Assert.Single(f.Portal.VerifiedCalls);   // a 422 is never re-sent
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    public async Task A_payment_event_the_portal_cannot_take_now_is_retried_later(int status)
    {
        using var f = new TestFactory();
        for (var i = 0; i < 30; i++) f.Portal.Respond((HttpStatusCode)status, "{\"code\":\"unavailable\"}");
        var id = await EnqueuePayment(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("pending", m.Status);
        Assert.Equal($"portal_{status}", m.LastError);
    }

    [Fact]
    public async Task An_unreachable_portal_means_retry()
    {
        using var f = new TestFactory();
        for (var i = 0; i < 30; i++) f.Portal.RespondTransportError();
        var id = await EnqueuePayment(f);
        await Dispatch(f);
        var m = await Load(f, id);
        Assert.Equal("pending", m.Status);
        Assert.Equal("portal_unavailable", m.LastError);
    }

    [Theory]
    [InlineData(200, false, OutboxOutcome.Delivered)]
    [InlineData(201, false, OutboxOutcome.Delivered)]
    [InlineData(404, true, OutboxOutcome.Retry)]
    [InlineData(409, true, OutboxOutcome.Retry)]
    [InlineData(401, false, OutboxOutcome.Dead)]
    [InlineData(401, true, OutboxOutcome.Dead)]
    [InlineData(413, true, OutboxOutcome.Dead)]
    [InlineData(408, false, OutboxOutcome.Retry)]
    [InlineData(429, false, OutboxOutcome.Retry)]
    [InlineData(500, false, OutboxOutcome.Retry)]
    [InlineData(503, false, OutboxOutcome.Retry)]
    [InlineData(400, false, OutboxOutcome.Dead)]
    [InlineData(404, false, OutboxOutcome.Dead)]
    [InlineData(413, false, OutboxOutcome.Dead)]
    [InlineData(422, false, OutboxOutcome.Dead)]
    public void Portal_answers_map_to_outbox_outcomes(int status, bool retryable, OutboxOutcome expected)
        => Assert.Equal(expected, PortalOutcome.From(new PortalResult<object>(status, null, new PortalError("c", "m"), 1) { Retryable = retryable }).Outcome);

    // ---- maintenance ----

    [Fact]
    public async Task Maintenance_purges_payloads_and_then_rows_on_schedule()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = With(h);
        var delivered = await Enqueue(f, key: "d");
        await Dispatch(f);
        var deadId = await Enqueue(f, key: "x");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var now = f.Clock.GetUtcNow();
            await db.OutboxMessages.Where(m => m.Id == deadId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "dead").SetProperty(m => m.DeadAt, (DateTimeOffset?)now));
        }
        async Task<OutboxMaintenance.Result> Purge() { using var s = f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<OutboxMaintenance>().PurgeAsync(); }

        f.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(new OutboxMaintenance.Result(0, 0), await Purge());
        Assert.NotNull((await Load(f, delivered)).Payload);
        f.Clock.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(new OutboxMaintenance.Result(1, 0), await Purge());   // delivered payload gone after a day
        var m1 = await Load(f, delivered);
        Assert.Null(m1.Payload);
        Assert.NotNull(m1.PayloadPurgedAt);
        Assert.NotNull((await Load(f, deadId)).Payload);                  // dead payload kept for a person to retry

        f.Clock.Advance(TimeSpan.FromDays(28));                            // 29.0 days after the dead-lettering
        Assert.Equal(new OutboxMaintenance.Result(0, 0), await Purge());
        f.Clock.Advance(TimeSpan.FromDays(1.5));                           // 30.5 days
        Assert.Equal(new OutboxMaintenance.Result(1, 0), await Purge());   // dead payload gone after 30 days
        Assert.Null((await Load(f, deadId)).Payload);

        f.Clock.Advance(TimeSpan.FromDays(60));                            // 90.5 days
        Assert.Equal(new OutboxMaintenance.Result(0, 2), await Purge());   // rows go after 90 days
    }

    [Fact]
    public async Task Maintenance_never_touches_pending_messages()
    {
        using var f = With(new ScriptedOutboxHandler().Then(OutboxResult.Retry("x")));
        var id = await Enqueue(f, delay: TimeSpan.FromDays(400));
        f.Clock.Advance(TimeSpan.FromDays(300));
        using var scope = f.Services.CreateScope();
        Assert.Equal(new OutboxMaintenance.Result(0, 0), await scope.ServiceProvider.GetRequiredService<OutboxMaintenance>().PurgeAsync());
        Assert.NotNull((await Load(f, id)).Payload);
    }

    // ---- admin ----

    private static HttpRequestMessage Req(HttpMethod m, string url, string? key = DevDefaults.AdminApiKey)
    {
        var r = new HttpRequestMessage(m, url);
        if (key is not null) r.Headers.Add(OutboxAdmin.KeyHeader, key);
        return r;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("dev-only-admin-api-key-not-for-production ")]
    [InlineData("dev-only-admin")]                                        // a prefix of the key
    [InlineData("dev-only-admin-api-key-not-for-production-x")]            // the key plus one character
    [InlineData("DEV-ONLY-ADMIN-API-KEY-NOT-FOR-PRODUCTION")]              // another case
    public async Task Admin_endpoints_answer_404_to_a_missing_or_wrong_key(string? key)
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", key))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{Guid.NewGuid()}/retry", key))).StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_are_disabled_when_no_key_is_configured()
    {
        using var f = new TestFactory(new() { ["Admin:ApiKey"] = "" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", ""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", DevDefaults.AdminApiKey))).StatusCode);
    }

    [Fact]
    public async Task Admin_list_shows_counts_and_never_a_payload()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered()).Then(OutboxResult.Dead("nope"));
        using var f = With(h);
        await Enqueue(f, payload: new { secret = "payload-secret-value" });
        await Dispatch(f);
        await Enqueue(f, payload: new { secret = "payload-secret-value" }); await Dispatch(f);
        await Enqueue(f, payload: new { secret = "payload-secret-value" });
        var res = await f.CreateClient().SendAsync(Req(HttpMethod.Get, "/api/admin/outbox"));
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("payload-secret-value", text);
        Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text);
        var counts = doc.RootElement.GetProperty("counts");
        Assert.Equal((1, 1, 1), (counts.GetProperty("pending").GetInt32(), counts.GetProperty("delivered").GetInt32(), counts.GetProperty("dead").GetInt32()));
        Assert.Equal(3, doc.RootElement.GetProperty("messages").GetArrayLength());

        var dead = await f.CreateClient().SendAsync(Req(HttpMethod.Get, "/api/admin/outbox?status=DEAD"));
        using var deadDoc = JsonDocument.Parse(await dead.Content.ReadAsStringAsync());
        var only = Assert.Single(deadDoc.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("dead", only.GetProperty("status").GetString());
        Assert.Equal("nope", only.GetProperty("lastError").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await f.CreateClient().SendAsync(Req(HttpMethod.Get, "/api/admin/outbox?status=bogus"))).StatusCode);
    }

    [Fact]
    public async Task Admin_retry_revives_a_dead_message_once_and_it_is_delivered_next()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Dead("nope")).Then(OutboxResult.Delivered());
        using var f = With(h);
        var id = await Enqueue(f);
        await Dispatch(f);
        Assert.Equal("dead", (await Load(f, id)).Status);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{id}/retry"))).StatusCode);
        var m = await Load(f, id);
        Assert.Equal(("pending", 0, null), (m.Status, m.Attempts, m.DeadAt));
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{id}/retry"))).StatusCode);   // second click: no longer dead
        Assert.Equal(1, await Dispatch(f));
        Assert.Equal("delivered", (await Load(f, id)).Status);
    }

    [Fact]
    public async Task Admin_retry_refuses_non_dead_unknown_and_purged_messages()
    {
        using var f = With(new ScriptedOutboxHandler().Then(OutboxResult.Delivered()));
        var pending = await Enqueue(f, key: "p");
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{pending}/retry"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{Guid.NewGuid()}/retry"))).StatusCode);
        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.Where(m => m.Id == pending)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "dead").SetProperty(m => m.Payload, (string?)null));
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{pending}/retry"))).StatusCode);   // nothing left to send
    }

    // ---- configuration ----

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40), ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> d)
        => StartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env("Production"));

    [Fact]
    public void The_admin_key_is_optional_in_production_but_held_to_the_same_rules_when_set()
    {
        Assert.Empty(Validate(GoodProd()));
        var ok = GoodProd(); ok["Admin:ApiKey"] = new string('d', 40); ok["Admin:Host"] = "admin.internal:8443";
        Assert.Empty(Validate(ok));
        var shortKey = new Dictionary<string, string?>(ok) { ["Admin:ApiKey"] = "short" };
        Assert.Contains(Validate(shortKey), p => p.Contains("Admin:ApiKey") && p.Contains("32"));
        var dev = new Dictionary<string, string?>(ok) { ["Admin:ApiKey"] = DevDefaults.AdminApiKey };
        Assert.Contains(Validate(dev), p => p.Contains("Admin:ApiKey") && p.Contains("Development default"));
        var reused = new Dictionary<string, string?>(ok) { ["Admin:ApiKey"] = ok["Web:ForwardingKey"] };
        Assert.Contains(Validate(reused), p => p.Contains("must differ"));
    }
}
