using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Lists;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Wishlists and recently viewed (M6, decisions M6-01..).</summary>
public sealed class ListsTests : IDisposable
{
    private readonly AuthKit _k = new();
    private bool _synced;
    public void Dispose() => _k.Dispose();

    private static Guid Listing(string slug = "challah-classic") => CatalogKit.ListingId(slug);

    private async Task Ready() { if (_synced) return; await CatalogKit.Sync(_k.F); _synced = true; }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod m, string path, string? token, object? body = null)
    {
        await Ready();
        var r = await _k.Send(m, path, body, token);
        var s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, s.Length == 0 ? default : JsonDocument.Parse(s).RootElement.Clone());
    }

    private async Task<Guid> NewList(string token, string name = "Gifts")
    {
        var (st, b) = await Call(HttpMethod.Post, "/api/v1/wishlists", token, new { name });
        Assert.Equal(HttpStatusCode.Created, st);
        return b.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Lists_are_created_renamed_and_named_once_per_buyer_whatever_the_case()
    {
        var t = await _k.VerifiedSession("l1@example.com");
        var id = await NewList(t, "Birthday");
        _k.F.Clock.Advance(TimeSpan.FromSeconds(1));   // lists are listed by creation time: two lists made in the same instant would tie
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/v1/wishlists", t, new { name = "  BIRTHDAY " })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/wishlists", t, new { name = "   " })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/wishlists", t, new { name = new string('x', 61) })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/wishlists", t, new { name = "bad\nname" })).Status);
        var other = await NewList(t, "Home");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Patch, $"/api/v1/wishlists/{other}", t, new { name = "birthday" })).Status);
        var (st, b) = await Call(HttpMethod.Patch, $"/api/v1/wishlists/{id}", t, new { name = "Birthday 2027" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal("Birthday 2027", b.GetProperty("name").GetString());
        var (_, all) = await Call(HttpMethod.Get, "/api/v1/wishlists", t);
        Assert.Equal(new[] { "Birthday 2027", "Home" }, all.GetProperty("wishlists").EnumerateArray().Select(w => w.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/wishlists", null)).Status);
    }

    [Fact]
    public async Task A_buyer_has_at_most_twenty_lists_even_when_they_are_made_in_parallel()
    {
        var t = await _k.VerifiedSession("l2@example.com");
        await Ready();
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Call(HttpMethod.Post, "/api/v1/wishlists", t, new { name = "List " + i })));
        var count = await _k.Db(db => db.Wishlists.CountAsync());
        Assert.InRange(count, 1, ListLimits.MaxLists);
        Assert.Equal(count, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status));
    }

    [Fact]
    public async Task Another_buyers_list_is_404_for_every_route()
    {
        var a = await _k.VerifiedSession("l3a@example.com");
        var b = await _k.VerifiedSession("l3b@example.com");
        var id = await NewList(a);
        var l = Listing();
        foreach (var (m, p, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/api/v1/wishlists/{id}", null), (HttpMethod.Patch, $"/api/v1/wishlists/{id}", new { name = "x" }), (HttpMethod.Delete, $"/api/v1/wishlists/{id}", null),
            (HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{l}", null), (HttpMethod.Delete, $"/api/v1/wishlists/{id}/items/{l}", null),
            (HttpMethod.Post, $"/api/v1/wishlists/{id}/share", null), (HttpMethod.Delete, $"/api/v1/wishlists/{id}/share", null),
        })
            Assert.Equal(HttpStatusCode.NotFound, (await Call(m, p, b, body)).Status);
        Assert.Equal(1, await _k.Db(db => db.Wishlists.CountAsync()));
    }

    [Fact]
    public async Task Adding_the_same_listing_in_parallel_is_one_row_and_an_unknown_listing_is_404()
    {
        var t = await _k.VerifiedSession("l4@example.com");
        var id = await NewList(t);
        await Ready();
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", t)));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
        Assert.Equal(1, await _k.Db(db => db.WishlistItems.CountAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Guid.NewGuid()}", t)).Status);
        var (_, view) = await Call(HttpMethod.Get, $"/api/v1/wishlists/{id}", t);
        Assert.Equal(1, view.GetProperty("wishlist").GetProperty("itemCount").GetInt32());
        Assert.Equal(Listing(), view.GetProperty("items")[0].GetProperty("listing").GetProperty("id").GetGuid());
        var (_, m) = await Call(HttpMethod.Get, $"/api/v1/wishlists/membership?listingIds={Listing()},{Listing("babka-chocolate")}", t);
        Assert.Single(m.GetProperty("membership").GetProperty(Listing().ToString()).EnumerateArray());
        Assert.Empty(m.GetProperty("membership").GetProperty(Listing("babka-chocolate").ToString()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/wishlists/{id}/items/{Listing()}", t)).Status);
        Assert.Equal(0, await _k.Db(db => db.WishlistItems.CountAsync()));
    }

    [Fact]
    public async Task A_listing_that_leaves_the_storefront_stays_on_the_list_as_unavailable_and_cannot_be_added()
    {
        var t = await _k.VerifiedSession("l5@example.com");
        var id = await NewList(t);
        await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", t);
        CatalogKit.Fixture(_k.F).Upsert(Listing(), l => l with { Status = "Archived" });
        await CatalogKit.Sync(_k.F);
        var (_, view) = await Call(HttpMethod.Get, $"/api/v1/wishlists/{id}", t);
        var item = view.GetProperty("items")[0];
        Assert.True(item.GetProperty("unavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("listing").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/wishlists/{await NewList(t, "Two")}/items/{Listing()}", t)).Status);
    }

    [Fact]
    public async Task Sharing_gives_a_key_shown_once_whose_hash_is_all_we_keep_and_a_new_key_revokes_the_old_one()
    {
        var t = await _k.VerifiedSession("l6@example.com");
        var id = await NewList(t, "For Dad");
        await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", t);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/v1/shared-wishlists/not-a-key-at-all-not-a-key", null)).Status);
        var (_, s1) = await Call(HttpMethod.Post, $"/api/v1/wishlists/{id}/share", t);
        var key1 = s1.GetProperty("shareKey").GetString()!;
        Assert.Equal(43, key1.Length);
        Assert.DoesNotContain(await _k.Db(db => db.Wishlists.ToListAsync()), w => w.ShareKeyHash == key1);   // only the hash is stored
        var r = await _k.Http.GetAsync($"/api/v1/shared-wishlists/{key1}");   // anonymous
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Headers.CacheControl!.NoStore && r.Headers.CacheControl.Private);
        Assert.Equal("noindex, nofollow", r.Headers.GetValues("X-Robots-Tag").Single());
        var shared = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("For Dad", shared.GetProperty("name").GetString());
        Assert.Equal(1, shared.GetProperty("items").GetArrayLength());
        Assert.False(shared.TryGetProperty("accountId", out _));
        var (_, s2) = await Call(HttpMethod.Post, $"/api/v1/wishlists/{id}/share", t);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/shared-wishlists/{key1}")).StatusCode);
        var key2 = s2.GetProperty("shareKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await _k.Http.GetAsync($"/api/v1/shared-wishlists/{key2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/wishlists/{id}/share", t)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/shared-wishlists/{key2}")).StatusCode);
        // a list is private until shared
        Assert.False((await Call(HttpMethod.Get, $"/api/v1/wishlists/{id}", t)).Body.GetProperty("wishlist").GetProperty("shared").GetBoolean());
    }

    /// <summary>Applies the catalog change and runs the queued wishlist watches (the sync only queues them).</summary>
    private async Task SyncAndWatch()
    {
        await CatalogKit.Sync(_k.F);
        while (await _k.Dispatch() > 0) { }
    }

    private Task<int> Count(string kind, Guid? account = null) => _k.Db(db => db.Notifications.CountAsync(n => n.Kind == kind && (account == null || n.AccountId == account)));

    private async Task<(string Token, Guid Account)> Buyer(string email, params string[] slugs)
    {
        var t = await _k.VerifiedSession(email);
        var id = await NewList(t);
        foreach (var slug in slugs) Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing(slug)}", t)).Status);
        return (t, (await _k.Account(email)).Id);
    }

    private void Reprice(long delta, string slug = "challah-classic")
    {
        var fx = CatalogKit.Fixture(_k.F);
        fx.Upsert(Listing(slug), l => l with { Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents + delta }).ToList() });
    }

    [Fact]
    public async Task Two_buyers_with_the_same_listing_are_both_told_and_the_catalog_still_gets_the_new_price()
    {
        var (_, a) = await Buyer("w1a@example.com", "challah-classic");
        var (_, b) = await Buyer("w1b@example.com", "challah-classic");
        var (_, c) = await Buyer("w1c@example.com", "babka-chocolate");   // another listing: not told
        Reprice(-100);
        await SyncAndWatch();
        Assert.Equal(550, (await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(l => l.Id == Listing()))).PriceFromCents);   // the snapshot was applied (H1: it used to be skipped)
        Assert.Equal((1, 1, 0), (await Count(NotificationKinds.WishlistPriceDrop, a), await Count(NotificationKinds.WishlistPriceDrop, b), await Count(NotificationKinds.WishlistPriceDrop, c)));
        var note = await _k.Db(db => db.Notifications.AsNoTracking().FirstAsync(n => n.AccountId == a && n.Kind == NotificationKinds.WishlistPriceDrop));
        var prm = JsonDocument.Parse(note.ParamsJson!).RootElement;
        Assert.Equal((550, 650), (prm.GetProperty("priceCents").GetInt64(), prm.GetProperty("previousPriceCents").GetInt64()));
        await SyncAndWatch();   // nothing new: no second notification
        Assert.Equal(2, await Count(NotificationKinds.WishlistPriceDrop));
        var mail = await _k.Mail("w1a@example.com");
        Assert.Contains(mail, m => m.Subject.StartsWith("Price drop"));
    }

    [Fact]
    public async Task A_notification_that_cannot_be_written_for_one_buyer_never_stops_the_catalog_or_the_other_buyers()
    {
        var (_, a) = await Buyer("w2a@example.com", "challah-classic");
        var (_, b) = await Buyer("w2b@example.com", "challah-classic");
        Reprice(-100);
        await CatalogKit.Sync(_k.F);
        var version = (await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(l => l.Id == Listing()))).Version;
        // buyer A's key is already taken by something else: A's insert violates the unique index
        await _k.Db(async db => { db.Notifications.Add(new Notification { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Kind = "x", DedupeKey = $"wishlist:{a:N}:{Listing():N}:{version}:{NotificationKinds.WishlistPriceDrop}", CreatedAt = _k.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
        while (await _k.Dispatch() > 0) { }
        Assert.Equal(550, (await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(l => l.Id == Listing()))).PriceFromCents);
        Assert.Equal(1, await Count(NotificationKinds.WishlistPriceDrop, b));
        Assert.Equal(0, await Count(NotificationKinds.WishlistPriceDrop, a));
    }

    [Fact]
    public async Task A_cheaper_variant_coming_back_in_stock_is_not_a_price_drop_and_a_restock_is_a_restock()
    {
        var (_, a) = await Buyer("w3@example.com", "challah-classic");
        var fx = CatalogKit.Fixture(_k.F);
        var v = fx.All().First(l => l.Id == Listing()).Variants;
        var cheapest = v.OrderBy(x => x.PriceCents).First();
        // the cheapest variant goes out of stock: the "from" price over available variants rises, the lowest price over all variants does not move
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x.Id == cheapest.Id ? x with { Available = false } : x).ToList() });
        await SyncAndWatch();
        // ... and comes back: that is NOT a price drop
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x.Id == cheapest.Id ? x with { Available = true } : x).ToList() });
        await SyncAndWatch();
        Assert.Equal(0, await Count(NotificationKinds.WishlistPriceDrop, a));
        Assert.Equal(0, await Count(NotificationKinds.WishlistBackInStock, a));   // the listing stayed available through the other variants

        // everything out of stock, then back: ONE back-in-stock
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = false }).ToList() });
        await SyncAndWatch();
        Assert.Equal(0, await Count(NotificationKinds.WishlistBackInStock, a));
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = true }).ToList() });
        await SyncAndWatch();
        Assert.Equal(1, await Count(NotificationKinds.WishlistBackInStock, a));
    }

    [Fact]
    public async Task A_buyer_is_told_at_most_once_per_week_per_kind_however_often_the_listing_flaps()
    {
        var (_, a) = await Buyer("w4@example.com", "challah-classic");
        var fx = CatalogKit.Fixture(_k.F);
        for (var i = 0; i < 5; i++)
        {
            fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = false }).ToList() });
            await SyncAndWatch();
            fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = true }).ToList() });
            await SyncAndWatch();
        }
        Assert.Equal(1, await Count(NotificationKinds.WishlistBackInStock, a));
        _k.F.Clock.Advance(WishlistWatchHandler.Cooldown + TimeSpan.FromHours(1));
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = false }).ToList() });
        await SyncAndWatch();
        fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { Available = true }).ToList() });
        await SyncAndWatch();
        Assert.Equal(2, await Count(NotificationKinds.WishlistBackInStock, a));

        // price: a drop, a rise and another drop inside the cooldown are one mail
        Reprice(-100); await SyncAndWatch();
        Reprice(+300); await SyncAndWatch();
        Reprice(-200); await SyncAndWatch();
        Assert.Equal(1, await Count(NotificationKinds.WishlistPriceDrop, a));
    }

    [Fact]
    public async Task Demo_listings_and_listings_off_the_storefront_are_never_announced_and_the_baseline_stays()
    {
        var (_, a) = await Buyer("w5@example.com", "challah-classic", "babka-chocolate");
        var fx = CatalogKit.Fixture(_k.F);
        fx.Upsert(Listing(), l => l with { Demo = true, Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        await SyncAndWatch();
        Assert.Equal(0, await Count(NotificationKinds.WishlistPriceDrop, a));
        var other = fx.All().First(l => l.Id == Listing("babka-chocolate"));
        fx.Upsert(Listing("babka-chocolate"), l => l with { Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        await CatalogKit.Sync(_k.F);   // the sync applies the price (and the seller as the feed has it) ...
        await _k.Db(async db => { await db.CatalogSellers.Where(s => s.Id == other.Seller.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false)); return 0; });   // ... then the seller is suspended before the watch runs
        while (await _k.Dispatch() > 0) { }
        Assert.Equal(0, await Count(NotificationKinds.WishlistPriceDrop, a));
        Assert.All(await _k.Db(db => db.WishlistItems.AsNoTracking().ToListAsync()), i => Assert.True(i.BaselinePriceCents > 0));
    }

    [Fact]
    public async Task A_price_in_another_currency_is_no_drop_and_a_list_holds_at_most_two_hundred_items_even_in_parallel()
    {
        var (t, a) = await Buyer("w6@example.com", "challah-classic");
        CatalogKit.Fixture(_k.F).Upsert(Listing(), l => l with { Currency = "EUR", Variants = l.Variants.Select(x => x with { PriceCents = 1 }).ToList() });
        await SyncAndWatch();
        Assert.Equal(0, await Count(NotificationKinds.WishlistPriceDrop, a));

        var fx = CatalogKit.Fixture(_k.F);
        var proto = fx.All()[0];
        var ids = Enumerable.Range(0, 12).Select(i => fx.Add(CatalogKit.Copy(proto, Guid.NewGuid(), "Cap " + i)).Id).ToList();
        await CatalogKit.Sync(_k.F);
        var list = await NewList(t, "Big");
        // 195 items are put in directly (the cap itself is what is tested, not 200 requests: that would exhaust the database's connections), then 12 parallel adds race for the last 5 places
        await _k.Db(async db =>
        {
            for (var i = 0; i < ListLimits.MaxItemsPerList - 5; i++) db.WishlistItems.Add(new WishlistItem { Id = Guid.NewGuid(), WishlistId = list, ListingId = Guid.NewGuid(), AddedAt = _k.F.Clock.GetUtcNow(), BaselinePriceCents = 1, BaselineCurrency = "USD", BaselineAvailable = true });
            await db.SaveChangesAsync();
            return 0;
        });
        var rs = await Task.WhenAll(ids.Select(id => Call(HttpMethod.Put, $"/api/v1/wishlists/{list}/items/{id}", t)));
        var held = await _k.Db(db => db.WishlistItems.CountAsync(i => i.WishlistId == list));
        Assert.InRange(held, ListLimits.MaxItemsPerList - 5, ListLimits.MaxItemsPerList);   // the post-insert check is fail-closed: a race can leave a little room, never more than the cap (M6-01)
        Assert.All(rs.Where(r => r.Status != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status));
        Assert.True(rs.Any(r => r.Status == HttpStatusCode.NoContent), "some of the adds must succeed (a list that refuses every add would also keep the count in range)");
        Assert.True(rs.Any(r => r.Status == HttpStatusCode.UnprocessableEntity), "and the ones over the cap are refused");
    }

    [Fact]
    public async Task The_same_listing_on_two_lists_of_one_buyer_is_one_notification()
    {
        var t = await _k.VerifiedSession("w7@example.com");
        var one = await NewList(t, "One"); var two = await NewList(t, "Two");
        foreach (var l in new[] { one, two }) await Call(HttpMethod.Put, $"/api/v1/wishlists/{l}/items/{Listing()}", t);
        Reprice(-100);
        await SyncAndWatch();
        Assert.Equal(1, await Count(NotificationKinds.WishlistPriceDrop));
        Assert.All(await _k.Db(db => db.WishlistItems.AsNoTracking().ToListAsync()), i => Assert.Equal(550, i.BaselinePriceCents));
    }

    [Fact]
    public async Task A_drop_that_comes_after_a_rise_is_announced_once_the_cooldown_has_passed()
    {
        var (_, a) = await Buyer("w8@example.com", "challah-classic");
        Reprice(-100); await SyncAndWatch();
        Assert.Equal(1, await Count(NotificationKinds.WishlistPriceDrop, a));
        Reprice(+300); await SyncAndWatch();                                   // up: the baseline rises quietly
        _k.F.Clock.Advance(WishlistWatchHandler.Cooldown + TimeSpan.FromHours(1));
        Reprice(-200); await SyncAndWatch();                                   // down again, after the cooldown: announced
        Assert.Equal(2, await Count(NotificationKinds.WishlistPriceDrop, a));
        var last = await _k.Db(db => db.Notifications.AsNoTracking().Where(n => n.AccountId == a && n.Kind == NotificationKinds.WishlistPriceDrop).OrderByDescending(n => n.CreatedAt).FirstAsync());
        var prm = JsonDocument.Parse(last.ParamsJson!).RootElement;
        Assert.Equal((650, 850), (prm.GetProperty("priceCents").GetInt64(), prm.GetProperty("previousPriceCents").GetInt64()));
        var item = await _k.Db(db => db.WishlistItems.AsNoTracking().SingleAsync());
        Assert.NotNull(item.LastPriceNotifiedAt);
    }

    [Fact]
    public async Task The_watch_message_exists_exactly_when_the_snapshot_is_saved_never_before()
    {
        await Buyer("w9@example.com", "challah-classic");
        var fx = CatalogKit.Fixture(_k.F);
        var snap = fx.Upsert(Listing(), l => l with { Variants = l.Variants.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        var key = $"wishlist-watch:{snap.Id:N}:{snap.Version}";
        using var scope = _k.F.Services.CreateScope();
        var sync = CatalogKit.Service(scope, fx, _k.F.Clock);
        Assert.True(await sync.ApplySnapshotAsync(snap, default));
        Assert.Equal(0, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == key)));   // applied but not saved: no message a worker could act on against the old data
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == key)));   // saved together with the snapshot
        Assert.Equal(550, (await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(l => l.Id == Listing()))).PriceFromCents);
    }

    [Fact]
    public async Task Recently_viewed_keeps_twenty_newest_with_server_time_and_the_other_buyers_list_is_separate()
    {
        var a = await _k.VerifiedSession("l8a@example.com");
        var b = await _k.VerifiedSession("l8b@example.com");
        await Ready();
        var fx = CatalogKit.Fixture(_k.F);
        var proto = fx.All()[0];
        var ids = new List<Guid>();
        for (var i = 0; i < 24; i++) { var c = fx.Add(CatalogKit.Copy(proto, Guid.NewGuid(), "Copy " + i)); ids.Add(c.Id); }
        await CatalogKit.Sync(_k.F);
        foreach (var id in ids) { _k.F.Clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Put, $"/api/v1/recently-viewed/{id}", a)).Status); }
        var (_, list) = await Call(HttpMethod.Get, "/api/v1/recently-viewed", a);
        var shown = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("listing").GetProperty("id").GetGuid()).ToList();
        Assert.Equal(ids.AsEnumerable().Reverse().Take(20), shown);                                     // newest first, capped
        _k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        await Call(HttpMethod.Put, $"/api/v1/recently-viewed/{ids[10]}", a);                           // seen again: moves to the front, no second row
        Assert.Equal(20, await _k.Db(db => db.RecentlyViewed.CountAsync()));
        Assert.Equal(ids[10], (await Call(HttpMethod.Get, "/api/v1/recently-viewed", a)).Body.GetProperty("items")[0].GetProperty("listing").GetProperty("id").GetGuid());
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/recently-viewed", b)).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/recently-viewed/{Guid.NewGuid()}", a)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, "/api/v1/recently-viewed", a)).Status);
        Assert.Equal(0, await _k.Db(db => db.RecentlyViewed.CountAsync()));
    }

    [Fact]
    public async Task Merging_a_guests_list_never_outranks_what_the_server_saw_and_ignores_client_times_and_unknown_ids()
    {
        var t = await _k.VerifiedSession("l9@example.com");
        await Ready();
        var (x, y, z) = (Listing("challah-classic"), Listing("babka-chocolate"), Listing("challah-classic") == Listing("babka-chocolate") ? Guid.NewGuid() : Listing("challah-classic"));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Put, $"/api/v1/recently-viewed/{x}", t)).Status);   // seen on the server now
        _k.F.Clock.Advance(TimeSpan.FromMinutes(5));
        var guest = new[] { y, x, Guid.NewGuid(), Guid.Empty, y };                                       // newest first, an unknown id, an empty id, a duplicate
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, "/api/v1/recently-viewed/merge", t, new { listingIds = guest, viewedAt = "2099-01-01T00:00:00Z" })).Status);
        var rows = await _k.Db(db => db.RecentlyViewed.AsNoTracking().OrderByDescending(r => r.ViewedAt).ToListAsync());
        Assert.Equal([y, x], rows.Select(r => r.ListingId));
        Assert.All(rows, r => Assert.True(r.ViewedAt <= _k.F.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, "/api/v1/recently-viewed/merge", t, new { listingIds = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()) })).Status);
        Assert.Equal(2, await _k.Db(db => db.RecentlyViewed.CountAsync()));
        _ = z;
    }

    [Fact]
    public async Task Deleting_the_account_removes_lists_items_share_links_and_recently_viewed_and_the_export_holds_them()
    {
        var t = await _k.VerifiedSession("l10@example.com");
        var keep = await _k.VerifiedSession("l10b@example.com");
        var id = await NewList(t, "Mine");
        var keepId = await NewList(keep, "Theirs");
        await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", t);
        await Call(HttpMethod.Put, $"/api/v1/wishlists/{keepId}/items/{Listing()}", keep);
        await Call(HttpMethod.Put, $"/api/v1/recently-viewed/{Listing()}", t);
        var key = (await Call(HttpMethod.Post, $"/api/v1/wishlists/{id}/share", t)).Body.GetProperty("shareKey").GetString()!;
        var account = await _k.Account("l10@example.com");

        using (var scope = _k.F.Services.CreateScope())
        {
            var w = await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "wishlists").ExportAsync(account.Id, default);
            var json = JsonSerializer.Serialize(w);
            Assert.Contains("Mine", json);
            Assert.DoesNotContain(key, json);
            Assert.DoesNotContain("Theirs", json);
            Assert.Contains(Listing().ToString(), JsonSerializer.Serialize(await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "recently-viewed").ExportAsync(account.Id, default)));
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ListsAccountEraser>().Single().EraseAsync(account.Id, default);
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ListsAccountEraser>().Single().EraseAsync(account.Id, default);   // again: no error
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/shared-wishlists/{key}")).StatusCode);
        Assert.Equal(1, await _k.Db(db => db.Wishlists.CountAsync()));
        Assert.Equal(1, await _k.Db(db => db.WishlistItems.CountAsync()));
        Assert.Equal(0, await _k.Db(db => db.RecentlyViewed.CountAsync()));
    }
}
