using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Carts;

public sealed record CartOwner(Guid? AccountId, string? GuestToken);

public static class CartLimits
{
    public const int MaxQuantityPerLine = 20, MaxLines = 50;
    /// <summary>A unit price above this is treated as unavailable and an order total above <see cref="MaxTotalCents"/> is refused (decision M3-22): keeps every sum far from overflow and inside Stripe's limit.</summary>
    public const long MaxUnitPriceCents = 99_999_999, MaxTotalCents = 99_999_999;
    /// <summary>The marketplace sells in one currency (decision M3-16). A line whose listing is priced in anything else is unavailable.</summary>
    public const string Currency = "USD";
}

public static class CartLineStatus
{
    /// <summary>Can be bought now at <c>unitPriceCents</c>.</summary>
    public const string Ok = "ok";
    /// <summary>Listing hidden or unlisted, seller inactive, variant sold out or gone. Counts for nothing and blocks checkout until removed.</summary>
    public const string Unavailable = "unavailable";
}

public sealed record CartLineView(Guid Id, Guid ListingId, Guid VariantId, string Slug, string Title, string VariantName, Guid? ImageId, int Quantity,
    long UnitPriceCents, long? CompareAtCents, long LineTotalCents, string Status, long? PreviousUnitPriceCents);
/// <summary>One group per (seller, brand), exactly how the portal splits seller orders (CONTRACT.md §3).</summary>
public sealed record CartGroupView(Guid SellerId, string SellerName, Guid BrandId, string BrandName, long SubtotalCents, IReadOnlyList<CartLineView> Lines);
public sealed record CartView(string Currency, IReadOnlyList<CartGroupView> Groups, int ItemCount, long SubtotalCents, bool CanCheckout, string? CartToken = null);

public enum AddStatus { Ok, NotFound, NotPurchasable, QuantityLimit, TooManyLines, CurrencyMismatch, Busy }

/// <summary>Test seams (one instance per application, so parallel tests never see each other's): empty in production.</summary>
public sealed class CartHooks
{
    /// <summary>Awaited just before a new cart row is inserted, so a test can make two requests reach the insert together.</summary>
    public Func<Task>? BeforeCartInsert { get; set; }
    /// <summary>Awaited by a merge after it took its locks and before it reads the guest lines.</summary>
    public Func<Task>? InsideMerge { get; set; }
    /// <summary>Awaited by a quantity change or a removal right after it took the cart's lock, inside its transaction (the lock is held until the write is done).</summary>
    public Func<Task>? AfterLineRead { get; set; }
}

public sealed class CartService(MarketplaceDb db, TimeProvider time, CartHooks hooks)
{
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    /// <summary>
    /// Takes the cart's row lock by updating it (every writer of a cart's lines starts here: add, merge, quantity change; purge deletes the row, which takes the same lock) and stamps
    /// <c>UpdatedAt</c>. False means the cart no longer exists (merged into a buyer's, purged): the caller starts over with a fresh cart. This one statement is what serialises an add
    /// against a merge or a purge, makes the 50-line cap atomic, and keeps retention honest.
    /// </summary>
    private async Task<bool> LockAsync(Guid cartId, CancellationToken ct)
    {
        var now = Now;
        return await db.Carts.Where(c => c.Id == cartId).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), ct) == 1;
    }

    /// <summary>True when the owner's cart has any line created by "Fill with demo data" (M9-04): checkout and the quote refuse such a cart.</summary>
    public Task<bool> HasDemoLinesAsync(CartOwner o, CancellationToken ct)
        => db.CartLines.AsNoTracking().AnyAsync(l => l.IsDemo && Find(o).Select(c => c.Id).Contains(l.CartId), ct);

    private IQueryable<Data.Cart> Find(CartOwner o)
        => o.AccountId is { } a ? db.Carts.Where(c => c.AccountId == a)
         : o.GuestToken is { Length: > 0 and <= 100 } t ? db.Carts.Where(c => c.GuestTokenHash == Tokens.Hash(t)) : db.Carts.Where(_ => false);

    /// <summary>The owner's cart, created when missing. A guest never chooses a token: an unknown one gets a new cart and a new token (returned once).</summary>
    private async Task<(Data.Cart Cart, string? NewToken)> GetOrCreateAsync(CartOwner o, CancellationToken ct)
    {
        var existing = await Find(o).FirstOrDefaultAsync(ct);
        if (existing is not null) return (existing, null);
        var now = Now;
        var cart = new Data.Cart { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        string? token = null;
        if (o.AccountId is { } a) cart.AccountId = a;
        else { token = Tokens.New(); cart.GuestTokenHash = Tokens.Hash(token); }
        db.Carts.Add(cart);
        if (hooks.BeforeCartInsert is { } hook) await hook();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (o.AccountId is not null)
        {
            db.ChangeTracker.Clear();   // a parallel request created this buyer's cart first
            return (await Find(o).FirstAsync(ct), null);
        }
        return (cart, token);
    }

    public Task<Guid?> CartIdAsync(CartOwner o, CancellationToken ct) => Find(o).AsNoTracking().Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    public async Task<CartView> GetAsync(CartOwner o, CancellationToken ct)
    {
        var cart = await Find(o).AsNoTracking().FirstOrDefaultAsync(ct);
        return cart is null ? Empty() : await BuildAsync(cart.Id, null, true, ct);
    }

    public async Task<(AddStatus Status, CartView? View)> AddAsync(CartOwner o, Guid variantId, int quantity, CancellationToken ct)
    {
        if (quantity < 1 || quantity > CartLimits.MaxQuantityPerLine) return (AddStatus.QuantityLimit, null);
        var item = await (from v in db.CatalogVariants.AsNoTracking()
                          join l in db.CatalogListings.AsNoTracking() on v.ListingId equals l.Id
                          join s in db.CatalogSellers.AsNoTracking() on l.SellerId equals s.Id
                          where v.Id == variantId
                          select new { v.Id, v.ListingId, v.PriceCents, v.Available, l.Visible, l.Type, l.Currency, SellerActive = s.Active }).FirstOrDefaultAsync(ct);
        // Hidden listings are "not found" (no hint that they exist); services are booked, never carted (M4).
        if (item is null || !item.Visible || !item.SellerActive) return (AddStatus.NotFound, null);
        if (item.Type != ListingTypes.Product || !item.Available) return (AddStatus.NotPurchasable, null);

        if (item.Currency != CartLimits.Currency) return (AddStatus.CurrencyMismatch, null);
        var max = CartLimits.MaxQuantityPerLine;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (cart, token) = await GetOrCreateAsync(o, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Under the cart's lock: the line count, the insert and the answer all see one consistent cart. A cart that vanished (merged, purged) is replaced, never written to.
            if (!await LockAsync(cart.Id, ct)) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); continue; }
            var count = await db.CartLines.CountAsync(l => l.CartId == cart.Id, ct);
            var has = await db.CartLines.AnyAsync(l => l.CartId == cart.Id && l.VariantId == variantId, ct);
            if (!has && count >= CartLimits.MaxLines) { await tx.RollbackAsync(ct); return (AddStatus.TooManyLines, null); }
            // One atomic statement: a parallel add of the same variant adds to the same row instead of failing or losing a quantity (capped at the limit).
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cart_lines ("Id", "CartId", "ListingId", "VariantId", "Quantity", "SeenUnitPriceCents", "AddedAtMs")
                VALUES ({Guid.NewGuid()}, {cart.Id}, {item.ListingId}, {variantId}, {quantity}, {item.PriceCents}, {Now.ToUnixTimeMilliseconds()})
                ON CONFLICT ("CartId", "VariantId") DO UPDATE SET
                  "Quantity" = CASE WHEN cart_lines."Quantity" + {quantity} > {max} THEN {max} ELSE cart_lines."Quantity" + {quantity} END,
                  "SeenUnitPriceCents" = {item.PriceCents},
                  "IsDemo" = FALSE   -- the buyer chose these units themselves: a demo line is demo only while nobody touches it (M9-07)
                """, ct);
            if (changed != 1) { await tx.RollbackAsync(ct); continue; }   // never answer 200 for a line that was not stored
            await tx.CommitAsync(ct);
            return (AddStatus.Ok, await BuildAsync(cart.Id, token, true, ct));
        }
        return (AddStatus.Busy, null);
    }

    /// <summary>Sets a quantity (0 removes the line). Another buyer's line id is simply "not found".</summary>
    public async Task<CartView?> SetQuantityAsync(CartOwner o, Guid lineId, int quantity, CancellationToken ct)
    {
        var cart = await Find(o).AsNoTracking().FirstOrDefaultAsync(ct);
        if (cart is null) return null;
        // One transaction under the cart's lock (the lock is only held until the transaction ends): a sign-in merge or a purge waits for this write, or has already finished, in which case
        // the lock finds no cart and the answer is 404 instead of a 200 for a change that was lost (review C1).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(cart.Id, ct)) { await tx.RollbackAsync(ct); return null; }   // the cart is gone (merged, purged)
        if (hooks.AfterLineRead is { } hook) await hook();
        var line = await db.CartLines.AsNoTracking().Where(l => l.Id == lineId && l.CartId == cart.Id).FirstOrDefaultAsync(ct);
        if (line is null) { await tx.RollbackAsync(ct); return null; }
        int touched;
        if (quantity <= 0) touched = await db.CartLines.Where(l => l.Id == lineId && l.CartId == cart.Id).ExecuteDeleteAsync(ct);
        else
        {
            var price = await db.CatalogVariants.Where(v => v.Id == line.VariantId).Select(v => (long?)v.PriceCents).FirstOrDefaultAsync(ct);
            var q = Math.Min(quantity, CartLimits.MaxQuantityPerLine);
            touched = await db.CartLines.Where(l => l.Id == lineId && l.CartId == cart.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, q).SetProperty(l => l.SeenUnitPriceCents, price ?? line.SeenUnitPriceCents).SetProperty(l => l.IsDemo, false), ct);   // seeing the cart again accepts the current price
        }
        if (touched != 1) { await tx.RollbackAsync(ct); return null; }   // never a 200 for a change that was not stored
        await tx.CommitAsync(ct);
        return await BuildAsync(cart.Id, null, true, ct);
    }

    /// <summary>
    /// Moves a guest cart into the buyer's cart at sign-in. Under the row locks of BOTH carts (guest first, then the buyer's: a fixed order, so merges never deadlock) the guest lines are
    /// read AFTER the claim, so an add that raced the merge either finished before it (and is merged) or waits and starts over on a new cart; nothing is lost and no 200 is a lie.
    /// Only the request that finds and deletes the guest cart merges it: a retry, a parallel merge or an unknown token changes nothing.
    /// </summary>
    public async Task<CartView> MergeGuestAsync(Guid accountId, string guestToken, CancellationToken ct)
    {
        var owner = new CartOwner(accountId, null);
        var guest = guestToken is { Length: > 0 and <= 100 } ? await db.Carts.AsNoTracking().FirstOrDefaultAsync(c => c.GuestTokenHash == Tokens.Hash(guestToken), ct) : null;
        if (guest is not null)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (mine, _) = await GetOrCreateAsync(owner, ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await LockAsync(guest.Id, ct)) { await tx.RollbackAsync(ct); break; }                       // already merged or purged by someone else
                if (!await LockAsync(mine.Id, ct)) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); continue; }   // the buyer's own cart was purged meanwhile: make a new one
                if (hooks.InsideMerge is { } hook) await hook();
                var lines = await db.CartLines.AsNoTracking().Where(l => l.CartId == guest.Id).ToListAsync(ct);
                var claimed = await db.Carts.Where(c => c.Id == guest.Id && c.GuestTokenHash == guest.GuestTokenHash).ExecuteDeleteAsync(ct);   // cascades to the lines
                if (claimed == 1)
                {
                    var have = (await db.CartLines.AsNoTracking().Where(l => l.CartId == mine.Id).Select(l => l.VariantId).ToListAsync(ct)).ToHashSet();
                    var max = CartLimits.MaxQuantityPerLine;
                    foreach (var l in lines.OrderBy(l => l.AddedAtMs))
                    {
                        if (!have.Contains(l.VariantId)) { if (have.Count >= CartLimits.MaxLines) continue; have.Add(l.VariantId); }
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO cart_lines ("Id", "CartId", "ListingId", "VariantId", "Quantity", "SeenUnitPriceCents", "AddedAtMs")
                            VALUES ({Guid.NewGuid()}, {mine.Id}, {l.ListingId}, {l.VariantId}, {l.Quantity}, {l.SeenUnitPriceCents}, {l.AddedAtMs})
                            ON CONFLICT ("CartId", "VariantId") DO UPDATE SET
                              "Quantity" = CASE WHEN cart_lines."Quantity" + {l.Quantity} > {max} THEN {max} ELSE cart_lines."Quantity" + {l.Quantity} END,
                              "IsDemo" = FALSE   -- merged units are the buyer's own
                            """, ct);
                    }
                }
                await tx.CommitAsync(ct);
                break;
            }
        }
        var cart = await Find(owner).AsNoTracking().FirstOrDefaultAsync(ct);
        return cart is null ? Empty() : await BuildAsync(cart.Id, null, true, ct);
    }

    private static CartView Empty() => new(CartLimits.Currency, [], 0, 0, false);

    /// <summary>
    /// The cart as the catalog says it is NOW: prices, names, availability and visibility are re-read on every view; nothing a client sent is stored.
    /// Unavailable lines count for nothing and block checkout. Prices are the catalog's own (sale prices are already applied by the feed).
    /// </summary>
    private async Task<CartView> BuildAsync(Guid cartId, string? token, bool includeUnavailable, CancellationToken ct)
    {
        var rows = await (from cl in db.CartLines.AsNoTracking().Where(l => l.CartId == cartId)
                          join v in db.CatalogVariants.AsNoTracking() on cl.VariantId equals v.Id into vj
                          from v in vj.DefaultIfEmpty()
                          join l in db.CatalogListings.AsNoTracking() on (v == null ? cl.ListingId : v.ListingId) equals l.Id into lj
                          from l in lj.DefaultIfEmpty()
                          join s in db.CatalogSellers.AsNoTracking() on (l == null ? Guid.Empty : l.SellerId) equals s.Id into sj
                          from s in sj.DefaultIfEmpty()
                          join b in db.CatalogBrands.AsNoTracking() on (l == null ? Guid.Empty : l.BrandId) equals b.Id into bj
                          from b in bj.DefaultIfEmpty()
                          orderby cl.AddedAtMs, cl.Id
                          select new
                          {
                              cl.Id, ListingId = v == null ? cl.ListingId : v.ListingId, cl.VariantId, cl.Quantity, cl.SeenUnitPriceCents,
                              VName = v == null ? null : v.Name, PriceCents = v == null ? 0L : v.PriceCents, CompareAtCents = v == null ? null : v.CompareAtCents, VAvail = v != null && v.Available,
                              LTitle = l == null ? null : l.Title, LSlug = l == null ? null : l.Slug, LVisible = l != null && l.Visible, LType = l == null ? null : l.Type,
                              Currency = l == null ? null : l.Currency, SellerId = l == null ? Guid.Empty : l.SellerId, BrandId = l == null ? Guid.Empty : l.BrandId,
                              SellerActive = s != null && s.Active, SName = s == null ? null : s.DisplayName, BName = b == null ? null : b.Name,
                          }).ToListAsync(ct);
        var imageIds = await db.CatalogImages.AsNoTracking().Where(i => rows.Select(r => r.ListingId).Contains(i.ListingId))
            .OrderBy(i => i.SortOrder).Select(i => new { i.ListingId, i.Id }).ToListAsync(ct);
        var firstImage = imageIds.GroupBy(i => i.ListingId).ToDictionary(g => g.Key, g => g.First().Id);

        var currency = CartLimits.Currency;
        var groups = new List<CartGroupView>();
        var canCheckout = rows.Count > 0;
        foreach (var g in rows.GroupBy(r => (r.SellerId, r.BrandId)))
        {
            var views = new List<CartLineView>();
            foreach (var r in g)
            {
                var ok = r.VName is not null && r.LVisible && r.SellerActive && r.VAvail && r.LType == ListingTypes.Product && r.Currency == CartLimits.Currency && r.PriceCents is >= 0 and <= CartLimits.MaxUnitPriceCents;
                if (!ok) canCheckout = false;
                var unit = ok ? r.PriceCents : 0;
                views.Add(new CartLineView(r.Id, r.ListingId, r.VariantId, r.LSlug ?? "", r.LTitle ?? "", r.VName ?? "",
                    firstImage.TryGetValue(r.ListingId, out var im) ? im : (Guid?)null, r.Quantity, unit, ok ? r.CompareAtCents : null, unit * r.Quantity,
                    ok ? CartLineStatus.Ok : CartLineStatus.Unavailable, ok && r.PriceCents != r.SeenUnitPriceCents ? r.SeenUnitPriceCents : null));
            }
            var first = g.First();
            groups.Add(new CartGroupView(g.Key.SellerId, first.SName ?? "", g.Key.BrandId, first.BName ?? "", views.Sum(v => v.LineTotalCents), views));
        }
        // Lines of a seller/brand the catalog no longer knows share the empty group; they are all unavailable.
        var items = groups.SelectMany(g => g.Lines).Where(l => l.Status == CartLineStatus.Ok).Sum(l => l.Quantity);
        return new CartView(currency, groups, items, groups.Sum(g => g.SubtotalCents), canCheckout, token);
    }
}

/// <summary>Retention: guest carts 30 days after the last change, buyer carts 90 days (see the PII table).</summary>
public sealed class CartMaintenance(MarketplaceDb db, TimeProvider time)
{
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var guestCut = now.AddDays(-30);
        var buyerCut = now.AddDays(-90);
        return await db.Carts.Where(c => (c.AccountId == null && c.UpdatedAt < guestCut) || (c.AccountId != null && c.UpdatedAt < buyerCut)).ExecuteDeleteAsync(ct);
    }
}

public sealed class CartWorker(IServiceScopeFactory scopes, ILogger<CartWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<CartMaintenance>().PurgeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError("Cart purge failed: {ExceptionType}", ex.GetType().FullName); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
