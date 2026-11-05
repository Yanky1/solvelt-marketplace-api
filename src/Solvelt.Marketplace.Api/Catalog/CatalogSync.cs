using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public sealed class SyncCounts { public int Applied, Skipped, Fetched, Pending, Quarantined; }

public sealed record CatalogSyncResult(bool Ran, int Applied, int Skipped, int Pending, int Fetched, string? Error);

/// <summary>The lease was taken over by another instance (ours expired): stop at once and write nothing more.</summary>
public sealed class LeaseLostException : Exception;

/// <summary>Limits on what one snapshot may carry (M2-10). Variants cannot be truncated (a buyer must be able to buy every one), so more is an invalid snapshot; the rest is cut.</summary>
public static class SnapshotLimits
{
    public const int MaxVariants = 200, MaxImages = 50, MaxAttributes = 100, MaxFaqs = 50;
    /// <summary>The most a unit may cost and still be bought (M3-22: checkout treats a larger unit price as unavailable). A variant above it is stored as not available.</summary>
    public const long MaxUnitPriceCents = 99_999_999;
    public const int Title = 300, Summary = 600, Description = 50_000, Name = 300, Alt = 300, AttributeKey = 100, AttributeValue = 500, Question = 500, DisplayName = 200, Slug = 120;
    public static string Clip(string? s, int max) => SearchText.Clip(s ?? "", max);   // never cuts inside a surrogate pair (a lone surrogate is invalid UTF-8 and a deterministic error on PostgreSQL)
}

/// <summary>
/// Keeps the read model in step with <see cref="ICatalogSource"/>: reference data (categories, brands), then a cursor-based poll of the
/// listing feed, then a fetch of the listings that events said have a newer version than we hold. Everything is versioned by the portal's
/// <c>version</c>: a snapshot is applied only when newer than what we have, so the job is idempotent, may overlap with events, and does not
/// care about order (CONTRACT.md §2). One instance runs at a time (a lease row with an owner token, renewed per page). The cursor moves in the
/// same transaction as the page, and only a <b>deterministic</b> failure skips a record (see <see cref="SyncErrors"/>).
/// </summary>
public sealed class CatalogSyncService(MarketplaceDb db, ICatalogSource source, TimeProvider time, IOptions<CatalogOptions> options, ILogger<CatalogSyncService> log, Outbox.OpsCounters? ops = null)
{
    public const string StateKey = "listings";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private string _owner = "";

    public async Task<CatalogSyncResult> RunAsync(CancellationToken ct = default)
    {
        if (!await TryAcquireLeaseAsync(ct)) return new(false, 0, 0, 0, 0, null);
        var counts = new SyncCounts();
        string? error = null;
        var lost = false;
        try
        {
            // A broken reference record or a failing reference source must not stop the listings: the poll goes on, and listings whose category is not
            // known yet wait as pending fetches (with backoff) until it is.
            try { await SyncReferenceDataAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException and not LeaseLostException)
            {
                error = ex.GetType().Name;
                log.LogError("Catalog reference data failed: {ExceptionType}", ex.GetType().FullName);
                db.ChangeTracker.Clear();
            }
            await PollAsync(counts, ct);
            await FetchPendingAsync(counts, ct);
            await SearchIndexer.BackfillAsync(db, ct);
            await MaintainSellersAsync(ct);
        }
        catch (LeaseLostException)
        {
            lost = true;
            log.LogWarning("Catalog sync stopped: its lease was taken over by another instance");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.GetType().Name;   // never the message: it may carry portal payload
            log.LogError("Catalog sync failed: {ExceptionType}", ex.GetType().FullName);
        }
        finally
        {
            db.ChangeTracker.Clear();
            if (!lost)
            {
                var now = time.GetUtcNow().ToUniversalTime();
                var err = error;
                var owner = _owner;
                // only the owner releases: a run whose lease expired and was taken over must not null the new owner's lease
                await db.CatalogSyncStates.Where(s => s.Key == StateKey && s.LeaseOwner == owner).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LastRunAt, (DateTimeOffset?)now)
                    .SetProperty(x => x.LastError, err)
                    .SetProperty(x => x.LastSuccessAt, x => err == null ? now : x.LastSuccessAt), CancellationToken.None);
            }
        }
        try { ops?.SetCatalogQuarantined(await db.CatalogQuarantine.CountAsync(CancellationToken.None)); } catch (Exception ex) when (ex is not OperationCanceledException) { /* the gauge is best effort: /health must not make a run fail */ }
        log.LogInformation("Catalog sync: {Applied} applied, {Skipped} skipped, {Fetched} fetched, {Pending} still pending, {Quarantined} set aside this run", counts.Applied, counts.Skipped, counts.Fetched, counts.Pending, counts.Quarantined);
        return new(true, counts.Applied, counts.Skipped, counts.Pending, counts.Fetched, error);
    }

    private async Task<bool> TryAcquireLeaseAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        if (!await db.CatalogSyncStates.AnyAsync(s => s.Key == StateKey, ct))
        {
            try { db.CatalogSyncStates.Add(new CatalogSyncState { Key = StateKey }); await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // another instance created it first
        }
        var until = now + Lease;
        var owner = Guid.NewGuid().ToString("N");
        var rows = await db.CatalogSyncStates.Where(s => s.Key == StateKey && (s.LeaseUntil == null || s.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)until).SetProperty(x => x.LeaseOwner, (string?)owner), ct);
        if (rows == 1) _owner = owner;
        return rows == 1;
    }

    /// <summary>Extends the lease while we still own it; if another instance has taken it over, stops this run.</summary>
    private async Task RenewLeaseAsync(CancellationToken ct)
    {
        var until = time.GetUtcNow().ToUniversalTime() + Lease;
        var owner = _owner;
        var rows = await db.CatalogSyncStates.Where(s => s.Key == StateKey && s.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)until), ct);
        if (rows != 1) throw new LeaseLostException();
    }

    // ---------- reference data ----------
    /// <summary>
    /// Categories and brands from the feed, made safe before they touch the database: names are cut to their column, slugs are made unique
    /// (an existing row keeps its slug; a clash gives the newcomer an id suffix), categories deeper than <see cref="CategoryRules.MaxDepth"/> are
    /// attached to their ancestor at that depth, parent cycles and self-parents are broken. If the batch still cannot be saved, the records are
    /// applied one at a time and only a record that breaks a database rule is skipped, so one bad record never blocks the others.
    /// </summary>
    private async Task SyncReferenceDataAsync(CancellationToken ct)
    {
        var categories = CategoryRules.Normalise(await source.GetCategoriesAsync(ct));
        var brands = BrandRules.Normalise(await source.GetBrandsAsync(ct));
        await ApplyReferenceAsync(perRecord => ApplyCategoriesAsync(categories, perRecord, ct), ct);
        await ApplyReferenceAsync(perRecord => ApplyBrandsAsync(brands, perRecord, ct), ct);
    }

    private async Task ApplyReferenceAsync(Func<bool, Task> apply, CancellationToken ct)
    {
        try { await apply(false); }
        catch (Exception ex) when (SyncErrors.IsDeterministic(ex))
        {
            log.LogWarning("Catalog reference data could not be saved as a whole ({ExceptionType}); applying it record by record", ex.GetType().Name);
            db.ChangeTracker.Clear();
            await apply(true);
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task ApplyCategoriesAsync(IReadOnlyList<CategoryDto> feed, bool perRecord, CancellationToken ct)
    {
        var existing = await db.CatalogCategories.ToDictionaryAsync(c => c.Id, ct);
        var slugs = existing.Values.ToDictionary(c => c.Slug, c => c.Id, StringComparer.Ordinal);
        var now = time.GetUtcNow().ToUniversalTime();
        foreach (var c in feed)
        {
            var row = existing.GetValueOrDefault(c.Id);
            var slug = SlugRules.Choose(c.Slug, c.Id, row?.Slug, slugs);
            if (row is null) { row = new CatalogCategory { Id = c.Id }; db.CatalogCategories.Add(row); existing[c.Id] = row; }
            else if (row.Slug != slug) slugs.Remove(row.Slug);
            if (row.UpdatedAt == default || row.ParentId != c.ParentId || row.Slug != slug || row.Name != c.Name || row.SortOrder != c.SortOrder) row.UpdatedAt = now;
            row.ParentId = c.ParentId; row.Slug = slug; row.Name = c.Name; row.SortOrder = c.SortOrder;
            slugs[slug] = c.Id;
            if (perRecord && !await TrySaveRecordAsync(c.Id, "category", ct))
            {
                existing.Remove(c.Id); slugs.Remove(slug);
                db.ChangeTracker.Clear();
                existing = await db.CatalogCategories.ToDictionaryAsync(x => x.Id, ct);
                slugs = existing.Values.ToDictionary(x => x.Slug, x => x.Id, StringComparer.Ordinal);
            }
        }
        if (!perRecord) await db.SaveChangesAsync(ct);
    }

    private async Task ApplyBrandsAsync(IReadOnlyList<BrandDto> feed, bool perRecord, CancellationToken ct)
    {
        var existing = await db.CatalogBrands.ToDictionaryAsync(b => b.Id, ct);
        var slugs = existing.Values.ToDictionary(b => b.Slug, b => b.Id, StringComparer.Ordinal);
        var now = time.GetUtcNow().ToUniversalTime();
        foreach (var b in feed)
        {
            var row = existing.GetValueOrDefault(b.Id);
            var slug = SlugRules.Choose(b.Slug, b.Id, row?.Slug, slugs);
            if (row is null) { row = new CatalogBrand { Id = b.Id }; db.CatalogBrands.Add(row); existing[b.Id] = row; }
            else if (row.Slug != slug) slugs.Remove(row.Slug);
            var renamed = row.Name != b.Name && !string.IsNullOrEmpty(row.Name);
            if (row.UpdatedAt == default || row.SellerId != b.SellerId || row.Slug != slug || row.Name != b.Name || row.LogoImageId != b.LogoImageId) row.UpdatedAt = now;
            row.SellerId = b.SellerId; row.Slug = slug; row.Name = b.Name; row.LogoImageId = b.LogoImageId;
            slugs[slug] = b.Id;
            if (renamed) await SearchIndexer.RenameBrandAsync(db, b.Id, b.Name, ct);
            if (perRecord && !await TrySaveRecordAsync(b.Id, "brand", ct))
            {
                db.ChangeTracker.Clear();
                existing = await db.CatalogBrands.ToDictionaryAsync(x => x.Id, ct);
                slugs = existing.Values.ToDictionary(x => x.Slug, x => x.Id, StringComparer.Ordinal);
            }
        }
        if (!perRecord) await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves what is tracked; a deterministic failure skips (and logs the id only), anything else propagates.</summary>
    private async Task<bool> TrySaveRecordAsync(Guid id, string what, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (Exception ex) when (SyncErrors.IsDeterministic(ex))
        {
            log.LogWarning("Skipping catalog {What} {Id}: it could not be saved ({ExceptionType})", what, id, ex.GetType().Name);
            return false;
        }
    }

    // ---------- seller retention (PII) ----------
    /// <summary>
    /// Seller display names can be a person's name. A seller is kept while it has a visible listing and for <see cref="CatalogOptions.SellerRetentionDays"/>
    /// (90) days after the last time it had one, then deleted; a seller that comes back is re-created by its next snapshot.
    /// </summary>
    private async Task MaintainSellersAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        await db.CatalogSellers.Where(s => s.Active && db.CatalogListings.Any(l => l.SellerId == s.Id && l.Visible))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastVisibleAt, now), ct);
        var cutoff = now - TimeSpan.FromDays(Math.Max(1, options.Value.SellerRetentionDays));
        // "visible" means shown to buyers: an active seller with a visible listing (refreshed just above). A suspended seller's listings keep their own Visible flag
        // (the seller's flag is joined at query time), so that flag alone must not keep the display name alive.
        await db.CatalogSellers.Where(s => s.LastVisibleAt < cutoff && !(s.Active && db.CatalogListings.Any(l => l.SellerId == s.Id && l.Visible))).ExecuteDeleteAsync(ct);
    }

    // ---------- poll ----------
    private async Task PollAsync(SyncCounts counts, CancellationToken ct)
    {
        var o = options.Value;
        var cursor = (await db.CatalogSyncStates.AsNoTracking().SingleAsync(s => s.Key == StateKey, ct)).Cursor;
        for (var pageNo = 0; pageNo < Math.Max(1, o.MaxPagesPerRun); pageNo++)
        {
            await RenewLeaseAsync(ct);
            var page = await source.GetListingsAsync(cursor, Math.Clamp(o.PageSize, 1, 500), ct);
            var next = page.NextCursor ?? cursor;
            // Documents the feed gave us that cannot be read are set aside FIRST (idempotent: a crash re-polls the page), so the page and its cursor still commit and one bad document never stops the run.
            foreach (var bad in page.Unreadable ?? []) await QuarantineAsync(bad.Id, bad.Version, bad.Reason, counts, ct);
            await ApplyPageAsync(page, next, counts, ct);
            cursor = next;
            if (!page.HasMore) break;
        }
    }

    /// <summary>
    /// The page and its cursor commit in one transaction: a crash re-polls the page, harmlessly. If the page cannot be saved as a whole (one record
    /// breaks a database rule, e.g. a variant id already used by another listing), the snapshots are applied one at a time and only the offender is
    /// skipped, so a single bad record can never stall the cursor.
    /// </summary>
    private async Task ApplyPageAsync(ListingPage page, string? next, SyncCounts counts, CancellationToken ct)
    {
        int applied = 0, skipped = 0;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var snapshot in page.Items)
                if (await ApplySnapshotAsync(snapshot, ct)) applied++; else skipped++;
            foreach (var t in page.Tombstones ?? []) if (await ApplyTombstoneAsync(t, ct)) applied++;
            await db.SaveChangesAsync(ct);
            await WriteCursorAsync(next, ct);       // inside the transaction: a lost lease rolls the whole page back
            await tx.CommitAsync(ct);
            counts.Applied += applied; counts.Skipped += skipped;
            db.ChangeTracker.Clear();
            return;
        }
        catch (Exception ex) when (SyncErrors.IsDeterministic(ex))
        {
            log.LogWarning("A catalog page could not be applied as a whole ({ExceptionType}); applying its {Count} listings one by one", ex.GetType().Name, page.Items.Count);
            db.ChangeTracker.Clear();
        }

        foreach (var t in page.Tombstones ?? [])
        {
            if (await ApplyTombstoneAsync(t, ct)) counts.Applied++;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        foreach (var snapshot in page.Items)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var ok = await ApplySnapshotAsync(snapshot, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                if (ok) counts.Applied++; else counts.Skipped++;
            }
            catch (Exception ex) when (SyncErrors.IsDeterministic(ex))
            {
                log.LogWarning("Skipping catalog listing {ListingId}: it could not be saved ({ExceptionType})", snapshot.Id, ex.GetType().Name);
                counts.Skipped++;
            }
            db.ChangeTracker.Clear();
        }
        await WriteCursorAsync(next, ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Moves the cursor only while we still own the lease: a run whose lease was taken over must not overwrite the new owner's progress.</summary>
    private async Task WriteCursorAsync(string? next, CancellationToken ct)
    {
        var owner = _owner;
        var rows = await db.CatalogSyncStates.Where(s => s.Key == StateKey && s.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Cursor, next), ct);
        if (rows != 1) throw new LeaseLostException();
    }

    // ---------- listings an event announced but we have not fetched ----------
    private async Task FetchPendingAsync(SyncCounts counts, CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().ToUniversalTime();
        // Rows that never apply (a snapshot that fails every time) back off instead of taking the same hundred slots on every run, so good rows behind them are reached.
        var due = await db.CatalogListings.AsNoTracking().Where(l => l.KnownVersion > l.Version && (l.NextFetchAt == null || l.NextFetchAt <= now))
            .OrderBy(l => l.NextFetchAt == null ? 0 : 1).ThenBy(l => l.NextFetchAt).ThenBy(l => l.KnownVersion)
            .Select(l => new { l.Id, l.FetchAttempts }).Take(Math.Max(1, o.MaxPendingFetchesPerRun)).ToListAsync(ct);
        foreach (var item in due)
        {
            await RenewLeaseAsync(ct);
            ListingSnapshot? snapshot;
            try { snapshot = await source.GetListingAsync(item.Id, ct); }     // a portal that is down throws: the run fails and the rows stay pending
            catch (UnreadableListingException bad)
            {
                // The portal answered, with a document we cannot read: that is not an outage and asking at once again changes nothing. Set it aside, back off (the portal may fix it), and go on with the others.
                db.ChangeTracker.Clear();
                await QuarantineAsync(item.Id, null, bad.Reason, counts, ct);
                await BackOffIfStillPendingAsync(item.Id, item.FetchAttempts, ct);
                continue;
            }
            db.ChangeTracker.Clear();
            if (snapshot is null) { await MarkRemovedAsync(item.Id, ct); continue; }
            try
            {
                if (await ApplySnapshotAsync(snapshot, ct)) counts.Fetched++;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (SyncErrors.IsDeterministic(ex))
            {
                log.LogWarning("Skipping catalog listing {ListingId}: it could not be saved ({ExceptionType})", item.Id, ex.GetType().Name);
                counts.Skipped++;
            }
            db.ChangeTracker.Clear();
            await BackOffIfStillPendingAsync(item.Id, item.FetchAttempts, ct);
        }
        counts.Pending = await db.CatalogListings.CountAsync(l => l.KnownVersion > l.Version, ct);
    }

    /// <summary>
    /// Sets a listing document we cannot read aside (see <see cref="CatalogQuarantine"/>): one row per listing with the short reason, and a listing we already hold is HIDDEN (status <c>Quarantined</c>) until a readable
    /// version arrives, because its newer document cannot be shown and the old one may be wrong. Never the document, only the id and the code, in the log. No id means nothing to record: it is only counted.
    /// </summary>
    private async Task QuarantineAsync(Guid? id, long? version, string reason, SyncCounts counts, CancellationToken ct)
    {
        counts.Quarantined++; counts.Skipped++;
        if (id is not { } listingId) { log.LogWarning("Set aside a catalog document without a readable id ({Reason})", reason); return; }
        var now = time.GetUtcNow().ToUniversalTime();
        var row = await db.CatalogQuarantine.FirstOrDefaultAsync(q => q.ListingId == listingId, ct);
        if (row is null) db.CatalogQuarantine.Add(new CatalogQuarantine { ListingId = listingId, Reason = reason, Version = version, FirstSeenAt = now, LastSeenAt = now, Seen = 1 });
        else { row.Reason = reason; row.Version = version ?? row.Version; row.LastSeenAt = now; row.Seen++; }
        var listing = await db.CatalogListings.FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is { Visible: true }) { listing.Status = "Quarantined"; listing.RecomputeVisibility(); listing.Row++; }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        log.LogWarning("Set aside catalog listing {ListingId}: its document cannot be read ({Reason})", listingId, reason);
    }

    /// <summary>A listing that applied readable, or was removed, is no longer set aside.</summary>
    private Task ClearQuarantineAsync(Guid id, CancellationToken ct) => db.CatalogQuarantine.Where(q => q.ListingId == id).ExecuteDeleteAsync(ct);

    /// <summary>The wait before the next try of a pending fetch: 1, 2, 4 ... minutes, at most 6 hours.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Clamp(attempts, 1, 10) - 1)));

    private async Task BackOffIfStillPendingAsync(Guid id, int previousAttempts, CancellationToken ct)
    {
        var attempts = previousAttempts + 1;
        var next = time.GetUtcNow().ToUniversalTime() + Backoff(attempts);
        await db.CatalogListings.Where(l => l.Id == id && l.KnownVersion > l.Version)
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.FetchAttempts, attempts).SetProperty(x => x.NextFetchAt, (DateTimeOffset?)next), ct);
    }

    /// <summary>The portal answered "no such listing" for a version we were told about: hide it and stop asking for that version.</summary>
    private async Task MarkRemovedAsync(Guid id, CancellationToken ct)
    {
        var l = await db.CatalogListings.FirstOrDefaultAsync(x => x.Id == id, ct);
        await ClearQuarantineAsync(id, ct);   // gone at the portal: nothing left to set aside
        if (l is null || l.KnownVersion <= l.Version) return;
        l.Status = "Removed";
        l.Version = l.KnownVersion;
        l.RecomputeVisibility();
        l.Row++;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// A feed tombstone for a listing we hold hides it at that version (the same state an unlist event leaves, and finished: nothing is left to fetch). A newer
    /// snapshot (a re-listing) shows it again. A listing we never held stays unknown, because there is nothing to hide. The caller saves.
    /// </summary>
    private async Task<bool> ApplyTombstoneAsync(ListingTombstone t, CancellationToken ct)
    {
        if (t.Version < 0) return false;
        await ClearQuarantineAsync(t.Id, ct);   // unlisted or gone at the portal: nothing left to set aside
        var l = await db.CatalogListings.FirstOrDefaultAsync(x => x.Id == t.Id, ct);
        if (l is null) return false;
        if (t.Version == ListingTombstone.UnknownVersion)
        {
            // an unreadable version: hide at the version we hold (idempotent); any newer snapshot, a re-listing, shows it again
            if (l.IsUnlisted) return false;
            l.UnlistedVersion = Math.Max(l.UnlistedVersion ?? 0, l.Version);
            l.RecomputeVisibility();
            l.Row++;
            return true;
        }
        if (t.Version < l.Version) return false;                                  // older than what we hold: stale
        l.KnownVersion = Math.Max(l.KnownVersion, t.Version);
        l.UnlistedVersion = Math.Max(l.UnlistedVersion ?? 0, t.Version);
        l.Version = Math.Max(l.Version, t.Version);
        l.FetchAttempts = 0; l.NextFetchAt = null;
        l.RecomputeVisibility();
        l.Row++;
        return true;
    }

    // ---------- apply ----------
    /// <summary>
    /// Applies one snapshot if it is newer than what we hold. Returns false for a stale or invalid snapshot (invalid ones are logged by id and
    /// skipped, so one bad record can never block the cursor). The caller saves.
    /// </summary>
    public async Task<bool> ApplySnapshotAsync(ListingSnapshot s, CancellationToken ct)
    {
        if (!IsValid(s, out var why)) { log.LogWarning("Skipping an invalid catalog snapshot ({Why}) for listing {ListingId}", why, s.Id); return false; }
        // four collection includes in one query multiply (variants x images x attributes x FAQs rows): always split
        var listing = await db.CatalogListings.Include(l => l.Variants).Include(l => l.Images).Include(l => l.Attributes).Include(l => l.Faqs).AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == s.Id, ct);
        if (listing is not null && s.Version <= listing.Version)
        {
            if (s.Version > listing.KnownVersion) { listing.KnownVersion = s.Version; listing.Row++; }
            return false;   // stale or duplicate
        }
        if (!await db.CatalogCategories.AnyAsync(c => c.Id == s.CategoryId, ct) && !db.CatalogCategories.Local.Any(c => c.Id == s.CategoryId))
        {
            // The category feed may be late or failing. Dropping the snapshot would lose the listing (the cursor moves on), so it is held as a hidden
            // stub that knows the version it is waiting for; the pending fetch (with backoff) applies it once the category exists.
            log.LogWarning("Holding catalog listing {ListingId}: its category is not known yet", s.Id);
            if (listing is null) { listing = CatalogEventHandler.NewStub(s.Id, time.GetUtcNow().ToUniversalTime()); db.CatalogListings.Add(listing); }
            listing.KnownVersion = Math.Max(listing.KnownVersion, s.Version);
            listing.Row++;
            return false;
        }

        await UpsertSellerAndBrandAsync(s, ct);
        var now = time.GetUtcNow().ToUniversalTime();
        if (listing is null)
        {
            listing = new CatalogListing { Id = s.Id, FirstSeenAt = now, Slug = await UniqueSlugAsync(s.Title, s.Id, ct) };
            db.CatalogListings.Add(listing);
        }
        else if (listing.IsStub) listing.Slug = await UniqueSlugAsync(SnapshotLimits.Clip(s.Title, SnapshotLimits.Title), s.Id, ct);   // a stub made from an event has a placeholder slug

        // oversized text is cut to its column rather than dropping the whole listing
        var title = SnapshotLimits.Clip(s.Title, SnapshotLimits.Title);
        listing.Type = s.Type; listing.Title = title; listing.TitleSort = Slug.From(title); listing.Summary = SnapshotLimits.Clip(s.Summary, SnapshotLimits.Summary); listing.Description = SnapshotLimits.Clip(s.Description, SnapshotLimits.Description);
        listing.SellerId = s.Seller.Id; listing.BrandId = s.Brand.Id; listing.CategoryId = s.CategoryId;
        listing.Status = s.Status; listing.Currency = s.Currency.Trim().ToUpperInvariant();   // ISO 4217 in upper case, whatever the feed sends (review L5)
        listing.CreatedAt = s.CreatedAt.ToUniversalTime(); listing.UpdatedAt = s.UpdatedAt.ToUniversalTime();
        listing.Version = s.Version;
        listing.KnownVersion = Math.Max(listing.KnownVersion, s.Version);
        listing.IsStub = false;
        listing.IsDemoListing = s.Demo;
        listing.LengthMm = s.Dimensions?.L; listing.WidthMm = s.Dimensions?.W; listing.HeightMm = s.Dimensions?.H;
        listing.FetchAttempts = 0; listing.NextFetchAt = null;
        listing.Row++;

        var basis = s.Variants.Any(v => v.Available) ? s.Variants.Where(v => v.Available).ToList() : s.Variants.ToList();
        listing.PriceFromCents = basis.Min(v => v.PriceCents);
        listing.PriceToCents = basis.Max(v => v.PriceCents);
        listing.CompareAtFromCents = basis.Where(v => v.CompareAtCents > v.PriceCents).Select(v => v.CompareAtCents).Min();
        listing.Available = s.Variants.Any(v => v.Available);
        listing.RecomputeVisibility();
        // (the wishlist watch is queued after the variants are in the unit of work, below)

        var brandName = (await db.CatalogBrands.FindAsync([s.Brand.Id], ct))?.Name ?? s.Brand.Name;
        SearchIndexer.Upsert(db, s.Id, s.Brand.Id, title, brandName, s.SearchTerms, listing.Description);
        SyncVariants(listing, s);
        if (listing.Visible) await Lists.WishlistWatchQueue.QueueAsync(db, time, listing.Id, listing.Version, ct);
        SyncImages(listing, s);
        await ClearQuarantineAsync(s.Id, ct);   // readable now
        db.CatalogAttributes.RemoveRange(listing.Attributes);
        listing.Attributes = s.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Value)).Take(SnapshotLimits.MaxAttributes)
            .Select((a, i) => new CatalogAttribute { ListingId = s.Id, Key = SnapshotLimits.Clip(a.Key, SnapshotLimits.AttributeKey), Value = SnapshotLimits.Clip(a.Value, SnapshotLimits.AttributeValue), SortOrder = i }).ToList();
        db.CatalogFaqs.RemoveRange(listing.Faqs);
        listing.Faqs = s.Faqs.Where(f => !string.IsNullOrWhiteSpace(f.Question)).Take(SnapshotLimits.MaxFaqs)
            .Select((f, i) => new CatalogFaq { ListingId = s.Id, Question = SnapshotLimits.Clip(f.Question, SnapshotLimits.Question), Answer = f.Answer ?? "", SortOrder = i }).ToList();
        return true;
    }

    private static bool IsValid(ListingSnapshot s, out string why)
    {
        why = "";
        if (!ListingTypes.IsValid(s.Type)) why = "type";
        else if (string.IsNullOrWhiteSpace(s.Title)) why = "title";
        else if (string.IsNullOrWhiteSpace(s.Status)) why = "status";
        else if (s.Version < 1) why = "version";
        else if (s.Variants.Count == 0) why = "no variants";
        else if (s.Variants.Count > SnapshotLimits.MaxVariants) why = "too many variants";
        else if (s.Variants.Select(v => v.Id).Distinct().Count() != s.Variants.Count) why = "duplicate variant ids";
        else if (s.Variants.Any(v => v.PriceCents < 0)) why = "negative price";
        else if (s.Currency?.Trim() is not { Length: 3 } cur || !cur.All(char.IsAsciiLetter)) why = "currency";
        return why.Length == 0;
    }

    private async Task UpsertSellerAndBrandAsync(ListingSnapshot s, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        // Compared and stored at millisecond precision: SQLite keeps times as a binary long with millisecond resolution and PostgreSQL keeps microseconds, so only
        // a coarse common precision makes "the same time" mean the same on both (a tie applies; strictly older does not).
        var utc = s.UpdatedAt.ToUniversalTime();
        var stateAt = new DateTimeOffset(utc.UtcTicks - utc.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var seller = await db.CatalogSellers.FindAsync([s.Seller.Id], ct);
        if (seller is null) db.CatalogSellers.Add(new CatalogSeller { Id = s.Seller.Id, DisplayName = SnapshotLimits.Clip(s.Seller.DisplayName, SnapshotLimits.DisplayName), Active = s.Seller.Active, StateAt = stateAt, LastVisibleAt = now });
        else if (stateAt >= seller.StateAt)   // the seller's state is ordered by the portal time of the snapshot that carried it: an older page cannot reactivate a suspended seller
        { seller.DisplayName = SnapshotLimits.Clip(s.Seller.DisplayName, SnapshotLimits.DisplayName); seller.Active = s.Seller.Active; seller.StateAt = stateAt; }
        if (await db.CatalogBrands.FindAsync([s.Brand.Id], ct) is null)   // normally supplied by the brand feed; keep the listing usable if it is late
        {
            var slug = Slug.From(s.Brand.Name);
            if (await db.CatalogBrands.AnyAsync(b => b.Slug == slug, ct) || db.CatalogBrands.Local.Any(b => b.Slug == slug)) slug += "-" + s.Brand.Id.ToString("N")[..6];
            db.CatalogBrands.Add(new CatalogBrand { Id = s.Brand.Id, SellerId = s.Seller.Id, Name = SnapshotLimits.Clip(s.Brand.Name, SnapshotLimits.DisplayName), Slug = SnapshotLimits.Clip(slug, SnapshotLimits.Slug), UpdatedAt = now });
        }
    }

    private async Task<string> UniqueSlugAsync(string title, Guid id, CancellationToken ct)
    {
        var baseSlug = Slug.From(title);
        var n = id.ToString("N");
        foreach (var candidate in new[] { baseSlug, $"{baseSlug}-{n[..6]}", $"{baseSlug}-{n[..12]}", $"{baseSlug}-{n}" })
            if (!await db.CatalogListings.AnyAsync(l => l.Slug == candidate, ct) && !db.CatalogListings.Local.Any(l => l.Slug == candidate)) return candidate;
        return $"{baseSlug}-{n}";
    }

    private void SyncVariants(CatalogListing listing, ListingSnapshot s)
    {
        var incoming = s.Variants.Select((v, i) => (v, i)).ToList();
        foreach (var gone in listing.Variants.Where(e => incoming.All(x => x.v.Id != e.Id)).ToList()) { listing.Variants.Remove(gone); db.CatalogVariants.Remove(gone); }
        if (incoming.Any(x => x.v.PriceCents > SnapshotLimits.MaxUnitPriceCents))
            log.LogWarning("Listing {ListingId} has a variant priced above {Max} cents: it is stored as not purchasable", s.Id, SnapshotLimits.MaxUnitPriceCents);
        foreach (var (v, i) in incoming)
        {
            var row = listing.Variants.FirstOrDefault(e => e.Id == v.Id);
            if (row is null) { row = new CatalogVariant { Id = v.Id, ListingId = s.Id }; listing.Variants.Add(row); }
            row.Name = SnapshotLimits.Clip(v.Name, SnapshotLimits.Name); row.PriceCents = v.PriceCents; row.CompareAtCents = v.CompareAtCents; row.Available = v.Available && v.PriceCents <= SnapshotLimits.MaxUnitPriceCents; row.IsDefault = v.IsDefault; row.WeightGrams = v.WeightGrams is > 0 ? v.WeightGrams : null; row.SortOrder = i;
        }
    }

    private void SyncImages(CatalogListing listing, ListingSnapshot s)
    {
        var incoming = s.Images.GroupBy(im => im.Id).Select(g => g.First()).Take(SnapshotLimits.MaxImages).Select((im, i) => (im, i)).ToList();
        foreach (var gone in listing.Images.Where(e => incoming.All(x => x.im.Id != e.Id)).ToList()) { listing.Images.Remove(gone); db.CatalogImages.Remove(gone); }
        foreach (var (im, i) in incoming)
        {
            var row = listing.Images.FirstOrDefault(e => e.Id == im.Id);
            if (row is null) { row = new CatalogImage { Id = im.Id, ListingId = s.Id }; listing.Images.Add(row); }
            row.Width = im.Width; row.Height = im.Height; row.Alt = SnapshotLimits.Clip(im.Alt, SnapshotLimits.Alt); row.SortOrder = i;
        }
    }
}

public sealed class CatalogSyncWorker(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<CatalogSyncWorker> log) : BackgroundService
{
    /// <summary>The wait after <paramref name="failures"/> failed runs in a row: the normal interval, doubled each time, at most 10 minutes (a down portal or database is not hammered).</summary>
    public static TimeSpan Delay(int intervalSeconds, int failures)
        => TimeSpan.FromSeconds(Math.Min(600, Math.Max(5, intervalSeconds) * Math.Pow(2, Math.Min(failures, 10))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<CatalogSyncService>().RunAsync(stoppingToken);
                failures = result.Error is null ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { failures++; log.LogError("Catalog sync worker failed: {ExceptionType}", ex.GetType().FullName); }
            try { await Task.Delay(Delay(options.Value.SyncIntervalSeconds, failures), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
