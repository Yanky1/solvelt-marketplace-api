using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public sealed class SyncCounts { public int Applied, Skipped, Fetched, Pending; }

public sealed record CatalogSyncResult(bool Ran, int Applied, int Skipped, int Pending, int Fetched, string? Error);

/// <summary>
/// Keeps the read model in step with <see cref="ICatalogSource"/>: reference data (categories, brands), then a cursor-based poll of the
/// listing feed, then a fetch of the listings that events said have a newer version than we hold. Everything is versioned by the portal's
/// <c>version</c>: a snapshot is applied only when newer than what we have, so the job is idempotent, may overlap with events, and does not
/// care about order (CONTRACT.md §2). One instance runs at a time (a lease row). The cursor moves in the same transaction as the page.
/// </summary>
public sealed class CatalogSyncService(MarketplaceDb db, ICatalogSource source, TimeProvider time, IOptions<CatalogOptions> options, ILogger<CatalogSyncService> log)
{
    public const string StateKey = "listings";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<CatalogSyncResult> RunAsync(CancellationToken ct = default)
    {
        if (!await TryAcquireLeaseAsync(ct)) return new(false, 0, 0, 0, 0, null);
        var counts = new SyncCounts();
        string? error = null;
        try
        {
            await SyncReferenceDataAsync(ct);
            await PollAsync(counts, ct);
            await FetchPendingAsync(counts, ct);
            await SearchIndexer.BackfillAsync(db, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.GetType().Name;   // never the message: it may carry portal payload
            log.LogError("Catalog sync failed: {ExceptionType}", ex.GetType().FullName);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow().ToUniversalTime();
            var err = error;
            await db.CatalogSyncStates.Where(s => s.Key == StateKey).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LastRunAt, (DateTimeOffset?)now)
                .SetProperty(x => x.LastError, err)
                .SetProperty(x => x.LastSuccessAt, x => err == null ? now : x.LastSuccessAt), CancellationToken.None);
        }
        log.LogInformation("Catalog sync: {Applied} applied, {Skipped} skipped, {Fetched} fetched, {Pending} still pending", counts.Applied, counts.Skipped, counts.Fetched, counts.Pending);
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
        var rows = await db.CatalogSyncStates.Where(s => s.Key == StateKey && (s.LeaseUntil == null || s.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)until), ct);
        return rows == 1;
    }

    // ---------- reference data ----------
    private async Task SyncReferenceDataAsync(CancellationToken ct)
    {
        var categories = await source.GetCategoriesAsync(ct);
        var brands = await source.GetBrandsAsync(ct);
        var existingCategories = await db.CatalogCategories.ToDictionaryAsync(c => c.Id, ct);
        foreach (var c in categories)
        {
            if (string.IsNullOrWhiteSpace(c.Slug) || string.IsNullOrWhiteSpace(c.Name)) continue;
            if (!existingCategories.TryGetValue(c.Id, out var row)) { row = new CatalogCategory { Id = c.Id }; db.CatalogCategories.Add(row); existingCategories[c.Id] = row; }
            row.ParentId = c.ParentId; row.Slug = c.Slug; row.Name = c.Name; row.SortOrder = c.SortOrder;
        }
        var existingBrands = await db.CatalogBrands.ToDictionaryAsync(b => b.Id, ct);
        foreach (var b in brands)
        {
            if (string.IsNullOrWhiteSpace(b.Slug) || string.IsNullOrWhiteSpace(b.Name)) continue;
            if (!existingBrands.TryGetValue(b.Id, out var row)) { row = new CatalogBrand { Id = b.Id }; db.CatalogBrands.Add(row); existingBrands[b.Id] = row; }
            var renamed = row.Name != b.Name && !string.IsNullOrEmpty(row.Name);
            row.SellerId = b.SellerId; row.Slug = b.Slug; row.Name = b.Name; row.LogoImageId = b.LogoImageId;
            if (renamed) await SearchIndexer.RenameBrandAsync(db, b.Id, b.Name, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------- poll ----------
    private async Task PollAsync(SyncCounts counts, CancellationToken ct)
    {
        var o = options.Value;
        var cursor = (await db.CatalogSyncStates.AsNoTracking().SingleAsync(s => s.Key == StateKey, ct)).Cursor;
        for (var pageNo = 0; pageNo < Math.Max(1, o.MaxPagesPerRun); pageNo++)
        {
            var page = await source.GetListingsAsync(cursor, Math.Clamp(o.PageSize, 1, 500), ct);
            var next = page.NextCursor ?? cursor;
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
            (await db.CatalogSyncStates.SingleAsync(s => s.Key == StateKey, ct)).Cursor = next;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            counts.Applied += applied; counts.Skipped += skipped;
            db.ChangeTracker.Clear();
            return;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            log.LogWarning("A catalog page could not be applied as a whole ({ExceptionType}); applying its {Count} listings one by one", ex.GetType().Name, page.Items.Count);
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
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                log.LogWarning("Skipping catalog listing {ListingId}: it could not be saved ({ExceptionType})", snapshot.Id, ex.GetType().Name);
                counts.Skipped++;
            }
            db.ChangeTracker.Clear();
        }
        (await db.CatalogSyncStates.SingleAsync(s => s.Key == StateKey, ct)).Cursor = next;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    // ---------- listings an event announced but we have not fetched ----------
    private async Task FetchPendingAsync(SyncCounts counts, CancellationToken ct)
    {
        var o = options.Value;
        var ids = await db.CatalogListings.AsNoTracking().Where(l => l.KnownVersion > l.Version)
            .OrderBy(l => l.KnownVersion).Select(l => l.Id).Take(Math.Max(1, o.MaxPendingFetchesPerRun)).ToListAsync(ct);
        foreach (var id in ids)
        {
            var snapshot = await source.GetListingAsync(id, ct);
            db.ChangeTracker.Clear();
            if (snapshot is null) { await MarkRemovedAsync(id, ct); continue; }
            try
            {
                if (await ApplySnapshotAsync(snapshot, ct)) counts.Fetched++;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                log.LogWarning("Skipping catalog listing {ListingId}: it could not be saved ({ExceptionType})", id, ex.GetType().Name);
                counts.Skipped++;
            }
            db.ChangeTracker.Clear();
        }
        counts.Pending = await db.CatalogListings.CountAsync(l => l.KnownVersion > l.Version, ct);
    }

    /// <summary>The portal answered "no such listing" for a version we were told about: hide it and stop asking for that version.</summary>
    private async Task MarkRemovedAsync(Guid id, CancellationToken ct)
    {
        var l = await db.CatalogListings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null || l.KnownVersion <= l.Version) return;
        l.Status = "Removed";
        l.Version = l.KnownVersion;
        l.RecomputeVisibility();
        l.Row++;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    // ---------- apply ----------
    /// <summary>
    /// Applies one snapshot if it is newer than what we hold. Returns false for a stale or invalid snapshot (invalid ones are logged by id and
    /// skipped, so one bad record can never block the cursor). The caller saves.
    /// </summary>
    public async Task<bool> ApplySnapshotAsync(ListingSnapshot s, CancellationToken ct)
    {
        if (!IsValid(s, out var why)) { log.LogWarning("Skipping an invalid catalog snapshot ({Why}) for listing {ListingId}", why, s.Id); return false; }
        var listing = await db.CatalogListings.Include(l => l.Variants).Include(l => l.Images).Include(l => l.Attributes).Include(l => l.Faqs)
            .FirstOrDefaultAsync(l => l.Id == s.Id, ct);
        if (listing is not null && s.Version <= listing.Version)
        {
            if (s.Version > listing.KnownVersion) { listing.KnownVersion = s.Version; listing.Row++; }
            return false;   // stale or duplicate
        }
        if (!await db.CatalogCategories.AnyAsync(c => c.Id == s.CategoryId, ct) && !db.CatalogCategories.Local.Any(c => c.Id == s.CategoryId))
        {
            log.LogWarning("Skipping catalog listing {ListingId}: unknown category", s.Id);
            return false;
        }

        await UpsertSellerAndBrandAsync(s, ct);
        var now = time.GetUtcNow().ToUniversalTime();
        if (listing is null)
        {
            listing = new CatalogListing { Id = s.Id, FirstSeenAt = now, Slug = await UniqueSlugAsync(s.Title, s.Id, ct) };
            db.CatalogListings.Add(listing);
        }
        else if (listing.IsStub) listing.Slug = await UniqueSlugAsync(s.Title, s.Id, ct);   // a stub made from an event has a placeholder slug

        listing.Type = s.Type; listing.Title = s.Title; listing.TitleSort = Slug.From(s.Title); listing.Summary = s.Summary; listing.Description = s.Description;
        listing.SellerId = s.Seller.Id; listing.BrandId = s.Brand.Id; listing.CategoryId = s.CategoryId;
        listing.Status = s.Status; listing.Currency = s.Currency;
        listing.CreatedAt = s.CreatedAt.ToUniversalTime(); listing.UpdatedAt = s.UpdatedAt.ToUniversalTime();
        listing.Version = s.Version;
        listing.KnownVersion = Math.Max(listing.KnownVersion, s.Version);
        listing.IsStub = false;
        listing.Row++;

        var basis = s.Variants.Any(v => v.Available) ? s.Variants.Where(v => v.Available).ToList() : s.Variants.ToList();
        listing.PriceFromCents = basis.Min(v => v.PriceCents);
        listing.PriceToCents = basis.Max(v => v.PriceCents);
        listing.CompareAtFromCents = basis.Where(v => v.CompareAtCents > v.PriceCents).Select(v => v.CompareAtCents).Min();
        listing.Available = s.Variants.Any(v => v.Available);
        listing.RecomputeVisibility();

        var brandName = (await db.CatalogBrands.FindAsync([s.Brand.Id], ct))?.Name ?? s.Brand.Name;
        SearchIndexer.Upsert(db, s.Id, s.Brand.Id, s.Title, brandName, s.SearchTerms, s.Description);
        SyncVariants(listing, s);
        SyncImages(listing, s);
        db.CatalogAttributes.RemoveRange(listing.Attributes);
        listing.Attributes = s.Attributes.Select((a, i) => new CatalogAttribute { ListingId = s.Id, Key = a.Key, Value = a.Value, SortOrder = i }).ToList();
        db.CatalogFaqs.RemoveRange(listing.Faqs);
        listing.Faqs = s.Faqs.Select((f, i) => new CatalogFaq { ListingId = s.Id, Question = f.Question, Answer = f.Answer, SortOrder = i }).ToList();
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
        else if (s.Variants.Any(v => v.PriceCents < 0)) why = "negative price";
        else if (s.Currency is not { Length: 3 }) why = "currency";
        return why.Length == 0;
    }

    private async Task UpsertSellerAndBrandAsync(ListingSnapshot s, CancellationToken ct)
    {
        var seller = await db.CatalogSellers.FindAsync([s.Seller.Id], ct);
        if (seller is null) db.CatalogSellers.Add(new CatalogSeller { Id = s.Seller.Id, DisplayName = s.Seller.DisplayName, Active = s.Seller.Active });
        else { seller.DisplayName = s.Seller.DisplayName; seller.Active = s.Seller.Active; }
        if (await db.CatalogBrands.FindAsync([s.Brand.Id], ct) is null)   // normally supplied by the brand feed; keep the listing usable if it is late
        {
            var slug = Slug.From(s.Brand.Name);
            if (await db.CatalogBrands.AnyAsync(b => b.Slug == slug, ct) || db.CatalogBrands.Local.Any(b => b.Slug == slug)) slug += "-" + s.Brand.Id.ToString("N")[..6];
            db.CatalogBrands.Add(new CatalogBrand { Id = s.Brand.Id, SellerId = s.Seller.Id, Name = s.Brand.Name, Slug = slug });
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
        foreach (var (v, i) in incoming)
        {
            var row = listing.Variants.FirstOrDefault(e => e.Id == v.Id);
            if (row is null) { row = new CatalogVariant { Id = v.Id, ListingId = s.Id }; listing.Variants.Add(row); }
            row.Name = v.Name; row.PriceCents = v.PriceCents; row.CompareAtCents = v.CompareAtCents; row.Available = v.Available; row.SortOrder = i;
        }
    }

    private void SyncImages(CatalogListing listing, ListingSnapshot s)
    {
        var incoming = s.Images.Select((im, i) => (im, i)).ToList();
        foreach (var gone in listing.Images.Where(e => incoming.All(x => x.im.Id != e.Id)).ToList()) { listing.Images.Remove(gone); db.CatalogImages.Remove(gone); }
        foreach (var (im, i) in incoming)
        {
            var row = listing.Images.FirstOrDefault(e => e.Id == im.Id);
            if (row is null) { row = new CatalogImage { Id = im.Id, ListingId = s.Id }; listing.Images.Add(row); }
            row.Width = im.Width; row.Height = im.Height; row.Alt = im.Alt; row.SortOrder = i;
        }
    }
}

public sealed class CatalogSyncWorker(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, ILogger<CatalogSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.SyncIntervalSeconds)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CatalogSyncService>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Catalog sync worker failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
