using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

/// <summary>
/// Retention of the inbound event store (PII table in DECISIONS): payloads are dropped after <c>Portal:InboundPayloadRetentionDays</c> (30),
/// rows after <c>Portal:InboundRowRetentionDays</c> (90, far beyond the portal's retry horizon, so the id guard keeps working).
/// Parked events keep payload and row until they are applied; events of the short-retention types (positions, proof URLs) keep their payload only 24 h.
/// </summary>
public sealed class PortalEventMaintenance(MarketplaceDb db, IOptions<PortalOptions> options, TimeProvider time)
{
    public async Task<(int PayloadsPurged, int RowsDeleted)> PurgeAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        var payloadCutoff = now.AddDays(-Math.Max(1, o.InboundPayloadRetentionDays));
        var rowCutoff = now.AddDays(-Math.Max(o.InboundPayloadRetentionDays, o.InboundRowRetentionDays));
        // CONTRACT.md §5: positions and proof URLs live at most 24 h. A parked event of these types cannot wait for its aggregate any longer.
        var shortTypes = o.InboundShortRetentionTypes;
        var shortCutoff = now.AddHours(-Math.Max(1, o.InboundShortRetentionHours));
        await db.PortalInboundEvents
            .Where(e => shortTypes.Contains(e.Type) && e.Status == InboundStatus.Parked && e.ReceivedAt < shortCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, InboundStatus.Skipped).SetProperty(e => e.Note, "expired before its aggregate appeared"), ct);
        var shortPurged = await db.PortalInboundEvents
            .Where(e => shortTypes.Contains(e.Type) && e.PayloadJson != null && e.ReceivedAt < shortCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadJson, (string?)null).SetProperty(e => e.PayloadPurgedAt, (DateTimeOffset?)now), ct);
        var purged = shortPurged + await db.PortalInboundEvents
            .Where(e => e.Status != InboundStatus.Parked && e.PayloadJson != null && e.ReceivedAt < payloadCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadJson, (string?)null).SetProperty(e => e.PayloadPurgedAt, (DateTimeOffset?)now), ct);
        var deleted = await db.PortalInboundEvents
            .Where(e => e.Status != InboundStatus.Parked && e.ReceivedAt < rowCutoff)
            .ExecuteDeleteAsync(ct);
        return (purged, deleted);
    }
}

/// <summary>Hourly: retention and a sweep of parked events (outside Testing, where tests call the services directly).</summary>
public sealed class PortalEventWorker(IServiceScopeFactory scopes, ILogger<PortalEventWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var purge = await scope.ServiceProvider.GetRequiredService<PortalEventMaintenance>().PurgeAsync(stoppingToken);
                var replayed = await scope.ServiceProvider.GetRequiredService<PortalEventProcessor>().ReplayAllParkedAsync(ct: stoppingToken);
                log.LogInformation("Portal events: {Purged} payloads purged, {Deleted} rows deleted, {Replayed} parked events re-applied", purge.PayloadsPurged, purge.RowsDeleted, replayed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Portal event maintenance failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
