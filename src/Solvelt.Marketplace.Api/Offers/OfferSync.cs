using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Offers;

/// <summary>
/// <c>offer.updated</c> / <c>offer.ended</c> (portal C18.3, <c>{offerId, version, ...}</c>): hints only, like the listing events. It never calls the network: an update records the version we now know exists and the sync fetches the document;
/// an end hides the offer at once and keeps it hidden against any document of that version or older. A deleted offer carries no version and hides for good.
/// </summary>
public sealed class OfferEventHandler : IProjectionHandler
{
    public const string Updated = "offer.updated", Ended = "offer.ended";
    public IReadOnlyCollection<string> EventTypes { get; } = [Updated, Ended];

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var data = c.Event.Data;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("offerId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Guid.TryParse(idEl.GetString(), out var id))
            return HandlerOutcome.Skipped("invalid offer event payload");
        long? version = data.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.Number && vEl.TryGetInt64(out var v) && v >= 1 ? v : null;
        if (c.Event.Type == Updated && version is null) return HandlerOutcome.Skipped("invalid offer event payload");
        var now = c.Time.GetUtcNow().ToUniversalTime();
        var offer = await c.Db.CatalogOffers.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (offer is null)
        {
            offer = new CatalogOffer { Id = id, FirstSeenAt = now, UpdatedAt = now };
            c.Db.CatalogOffers.Add(offer);
        }
        if (c.Event.Type == Updated)
        {
            if (version > offer.KnownVersion) { offer.FetchAttempts = 0; offer.NextFetchAt = null; }
            offer.KnownVersion = Math.Max(offer.KnownVersion, version!.Value);
        }
        else
        {
            offer.UnlistedVersion = version is null ? long.MaxValue : Math.Max(offer.UnlistedVersion ?? 0, version.Value);
            offer.KnownVersion = Math.Max(offer.KnownVersion, version ?? 0);
        }
        offer.RecomputeVisibility();
        offer.Row++;
        return HandlerOutcome.Applied();
    }
}

public sealed record OfferSyncResult(bool Ran, int Applied, int Fetched, string? Error);

/// <summary>
/// Keeps <c>catalog_offers</c> in step with the portal's offer feed: the cursor poll (documents come inline, the highest version per offer wins, tombstones hide) and the fetch of offers an event announced. One instance at a time (a lease
/// in <c>catalog_sync_state</c>, key <c>offers</c>). A document of an older version than the one we hold, or hidden by an unlist of that version, never changes anything.
/// </summary>
public sealed class OfferSyncService(MarketplaceDb db, IOfferSource source, TimeProvider time, IOptions<CatalogOptions> options, ILogger<OfferSyncService> log)
{
    public const string StateKey = "offers";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Offers first seen in one run get strictly increasing <c>FirstSeenAt</c> (a millisecond apart: the stored time keeps milliseconds on SQLite and microseconds on PostgreSQL) in FEED order. The portal breaks a tie between equally specific offers by the offer's own creation time (then id; portal
    /// <c>Offers/OfferIntake.cs:111</c>), which the feed does not carry yet (decision Q-01, field requested): until it does, the feed order (version order, which follows creation order for new offers) is the nearest
    /// stand-in, and it is deterministic, where a shared timestamp would leave the id alone to decide.
    /// </summary>
    private long _firstSeenSeq;

    public async Task<OfferSyncResult> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        if (!await db.CatalogSyncStates.AnyAsync(s => s.Key == StateKey, ct))
        {
            try { db.CatalogSyncStates.Add(new CatalogSyncState { Key = StateKey }); await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        var owner = Guid.NewGuid().ToString("N");
        var got = await db.CatalogSyncStates.Where(s => s.Key == StateKey && (s.LeaseUntil == null || s.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)(now + Lease)).SetProperty(x => x.LeaseOwner, owner), ct);
        if (got == 0) return new(false, 0, 0, null);
        string? error = null; int applied = 0, fetched = 0;
        try
        {
            var state = await db.CatalogSyncStates.AsNoTracking().FirstAsync(s => s.Key == StateKey, ct);
            var cursor = state.Cursor;
            for (var page = 0; page < Math.Max(1, options.Value.MaxPagesPerRun); page++)
            {
                var p = await source.GetOffersAsync(cursor, 100, ct);
                foreach (var o in p.Items) applied += await ApplyAsync(o, ct) ? 1 : 0;
                foreach (var t in p.Tombstones) applied += await TombstoneAsync(t, ct) ? 1 : 0;
                cursor = p.NextCursor ?? cursor;
                await db.CatalogSyncStates.Where(s => s.Key == StateKey && s.LeaseOwner == owner).ExecuteUpdateAsync(s => s.SetProperty(x => x.Cursor, cursor).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)(time.GetUtcNow().ToUniversalTime() + Lease)), ct);
                if (!p.HasMore) break;
            }
            // offers an event announced but the cursor has not delivered (or delivered an older version of)
            var due = time.GetUtcNow().ToUniversalTime();
            var pending = await db.CatalogOffers.AsNoTracking().Where(o => o.KnownVersion > o.Version && (o.NextFetchAt == null || o.NextFetchAt <= due)).OrderBy(o => o.KnownVersion).Select(o => o.Id).Take(50).ToListAsync(ct);
            foreach (var id in pending)
            {
                try
                {
                    var doc = await source.GetOfferAsync(id, ct);
                    fetched++;
                    if (doc is null) await MarkGoneAsync(id, ct); else await ApplyAsync(doc, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("An offer could not be fetched ({Type}); it is tried again later.", ex.GetType().Name);
                    await BackoffAsync(id, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.GetType().Name; log.LogWarning("Offer sync failed ({Type}).", ex.GetType().Name); }
        finally
        {
            db.ChangeTracker.Clear();
            var end = time.GetUtcNow().ToUniversalTime(); var err = error;
            await db.CatalogSyncStates.Where(s => s.Key == StateKey && s.LeaseOwner == owner).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LastRunAt, (DateTimeOffset?)end).SetProperty(x => x.LastError, err).SetProperty(x => x.LastSuccessAt, x => err == null ? end : x.LastSuccessAt), CancellationToken.None);
        }
        return new(true, applied, fetched, error);
    }

    private async Task<bool> ApplyAsync(OfferSnapshot s, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var row = await db.CatalogOffers.FirstOrDefaultAsync(o => o.Id == s.OfferId, ct);
        if (row is null) { row = new CatalogOffer { Id = s.OfferId, FirstSeenAt = now.AddMilliseconds(++_firstSeenSeq) }; db.CatalogOffers.Add(row); }
        if (s.Version <= row.Version && row.Version > 0) { db.ChangeTracker.Clear(); return false; }   // an older (or the same) version than we hold: nothing to do
        row.SellerId = s.SellerId; row.BrandId = s.BrandId; row.Name = s.Name; row.Currency = s.Currency; row.Scope = s.Scope;
        row.ProductIdsJson = JsonSerializer.Serialize(s.ProductIds); row.CategoryIdsJson = JsonSerializer.Serialize(s.CategoryIds);
        row.TiersJson = JsonSerializer.Serialize(s.Tiers); row.GiftsJson = JsonSerializer.Serialize(s.Gifts);
        row.ActiveFrom = s.ActiveFrom; row.ActiveTo = s.ActiveTo; row.Demo = s.Demo;
        if (s.CreatedAt is { } created) row.CreatedAtTicks = created.UtcTicks;
        row.Version = s.Version; row.KnownVersion = Math.Max(row.KnownVersion, s.Version); row.UpdatedAt = now; row.FetchAttempts = 0; row.NextFetchAt = null;
        row.RecomputeVisibility(); row.Row++;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }   // an event changed the row meanwhile: the next run sees the newer state
    }

    private async Task<bool> TombstoneAsync(OfferTombstone t, CancellationToken ct)
    {
        var row = await db.CatalogOffers.FirstOrDefaultAsync(o => o.Id == t.OfferId, ct);
        if (row is null) { db.ChangeTracker.Clear(); return false; }   // never held: nothing to hide
        var at = t.Version ?? (t.Reason == "deleted" ? long.MaxValue : row.Version);
        if (t.Version is { } v && v < row.Version) { db.ChangeTracker.Clear(); return false; }   // stale: a newer version is already held
        row.UnlistedVersion = Math.Max(row.UnlistedVersion ?? 0, at); row.RecomputeVisibility(); row.Row++;
        try { await db.SaveChangesAsync(ct); return true; } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
    }

    private async Task MarkGoneAsync(Guid id, CancellationToken ct)
        => await TombstoneAsync(new OfferTombstone(id, null, "ended"), ct);

    private async Task BackoffAsync(Guid id, CancellationToken ct)
    {
        var row = await db.CatalogOffers.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row is null) return;
        row.FetchAttempts++; row.NextFetchAt = time.GetUtcNow().ToUniversalTime().AddMinutes(Math.Min(360, Math.Pow(2, Math.Min(row.FetchAttempts, 9))));
        await db.SaveChangesAsync(ct);
    }
}

public sealed class OfferSyncWorker(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<OfferSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var r = await scope.ServiceProvider.GetRequiredService<OfferSyncService>().RunAsync(stoppingToken);
                failures = r.Error is null ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { failures++; log.LogError("Offer sync worker failed: {ExceptionType}", ex.GetType().FullName); }
            try { await Task.Delay(CatalogSyncWorker.Delay(options.Value.SyncIntervalSeconds, failures), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
