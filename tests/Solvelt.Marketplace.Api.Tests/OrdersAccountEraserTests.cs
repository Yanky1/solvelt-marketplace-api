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

    /// <summary>Every seller order of the order delivered: the order is closed.</summary>
    private Task CloseOrder(MarketplaceOrder order) => _k.Db(async db =>
    {
        foreach (var g in await db.OrderGroups.Where(x => x.OrderId == order.Id).ToListAsync())
            db.SellerOrderStates.Add(new SellerOrderState { GroupId = g.Id, OrderId = order.Id, Status = FulfilmentStatus.Delivered, UpdatedAt = _k.F.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        return 0;
    });

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
        await CloseOrder(before);                                         // only CLOSED orders are redacted at deletion (decision M3-40): this one is delivered

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

        // a guest order placed with ANOTHER address must keep its address: only the deleted account's mailbox is cleared
        var other = Guid.NewGuid();
        await _k.Db(async db =>
        {
            var template = await db.Orders.AsNoTracking().SingleAsync(o => o.Number == number);
            template.Id = other; template.Number = "MP-OTHER01"; template.GuestEmail = "someone-else@example.com"; template.AccountId = null; template.ActiveKey = null; template.PaymentIntentId = null;
            db.Orders.Add(template);
            await db.SaveChangesAsync();
            return 0;
        });

        await DeleteAccountAndRedact(Infrastructure.AuthKit.Password);

        // the account's own address was already null when the erasers ran (M1 frees it at delete time), and the guest order still lost it: the hook is handed the address before it is freed
        var account = await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.DeletedAt != null));
        Assert.Null(account.Email);
        Assert.Null((await Order(number)).GuestEmail);                                      // otherwise a new account with the same mailbox could claim it
        Assert.Equal("someone-else@example.com", (await _k.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == other))).GuestEmail);
    }

    [Fact]
    public async Task An_open_order_keeps_its_address_and_relay_so_the_seller_can_still_ship_and_the_portal_gets_the_real_data()
    {
        await Login("eraser3@example.com");
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = Address() });
        await Pay(number, intent, total);                                 // paid, but the ingest has NOT run yet
        var before = await Order(number);

        await DeleteAccountAndRedact(Infrastructure.AuthKit.Password);   // the redaction job runs, then the ingest (Drain)

        var o = await Order(number);
        Assert.Equal(("Jane B.", "Jane Brown", "1 Main St", "Brooklyn", "11201"), (o.BuyerName, o.ShipName, o.ShipLine1, o.ShipCity, o.ShipPostalCode));   // untouched
        Assert.Equal(before.RelayEmail, o.RelayEmail);                    // the relay still works for seller messages and refund mail
        var sent = System.Text.Json.JsonDocument.Parse(Assert.Single(Portal("orders")).BodyText).RootElement.GetProperty("shippingAddress");
        Assert.Equal("1 Main St", sent.GetProperty("line1").GetString());   // the seller got a real, shippable address
        Assert.Equal("placed", o.Status);
        Assert.Equal(0, await _k.Db(db => db.Carts.CountAsync(c => c.AccountId == before.AccountId)));   // the carts are still deleted

        // the sweep (decision O-23): while the order is open it changes nothing; once it is closed the sweep redacts it, once
        Assert.Equal(0, await _k.Db(db => OrdersAccountEraser.SweepDeletedAccountsAsync(db, default)));
        Assert.Equal("1 Main St", (await Order(number)).ShipLine1);
        await CloseOrder(o);
        Assert.Equal(1, await _k.Db(db => OrdersAccountEraser.SweepDeletedAccountsAsync(db, default)));
        var swept = await Order(number);
        Assert.Equal(("Redacted", "Redacted", "Redacted", "", OrdersAccountEraser.Tombstone(before.AccountId!.Value)), (swept.BuyerName, swept.ShipName, swept.ShipLine1, swept.ShipPostalCode, swept.RelayEmail));
        Assert.Equal((before.TotalCents, before.TaxCents), (swept.TotalCents, swept.TaxCents));          // accounting stays
        Assert.Equal(0, await _k.Db(db => OrdersAccountEraser.SweepDeletedAccountsAsync(db, default)));   // idempotent
    }

    [Fact]
    public async Task The_sweep_never_touches_a_closed_order_of_a_live_account()
    {
        await Login("eraser6@example.com");
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = Address() });
        await Pay(number, intent, total);
        await Drain();
        await CloseOrder(await Order(number));
        Assert.Equal(0, await _k.Db(db => OrdersAccountEraser.SweepDeletedAccountsAsync(db, default)));   // the account is not deleted: its order keeps its address (the 90-day job redacts it later)
        Assert.Equal("1 Main St", (await Order(number)).ShipLine1);
    }

    [Fact]
    public async Task A_claimed_guest_order_loses_its_guest_address_open_or_closed()
    {
        await Login("eraser4@example.com");
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout(new { address = Address() });
        await Pay(number, intent, total);
        await Drain();
        await _k.Db(db => db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(s => s.SetProperty(o => o.GuestEmail, "claimed-guest@example.com")).ContinueWith(_ => 0));   // a guest order claimed by this account
        Assert.Equal("claimed-guest@example.com", (await Order(number)).GuestEmail);

        await DeleteAccountAndRedact(Infrastructure.AuthKit.Password);

        Assert.Null((await Order(number)).GuestEmail);                    // the order is still open (kept in full), but the guest address is gone
    }
}
