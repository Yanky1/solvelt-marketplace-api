using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The orchestrator's review of the cart (86a63bf): races, currency, the line cap, current listing, caching, and the survivors of its mutation run.</summary>
public sealed class CartReviewTests : IDisposable
{
    private readonly AuthKit _k = new();
    private readonly List<Guid> _extra = [];
    private bool _ready;

    public void Dispose() => _k.Dispose();

    private CartHooks Hooks => _k.F.Services.GetRequiredService<CartHooks>();
    private static Guid Variant(string slug, int i = 0) => FixtureCatalogSource.Id($"variant:{slug}:{i}");

    private async Task Ready(int extraListings = 0)
    {
        if (!_ready)
        {
            var f = CatalogKit.Fixture(_k.F);
            var template = f.All().Single(l => l.Id == CatalogKit.ListingId("challah-classic"));
            for (var i = 0; i < extraListings; i++)
            {
                var added = f.Add(CatalogKit.Copy(template, Guid.NewGuid(), $"Review copy {i:D3}"));
                _extra.Add(added.Variants[0].Id);
            }
            await CatalogKit.Sync(_k.F);
            _ready = true;
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null, string? bearer = null, string? guest = null)
    {
        await Ready();
        var r = new HttpRequestMessage(m, path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (bearer != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (guest != null) r.Headers.Add(CartEndpoints.GuestHeader, guest);
        return await _k.Http.SendAsync(r);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var s = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {s}");
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    private Task<HttpResponseMessage> Add(Guid variant, int qty = 1, string? bearer = null, string? guest = null) => Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = variant, quantity = qty }, bearer, guest);
    private async Task<JsonElement> View(string? bearer = null, string? guest = null) => await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: bearer, guest: guest));
    private static int Items(JsonElement v) => v.GetProperty("itemCount").GetInt32();
    private static int LineCount(JsonElement v) => v.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("lines").GetArrayLength());
    private static JsonElement OnlyLine(JsonElement v) => v.GetProperty("groups")[0].GetProperty("lines")[0];

    // ---- races (review items 1 and 3) ----

    [Fact]
    public async Task An_add_that_races_a_merge_is_never_a_500_and_never_a_200_for_a_line_that_is_not_stored()
    {
        var first = await Body(await Add(Variant("challah-classic"), 2));
        var oldToken = first.GetProperty("cartToken").GetString()!;
        var buyer = await _k.VerifiedSession("race1@example.com");
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Hooks.InsideMerge = async () => { inside.TrySetResult(); await release.Task; };

        var merge = Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = oldToken }, bearer: buyer);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(20));                    // the merge holds both locks and has not read the lines yet
        var add = Add(Variant("kiddush-cup-pewter"), 1, guest: oldToken);          // arrives for the very cart that is being merged away
        await Task.Delay(300);
        Assert.False(add.IsCompleted);                                             // it waits for the lock: it cannot slip a line in behind the merge's read
        Hooks.InsideMerge = null;
        release.SetResult();
        var merged = await Body(await merge);
        var added = await Body(await add);                                         // 200 ...

        Assert.Equal(2, Items(merged));                                            // the merge moved what the guest had
        var newToken = added.GetProperty("cartToken").GetString();
        Assert.False(string.IsNullOrEmpty(newToken));                              // ... and the new line lives in a NEW guest cart with a new token: it was stored
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(1, Items(await View(guest: newToken)));
        Assert.Equal(0, Items(await View(guest: oldToken)));                       // the old token is dead
        Assert.Equal(2, Items(await View(bearer: buyer)));
        var orphans = await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == null));
        Assert.Equal(1, orphans);                                                  // exactly the new guest cart, no orphan
    }

    [Fact]
    public async Task Repeated_adds_against_repeated_merges_lose_nothing_and_never_fail()
    {
        await Ready();
        var buyer = await _k.VerifiedSession("race2@example.com");
        for (var round = 0; round < 12; round++)
        {
            var token = (await Body(await Add(Variant("challah-classic"), 1))).GetProperty("cartToken").GetString()!;
            var results = await Task.WhenAll(
                Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: buyer),
                Add(Variant("kiddush-cup-pewter"), 1, guest: token),
                Add(Variant("kiddush-cup-pewter"), 1, guest: token));
            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            // every 200 add is stored somewhere (the buyer's cart, or a fresh guest cart when it came after the merge): 1 + 1 + 1 units in all, none lost, none duplicated
            Assert.Equal(3, await _k.Db(db => db.CartLines.SumAsync(l => l.Quantity)));
            await _k.Db(db => db.CartLines.ExecuteDeleteAsync());
            await _k.Db(db => db.Carts.ExecuteDeleteAsync());
            buyer = await _k.VerifiedSession($"race2-{round}@example.com");
        }
    }

    [Fact]
    public async Task The_line_cap_is_atomic_under_parallel_adds_of_different_variants()
    {
        await Ready(60);
        var token = (await Body(await Add(_extra[0]))).GetProperty("cartToken").GetString()!;
        for (var i = 1; i < 45; i++) Assert.Equal(HttpStatusCode.OK, (await Add(_extra[i], guest: token)).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(45, 15).Select(i => Add(_extra[i], guest: token)));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(50, LineCount(await View(guest: token)));
        Assert.Equal(50, await _k.Db(db => db.CartLines.CountAsync()));
        // which variants made it in is the race's business; any variant that is NOT in the cart must now be refused, and one that is may still gain units
        var inCart = (await View(guest: token)).GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("lines").EnumerateArray()).Select(l => l.GetProperty("variantId").GetGuid()).ToHashSet();
        var absent = _extra.First(v => !inCart.Contains(v));
        var over = await Body(await Add(absent, guest: token), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("cart_full", over.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Add(inCart.First(), guest: token)).StatusCode);   // another unit of a variant already in the cart is not a new line
    }

    [Fact]
    public async Task Parallel_first_adds_for_a_buyer_reach_the_insert_together_and_make_one_cart()
    {
        var buyer = await _k.VerifiedSession("race3@example.com");
        await Ready();
        var arrived = 0;
        var both = new TaskCompletionSource();
        Hooks.BeforeCartInsert = async () => { if (Interlocked.Increment(ref arrived) == 2) both.SetResult(); await both.Task.WaitAsync(TimeSpan.FromSeconds(20)); };
        var results = await Task.WhenAll(Add(Variant("challah-classic"), 1, buyer), Add(Variant("kiddush-cup-pewter"), 1, buyer));
        Hooks.BeforeCartInsert = null;
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));       // the loser of the unique index re-reads the winner's cart
        var acct = await _k.Account("race3@example.com");
        Assert.Equal(1, await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == acct.Id)));
        Assert.Equal(2, Items(await View(bearer: buyer)));
    }

    // ---- currency, current listing, availability (items 2 and 4 and the survivors) ----

    [Fact]
    public async Task Only_usd_listings_can_be_carted_and_a_line_whose_listing_changes_currency_is_unavailable_and_blocks_checkout()
    {
        await Ready();
        var f = CatalogKit.Fixture(_k.F);
        var eur = f.Add(CatalogKit.Copy(f.All().Single(l => l.Id == CatalogKit.ListingId("challah-classic")), Guid.NewGuid(), "Euro challah") with { Currency = "EUR" });
        await CatalogKit.Sync(_k.F);
        var refused = await Body(await Add(eur.Variants[0].Id), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("currency_mismatch", refused.GetProperty("code").GetString());

        var token = (await Body(await Add(Variant("kiddush-cup-pewter")))).GetProperty("cartToken").GetString()!;
        f.Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Currency = "EUR" });
        await CatalogKit.Sync(_k.F);
        var v = await View(guest: token);
        Assert.Equal("USD", v.GetProperty("currency").GetString());
        Assert.Equal("unavailable", OnlyLine(v).GetProperty("status").GetString());
        Assert.Equal(0, v.GetProperty("subtotalCents").GetInt64());
        Assert.False(v.GetProperty("canCheckout").GetBoolean());
    }

    [Fact]
    public async Task A_euro_line_merged_into_a_dollar_cart_does_not_turn_the_cart_into_euros()
    {
        await Ready();
        var buyer = await _k.VerifiedSession("race4@example.com");
        await Add(Variant("challah-classic"), 1, buyer);
        var token = (await Body(await Add(Variant("kiddush-cup-pewter")))).GetProperty("cartToken").GetString()!;
        CatalogKit.Fixture(_k.F).Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Currency = "EUR" });
        await CatalogKit.Sync(_k.F);
        var merged = await Body(await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: buyer));
        Assert.Equal("USD", merged.GetProperty("currency").GetString());
        Assert.Equal(650, merged.GetProperty("subtotalCents").GetInt64());               // only the dollar line counts: no 4850 mixed sum
        Assert.False(merged.GetProperty("canCheckout").GetBoolean());                    // and the euro line must be removed first
        Assert.Contains(merged.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("lines").EnumerateArray()), l => l.GetProperty("status").GetString() == "unavailable");
    }

    [Fact]
    public async Task A_product_that_became_a_service_a_variant_removed_by_the_sync_and_a_suspended_seller_are_unavailable_not_errors()
    {
        await Ready();
        var f = CatalogKit.Fixture(_k.F);
        var token = (await Body(await Add(Variant("challah-classic", 1)))).GetProperty("cartToken").GetString()!;     // Large
        await Add(Variant("kiddush-cup-pewter"), 1, guest: token);
        await Add(Variant("roasting-chicken"), 1, guest: token);
        await Add(Variant("rugelach-assorted"), 1, guest: token);
        f.Upsert(CatalogKit.ListingId("challah-classic"), l => l with { Variants = l.Variants.Where(x => x.Name != "Large").ToList() });                // the variant is removed
        f.Upsert(CatalogKit.ListingId("kiddush-cup-pewter"), l => l with { Type = "service" });                                                               // a product that became a service
        f.Upsert(CatalogKit.ListingId("roasting-chicken"), l => l with { Seller = l.Seller with { Active = false } });                                      // a suspended seller
        await CatalogKit.Sync(_k.F);
        var v = await View(guest: token);
        var lines = v.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("lines").EnumerateArray()).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(3, lines.Count(l => l.GetProperty("status").GetString() == "unavailable"));
        Assert.Equal(1, Items(v));                                                       // only the rugelach still counts
        Assert.False(v.GetProperty("canCheckout").GetBoolean());
    }

    [Fact]
    public async Task A_line_follows_the_variants_current_listing_not_the_one_stored_when_it_was_added()
    {
        await Ready();
        var token = (await Body(await Add(Variant("challah-classic")))).GetProperty("cartToken").GetString()!;
        // the stored listing id is wrong (a variant that moved to another listing, or a bad row): the view must group by where the variant lives NOW
        await _k.Db(db => db.CartLines.ExecuteUpdateAsync(s => s.SetProperty(l => l.ListingId, CatalogKit.ListingId("kiddush-cup-pewter"))));
        var line = OnlyLine(await View(guest: token));
        Assert.Equal(CatalogKit.ListingId("challah-classic"), line.GetProperty("listingId").GetGuid());
        Assert.Equal("Classic Braided Challah", line.GetProperty("title").GetString());
    }

    // ---- tokens, retention, caching, echo (survivors and item 6) ----

    [Fact]
    public async Task The_stored_hash_is_not_a_token_and_the_token_is_shown_only_once()
    {
        var first = await Body(await Add(Variant("challah-classic"), 1));
        var token = first.GetProperty("cartToken").GetString()!;
        var hash = (await _k.Db(db => db.Carts.SingleAsync())).GuestTokenHash!;
        Assert.Equal(0, Items(await View(guest: hash)));                                  // what a database leak yields is worthless
        Assert.Equal(1, Items(await View(guest: token)));
        // never echoed again: not by an add, a quantity change or a merge
        var again = await Body(await Add(Variant("kiddush-cup-pewter"), 1, guest: token));
        Assert.Equal(JsonValueKind.Null, again.GetProperty("cartToken").ValueKind);
        var line = OnlyLine(again);
        var qty = await Body(await Send(HttpMethod.Patch, $"/api/v1/cart/lines/{line.GetProperty("id").GetGuid()}", new { quantity = 2 }, guest: token));
        Assert.Equal(JsonValueKind.Null, qty.GetProperty("cartToken").ValueKind);
        var buyer = await _k.VerifiedSession("tok1@example.com");
        var merged = await Body(await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: buyer));
        Assert.Equal(JsonValueKind.Null, merged.GetProperty("cartToken").ValueKind);
        // a hash used as the guest token in a merge merges nothing
        var other = await _k.VerifiedSession("tok2@example.com");
        var t2 = (await Body(await Add(Variant("challah-classic"), 1))).GetProperty("cartToken").GetString()!;
        var h2 = (await _k.Db(db => db.Carts.Where(c => c.AccountId == null).SingleAsync())).GuestTokenHash!;
        Assert.Equal(0, Items(await Body(await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = h2 }, bearer: other))));
        Assert.Equal(1, Items(await View(guest: t2)));
    }

    [Fact]
    public async Task The_merge_never_takes_a_buyer_cart_past_fifty_lines()
    {
        await Ready(60);
        var buyer = await _k.VerifiedSession("cap1@example.com");
        for (var i = 0; i < 45; i++) Assert.Equal(HttpStatusCode.OK, (await Add(_extra[i], 1, buyer)).StatusCode);
        var token = (await Body(await Add(_extra[0]))).GetProperty("cartToken").GetString()!;      // overlaps one the buyer has
        for (var i = 45; i < 55; i++) Assert.Equal(HttpStatusCode.OK, (await Add(_extra[i], 1, guest: token)).StatusCode);
        var merged = await Body(await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: buyer));
        Assert.Equal(50, LineCount(merged));                                                         // 45 + the overlap (quantity added) + 5 new ones; the other 5 do not fit
        Assert.Equal(51, Items(merged));                                                             // 45 + 1 more of the overlapping variant + 5 new lines
        Assert.Equal(0, Items(await View(guest: token)));
    }

    [Fact]
    public async Task Every_change_stamps_the_cart_so_retention_counts_from_the_last_change()
    {
        var guest = (await Body(await Add(Variant("challah-classic")))).GetProperty("cartToken").GetString()!;
        var t0 = _k.F.Clock.GetUtcNow();
        _k.F.Clock.Advance(TimeSpan.FromDays(20));
        await Add(Variant("kiddush-cup-pewter"), 1, guest: guest);
        Assert.Equal(t0.AddDays(20), (await _k.Db(db => db.Carts.SingleAsync())).UpdatedAt);           // the add stamped it
        async Task<int> Purge() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<CartMaintenance>().PurgeAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromDays(29));                                                      // 49 days after creation, 29 after the last change
        Assert.Equal(0, await Purge());
        var lineId = OnlyLineId(await View(guest: guest));
        await Send(HttpMethod.Patch, $"/api/v1/cart/lines/{lineId}", new { quantity = 2 }, guest: guest);      // a quantity change stamps it too
        _k.F.Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await Purge());
        _k.F.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await Purge());
    }

    private static Guid OnlyLineId(JsonElement v) => OnlyLine(v).GetProperty("id").GetGuid();

    [Fact]
    public async Task Cart_and_checkout_answers_are_private_and_never_stored_also_when_refused()
    {
        var token = (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic") }));
        Assert.True(token.Headers.CacheControl is { NoStore: true, Private: true });
        foreach (var r in new[]
        {
            await Send(HttpMethod.Get, "/api/v1/cart"),
            await Send(HttpMethod.Get, "/api/v1/cart", bearer: "bogus"),                                         // 401
            await Send(HttpMethod.Delete, $"/api/v1/cart/lines/{Guid.NewGuid()}"),                              // 404
            await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = "x" }),                         // 401
            await Send(HttpMethod.Post, "/api/v1/checkout/quote", new { address = new { country = "US", postalCode = "1" } }),
            await Send(HttpMethod.Post, "/api/v1/checkout", new { }),                                            // 401
            await Send(HttpMethod.Get, "/api/v1/checkout/orders/MP-1"),
        })
            Assert.True(r.Headers.CacheControl is { NoStore: true, Private: true }, $"{(int)r.StatusCode} {r.Headers.CacheControl}");
    }
}
