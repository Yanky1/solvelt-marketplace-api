using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M5 slice 2: carrier timeline and ETA, the local-delivery live view (positions only while the stop is next, kept at most 24 h, never logged) and proof-of-delivery images proxied through us.</summary>
public sealed class TrackingTests : OrderFlowBase
{
    private int _n;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private async Task<(MarketplaceOrder O, OrderGroup G)> Shipped()
    {
        await Login();
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        var g = o.Groups.Single();
        await Evt("order.shipped", o, g, new { shipmentId = "sh-1", carrier = "Local", trackingNumber = "LD-1", lines = new[] { new { marketplaceLineId = o.Lines.Single().MarketplaceLineId, quantity = 2 } } });
        return (o, g);
    }

    private async Task<string> Evt(string type, MarketplaceOrder o, OrderGroup g, object? extra = null, DateTimeOffset? at = null)
    {
        var data = new Dictionary<string, object?> { ["order"] = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number } };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"te-{++_n:D3}", type, at ?? _k.F.Clock.GetUtcNow().AddSeconds(_n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private async Task<HttpResponseMessage> Get(string path, string? session = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private async Task<JsonElement> Shipment(string number)
    {
        var d = JsonDocument.Parse(await (await Get($"/api/v1/orders/{number}")).Content.ReadAsStringAsync()).RootElement;
        return d.GetProperty("groups")[0].GetProperty("shipments")[0].Clone();
    }

    private async Task<FulfilmentMaintenance.Result> Maintain() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync(); }

    [Fact]
    public async Task A_carrier_timeline_and_eta_are_shown_newest_state_wins_and_the_timeline_is_bounded()
    {
        var (o, g) = await Shipped();
        var t = _k.F.Clock.GetUtcNow();
        var events = Enumerable.Range(0, 50).Select(i => new { status = "in_transit", detail = "Scan " + i, location = "Brooklyn, NY", at = t.AddMinutes(-60 + i) }).ToArray();
        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", carrier = "USPS", trackingNumber = "9400", status = "in_transit", statusDetail = "Departed facility", eta = t.AddDays(2), events }, at: t.AddMinutes(-10)));
        Assert.Equal("skipped", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", status = "pre_transit", statusDetail = "Label created" }, at: t.AddMinutes(-20)));   // older (both in the past: a future date would be clamped, M1-25)
        var s = await Shipment(o.Number);
        Assert.Equal(("in_transit", "Departed facility"), (s.GetProperty("trackingStatus").GetString(), s.GetProperty("trackingStatusDetail").GetString()));
        Assert.Equal(30, s.GetProperty("timeline").GetArrayLength());
        Assert.Equal(t.AddDays(2), s.GetProperty("eta").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_driver_position_is_shown_only_while_the_stop_is_next_rounded_cleared_by_the_next_event_and_never_logged()
    {
        var (o, g) = await Shipped();
        var t = _k.F.Clock.GetUtcNow();
        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", carrier = "Local", status = "in_transit", driverFirstName = "Sam",
            eta = new { windowStart = t.AddHours(1), windowEnd = t.AddHours(2), estimatedAt = t.AddMinutes(90) }, position = new { lat = 40.6931, lng = -73.9901 } }));   // not next: a position must not be kept
        var s = await Shipment(o.Number);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("position").ValueKind);
        Assert.Equal(("Sam", t.AddHours(1)), (s.GetProperty("driverFirstName").GetString(), s.GetProperty("etaWindowStart").GetDateTimeOffset()));

        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.69312, lng = -73.99014 } }));
        var live = (await Shipment(o.Number)).GetProperty("position");
        Assert.Equal((40.693, -73.99), (live.GetProperty("lat").GetDouble(), live.GetProperty("lng").GetDouble()));   // coarse: three decimals
        // the driver stops sharing: an event with no position clears it
        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery" }));
        Assert.Equal(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);
        // positions never reach the logs
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("40.69") || l.Contains("73.99"));
    }

    [Fact]
    public async Task A_stale_position_is_not_shown_and_the_job_deletes_it_after_24_hours_and_delivery_clears_it()
    {
        var (o, g) = await Shipped();
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        Assert.NotEqual(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);       // too old to draw
        Assert.Equal(0, (await Maintain()).PositionsCleared);
        Assert.NotNull(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));          // still stored: under 24 h
        _k.F.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(1, (await Maintain()).PositionsCleared);
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));
        // delivery clears a fresh one at once
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", method = "local", deliveredAt = DateTimeOffset.UtcNow });
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));
    }

    [Theory]
    [InlineData("order.delivered")]
    [InlineData("shipment.voided")]
    public async Task A_seller_order_that_is_over_or_a_voided_parcel_keeps_no_driver_position(string ending)
    {
        var (o, g) = await Shipped();
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        Assert.NotNull(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));
        Assert.Equal("applied", await Evt(ending, o, g, new { shipmentId = "sh-1", reason = "x", deliveredAt = DateTimeOffset.UtcNow }));
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));       // erased, not only hidden
        Assert.Equal(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);
    }

    [Fact]
    public async Task A_late_position_after_the_seller_order_ended_is_not_stored_and_a_stored_one_is_drawn_only_while_out_for_delivery()
    {
        var (o, g) = await Shipped();
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        // a stored position next to another tracking status is never drawn (belt and braces over the clearing)
        await _k.Db(async db => { await db.OrderShipments.ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackingStatus, "in_transit")); return 0; });
        Assert.Equal(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);
        var t = _k.F.Clock.GetUtcNow();
        Assert.Equal("applied", await Evt("order.delivered", o, g, new { deliveredAt = t }, at: t.AddMinutes(1)));
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));
        // even a position that somehow sits next to a finished seller order is not drawn
        var now = _k.F.Clock.GetUtcNow();
        await _k.Db(async db => { await db.OrderShipments.ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackingStatus, "out_for_delivery").SetProperty(x => x.PositionLat, 40.7).SetProperty(x => x.PositionLng, -73.9).SetProperty(x => x.PositionAt, now)); return 0; });
        Assert.Equal(JsonValueKind.Null, (await Shipment(o.Number)).GetProperty("position").ValueKind);
        await _k.Db(async db => { await db.OrderShipments.ExecuteUpdateAsync(s => s.SetProperty(x => x.PositionLat, (double?)null).SetProperty(x => x.PositionLng, (double?)null).SetProperty(x => x.PositionAt, (DateTimeOffset?)null)); return 0; });
        Assert.Equal("applied", await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.7, lng = -73.9 } }, at: t.AddMinutes(2)));
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(s => s.PositionLat).SingleAsync()));
    }

    // ------------------------------------------------------------------ proofs

    private object Proof(params string[] photos) => new { shipmentId = "sh-1", method = "local", deliveredAt = DateTimeOffset.UtcNow, placement = "front_door", recipientName = "Jane Brown",
        proof = new { photoUrls = photos, signatureUrl = "https://portal.test/api/public/delivery-proof/sig?exp=1&sig=b", expiresInMinutes = 60 } };

    [Fact]
    public async Task A_proof_image_is_fetched_with_its_short_lived_link_served_safely_and_never_after_it_expires()
    {
        var (o, g) = await Shipped();
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("text/html") } } };   // the portal's header is not trusted
        await Evt("shipment.delivered", o, g, Proof("https://portal.test/api/public/delivery-proof/p1?exp=1&sig=a", "/api/public/delivery-proof/p2?exp=1&sig=a"));
        var s = await Shipment(o.Number);
        Assert.Equal(3, s.GetProperty("proofCount").GetInt32());
        Assert.DoesNotContain("sig=", JsonSerializer.Serialize(s));                                          // the links never reach the browser
        Assert.DoesNotContain("recipient", JsonSerializer.Serialize(s), StringComparison.OrdinalIgnoreCase);   // nor the recipient's name
        var res = await Get($"/api/v1/orders/{o.Number}/shipments/{s.GetProperty("id").GetGuid()}/proof/0");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Png, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);                               // sniffed, not "text/html"
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var call = _k.F.PublicCalls.Single();
        Assert.Equal(("portal.test", "/api/public/delivery-proof/p1?exp=1&sig=a"), (call.Host, call.PathAndQuery));
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/orders/{o.Number}/shipments/{s.GetProperty("id").GetGuid()}/proof/9")).StatusCode);
        // after the link's life it is neither offered nor fetched, and the job drops it
        _k.F.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/orders/{o.Number}/shipments/{s.GetProperty("id").GetGuid()}/proof/0")).StatusCode);
        Assert.Equal(0, (await Shipment(o.Number)).GetProperty("proofCount").GetInt32());
        Assert.Equal(1, (await Maintain()).ProofsCleared);
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(x => x.ProofJson).SingleAsync()));
    }

    [Fact]
    public async Task A_portal_relative_proof_link_is_fetched_with_exactly_its_path_and_query()
    {
        var (o, g) = await Shipped();
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        // the portal sends links relative to its own origin (M5 review P1); the absolute one is only the signature
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "/api/public/delivery-proof/rel1?exp=9&sig=a%2Bb" }, expiresInMinutes = 60 } });
        var s = await Shipment(o.Number);
        Assert.Equal(1, s.GetProperty("proofCount").GetInt32());
        var res = await Get($"/api/v1/orders/{o.Number}/shipments/{s.GetProperty("id").GetGuid()}/proof/0");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var call = _k.F.PublicCalls.Single();
        Assert.Equal(("portal.test", "/api/public/delivery-proof/rel1?exp=9&sig=a%2Bb"), (call.Host, call.PathAndQuery));
    }

    [Theory]
    [InlineData("file:///api/public/delivery-proof/x")]
    [InlineData("ftp://portal.test/api/public/delivery-proof/x")]
    [InlineData("//evil.example/api/public/delivery-proof/x")]
    [InlineData("api/public/delivery-proof/x")]
    public async Task Links_that_are_neither_portal_relative_nor_http_are_dropped(string link)
    {
        var (o, g) = await Shipped();
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { link }, expiresInMinutes = 60 } });
        Assert.Equal(0, (await Shipment(o.Number)).GetProperty("proofCount").GetInt32());
    }

    [Fact]
    public async Task Only_links_under_the_portals_proof_path_are_kept_the_host_in_a_link_is_never_contacted_and_other_buyers_get_404()
    {
        var (o, g) = await Shipped();
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "https://evil.example/api/public/delivery-proof/x?sig=1", "https://evil.example/steal", "/api/public/delivery-proof/../admin", "javascript:alert(1)" }, expiresInMinutes = 30 } });
        var s = await Shipment(o.Number);
        Assert.Equal(1, s.GetProperty("proofCount").GetInt32());                                             // only the first one has the proof path
        var path = $"/api/v1/orders/{o.Number}/shipments/{s.GetProperty("id").GetGuid()}/proof/0";
        Assert.Equal(HttpStatusCode.OK, (await Get(path)).StatusCode);
        Assert.Equal("portal.test", _k.F.PublicCalls.Single().Host);                                        // evil.example was never contacted
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Get(path, other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_connection_dropped_while_fetching_a_proof_is_a_502_not_a_500()
    {
        var (o, g) = await Shipped();
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "/api/public/delivery-proof/p1?x=1" } } });
        var id = (await Shipment(o.Number)).GetProperty("id").GetGuid();
        _k.F.PublicResponder = _ => throw new System.IO.IOException("connection reset");
        Assert.Equal(HttpStatusCode.BadGateway, (await Get($"/api/v1/orders/{o.Number}/shipments/{id}/proof/0")).StatusCode);
    }

    [Fact]
    public async Task A_voided_parcel_or_an_order_that_is_not_placed_gives_no_proof()
    {
        var (o, g) = await Shipped();
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "/api/public/delivery-proof/p1?x=1" } } });
        var id = (await Shipment(o.Number)).GetProperty("id").GetGuid();
        var path = $"/api/v1/orders/{o.Number}/shipments/{id}/proof/0";
        Assert.Equal(HttpStatusCode.OK, (await Get(path)).StatusCode);
        await _k.Db(async db => { await db.Orders.Where(x => x.Number == o.Number).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, OrderStatus.Expired)); return 0; });
        Assert.Equal(HttpStatusCode.NotFound, (await Get(path)).StatusCode);
        await _k.Db(async db => { await db.Orders.Where(x => x.Number == o.Number).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, OrderStatus.Placed)); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await Get(path)).StatusCode);
        await _k.Db(async db => { await db.OrderShipments.ExecuteUpdateAsync(u => u.SetProperty(x => x.VoidedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)); return 0; });
        Assert.Equal(HttpStatusCode.NotFound, (await Get(path)).StatusCode);
    }

    [Fact]
    public async Task Position_retention_runs_on_its_own()
    {
        var (o, g) = await Shipped();
        await Evt("order.tracking_updated", o, g, new { shipmentId = "sh-1", method = "local", status = "out_for_delivery", position = new { lat = 40.693, lng = -73.99 } });
        _k.F.Clock.Advance(TimeSpan.FromHours(25));
        using var s = _k.F.Services.CreateScope();
        Assert.Equal(1, await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().ClearPositionsAsync());
        Assert.Null(await _k.Db(db => db.OrderShipments.Select(x => x.PositionLat).SingleAsync()));
    }

    [Fact]
    public async Task A_proof_that_is_not_an_image_or_too_big_is_refused()
    {
        var (o, g) = await Shipped();
        await Evt("shipment.delivered", o, g, new { shipmentId = "sh-1", proof = new { photoUrls = new[] { "/api/public/delivery-proof/p1?x=1" } } });
        var id = (await Shipment(o.Number)).GetProperty("id").GetGuid();
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><script>alert(1)</script></html>", System.Text.Encoding.UTF8, "image/png") };
        Assert.Equal(HttpStatusCode.BadGateway, (await Get($"/api/v1/orders/{o.Number}/shipments/{id}/proof/0")).StatusCode);   // the header says png, the bytes say html
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png.Concat(new byte[ProofFetcher.MaxBytes]).ToArray()) };
        Assert.Equal(HttpStatusCode.BadGateway, (await Get($"/api/v1/orders/{o.Number}/shipments/{id}/proof/0")).StatusCode);
        _k.F.PublicResponder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.BadGateway, (await Get($"/api/v1/orders/{o.Number}/shipments/{id}/proof/0")).StatusCode);
    }
}
