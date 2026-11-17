using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// FX1-01: a hold request whose insert fails on a lock (SQLite busy/locked, PostgreSQL serialization failure) and finds no row under its key must retry, not answer 500.
/// The failure is injected deterministically; a looped variant sends many parallel rounds and prints the exception behind any 500.
/// </summary>
public sealed class BookingHoldRaceTests : IDisposable
{
    private static readonly Guid Service = CatalogKit.ListingId("deep-house-cleaning");
    private readonly AuthKit _k;
    private readonly FakeBookingPortal _portal;
    private readonly BusyOnInsert _busy = new();
    private string _jane = "";

    /// <summary>Fails the first <see cref="Failures"/> saves that insert a booking, the way a lock that is not freed in time does.</summary>
    private sealed class BusyOnInsert : SaveChangesInterceptor
    {
        public int Failures;
        public int Injected;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Booking>().Any(e => e.State == EntityState.Added) && Interlocked.Decrement(ref Failures) >= 0)
            {
                Interlocked.Increment(ref Injected);
                throw new DbUpdateException("database is locked", new SqliteException("SQLite Error 5: 'database is locked'.", 5));
            }
            return ValueTask.FromResult(result);
        }
    }

    public BookingHoldRaceTests()
    {
        _k = new AuthKit(new Dictionary<string, string?> { ["Bookings:MaxActiveHolds"] = "20" }, services: s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(_busy)));
        _portal = new FakeBookingPortal(_k.F.Clock);
    }

    public void Dispose() => _k.Dispose();

    private async Task Ready()
    {
        await CatalogKit.Sync(_k.F);
        _k.F.Portal.RespondAlways(c => _portal.Handle(c) ?? FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}"));
        _jane = await _k.VerifiedSession("jane@example.com", name: "Jane Brown");
    }

    private object Body(int round = 0) => new
    {
        listingId = Service, start = new DateTimeOffset(_k.F.Clock.GetUtcNow().Date.AddDays(7 + round).AddHours(14), TimeSpan.Zero), location = "online", durationMinutes = 240, addOnIds = new[] { "deep" },
    };

    private async Task<HttpResponseMessage> Hold(object body, string key)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/holds") { Content = JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jane);
        r.Headers.Add("Idempotency-Key", key);
        return await _k.Http.SendAsync(r);
    }

    private string Why(IEnumerable<HttpResponseMessage> rs) =>
        string.Join("\n", _k.F.Logs.Lines.Where(l => l.Contains("ex:")).Take(5)) + "\nstatuses: " + string.Join(",", rs.Select(r => (int)r.StatusCode));

    [Fact]
    public async Task A_lock_that_fails_the_first_insert_is_retried_and_the_buyer_still_gets_one_hold()
    {
        await Ready();
        _busy.Failures = 3;
        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Hold(Body(), "key-fx1-busy-1")));
        Assert.True(_busy.Injected >= 1, "the failure was never injected");
        Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, Why(all)));
        Assert.Equal(1, await _k.Db(db => db.Bookings.AsNoTracking().CountAsync()));
        Assert.Single(_portal.Holds);
        var ids = new HashSet<Guid>();
        foreach (var r in all) ids.Add(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("booking").GetProperty("id").GetGuid());
        Assert.Single(ids);
    }

    [Fact]
    public async Task A_lock_that_never_frees_is_a_503_with_retry_after_not_a_500()
    {
        await Ready();
        _busy.Failures = int.MaxValue;
        var r = await Hold(Body(), "key-fx1-busy-2");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.True(r.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Many_rounds_of_parallel_holds_with_one_key_each_never_answer_500()
    {
        await Ready();
        for (var round = 0; round < 20; round++)
        {
            var key = "key-fx1-loop-" + round;
            var all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Hold(Body(round), key)));
            Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, $"round {round}: {Why(all)}"));
        }
        Assert.Equal(20, await _k.Db(db => db.Bookings.AsNoTracking().CountAsync()));
    }
}
