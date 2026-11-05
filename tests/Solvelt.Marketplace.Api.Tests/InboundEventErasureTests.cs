using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal.Webhooks;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Decision M1-82 (review of 1725a68, M3-40): the raw bodies in portal_inbound_events of a deleted account's events are dropped at erasure and never kept for an event that arrives later.</summary>
public class InboundEventErasureTests
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a"), B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private static TestFactory Factory(ScriptedHandler h)
    {
        var f = new TestFactory(null, s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IProjectionHandler) && d.ImplementationType == typeof(Solvelt.Marketplace.Api.Fulfilment.FulfilmentHandler)
                                           && h.EventTypes.Intersect(Solvelt.Marketplace.Api.Fulfilment.FulfilmentProjector.OwnTypes).Any()).ToList())
                s.Remove(d);
            s.AddSingleton<IProjectionHandler>(h);
        });
        f.Clock.Advance(TimeSpan.FromHours(3));
        return f;
    }

    private static void Seed(TestFactory f)
    {
        WithDb(f, db =>
        {
            foreach (var id in new[] { A, B })
                db.BuyerAccounts.Add(new BuyerAccount { Id = id, Email = $"{id:N}@example.test", DisplayName = "Jane", RelayEmail = $"relay-{id:N}@relay.test", CreatedAt = T0, UpdatedAt = T0 });
            db.Orders.Add(new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-A", AccountId = A, Currency = "USD", BuyerName = "Jane", RelayEmail = "relay-a@relay.test", ShipCountry = "US" });
            db.Orders.Add(new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-B", AccountId = B, Currency = "USD", BuyerName = "Bob", RelayEmail = "relay-b@relay.test", ShipCountry = "US" });
            db.Orders.Add(new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-G", AccountId = null, Currency = "USD", BuyerName = "Guest", RelayEmail = "relay-g@relay.test", ShipCountry = "US" });
            db.SaveChanges();
            return 0;
        });
    }

    private static async Task<ProcessOutcome> Send(TestFactory f, string id, string type, object data)
    {
        var body = Body(id, type, f.Clock.GetUtcNow(), data);
        PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes(body), out var evt, out _);
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<PortalEventProcessor>().ProcessAsync(evt!, body, default);
    }

    private static async Task Delete(TestFactory f, Guid id, bool redact = true)
    {
        WithDb(f, db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdate(s => s.SetProperty(a => a.DeletedAt, f.Clock.GetUtcNow())));
        if (!redact) return;
        using var s = f.Services.CreateScope();
        var erasers = s.ServiceProvider.GetServices<IAccountEraser>();
        Assert.True(await s.ServiceProvider.GetRequiredService<AccountService>().RedactAsync(id, erasers, default));
    }

    private static PortalInboundEvent Row(TestFactory f, string id) => WithDb(f, db => db.PortalInboundEvents.AsNoTracking().Single(e => e.Id == id));

    [Fact]
    public async Task Erasure_drops_the_payloads_of_stored_and_applied_events_of_the_account_only()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => Task.FromResult(HandlerOutcome.Applied()) };
        using var f = Factory(h);
        Seed(f);
        await Send(f, "e-a-applied", "order.shipped", Order("MP-A"));
        await Send(f, "e-a-stored", "unknown.type", Order("MP-A"));
        await Send(f, "e-b-applied", "order.shipped", Order("MP-B"));
        Assert.Equal(A, Row(f, "e-a-applied").AccountId);
        Assert.Equal(A, Row(f, "e-a-stored").AccountId);
        Assert.Equal(("applied", "stored"), (Row(f, "e-a-applied").Status, Row(f, "e-a-stored").Status));

        await Delete(f, A);

        foreach (var id in new[] { "e-a-applied", "e-a-stored" })
        {
            var r = Row(f, id);
            Assert.Null(r.PayloadJson);
            Assert.NotNull(r.PayloadPurgedAt);
            Assert.Equal(A, r.AccountId);                    // the row stays: it is the idempotency guard
        }
        Assert.NotNull(Row(f, "e-b-applied").PayloadJson);   // another account's event is untouched
    }

    [Fact]
    public async Task A_parked_event_keeps_its_payload_through_erasure_and_loses_it_when_it_is_finished()
    {
        var known = false;
        var h = new ScriptedHandler("order.shipped") { Script = c => Task.FromResult(known ? HandlerOutcome.Applied() : HandlerOutcome.Parked("order", "MP-A", "waiting")) };
        using var f = Factory(h);
        Seed(f);
        Assert.Equal(ProcessOutcome.Parked, await Send(f, "e-parked", "order.shipped", Order("MP-A")));
        Assert.Equal(A, Row(f, "e-parked").AccountId);

        await Delete(f, A);
        Assert.NotNull(Row(f, "e-parked").PayloadJson);      // replayed from it, so it must stay for now

        known = true;
        using (var s = f.Services.CreateScope())
            Assert.Equal(1, await s.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayParkedAsync("order", "MP-A", default));
        var r = Row(f, "e-parked");
        Assert.Equal("applied", r.Status);
        Assert.Null(r.PayloadJson);
        Assert.NotNull(r.PayloadPurgedAt);
    }

    [Fact]
    public async Task An_event_that_arrives_after_the_account_was_deleted_is_never_kept()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => Task.FromResult(HandlerOutcome.Applied()) };
        using var f = Factory(h);
        Seed(f);
        await Delete(f, A);
        await Send(f, "late-applied", "order.shipped", Order("MP-A"));
        await Send(f, "late-stored", "unknown.type", Order("MP-A"));
        foreach (var id in new[] { "late-applied", "late-stored" })
            Assert.Null(Row(f, id).PayloadJson);
        // a live account's event is kept
        await Send(f, "live", "order.shipped", Order("MP-B"));
        Assert.NotNull(Row(f, "live").PayloadJson);
    }

    [Fact]
    public async Task A_parked_event_that_arrives_after_the_deletion_keeps_its_payload_until_it_is_finished()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => Task.FromResult(HandlerOutcome.Parked("order", "MP-A", "waiting")) };
        using var f = Factory(h);
        Seed(f);
        await Delete(f, A);
        Assert.Equal(ProcessOutcome.Parked, await Send(f, "late-parked", "order.shipped", Order("MP-A")));
        Assert.NotNull(Row(f, "late-parked").PayloadJson);     // the replay reads it; it is dropped when the replay finishes the event
    }

    [Fact]
    public async Task A_deleted_but_not_yet_redacted_account_already_stops_new_payloads()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => Task.FromResult(HandlerOutcome.Applied()) };
        using var f = Factory(h);
        Seed(f);
        await Delete(f, A, redact: false);
        await Send(f, "between", "order.shipped", Order("MP-A"));
        Assert.Null(Row(f, "between").PayloadJson);
    }

    [Fact]
    public async Task A_guest_order_or_an_unknown_reference_names_no_account()
    {
        var h = new ScriptedHandler("order.shipped") { Script = _ => Task.FromResult(HandlerOutcome.Applied()) };
        using var f = Factory(h);
        Seed(f);
        await Send(f, "guest", "order.shipped", Order("MP-G"));
        await Send(f, "nobody", "order.shipped", Order("MP-NOPE"));
        await Send(f, "noref", "unknown.type", new { something = 1 });
        foreach (var id in new[] { "guest", "nobody", "noref" })
            Assert.Null(Row(f, id).AccountId);
    }

    [Fact]
    public async Task The_account_is_found_from_a_booking_a_quote_a_conversation_or_a_message_order_reference()
    {
        using var f = Factory(new ScriptedHandler("order.shipped"));
        Seed(f);
        var bookingId = "BK-1"; var quoteId = "QT-1"; var conv = "CV-ABC";
        WithDb(f, db =>
        {
            db.Bookings.Add(new Booking { Id = Guid.NewGuid(), MarketplaceBookingId = bookingId, AccountId = A });
            db.Quotes.Add(new BuyerQuote { Id = Guid.NewGuid(), MarketplaceQuoteId = quoteId, AccountId = B });
            db.MessageThreads.Add(new MessageThread { Id = Guid.NewGuid(), ConversationKey = conv, AccountId = A, Kind = ThreadKinds.Order, SellerName = "S", Subject = "x", CreatedAt = T0, LastMessageAt = T0 });
            db.SaveChanges();
            return 0;
        });
        var portalId = Guid.NewGuid();
        var cases = new (object Data, Guid Expected)[]
        {
            (new { booking = new { bookingId = portalId, number = "N", marketplaceBookingId = bookingId } }, A),
            (new { quote = new { quoteId = portalId, marketplaceQuoteId = quoteId } }, B),
            (new { marketplaceConversationId = conv }, A),
            (new { marketplaceOrderId = "MP-B", kind = "order" }, B),
        };
        var i = 0;
        foreach (var (data, expected) in cases)
        {
            var body = Body("ref-" + i++, "x.y", T0, data);
            PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes(body), out var evt, out _);
            using var s = f.Services.CreateScope();
            Assert.Equal(expected, await EventAccounts.FindAsync(s.ServiceProvider.GetRequiredService<MarketplaceDb>(), evt!, default));
        }
    }
}
