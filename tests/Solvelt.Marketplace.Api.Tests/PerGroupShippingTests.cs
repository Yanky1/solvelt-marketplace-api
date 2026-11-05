using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The portal's C8 (per seller order shipping speed, method and tax) and C9 (explicit tax on refund events), with Portal:PerGroupShipping ON, which is the default (decision M3-37). The refund numbers are
/// the worked example of the portal's ORDERS.md "Money in events": shipping 500 with tax 45 refunded as 218/200/18/18 (amount, shipping, shipping tax, tax), then 327/300/27/27.
/// </summary>
public sealed class PerGroupShippingTests : OrderFlowBase
{
    private int _portalEvent;

    private static OrderGroup Group(string kind, string? speed, long shipping, long shippingTax) => new() { SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), DeliveryKind = kind, ShippingSpeed = speed, ShippingCents = shipping, ShippingTaxCents = shippingTax };

    [Fact]
    public void Each_shipping_entry_carries_an_enum_speed_a_short_method_and_a_tax_only_when_there_is_an_amount()
    {
        var expedited = IngestMapping.ShippingCharge(Group("carrier", "Expedited", 900, 80), perGroup: true);
        Assert.Equal(("expedited", "Expedited", 900L, 80L), (expedited.Speed, expedited.Method, expedited.AmountCents, expedited.TaxCents));
        foreach (var speed in IngestMapping.Speeds)
        {
            var c = IngestMapping.ShippingCharge(Group("carrier", speed, 100, 0), perGroup: true);
            Assert.Equal(speed, c.Speed);
            Assert.InRange(c.Method!.Length, 1, IngestMapping.MaxMethodLength);
            Assert.Equal(0L, c.TaxCents);                                                   // zero tax with an amount is stated as zero
        }
        var free = IngestMapping.ShippingCharge(Group("carrier", "standard", 0, 0), perGroup: true);
        Assert.Null(free.TaxCents);                                                         // the portal refuses a tax on a shipping amount of zero: the field is left out
        var local = IngestMapping.ShippingCharge(Group("local", null, 300, 27), perGroup: true);
        Assert.Equal(("standard", "local", 27L), (local.Speed, local.Method, local.TaxCents));
        var off = IngestMapping.ShippingCharge(Group("carrier", "expedited", 900, 80), perGroup: false);
        Assert.Equal((null, null, null), (off.Speed, off.Method, off.TaxCents));            // the emergency path sends none of the C8 fields
    }

    [Fact]
    public void A_speed_the_portals_ingest_would_refuse_is_never_offered_to_a_buyer()
    {
        var zone = new Solvelt.Marketplace.Api.Portal.ShippingZone("US", "domestic", ["US"],
            [new("ground", true, "flat", 500, null, null), new("Expedited", true, "flat", 900, null, null), new("standard", true, "flat", 400, null, null)]);
        var s = new Solvelt.Marketplace.Api.Portal.ShippingSettings(Guid.NewGuid(), Guid.NewGuid(), 1, null,
            [new Solvelt.Marketplace.Api.Portal.ShippingProfile(Guid.NewGuid(), "d", true, "USD", null, [zone])]);
        var offered = ShippingRates.Options(s, "US", 1000, null, new DateOnly(2026, 11, 4), new ShippingOptions()).Options;
        Assert.Equal(["expedited", "standard"], offered.Select(o => o.Speed.ToLowerInvariant()).OrderBy(x => x));
    }

    [Theory]
    [InlineData("ground")]
    [InlineData("")]
    [InlineData(null)]
    public void A_speed_outside_the_portals_enum_cannot_be_mapped(string? speed)
    {
        Assert.Throws<InvalidShippingException>(() => IngestMapping.ShippingCharge(Group("carrier", speed, 500, 44), perGroup: true));
        Assert.Throws<InvalidShippingException>(() => IngestMapping.OrderMethodAndSpeed([Group("carrier", speed, 500, 44)]));
        Assert.Throws<InvalidShippingException>(() => IngestMapping.ShippingCharge(Group("carrier", "standard", 500, -1), perGroup: true));
    }

    [Fact]
    public async Task An_order_whose_stored_speed_is_not_in_the_enum_never_reaches_the_portal_and_waits_paid_for_a_person()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);                                                   // the ingest message is queued
        await _k.Db(db => db.OrderGroups.ExecuteUpdateAsync(s => s.SetProperty(g => g.ShippingSpeed, "ground")));
        await Drain();
        Assert.Empty(Portal("orders"));                                                     // nothing was sent: the portal would answer 422
        Assert.Equal("paid", (await Order(number)).Status);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Dead)));
    }

    [Fact]
    public async Task The_quoted_and_ingested_speed_is_one_of_the_enum_and_line_tax_does_not_include_the_shipping_tax()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var call = Assert.Single(Portal("orders"));
        using var doc = JsonDocument.Parse(call.BodyText);
        var ship = doc.RootElement.GetProperty("shipping")[0];
        Assert.Contains(ship.GetProperty("speed").GetString(), IngestMapping.Speeds);
        Assert.InRange(ship.GetProperty("method").GetString()!.Length, 1, 80);
        var o = await Order(number);
        Assert.Equal(o.Groups.Single().ShippingTaxCents, ship.GetProperty("taxCents").GetInt64());
        Assert.Equal(o.Lines.Single().TaxCents, doc.RootElement.GetProperty("lines")[0].GetProperty("taxCents").GetInt64());   // the line's own tax only
    }

    private async Task<string> PortalEvent(string type, MarketplaceOrder o, OrderGroup g, object data)
    {
        var d = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        foreach (var p in data.GetType().GetProperties()) d[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(data);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"pe-c9-{++_portalEvent:D3}", type, _k.F.Clock.GetUtcNow(), d)));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task The_portals_worked_example_of_a_shipping_refund_in_two_parts_adds_up_exactly_in_the_buyers_refund_and_in_refund_processed()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter", 3);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        var g = o.Groups.Single();
        Assert.Equal(500, g.ShippingCents);
        await _k.Db(db => db.OrderGroups.Where(x => x.Id == g.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShippingTaxCents, 45L).SetProperty(x => x.TaxCents, x => x.TaxCents + 45 - g.ShippingTaxCents)));
        Assert.Equal("applied", await PortalEvent("refund.issued", o, g, new { RefundId = "c9-1", AmountCents = 218, ShippingCents = 200, ShippingTaxCents = 18, TaxCents = 18 }));
        Assert.Equal("applied", await PortalEvent("refund.issued", o, g, new { RefundId = "c9-2", AmountCents = 327, ShippingCents = 300, ShippingTaxCents = 27, TaxCents = 27 }));
        await Drain();
        Assert.Equal([218L, 327L], Stripe.Refunds.Select(r => r.AmountCents).OrderBy(x => x));          // the buyer gets exactly the portal's amounts
        var sent = Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement.GetProperty("data")).Where(d => d.TryGetProperty("refundId", out _) && d.GetProperty("shippingCents").GetInt64() > 0).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal([200L, 300L], sent.Select(d => d.GetProperty("shippingCents").GetInt64()).OrderBy(x => x));   // shipping net of tax
        Assert.All(sent, d => Assert.Equal(0L, d.GetProperty("amountCents").GetInt64()));              // items = amount - tax - shipping
        var after = await _k.Db(db => db.OrderGroups.AsNoTracking().SingleAsync(x => x.Id == g.Id));
        Assert.Equal((500L, 45L, 45L), (after.RefundedShippingCents, after.RefundedShippingTaxCents, after.RefundedTaxCents));   // summed over both refunds, exact
    }

    [Fact]
    public void Every_currency_the_portal_receives_is_upper_case_whatever_the_caller_held()
    {
        var json = (object o) => JsonSerializer.Serialize(o, Solvelt.Marketplace.Api.Portal.PortalJson.Options);
        Assert.Contains("\"currency\":\"USD\"", json(new Solvelt.Marketplace.Api.Portal.PaymentCapturedData("order", "MP-1", null, "SO-1", "pi_1", "usd", 100)));
        Assert.Contains("\"currency\":\"USD\"", json(new Solvelt.Marketplace.Api.Portal.RefundProcessedData("re_1", "MP-1", "SO-1", null, 100, 0, "usd")));
        Assert.Contains("\"currency\":\"USD\"", json(new Solvelt.Marketplace.Api.Portal.DisputeData("dp_1", "MP-1", "SO-1", null, 100, "needs_response", null, " usd ")));
        Assert.DoesNotContain("currency", json(new Solvelt.Marketplace.Api.Portal.DisputeData("dp_1", "MP-1", "SO-1", null, 100, "needs_response")));   // none held: none sent
    }

    [Fact]
    public async Task Stripe_events_in_lower_case_usd_reach_the_portal_as_USD_for_the_order_the_capture_and_the_refund()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter", 2);
        var (number, intent, total) = await Checkout();
        Stripe.Capture(intent);
        Assert.True((await PostStripe(Event("payment_intent.succeeded", intent, number, total, currency: "usd"))).IsSuccessStatusCode);   // Stripe says "usd"
        await Drain();
        var order = JsonDocument.Parse(Assert.Single(Portal("orders")).BodyText).RootElement;
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        var events = Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement).ToList();
        Assert.Equal("USD", Assert.Single(events, e => e.GetProperty("type").GetString() == "payment.captured").GetProperty("data").GetProperty("currency").GetString());
        var o = await Order(number);
        var g = o.Groups.Single();
        Assert.Equal("applied", await PortalEvent("refund.issued", o, g, new { RefundId = "usd-1", AmountCents = 500, ShippingCents = 500, ShippingTaxCents = 0 }));
        await Drain();
        var refunded = Portal("payment-events").Select(c => JsonDocument.Parse(c.BodyText).RootElement).Single(e => e.GetProperty("type").GetString() == "refund.processed");
        Assert.Equal("USD", refunded.GetProperty("data").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task A_line_of_a_synthetic_default_variant_is_sent_without_a_variant_id_and_a_real_variant_keeps_its_id()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        await AddToCart("challah-classic");
        var (number, intent, total) = await Checkout();
        var cup = (await Order(number)).Lines.Single(l => l.ListingId == CatalogKit.ListingId("kiddush-cup-pewter")).VariantId;
        await _k.Db(db => db.CatalogVariants.Where(v => v.Id == cup).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsDefault, true)).ContinueWith(_ => 0));   // the portal sent this listing with no variants
        await Pay(number, intent, total);
        await Drain();
        using var doc = JsonDocument.Parse(Assert.Single(Portal("orders")).BodyText);
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        var cupLine = lines.Single(l => l.GetProperty("productId").GetGuid() == CatalogKit.ListingId("kiddush-cup-pewter"));
        Assert.False(cupLine.TryGetProperty("variantId", out _));                           // null is left out
        var challah = lines.Single(l => l.GetProperty("productId").GetGuid() == CatalogKit.ListingId("challah-classic"));
        Assert.True(challah.TryGetProperty("variantId", out var v) && v.GetGuid() != Guid.Empty);
    }

    [Fact]
    public async Task L4_a_line_whose_variant_id_is_its_listing_id_is_sent_without_a_variant_id_even_when_the_catalog_row_is_gone()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        var listing = CatalogKit.ListingId("kiddush-cup-pewter");
        await _k.Db(db => db.OrderLines.Where(l => l.ListingId == listing).ExecuteUpdateAsync(s => s.SetProperty(l => l.VariantId, listing)).ContinueWith(_ => 0));   // the synthetic default variant's id IS the listing id
        await Pay(number, intent, total);
        await Drain();
        using var doc = JsonDocument.Parse(Assert.Single(Portal("orders")).BodyText);
        Assert.False(doc.RootElement.GetProperty("lines")[0].TryGetProperty("variantId", out _));
    }

    [Fact]
    public void L5_a_shipping_speed_without_a_name_is_not_offered_and_does_not_throw()
    {
        var zone = new Solvelt.Marketplace.Api.Portal.ShippingZone("US", "domestic", ["US"], [new(null!, true, "flat", 500, null, null), new("standard", true, "flat", 400, null, null)]);
        var s = new Solvelt.Marketplace.Api.Portal.ShippingSettings(Guid.NewGuid(), Guid.NewGuid(), 1, null,
            [new Solvelt.Marketplace.Api.Portal.ShippingProfile(Guid.NewGuid(), "d", true, "USD", null, [zone])]);
        var offered = ShippingRates.Options(s, "US", 1000, null, new DateOnly(2026, 11, 4), new ShippingOptions()).Options;
        Assert.Equal(["standard"], offered.Select(o => o.Speed));
    }
}
