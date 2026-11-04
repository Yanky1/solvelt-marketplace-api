using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Checkout;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Deleting an account redacts the buyer's orders and deletes their carts; totals stay (decision M3-37).</summary>
public sealed class OrdersAccountEraserTests : OrderFlowBase
{
    private async Task DeleteAccountAndRedact(string password)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/delete") { Content = JsonContent.Create(new { password }) };
        r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session);
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Http.SendAsync(r)).StatusCode);
        await Drain();
    }

    [Fact]
    public async Task Deleting_the_account_redacts_the_orders_keeps_the_money_and_deletes_the_carts()
    {
        await Login("eraser1@example.com");
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = Address() });
        await Pay(number, intent, total);
        await Drain();
        await AddToCart("challah-classic");                               // a cart that is still open
        var before = await Order(number);
        Assert.NotNull(before.AccountId);

        await DeleteAccountAndRedact(Infrastructure.AuthKit.Password);

        var o = await Order(number);
        Assert.Equal(("Redacted", "Redacted", "Redacted", "Redacted", null, "", null), (o.BuyerName, o.ShipName, o.ShipLine1, o.ShipCity, o.Phone, o.ShipPostalCode, o.GiftMessage));
        Assert.Equal(OrdersAccountEraser.Tombstone(before.AccountId!.Value), o.RelayEmail);
        Assert.Equal((before.TotalCents, before.TaxCents, before.Status, before.Number), (o.TotalCents, o.TaxCents, o.Status, o.Number));   // accounting stays
        Assert.NotEmpty(o.Lines);
        Assert.Equal(0, await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == before.AccountId)));
        Assert.Equal(0, await _k.Db(db => db.CartLines.CountAsync()));

        // idempotent
        using var scope = _k.F.Services.CreateScope();
        await new OrdersAccountEraser(scope.ServiceProvider.GetRequiredService<MarketplaceDb>()).EraseAsync(before.AccountId!.Value, default);
        Assert.Equal(o.RelayEmail, (await Order(number)).RelayEmail);
    }

    [Fact]
    public async Task A_guest_order_placed_with_the_deleted_address_loses_that_address()
    {
        const string email = "eraser2@example.com";
        await Login(email);
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = Address() });
        await Pay(number, intent, total);
        await Drain();
        // make it a guest order placed with this address (no account)
        await _k.Db(async db =>
        {
            await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.AccountId, (Guid?)null).SetProperty(o => o.GuestEmail, email));
            return 0;
        });

        await DeleteAccountAndRedact(Infrastructure.AuthKit.Password);

        Assert.Null((await Order(number)).GuestEmail);
    }
}
