using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public sealed class CartTests : IDisposable
{
    private readonly AuthKit _k = new();
    private bool _synced;

    public void Dispose() => _k.Dispose();

    private static Guid Variant(string slug, int i = 0) => Solvelt.Marketplace.Api.Catalog.FixtureCatalogSource.Id($"variant:{slug}:{i}");

    private async Task Ready()
    {
        if (_synced) return;
        await CatalogKit.Sync(_k.F);
        _synced = true;
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

    private async Task<(JsonElement View, string Token)> GuestAdd(string slug, int qty = 1, int variant = 0, string? token = null)
    {
        var v = await Body(await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant(slug, variant), quantity = qty }, guest: token));
        return (v, token ?? v.GetProperty("cartToken").GetString()!);
    }

    [Fact]
    public async Task A_guest_cart_gets_a_token_once_and_prices_come_from_the_catalog_not_the_client()
    {
        await Ready();
        var r = await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic"), quantity = 2, unitPriceCents = 1, totalCents = 2, priceCents = 1 });
        var v = await Body(r);
        var token = v.GetProperty("cartToken").GetString()!;
        Assert.Equal(43, token.Length);
        var line = v.GetProperty("groups")[0].GetProperty("lines")[0];
        Assert.Equal(650, line.GetProperty("unitPriceCents").GetInt64());      // the catalog's price, whatever the body said
        Assert.Equal(1300, line.GetProperty("lineTotalCents").GetInt64());
        Assert.Equal(1300, v.GetProperty("subtotalCents").GetInt64());

        var again = await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: token));
        Assert.Equal(JsonValueKind.Null, again.GetProperty("cartToken").ValueKind);   // the token is shown once
        Assert.Equal(2, again.GetProperty("itemCount").GetInt32());
        // only the hash is stored
        var rows = await _k.Db(db => db.Carts.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(rows, c => c.GuestTokenHash == token);
    }

    [Fact]
    public async Task Lines_are_split_per_seller_and_brand_exactly_like_portal_seller_orders()
    {
        var (_, token) = await GuestAdd("challah-classic", 1);                 // mazel / challah-corner
        await GuestAdd("challah-whole-wheat", 2, token: token);                // same group
        await GuestAdd("rugelach-assorted", 1, token: token);                  // mazel / mazel-bakery: same seller, other brand
        var v = await GuestAdd("kiddush-cup-pewter", 1, token: token);         // other seller
        var groups = v.View.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(3, groups.Count);
        var challah = groups.Single(g => g.GetProperty("brandName").GetString() == "Challah Corner");
        Assert.Equal(2, challah.GetProperty("lines").GetArrayLength());
        Assert.Equal(650 + 2 * 750, challah.GetProperty("subtotalCents").GetInt64());
        Assert.Equal(groups.Sum(g => g.GetProperty("subtotalCents").GetInt64()), v.View.GetProperty("subtotalCents").GetInt64());
        Assert.Equal(650 + 1500 + 1800 + 4200, v.View.GetProperty("subtotalCents").GetInt64());
    }

    [Fact]
    public async Task Services_hidden_suspended_and_sold_out_items_cannot_be_added()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("deep-house-cleaning") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("closed-corner-mug") })).StatusCode);   // suspended seller
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("retired-spice-set") })).StatusCode);    // archived
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Guid.NewGuid() })).StatusCode);
        var soldOut = await Body(await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("honey-cake") }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("not_purchasable", soldOut.GetProperty("code").GetString());
        Assert.Equal(0, await _k.Db(db => db.Carts.CountAsync(c => c.Id != Guid.Empty && db.CartLines.Any(l => l.CartId == c.Id))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task Quantities_outside_one_to_twenty_are_refused(int qty)
        => Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic"), quantity = qty })).StatusCode);

    [Fact]
    public async Task Parallel_adds_of_one_variant_make_one_line_and_never_exceed_the_cap()
    {
        var (_, token) = await GuestAdd("challah-classic", 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic"), quantity = 3 }, guest: token)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var v = await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: token));
        var lines = v.GetProperty("groups")[0].GetProperty("lines");
        Assert.Equal(1, lines.GetArrayLength());
        Assert.Equal(20, lines[0].GetProperty("quantity").GetInt32());   // 1 + 36 capped at 20
    }

    [Fact]
    public async Task Parallel_first_adds_for_a_buyer_create_one_cart()
    {
        var session = await _k.VerifiedSession("buyer1@example.com");
        await Ready();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic", i % 2), quantity = 1 }, bearer: session)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var acct = await _k.Account("buyer1@example.com");
        Assert.Equal(1, await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == acct.Id)));
        var v = await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: session));
        Assert.Equal(8, v.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task Quantity_can_be_changed_and_zero_removes_the_line_and_a_missing_line_is_404()
    {
        var (view, token) = await GuestAdd("challah-classic", 2);
        var id = view.GetProperty("groups")[0].GetProperty("lines")[0].GetProperty("id").GetGuid();
        var v = await Body(await Send(HttpMethod.Patch, $"/api/v1/cart/lines/{id}", new { quantity = 5 }, guest: token));
        Assert.Equal(5, v.GetProperty("itemCount").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Patch, $"/api/v1/cart/lines/{id}", new { quantity = 21 }, guest: token)).StatusCode);
        v = await Body(await Send(HttpMethod.Delete, $"/api/v1/cart/lines/{id}", guest: token));
        Assert.Equal(0, v.GetProperty("groups").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/v1/cart/lines/{id}", guest: token)).StatusCode);
    }

    [Fact]
    public async Task Another_carts_line_is_404_and_a_bad_bearer_is_401_never_a_guest_cart()
    {
        var (view, _) = await GuestAdd("challah-classic", 1);
        var id = view.GetProperty("groups")[0].GetProperty("lines")[0].GetProperty("id").GetGuid();
        var (_, other) = await GuestAdd("kiddush-cup-pewter", 1);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Patch, $"/api/v1/cart/lines/{id}", new { quantity = 3 }, guest: other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/v1/cart/lines/{id}", guest: other)).StatusCode);
        var buyer = await _k.VerifiedSession("buyer2@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/v1/cart/lines/{id}", bearer: buyer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/v1/cart", bearer: "not-a-session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic") }, bearer: "nope")).StatusCode);
        // an unknown guest token does not adopt that token: it gets a fresh cart and a fresh token
        var fresh = await Body(await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic") }, guest: "attacker-chosen-token"));
        Assert.NotEqual("attacker-chosen-token", fresh.GetProperty("cartToken").GetString());
    }

    [Fact]
    public async Task Price_changes_and_unlisting_are_seen_on_the_next_view_and_block_checkout()
    {
        var (_, token) = await GuestAdd("challah-classic", 2);
        var f = CatalogKit.Fixture(_k.F);
        var id = CatalogKit.ListingId("challah-classic");
        f.Upsert(id, l => l with { Variants = l.Variants.Select(v => v.Id == Variant("challah-classic") ? v with { PriceCents = 700 } : v).ToList() });
        await CatalogKit.Sync(_k.F);
        var v = await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: token));
        var line = v.GetProperty("groups")[0].GetProperty("lines")[0];
        Assert.Equal(700, line.GetProperty("unitPriceCents").GetInt64());
        Assert.Equal(650, line.GetProperty("previousUnitPriceCents").GetInt64());
        Assert.Equal(1400, v.GetProperty("subtotalCents").GetInt64());
        Assert.True(v.GetProperty("canCheckout").GetBoolean());

        f.Upsert(id, l => l with { Status = "Archived" });
        await CatalogKit.Sync(_k.F);
        v = await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: token));
        line = v.GetProperty("groups")[0].GetProperty("lines")[0];
        Assert.Equal("unavailable", line.GetProperty("status").GetString());
        Assert.Equal(0, line.GetProperty("lineTotalCents").GetInt64());
        Assert.Equal(0, v.GetProperty("subtotalCents").GetInt64());
        Assert.Equal(0, v.GetProperty("itemCount").GetInt32());
        Assert.False(v.GetProperty("canCheckout").GetBoolean());
    }

    [Fact]
    public async Task Sign_in_merges_the_guest_cart_once_even_when_requested_in_parallel()
    {
        var (_, token) = await GuestAdd("challah-classic", 3);
        await GuestAdd("kiddush-cup-pewter", 1, token: token);
        var session = await _k.VerifiedSession("buyer3@example.com");
        await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic"), quantity = 4 }, bearer: session);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: session)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var v = await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: session));
        Assert.Equal(3 + 4 + 1, v.GetProperty("itemCount").GetInt32());   // merged exactly once
        // the guest cart is gone, and a retry is a no-op
        Assert.Equal(0, (await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: token))).GetProperty("itemCount").GetInt32());
        await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: session);
        Assert.Equal(8, (await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: session))).GetProperty("itemCount").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token })).StatusCode);
    }

    [Fact]
    public async Task Merging_caps_quantities_and_an_unknown_token_changes_nothing()
    {
        var (_, token) = await GuestAdd("challah-classic", 15);
        var session = await _k.VerifiedSession("buyer4@example.com");
        await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic"), quantity = 15 }, bearer: session);
        await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = "unknown" }, bearer: session);
        Assert.Equal(15, (await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: session))).GetProperty("itemCount").GetInt32());
        await Send(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token }, bearer: session);
        Assert.Equal(20, (await Body(await Send(HttpMethod.Get, "/api/v1/cart", bearer: session))).GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task Retention_purges_idle_guest_carts_after_30_days_and_buyer_carts_after_90()
    {
        var (_, guest) = await GuestAdd("challah-classic", 1);
        var session = await _k.VerifiedSession("buyer5@example.com");
        await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant("challah-classic") }, bearer: session);
        async Task<int> Purge() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<CartMaintenance>().PurgeAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await Purge());
        _k.F.Clock.Advance(TimeSpan.FromDays(2));    // 31 days: guest goes, buyer stays
        Assert.Equal(1, await Purge());
        Assert.Equal(0, (await Body(await Send(HttpMethod.Get, "/api/v1/cart", guest: guest))).GetProperty("itemCount").GetInt32());
        Assert.Equal(1, await _k.Db(db => db.CartLines.CountAsync()));   // the buyer's cart (their session itself idled out long ago)
        _k.F.Clock.Advance(TimeSpan.FromDays(60));   // 91 days
        Assert.Equal(1, await Purge());
        Assert.Equal(0, await _k.Db(db => db.CartLines.CountAsync()));
    }
}
