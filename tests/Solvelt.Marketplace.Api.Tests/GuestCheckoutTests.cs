using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Checkout as a guest, the order-access link, and claiming the order once the address is proven (decision M3-24).</summary>
public sealed class GuestCheckoutTests : OrderFlowBase
{
    private string _cartToken = "";

    private async Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null, string? guestCart = null, string? guestOrder = null, string? bearer = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (guestCart != null) r.Headers.Add(CartEndpoints.GuestHeader, guestCart);
        if (guestOrder != null) r.Headers.Add(CheckoutEndpoints.GuestOrderHeader, guestOrder);
        if (bearer != null) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return await _k.Http.SendAsync(r);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var s = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode}: {s}");
        return JsonDocument.Parse(s).RootElement.Clone();
    }

    private async Task GuestCart(string slug, int qty = 1)
    {
        await CatalogKit.Sync(_k.F);
        var res = await Json(await Send(HttpMethod.Post, "/api/v1/cart/lines", new { variantId = Variant(slug), quantity = qty }, guestCart: _cartToken.Length > 0 ? _cartToken : "unknown-start"));
        if (_cartToken.Length == 0 && res.TryGetProperty("cartToken", out var t) && t.ValueKind == JsonValueKind.String) _cartToken = t.GetString()!;
    }

    private async Task<JsonElement> GuestCheckout(string email, HttpStatusCode expected = HttpStatusCode.OK, string region = "NY")
    {
        var address = Address(region);
        var quote = await Json(await Send(HttpMethod.Post, "/api/v1/checkout/quote", new { address }, guestCart: _cartToken));
        return await Json(await Send(HttpMethod.Post, "/api/v1/checkout", new { address, email, quoteHash = quote.GetProperty("quoteHash").GetString() }, guestCart: _cartToken), expected);
    }

    [Fact]
    public async Task A_guest_checks_out_pays_gets_the_link_by_mail_and_follows_the_order_with_it()
    {
        await GuestCart("kiddush-cup-pewter");
        var placed = await GuestCheckout("  Guest@Example.COM ");
        var number = placed.GetProperty("orderNumber").GetString()!;
        var token = placed.GetProperty("guestOrderToken").GetString()!;
        var order = await Order(number);
        Assert.Null(order.AccountId);
        Assert.Equal("guest@example.com", order.GuestEmail);                                  // trimmed and lower-cased: what a verified account claims by
        Assert.EndsWith("@relay.solvelt.test", order.RelayEmail);                             // a relay address of its own: sellers never see the buyer's
        Assert.DoesNotContain("guest@example.com", order.RelayEmail);
        var intent = Stripe.Intents.Single();
        Assert.Equal(placed.GetProperty("quote").GetProperty("totalCents").GetInt64(), intent.AmountCents);
        // the confirmation page polls with the link's token
        Assert.Equal("pending_payment", (await Json(await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", guestOrder: token))).GetProperty("status").GetString());
        await Pay(number, intent.Id, intent.AmountCents);
        await Drain();
        var linkMail = Assert.Single(_k.Sink.Sent, m => m.To == "guest@example.com" && m.TextBody.Contains(token));   // the link goes to the checkout address only, once
        Assert.Contains(number, linkMail.Subject);
        Assert.Contains(_k.Sink.Sent, m => m.To == "guest@example.com" && m.Subject.Contains("Your Solvelt order"));   // and the usual "order placed" mail
        Assert.DoesNotContain(_k.Sink.Sent, m => m.To != "guest@example.com" && m.TextBody.Contains(token));
        Assert.Equal("placed", (await Json(await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", guestOrder: token))).GetProperty("status").GetString());
        var full = await Json(await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", guestOrder: token));
        Assert.True(full.TryGetProperty("groups", out _));                                    // before a claim the guest sees their own order
        // the cart was emptied by the payment
        Assert.Equal(0, (await Json(await Send(HttpMethod.Get, "/api/v1/cart", guestCart: _cartToken))).GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task A_guest_address_typed_in_decomposed_form_is_stored_in_the_same_form_as_the_accounts_and_is_erased_with_it()
    {
        const string nfd = "Jose\u0301.Perez@Example.COM";                                         // e + combining acute, mixed case
        const string nfc = "jos\u00e9.perez@example.com";                                          // what M1 stores for the account (NFC, lower-case)
        await GuestCart("kiddush-cup-pewter");
        var placed = await GuestCheckout("  " + nfd + " ");
        var order = await Order(placed.GetProperty("orderNumber").GetString()!);
        Assert.Equal(nfc, order.GuestEmail);                                                      // so a verified account with the NFC address claims it, and the claim matches
        Assert.Equal(nfc, Solvelt.Marketplace.Api.Auth.AuthValidation.NormaliseEmail(nfd));
        // deleting the account that owned the mailbox erases the guest order's address (the hook is handed the account's NFC address)
        await _k.Db(async db => { await new Solvelt.Marketplace.Api.Checkout.OrdersAccountEraser(db).OnDeletingAsync(Guid.NewGuid(), nfc, default); return 0; });
        Assert.Null((await Order(order.Number)).GuestEmail);
    }

    [Fact]
    public async Task The_guest_link_opens_only_its_own_order_and_a_missing_forged_or_expired_one_opens_nothing()
    {
        await GuestCart("kiddush-cup-pewter");
        var first = await GuestCheckout("one@example.com");
        var number1 = first.GetProperty("orderNumber").GetString()!;
        var token1 = first.GetProperty("guestOrderToken").GetString()!;
        // a second guest, a second order
        _cartToken = "";
        await GuestCart("challah-classic");
        var second = await GuestCheckout("two@example.com");
        var number2 = second.GetProperty("orderNumber").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number2}", guestOrder: token1)).StatusCode);   // a link of ANOTHER order
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number1}")).StatusCode);                  // none
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number1}", guestOrder: token1[..^2] + "xx")).StatusCode);   // forged
        _k.F.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number1}", guestOrder: token1)).StatusCode);   // expired
        // a bad Bearer never falls back to the guest path
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number1}", guestOrder: token1, bearer: "not-a-session")).StatusCode);
        // every answer is private
        var res = await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number1}");
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_guest_checkout_needs_a_cart_a_valid_email_and_a_real_session_if_one_is_sent()
    {
        await GuestCart("kiddush-cup-pewter");
        foreach (var bad in new[] { null, "", "not-an-email", "a@b", new string('x', 300) + "@example.com" })
        {
            var address = Address();
            var quote = await Json(await Send(HttpMethod.Post, "/api/v1/checkout/quote", new { address }, guestCart: _cartToken));
            var r = await Json(await Send(HttpMethod.Post, "/api/v1/checkout", new { address, email = bad, quoteHash = quote.GetProperty("quoteHash").GetString() }, guestCart: _cartToken), HttpStatusCode.UnprocessableEntity);
            Assert.True(r.GetProperty("errors").TryGetProperty("email", out _));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/checkout", new { address = Address(), email = "a@example.com", quoteHash = "x" })).StatusCode);   // no cart token, no session
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/checkout", new { address = Address(), email = "a@example.com", quoteHash = "x" }, guestCart: _cartToken, bearer: "bogus")).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.Orders.CountAsync()));
        Assert.Empty(Stripe.Intents);
    }

    [Fact]
    public async Task Checking_out_again_as_the_same_guest_is_the_same_order_and_a_changed_email_is_another_one()
    {
        await GuestCart("kiddush-cup-pewter");
        var a = await GuestCheckout("same@example.com");
        var b = await GuestCheckout("same@example.com");                                       // a double submit
        Assert.Equal(a.GetProperty("orderNumber").GetString(), b.GetProperty("orderNumber").GetString());
        Assert.Single(Stripe.Intents);
        var c = await GuestCheckout("other@example.com");                                      // another address is another checkout: the first is superseded
        Assert.NotEqual(a.GetProperty("orderNumber").GetString(), c.GetProperty("orderNumber").GetString());
        Assert.Equal("superseded", (await Order(a.GetProperty("orderNumber").GetString()!)).Status);
    }

    [Fact]
    public async Task A_verified_account_claims_the_guest_orders_of_its_address_once_and_the_guest_link_then_shows_only_the_status()
    {
        await GuestCart("kiddush-cup-pewter");
        var placed = await GuestCheckout("owner@example.com");
        var number = placed.GetProperty("orderNumber").GetString()!;
        var token = placed.GetProperty("guestOrderToken").GetString()!;
        var intent = Stripe.Intents.Single();
        await Pay(number, intent.Id, intent.AmountCents);
        await Drain();
        // someone else's account claims nothing
        var other = await _k.VerifiedSession("someone-else@example.com");
        Assert.Null((await Order(number)).AccountId);
        // the address's owner proves it (verified sign-up)
        var owner = await _k.VerifiedSession("owner@example.com");
        var claimed = await Order(number);
        Assert.NotNull(claimed.AccountId);
        Assert.NotNull(claimed.ClaimedAt);
        Assert.Equal((await _k.Account("owner@example.com")).Id, claimed.AccountId);
        // the owner reads it with their session (full view) ...
        var mine = await Json(await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", bearer: owner));
        Assert.True(mine.TryGetProperty("groups", out _));
        // ... another account cannot ...
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", bearer: other)).StatusCode);
        // ... and the guest link is FROZEN: only number, status and date, nothing of the owner's (review of the claim: whoever placed the order with this address still holds the link)
        var frozenText = await (await Send(HttpMethod.Get, $"/api/v1/checkout/orders/{number}", guestOrder: token)).Content.ReadAsStringAsync();
        var frozen = JsonDocument.Parse(frozenText).RootElement;
        Assert.Equal(["claimed", "createdAt", "number", "status"], frozen.EnumerateObject().Select(p => p.Name).Order().ToList());
        Assert.DoesNotContain("Main St", frozenText);
        Assert.DoesNotContain("Brooklyn", frozenText);
        // claiming again changes nothing
        using var scope = _k.F.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IGuestOrderClaimer>().ClaimAsync(claimed.AccountId!.Value, "owner@example.com", default));
    }

    [Fact]
    public async Task A_rejected_guest_order_is_refunded_and_the_guest_is_told_at_the_checkout_address()
    {
        await GuestCart("kiddush-cup-pewter");
        var placed = await GuestCheckout("refund-me@example.com");
        var number = placed.GetProperty("orderNumber").GetString()!;
        _onOrder = _ => FakePortal.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"invalid_order\",\"errors\":[{\"index\":0,\"code\":\"product_inactive\",\"message\":\"x\"}]}");
        var intent = Stripe.Intents.Single();
        await Pay(number, intent.Id, intent.AmountCents);
        await Drain();
        Assert.Equal("failed", (await Order(number)).Status);
        Assert.Equal(intent.AmountCents, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Single(_k.Sink.Sent, m => m.To == "refund-me@example.com" && m.Subject.Contains("couldn't place"));
    }
}
