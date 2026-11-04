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
        var stale = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.PendingPayment && o.CreatedAt < cutoff).Select(o => new { o.Id, o.Number, o.PaymentIntentId }).Take(200).ToListAsync(ct);
        var expired = 0;
        foreach (var o in stale)
        {
            var changed = await db.Orders.Where(x => x.Id == o.Id && x.Status == OrderStatus.PendingPayment)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OrderStatus.Expired).SetProperty(x => x.ActiveKey, (string?)null).SetProperty(x => x.UpdatedAt, now), ct);
            if (changed == 0) continue;   // paid in the meantime
            expired++;
            // the cancel goes through the outbox (retried until Stripe accepts it): a payable intent must never be left on a closed order
            if (o.PaymentIntentId is { } pi && !await outbox.ExistsAsync(StripeCancelHandler.DedupeKey(pi), ct))
            {
                outbox.Enqueue(StripeCancelHandler.Type, new StripeCancelHandler.Payload(pi), StripeCancelHandler.DedupeKey(pi));
                await db.SaveChangesAsync(ct);
            }
        }
        return expired;
    }

    /// <summary>
    /// Orders that never became paid are deleted after 30 days when they never had a PaymentIntent, and after 90 days when they did (a late payment on a purged order could never be matched,
    /// so it would loop 5xx and never be refunded: the cancel has long been delivered by then and Stripe stops retrying webhooks after three days).
    /// </summary>
    public Task<int> PurgeAbandonedAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var cut = now.AddDays(-30);
        var cutWithIntent = now.AddDays(-90);
        return db.Orders.Where(o => o.PaidAt == null && (o.Status == OrderStatus.Expired || o.Status == OrderStatus.Superseded)
            && ((o.PaymentIntentId == null && o.CreatedAt < cut) || (o.PaymentIntentId != null && o.CreatedAt < cutWithIntent))).ExecuteDeleteAsync(ct);
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
                var m = scope.ServiceProvider.GetRequiredService<OrderMaintenance>();
                await m.ExpirePendingAsync(stoppingToken);
                await m.ReleaseHeldDisputesAsync(stoppingToken);
                await m.PurgeAbandonedAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<Fulfilment.FulfilmentMaintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Order maintenance failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
