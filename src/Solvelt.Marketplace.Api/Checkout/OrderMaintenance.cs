using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// Pending orders live 23 hours (inside Stripe's 24 hour idempotency-key lifetime, so a repeated checkout can always re-read its intent): then the order expires, the key is
/// released and the intent is cancelled (best effort; a payment that still lands is honoured by the webhook, which also pays an expired order).
/// Orders that never completed are deleted after 30 days (see the PII table).
/// </summary>
public sealed class OrderMaintenance(MarketplaceDb db, IPaymentProvider payments, TimeProvider time, ILogger<OrderMaintenance> log)
{
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
            if (o.PaymentIntentId is { } pi)
                try { await payments.CancelIntentAsync(pi, "cancel:" + o.Number, ct); }
                catch (Exception ex) when (ex is PaymentsUnavailableException or PaymentRejectedException) { log.LogWarning("Could not cancel the intent of expired order {Number}", o.Number); }
        }
        return expired;
    }

    /// <summary>Orders that never became paid and are older than 30 days are deleted (no payment, nothing to account for).</summary>
    public Task<int> PurgeAbandonedAsync(CancellationToken ct = default)
    {
        var cut = time.GetUtcNow().ToUniversalTime().AddDays(-30);
        return db.Orders.Where(o => o.PaidAt == null && o.CreatedAt < cut && (o.Status == OrderStatus.Expired || o.Status == OrderStatus.Superseded)).ExecuteDeleteAsync(ct);
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
                await m.PurgeAbandonedAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<Fulfilment.FulfilmentMaintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Order maintenance failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
