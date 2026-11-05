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

    [Fact]
    public async Task A_price_drop_and_a_back_in_stock_are_each_one_notification_and_the_baseline_moves()
    {
        var t = await _k.VerifiedSession("l7@example.com");
        var other = await _k.VerifiedSession("l7b@example.com");
        var id = await NewList(t);
        await Call(HttpMethod.Put, $"/api/v1/wishlists/{id}/items/{Listing()}", t);
        var fx = CatalogKit.Fixture(_k.F);
        var v = fx.All().First(l => l.Id == Listing()).Variants;

        fx.Upsert(Listing(), l => l with { Variants = v.Select(x => x with { PriceCents = x.PriceCents - 100 }).ToList() });
        await CatalogKit.Sync(_k.F);
        await CatalogKit.Sync(_k.F);   // nothing new: no second notification
        var notes = await _k.Db(db => db.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKinds.WishlistPriceDrop).ToListAsync());
        var n1 = Assert.Single(notes);
        Assert.Equal((await _k.Account("l7@example.com")).Id, n1.AccountId);
        Assert.Contains("\"previousPriceCents\":650", n1.ParamsJson);
        Assert.Contains("\"priceCents\":550", n1.ParamsJson);

        // a price that goes up quietly raises the baseline; the next drop below it is announced again
        fx.Upsert(Listing(), l => l with { Variants = v.Select(x => x with { PriceCents = x.PriceCents + 300 }).ToList() });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == NotificationKinds.WishlistPriceDrop)));
        fx.Upsert(Listing(), l => l with { Variants = v.Select(x => x with { PriceCents = x.PriceCents + 200 }).ToList() });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(2, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == NotificationKinds.WishlistPriceDrop)));

        // back in stock
        fx.Upsert(Listing(), l => l with { Variants = v.Select(x => x with { Available = false }).ToList() });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(0, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == NotificationKinds.WishlistBackInStock)));
        fx.Upsert(Listing(), l => l with { Variants = v.ToList() });
        await CatalogKit.Sync(_k.F);
        Assert.Equal(1, await _k.Db(db => db.Notifications.CountAsync(n => n.Kind == NotificationKinds.WishlistBackInStock)));
        Assert.Equal(0, await _k.Db(db => db.Notifications.CountAsync(n => n.AccountId != n1.AccountId && n.Kind.StartsWith("wishlist"))));   // the other buyer got nothing
        _ = other;

        // the mail honours the "wishlist" preference
        Assert.Contains(await _k.Mail("l7@example.com"), m => m.Subject.StartsWith("Back in stock"));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/notifications/preferences", t, new { email = new Dictionary<string, bool> { ["wishlist"] = false } })).Status);
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
