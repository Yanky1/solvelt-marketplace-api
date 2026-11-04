using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// Pending orders live 23 hours (inside Stripe's 24 hour idempotency-key lifetime, so a repeated checkout can always re-read its intent): then the order expires, the key is
/// released and the intent is cancelled (best effort; a payment that still lands is honoured by the webhook, which also pays an expired order).
/// Orders that never completed are deleted after 30 days (see the PII table).
/// </summary>
public sealed class OrderMaintenance(MarketplaceDb db, Outbox.OutboxWriter outbox, Microsoft.Extensions.Options.IOptions<Infrastructure.PortalOptions> portalOptions, TimeProvider time)
{
    /// <summary>
    /// Releases the multi-seller dispute events held until the portal merged C10 (see StripeEventProcessor) once <c>Portal:PerSellerOrderDisputes</c> is on: they become due now.
    /// Returns how many were released.
    /// </summary>
    public Task<int> ReleaseHeldDisputesAsync(CancellationToken ct = default)
    {
        if (!portalOptions.Value.PerSellerOrderDisputes) return Task.FromResult(0);
        var now = time.GetUtcNow().ToUniversalTime();
        var farAway = now.AddDays(365);
        return db.OutboxMessages.Where(m => m.Type == Outbox.PaymentEventOutboxHandler.Type && m.Status == Data.OutboxStatus.Pending && m.DedupeKey!.StartsWith("payment:dispute:") && m.NextAttemptAt > farAway)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, now), ct);
    }

    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(23);

    public async Task<int> ExpirePendingAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var cutoff = now - PendingLifetime;
        var stale = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.PendingPayment && o.CreatedAt < cutoff).Select(o => new { o.Id, o.Number }).Take(200).ToListAsync(ct);
        var expired = 0;
        foreach (var o in stale)
        {
            // One transaction: the status change, the read of the intent id AFTER it (a checkout retry may have stored one since the listing above; the store only succeeds while the order is pending, so
            // after this change no intent can appear) and the cancel's outbox row. A crash in between rolls all of it back, so a cancel is never lost (decision M3-25).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Orders.Where(x => x.Id == o.Id && x.Status == OrderStatus.PendingPayment)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Expired).SetProperty(x => x.ActiveKey, (string?)null).SetProperty(x => x.UpdatedAt, now), ct);
            if (changed == 0) { await tx.RollbackAsync(CancellationToken.None); continue; }   // paid (or began processing) in the meantime
            var pi = await db.Orders.AsNoTracking().Where(x => x.Id == o.Id).Select(x => x.PaymentIntentId).FirstAsync(ct);
            await EnqueueCancelAsync(outbox, o.Number, pi, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            expired++;
        }
        return expired;
    }

    /// <summary>Queues the cancel of a closed order's intent through the outbox (retried): the intent's own cancel when one is stored, otherwise the "orphan" cancel that finds it again by its idempotency key.</summary>
    public static async Task EnqueueCancelAsync(Outbox.OutboxWriter outbox, string orderNumber, string? intentId, CancellationToken ct)
    {
        var (type, key, payload) = intentId is { } pi
            ? (StripeCancelHandler.Type, StripeCancelHandler.DedupeKey(pi), (object)new StripeCancelHandler.Payload(pi))
            : (StripeOrphanCancelHandler.Type, StripeOrphanCancelHandler.DedupeKey(orderNumber), new StripeOrphanCancelHandler.Payload(orderNumber));
        if (!await outbox.ExistsAsync(key, ct)) outbox.Enqueue(type, payload, key);
    }

    /// <summary>
    /// Orders that never became paid are deleted after 30 days when they never had a PaymentIntent, and after 90 days when they did, and ONLY once their cancel was DELIVERED (the intent is verifiably
    /// closed): a payment that lands on a purged order could not be matched to anything. A cancel that is still pending or dead keeps the order, so a person can see it (decision M3-25).
    /// </summary>
    public Task<int> PurgeAbandonedAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var cut = now.AddDays(-30);
        var cutWithIntent = now.AddDays(-90);
        return db.Orders.Where(o => o.PaidAt == null && (o.Status == OrderStatus.Expired || o.Status == OrderStatus.Superseded)
            && ((o.PaymentIntentId == null && o.CreatedAt < cut && db.OutboxMessages.Any(m => m.DedupeKey == "cancel-orphan:" + o.Number && m.Status == Data.OutboxStatus.Delivered))
                || (o.PaymentIntentId != null && o.CreatedAt < cutWithIntent && db.OutboxMessages.Any(m => m.DedupeKey == "cancel:" + o.PaymentIntentId && m.Status == Data.OutboxStatus.Delivered)))).ExecuteDeleteAsync(ct);
    }
}

public sealed class OrderWorker(IServiceScopeFactory scopes, ILogger<OrderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var fulfilment = scope.ServiceProvider.GetRequiredService<Fulfilment.FulfilmentMaintenance>();
                // the privacy-critical retention first and on its own: nothing that fails below can stop it (M5 review P7)
                try { await fulfilment.ClearPositionsAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError("Position retention failed: {ExceptionType}", ex.GetType().FullName); }
                var m = scope.ServiceProvider.GetRequiredService<OrderMaintenance>();
                await m.ExpirePendingAsync(stoppingToken);
                await m.ReleaseHeldDisputesAsync(stoppingToken);
                await m.PurgeAbandonedAsync(stoppingToken);
                await fulfilment.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Order maintenance failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
