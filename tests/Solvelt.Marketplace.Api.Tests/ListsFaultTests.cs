using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Lists;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>What the wishlist watch does when the database or the connection fails (review MED-2): a transient fault is retried, never silently dropped; a buyer's own constraint failure is skipped.</summary>
public sealed class ListsFaultTests
{
    /// <summary>Throws once for the first command whose text contains <see cref="Match"/> while <see cref="Armed"/>.</summary>
    private sealed class Fault : DbCommandInterceptor
    {
        public volatile bool Armed;
        public string Match = "";
        public Func<Exception> Make = () => new TimeoutException();
        public int Thrown;
        private void Check(DbCommand c) { if (Armed && Thrown == 0 && c.CommandText.Contains(Match, StringComparison.Ordinal)) { Interlocked.Increment(ref Thrown); throw Make(); } }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Check(command); return base.ReaderExecutingAsync(command, eventData, result, ct); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) { Check(command); return base.NonQueryExecutingAsync(command, eventData, result, ct); }
    }

    private static Guid Listing() => CatalogKit.ListingId("challah-classic");

    private static async Task<Guid> Wisher(AuthKit k, string email)
    {
        var t = await k.VerifiedSession(email);
        var created = await k.Send(HttpMethod.Post, "/api/v1/wishlists", new { name = "Gifts" }, t);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", null, t)).StatusCode);
        return (await k.Account(email)).Id;
    }

    private static async Task<int> Drops(AuthKit k) => await k.Db(db => db.Notifications.CountAsync(n => n.Kind == NotificationKinds.WishlistPriceDrop));

    [Theory]
    [InlineData("INSERT INTO \"notifications\"", "write")]   // a buyer's own save fails with a timeout
    [InlineData("FROM \"wishlist_items\"", "read")]          // the read of who wishes for the listing fails
    public async Task A_transient_fault_is_retried_not_dropped_and_the_retry_tells_every_buyer_exactly_once(string match, string where)
    {
        var fault = new Fault { Match = match };
        using var k = new AuthKit(null, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(fault)));
        await CatalogKit.Sync(k.F);
        await Wisher(k, $"f1a-{where}@example.com");
        await Wisher(k, $"f1b-{where}@example.com");
        while (await k.Dispatch() > 0) { }                                                        // the watches of the first sync (nothing changed for anybody) are done: the next one is the price change
        var fx = CatalogKit.Fixture(k.F);
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        await CatalogKit.Sync(k.F);

        fault.Armed = true;
        await k.Dispatch();                                                                       // ONE pass: the watch fails with the transient fault
        fault.Armed = false;
        Assert.Equal(1, fault.Thrown);
        var retried = await k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == WishlistWatchHandler.Type && m.Status == OutboxStatus.Pending).ToListAsync());
        var msg = Assert.Single(retried);                                                          // NOT delivered: the watch waits for its retry, the notification is not lost
        Assert.Equal(1, msg.Attempts);
        Assert.StartsWith("wishlist_watch_", msg.LastError);

        k.F.Clock.Advance(TimeSpan.FromHours(2));                                                 // past the backoff
        while (await k.Dispatch() > 0) { }
        Assert.Equal(2, await Drops(k));                                                           // both buyers, once each (the buyer finished before the fault is not told twice)
        Assert.Equal(2, await k.Db(db => db.Notifications.Where(n => n.Kind == NotificationKinds.WishlistPriceDrop).Select(n => n.AccountId).Distinct().CountAsync()));
        k.F.Clock.Advance(TimeSpan.FromHours(2));
        while (await k.Dispatch() > 0) { }
        Assert.Equal(2, await Drops(k));
    }

    [Fact]
    public async Task A_constraint_failure_of_one_buyers_own_write_is_skipped_and_the_message_is_delivered()
    {
        var fault = new Fault { Match = "INSERT INTO \"notifications\"", Make = () => new DbUpdateException("unique", new InvalidOperationException("not transient")) };
        using var k = new AuthKit(null, s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(fault)));
        await CatalogKit.Sync(k.F);
        await Wisher(k, "f2a@example.com");
        await Wisher(k, "f2b@example.com");
        while (await k.Dispatch() > 0) { }
        CatalogKit.Fixture(k.F).Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        await CatalogKit.Sync(k.F);
        fault.Armed = true;
        while (await k.Dispatch() > 0) { }
        Assert.Equal(1, await Drops(k));                                                           // the other buyer was told
        Assert.Empty(await k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == WishlistWatchHandler.Type && m.Status != OutboxStatus.Delivered).ToListAsync()));   // every watch is delivered: the failed buyer is skipped, nothing waits
    }

    [Fact]
    public void Which_exceptions_count_as_transient()
    {
        Assert.True(WishlistWatchHandler.IsTransient(new TimeoutException()));
        Assert.True(WishlistWatchHandler.IsTransient(new DbUpdateException("x", new IOException("reset"))));
        Assert.True(WishlistWatchHandler.IsTransient(new InvalidOperationException("An exception has been raised that is likely due to a transient failure.")));
        Assert.True(WishlistWatchHandler.IsTransient(new DbUpdateException("x", new InvalidOperationException("outer", new System.Net.Sockets.SocketException()))));
        Assert.False(WishlistWatchHandler.IsTransient(new DbUpdateException("unique", new InvalidOperationException("constraint"))));
        Assert.False(WishlistWatchHandler.IsTransient(new InvalidOperationException("a bug")));
    }
}
