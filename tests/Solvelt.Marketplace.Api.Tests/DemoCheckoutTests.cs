using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Data;
using Xunit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M9-04 and M9-07: a cart with a demo line is never priced or paid; and a buyer who touches a demo line (same variant, a new quantity, a merge) owns it, so Remove cannot delete their units.</summary>
public sealed class DemoCheckoutTests : OrderFlowBase
{
    public DemoCheckoutTests() : base(new() { ["Demo:Enabled"] = "true" }) { }

    private async Task<HttpResponseMessage> Call(HttpMethod m, string path, object? body = null, string? guest = null, bool signedIn = true)
    {
        var r = new HttpRequestMessage(m, path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (signedIn) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        if (guest is not null) r.Headers.Add(CartEndpoints.GuestHeader, guest);
        return await _k.Http.SendAsync(r);
    }
    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    private async Task FillDemo() => Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/demo/fill")).StatusCode);
    private async Task RemoveDemo() => Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/demo/remove")).StatusCode);
    private Task<List<CartLine>> Lines() => _k.Db(db => db.CartLines.AsNoTracking().Where(l => db.Carts.Any(c => c.Id == l.CartId && db.BuyerAccounts.Any(a => a.Id == c.AccountId && a.Email == "dco@example.com"))).ToListAsync());

    private async Task<string> Refusal(HttpMethod m, string path, object body)
    {
        var res = await Call(m, path, body);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var code = (await Body(res)).GetProperty("code").GetString()!;
        return code;
    }

    [Fact]
    public async Task A_cart_with_a_demo_line_is_refused_at_the_quote_and_at_checkout_and_nothing_is_created_or_charged()
    {
        await Login("dco@example.com");
        await FillDemo();
        Assert.Equal("demo_data", await Refusal(HttpMethod.Post, "/api/v1/checkout/quote", new { address = Address() }));
        Assert.Equal("demo_data", await Refusal(HttpMethod.Post, "/api/v1/checkout", new { address = Address(), quoteHash = "anything" }));
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync(o => !o.IsDemo)));
        Assert.Empty(Stripe.Intents);
    }

    [Fact]
    public async Task The_refusal_looks_at_the_lines_so_a_real_cart_with_one_demo_line_is_refused_and_a_cart_handed_back_by_Remove_can_be_checked_out()
    {
        await Login("dco@example.com");
        await FillDemo();
        // the buyer adds a REAL line (a variant the demo cart does not hold) to the same cart
        var held = (await Lines()).Select(l => l.VariantId).ToHashSet();
        var real = await _k.Db(db => (from v in db.CatalogVariants join l in db.CatalogListings on v.ListingId equals l.Id where l.Visible && l.Available && v.Available && l.Type == "product" select v.Id).ToListAsync());
        var realVariant = real.First(v => !held.Contains(v));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = realVariant, quantity = 1 })).StatusCode);
        Assert.Equal("demo_data", await Refusal(HttpMethod.Post, "/api/v1/checkout/quote", new { address = Address() }));      // still demo lines in it
        await RemoveDemo();
        var left = await Lines();
        Assert.Equal(realVariant, Assert.Single(left).VariantId);                                                           // only the real line is left
        // and the cart can be checked out: a real order, a real intent
        var (number, _, _) = await Checkout();
        Assert.False((await Order(number)).IsDemo);
    }

    [Fact]
    public async Task Adding_the_same_variant_to_a_demo_line_makes_the_units_the_buyers_own_and_Remove_keeps_them()
    {
        await Login("dco@example.com");
        await FillDemo();
        var demoLine = (await Lines()).First(l => l.IsDemo);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = demoLine.VariantId, quantity = 2 })).StatusCode);
        var line = (await Lines()).Single(l => l.VariantId == demoLine.VariantId);
        Assert.False(line.IsDemo); Assert.Equal(demoLine.Quantity + 2, line.Quantity);                                      // the marker is gone, the units add up
        await RemoveDemo();
        var kept = Assert.Single(await Lines());                                                                            // the other demo lines went, this one stayed
        Assert.Equal((demoLine.VariantId, demoLine.Quantity + 2, false), (kept.VariantId, kept.Quantity, kept.IsDemo));
    }

    [Fact]
    public async Task Changing_the_quantity_of_a_demo_line_makes_it_the_buyers_own_and_Remove_keeps_it()
    {
        await Login("dco@example.com");
        await FillDemo();
        var demoLine = (await Lines()).First(l => l.IsDemo);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/cart/lines/{demoLine.Id}", new { quantity = 3 })).StatusCode);
        Assert.False((await Lines()).Single(l => l.Id == demoLine.Id).IsDemo);
        await RemoveDemo();
        var kept = Assert.Single(await Lines());
        Assert.Equal((demoLine.Id, 3), (kept.Id, kept.Quantity));
    }

    [Fact]
    public async Task A_guest_cart_merged_into_a_demo_line_makes_the_units_the_buyers_own_and_Remove_keeps_them()
    {
        await Login("dco@example.com");
        await FillDemo();
        var demoLine = (await Lines()).First(l => l.IsDemo);
        var add = await Call(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = demoLine.VariantId, quantity = 2 }, signedIn: false);
        var token = (await Body(add)).GetProperty("cartToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/cart/merge", new { guestToken = token })).StatusCode);
        var line = (await Lines()).Single(l => l.VariantId == demoLine.VariantId);
        Assert.False(line.IsDemo); Assert.Equal(demoLine.Quantity + 2, line.Quantity);
        await RemoveDemo();
        Assert.Equal(demoLine.VariantId, Assert.Single(await Lines()).VariantId);
    }
}
