using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The request header names the web site depends on, written out as LITERALS (the other tests use the constants, so a wrong constant would pass them all). The API's docs once said
/// "X-Cart-Token" while the code read "X-Solvelt-Cart-Token": the web followed the docs and every guest cart silently had no owner (review H1).
/// </summary>
public sealed class HeaderContractTests : IDisposable
{
    private readonly AuthKit _k = new();
    public void Dispose() => _k.Dispose();

    [Fact]
    public void The_guest_header_names_are_the_ones_the_web_sends()
    {
        Assert.Equal("X-Solvelt-Cart-Token", CartEndpoints.GuestHeader);
        Assert.Equal("X-Guest-Order", CheckoutEndpoints.GuestOrderHeader);
    }

    [Fact]
    public async Task A_guest_cart_is_found_by_X_Solvelt_Cart_Token_and_not_by_the_old_wrong_name()
    {
        await CatalogKit.Sync(_k.F);
        var variant = Solvelt.Marketplace.Api.Catalog.FixtureCatalogSource.Id("variant:challah-classic:0");
        var add = await _k.Http.PostAsJsonAsync("/api/v1/cart/lines", new { variantId = variant, quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var token = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement.GetProperty("cartToken").GetString()!;

        async Task<int> ItemCount(string header)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cart");
            r.Headers.Add(header, token);
            var res = await _k.Http.SendAsync(r);
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("itemCount").GetInt32();
        }
        Assert.Equal(1, await ItemCount("X-Solvelt-Cart-Token"));        // the real name finds the cart
        Assert.Equal(0, await ItemCount("X-Cart-Token"));                // the old name finds nothing: the request has no owner
    }
}
