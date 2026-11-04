using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal.Webhooks;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Cancels a token right after the n-th successful SaveChanges: the point between "saved" and "committed" that a client disconnect can hit.</summary>
public sealed class CancelAfterSave : SaveChangesInterceptor
{
    private CancellationTokenSource? _cts;
    private int _left;
    public void Arm(CancellationTokenSource cts, int saves) { _cts = cts; _left = saves; }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (_cts is not null && Interlocked.Decrement(ref _left) == 0) _cts.Cancel();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Runs an action just before the first UPDATE of the inbound-events table that touches the version (the replay's claim): another replayer getting there first.</summary>
public sealed class BeforeClaim : DbCommandInterceptor
{
    private Action? _action;
    public void Arm(Action action) => _action = action;

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var text = command.CommandText;
        if (_action is { } a && text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && text.Contains("portal_inbound_events", StringComparison.OrdinalIgnoreCase) && text.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            _action = null;     // once
            a();
        }
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Follow-ups of the orchestrator's review of 02c0e6a (review 4): real-socket chunked cap, deterministic claim race, one bad event, commit after cancel, expiry boundary.</summary>
public class ReplayFollowUpTests
{
    private static TestFactory Factory(ScriptedHandler h, Action<IServiceCollection>? more = null)
    {
        var f = new TestFactory(null, s => { s.AddSingleton<IProjectionHandler>(h); more?.Invoke(s); });
        f.Clock.Advance(TimeSpan.FromHours(3));
        return f;
    }

    private static int Markers(TestFactory f) => WithDb(f, db => db.ProjectionStates.Count(s => s.AggregateType == "marker"));

    private static async Task<int> Replay(TestFactory f, CancellationToken ct = default)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-1", ct);
    }

    // ---------- (b) the in-read body cap, over a real socket ----------

    [Fact]
    public async Task The_body_cap_holds_for_a_really_chunked_request_over_a_real_socket()
    {
        const int cap = 262144;
        using var f = new TestFactory();
        f.UseKestrel();                                   // a real Kestrel server: the in-process test server adds a Content-Length, a real chunked body has none
        f.StartServer();
        var c = f.CreateClient();
        Assert.Equal("http", c.BaseAddress!.Scheme);
        var baseLen = Encoding.UTF8.GetByteCount(Body("chunkA", "big.type", T0, new { pad = "" }));
        var atCap = Request(f, Body("chunkA", "big.type", T0.AddHours(-1), new { pad = new string('x', cap - baseLen) }), chunked: true);
        Assert.Null(atCap.Content!.Headers.ContentLength);                                            // sent as Transfer-Encoding: chunked
        Assert.Equal(HttpStatusCode.OK, (await Post(c, atCap)).Status);
        var over = Body("chunkB", "big.type", T0.AddHours(-1), new { pad = new string('x', cap - baseLen + 1) });
        Assert.Equal(cap + 1, Encoding.UTF8.GetByteCount(over));
        var res = await Post(c, Request(f, over, chunked: true));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.Status);                              // one byte over: only the in-read cap can see it
        Assert.Contains("too_large", res.Json);
        Assert.Equal(1, WithDb(f, db => db.PortalInboundEvents.Count()));
    }

    // ---------- (c) the claim, deterministically ----------

    [Fact]
    public async Task A_replay_whose_claim_is_taken_by_another_replayer_applies_nothing_and_leaves_the_event_parked()
    {
        var known = false;
        var h = new ScriptedHandler("order.shipped")
        {
            Script = c =>
            {
                if (!Volatile.Read(ref known)) return Task.FromResult(HandlerOutcome.Parked("order", "MP-1"));
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = c.Event.Id, FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                return Task.FromResult(HandlerOutcome.Applied());
            },
        };
        var race = new BeforeClaim();
        using var f = Factory(h, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(race)));
        await Post(f.CreateClient(), Request(f, Body("p1", "order.shipped", T0, Order("MP-1"))));
        Assert.Equal(1, h.Calls);                                         // the delivery itself, which parked
        Volatile.Write(ref known, true);
        // our replay has read the event and its version; before it can claim, someone else claims it (the version moves on, the status is still parked)
        race.Arm(() => WithDb(f, db => db.PortalInboundEvents.Where(e => e.Id == "p1").ExecuteUpdate(s => s.SetProperty(e => e.Version, e => e.Version + 1))));
        Assert.Equal(0, await Replay(f));
        Assert.Equal(1, h.Calls);                                         // the handler never ran for the replay
        Assert.Equal(0, Markers(f));
        Assert.Equal(InboundStatus.Parked, WithDb(f, db => db.PortalInboundEvents.Single().Status));
        Assert.Equal(1, await Replay(f));                                 // the lost claim blocks nothing: the next pass applies it
        Assert.Equal(1, Markers(f));
    }

    // ---------- (d) one bad event must not stop the rest ----------

    [Fact]
    public async Task A_handler_that_throws_leaves_its_event_parked_and_the_rest_of_the_replay_goes_on()
    {
        var known = false;
        var h = new ScriptedHandler("order.shipped", "order.delivered")
        {
            Script = c =>
            {
                if (!Volatile.Read(ref known)) return Task.FromResult(HandlerOutcome.Parked("order", "MP-1"));
                if (c.Event.Type == "order.shipped") throw new InvalidOperationException("boom: secret text for jane@example.com");
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = c.Event.Id, FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                return Task.FromResult(HandlerOutcome.Applied());
            },
        };
        using var f = Factory(h);
        var c = f.CreateClient();
        await Post(c, Request(f, Body("poison", "order.shipped", T0, Order("MP-1"))));              // older: replayed first
        await Post(c, Request(f, Body("good", "order.delivered", T0.AddMinutes(5), Order("MP-1"))));
        Volatile.Write(ref known, true);
        Assert.Equal(1, await Replay(f));                                                            // the newer one is applied although the older one threw
        var rows = WithDb(f, db => db.PortalInboundEvents.AsNoTracking().ToDictionary(e => e.Id));
        Assert.Equal(InboundStatus.Applied, rows["good"].Status);
        Assert.Equal(InboundStatus.Parked, rows["poison"].Status);
        Assert.NotNull(rows["poison"].PayloadJson);                                                  // still there for the next pass
        Assert.Equal(f.Clock.GetUtcNow(), rows["poison"].LastReplayAt);                              // the sweep stamp is written, so it serves other aggregates first
        Assert.Equal(1, Markers(f));
        var logs = string.Join("\n", f.Logs.Lines);
        Assert.Contains("InvalidOperationException", logs);
        Assert.DoesNotContain("jane@example.com", logs);                                             // the type only, never the message
        Assert.DoesNotContain("secret text", logs);
    }

    [Fact]
    public async Task The_sweep_survives_a_handler_that_always_throws()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => throw new InvalidOperationException("always") };
        using var f = Factory(h);
        WithDb(f, db =>
        {
            db.PortalInboundEvents.Add(new PortalInboundEvent
            {
                Id = "p", Type = "order.shipped", PortalCreatedAt = T0, OrderingAt = T0, ReceivedAt = f.Clock.GetUtcNow(), Status = InboundStatus.Parked,
                AggregateType = "order", AggregateKey = "MP-1", PayloadJson = Body("p", "order.shipped", T0, Order("MP-1")),
            });
            return db.SaveChanges();
        });
        using var s = f.Services.CreateScope();
        Assert.Equal(0, await s.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayAllParkedAsync());   // no exception
        Assert.Equal(InboundStatus.Parked, WithDb(f, db => db.PortalInboundEvents.Single().Status));
    }

    [Fact]
    public async Task A_cancelled_replay_stops_and_does_not_swallow_the_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var known = false;
        var h = new ScriptedHandler("order.shipped")
        {
            Script = c =>
            {
                if (!Volatile.Read(ref known)) return Task.FromResult(HandlerOutcome.Parked("order", "MP-1"));
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        using var f = Factory(h);
        await Post(f.CreateClient(), Request(f, Body("p1", "order.shipped", T0, Order("MP-1"))));
        Volatile.Write(ref known, true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Replay(f, cts.Token));
        Assert.Equal(InboundStatus.Parked, WithDb(f, db => db.PortalInboundEvents.Single().Status));   // nothing half-applied
    }

    // ---------- (e) a disconnect between "saved" and "committed" must not undo the work ----------

    [Fact]
    public async Task A_request_cancelled_after_the_final_save_still_commits_the_event_and_its_changes()
    {
        var h = new ScriptedHandler("order.shipped");
        h.KnownOrders.Add("MP-1");
        var cancel = new CancelAfterSave();
        using var f = Factory(h, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(cancel)));
        using var cts = new CancellationTokenSource();
        var body = Body("late-cancel", "order.shipped", T0, Order("MP-1"));
        Assert.True(PortalEvent.TryParse(Encoding.UTF8.GetBytes(body), out var evt, out _));
        cancel.Arm(cts, saves: 2);                                        // save 1 inserts the event row, save 2 is the final one; cancel right after it
        using (var scope = f.Services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ProcessAsync(evt!, body, cts.Token);
            Assert.Equal(ProcessOutcome.Applied, outcome);
        }
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(InboundStatus.Applied, WithDb(f, db => db.PortalInboundEvents.Single(e => e.Id == "late-cancel").Status));   // committed, not rolled back
        Assert.Equal(1, Markers(f));
    }

    [Fact]
    public async Task A_replay_cancelled_after_its_save_still_commits()
    {
        var known = false;
        var h = new ScriptedHandler("order.shipped")
        {
            Script = c =>
            {
                if (!Volatile.Read(ref known)) return Task.FromResult(HandlerOutcome.Parked("order", "MP-1"));
                c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = c.Event.Id, FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
                return Task.FromResult(HandlerOutcome.Applied());
            },
        };
        var cancel = new CancelAfterSave();
        using var f = Factory(h, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(cancel)));
        await Post(f.CreateClient(), Request(f, Body("p1", "order.shipped", T0, Order("MP-1"))));
        Volatile.Write(ref known, true);
        using var cts = new CancellationTokenSource();
        cancel.Arm(cts, saves: 1);                                        // the replay saves once; cancel right after it
        // The caller may be told about the cancellation (the stamp that follows the loop honours the token), but what was committed stays committed.
        try { await Replay(f, cts.Token); } catch (OperationCanceledException) { }
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(InboundStatus.Applied, WithDb(f, db => db.PortalInboundEvents.Single().Status));
        Assert.Equal(1, Markers(f));
    }

    // ---------- (e) the 30-day parked expiry, exactly ----------

    [Fact]
    public async Task A_parked_event_expires_strictly_after_30_days()
    {
        using var f = new TestFactory();
        var received = f.Clock.GetUtcNow();
        WithDb(f, db =>
        {
            db.PortalInboundEvents.Add(new PortalInboundEvent
            {
                Id = "parked", Type = "message.sent", PortalCreatedAt = received, OrderingAt = received, ReceivedAt = received, Status = InboundStatus.Parked,
                AggregateType = "order", AggregateKey = "MP-1", PayloadJson = Body("parked", "message.sent", received, new { text = "hello" }),
            });
            return db.SaveChanges();
        });
        async Task<PurgeResult> Purge() { using var s = f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync(); }
        PortalInboundEvent Row() => WithDb(f, db => db.PortalInboundEvents.AsNoTracking().Single());

        f.Clock.Advance(TimeSpan.FromDays(29) + TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, (await Purge()).ParkedExpired);                   // 29 days 23:59:59: still parked, payload kept
        Assert.Equal((InboundStatus.Parked, true), (Row().Status, Row().PayloadJson is not null));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await Purge()).ParkedExpired);                   // exactly 30 days: not yet (strictly older than 30 days expires)
        Assert.Equal(InboundStatus.Parked, Row().Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await Purge()).ParkedExpired);                   // 30 days and one second
        Assert.Equal((InboundStatus.Skipped, true), (Row().Status, Row().PayloadJson is null));
    }
}
