using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Review X1/P6: one scope serves a whole outbox drain, so a failed handler's half-applied entities must never be saved by the next handler.</summary>
public sealed class OutboxTrackerTests
{
    private sealed class DirtyHandler(MarketplaceDb db) : IOutboxHandler
    {
        public IReadOnlyCollection<string> Types { get; } = ["test.dirty"];
        public Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
        {
            db.ProjectionStates.Add(new ProjectionState { AggregateType = "test", AggregateKey = "half-applied", FieldGroup = "x", LastAppliedAt = DateTimeOffset.UtcNow, LastEventId = "e" });   // tracked, never saved by this handler
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class SavingHandler(MarketplaceDb db) : IOutboxHandler
    {
        public IReadOnlyCollection<string> Types { get; } = ["test.save"];
        public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
        {
            db.ProjectionStates.Add(new ProjectionState { AggregateType = "test", AggregateKey = "good", FieldGroup = "x", LastAppliedAt = DateTimeOffset.UtcNow, LastEventId = "e2" });
            await db.SaveChangesAsync(ct);
            return OutboxResult.Delivered();
        }
    }

    [Fact]
    public async Task A_failing_handler_followed_by_a_saving_handler_in_one_scope_saves_only_what_the_second_one_did()
    {
        using var k = new AuthKit(null, s => { s.AddScoped<IOutboxHandler, DirtyHandler>(); s.AddScoped<IOutboxHandler, SavingHandler>(); });
        using (var scope = k.F.Services.CreateScope())
        {
            var w = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
            var first = w.Enqueue("test.dirty", new { });
            first.CreatedAt = k.F.Clock.GetUtcNow().AddSeconds(-10);                                           // the failing one is handled first
            w.Enqueue("test.save", new { });
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        Assert.Equal(2, await k.Dispatch());                                                                   // both in ONE scope
        var keys = await k.Db(db => db.ProjectionStates.AsNoTracking().Where(x => x.AggregateType == "test").Select(x => x.AggregateKey).ToListAsync());
        Assert.Equal(new[] { "good" }, keys);                                                                  // the half-applied entity of the failed handler was never written
    }
}
