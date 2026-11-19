using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Offers;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The offer feed projection (decision Q-02, portal C18.2/C18.3): documents, versions, tombstones and events, the same rules as listings.</summary>
public class OfferSyncTests
{
    private static readonly Guid Seller = Guid.Parse("5d2a0000-0000-0000-0000-000000000001"), Brand = Guid.Parse("a71e0000-0000-0000-0000-000000000001"), P1 = Guid.Parse("0a5d0000-0000-0000-0000-000000000001"), Gift = Guid.Parse("c3f00000-0000-0000-0000-000000000001");

    public static OfferSnapshot Doc(Guid? id = null, int price = 90, int min = 20, string scope = "products", bool demo = false, DateTimeOffset? from = null, DateTimeOffset? to = null) => new(
        id ?? Guid.NewGuid(), 1, 0, Seller, Brand, "Blitz cards bundle", "USD", scope, scope == "products" ? [P1] : [], scope == "category" ? [Guid.NewGuid()] : [],
        [new OfferTier(min, price)], [new OfferGiftDoc(10, Gift, 1, "Card wallet", true)], from, to, demo, DateTimeOffset.UnixEpoch);

    private static async Task<OfferSyncResult> Sync(TestFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OfferSyncService>().RunAsync();
    }

    private static CatalogOffer Row(TestFactory f, Guid id) => WithDb(f, db => db.CatalogOffers.AsNoTracking().Single(o => o.Id == id));
    private static OfferSyncService Service(IServiceScope s, IOfferSource source) => new(s.ServiceProvider.GetRequiredService<MarketplaceDb>(), source, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
        Microsoft.Extensions.Options.Options.Create(new Solvelt.Marketplace.Api.Infrastructure.CatalogOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<OfferSyncService>.Instance);

    private static async Task<(HttpStatusCode Status, string Json)> Event(TestFactory f, HttpClient c, string type, Guid offerId, long? version)
        => await Post(c, Request(f, Body($"evt-{type}-{offerId:N}-{version}-{Guid.NewGuid():N}"[..60], type, DateTimeOffset.UtcNow, new { offerId, version, sellerId = Seller, brandId = Brand })));

    // ---------- the document ----------

    [Fact]
    public void A_valid_document_is_read_and_a_malformed_one_is_refused_without_throwing()
    {
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        const string ok = """{"offerId":"6f1c0e52-3b4a-4f0e-9a51-0c8d2b7e9d10","version":2,"feedVersion":1902,"listed":true,"status":"active","sellerId":"5d2a0000-0000-0000-0000-000000000001","brandId":"a71e0000-0000-0000-0000-000000000001","name":"Blitz","currency":"USD","scope":{"kind":"products","categoryIds":[],"productIds":["0a5d0000-0000-0000-0000-000000000001"]},"tiers":[{"minQuantity":50,"unitPriceCents":80},{"minQuantity":20,"unitPriceCents":90}],"gifts":[{"minQuantity":10,"productId":"c3f00000-0000-0000-0000-000000000001","quantity":1,"title":"Card wallet","available":true}],"activeNow":true,"demo":false,"updatedAt":"2026-11-18T21:30:00Z"}""";
        var d = HttpOfferSource.Parse(J(ok))!;
        Assert.Equal((2L, "products", 2, 1), (d.Version, d.Scope, d.Tiers.Count, d.Gifts.Count));
        Assert.Equal([20, 50], d.Tiers.Select(t => t.MinQuantity).ToArray());                                            // sorted
        foreach (var bad in new[]
        {
            ok.Replace("\"version\":2", "\"version\":0"), ok.Replace("\"unitPriceCents\":80", "\"unitPriceCents\":-1"), ok.Replace("\"minQuantity\":50", "\"minQuantity\":20"),   // negative price, duplicate step
            ok.Replace("\"kind\":\"products\"", "\"kind\":\"weird\""), ok.Replace("\"productIds\":[\"0a5d0000-0000-0000-0000-000000000001\"]", "\"productIds\":[]"),         // unknown scope, products scope with no products
            ok.Replace("\"status\":\"active\"", "\"status\":\"draft\""), ok.Replace("\"sellerId\":\"5d2a0000-0000-0000-0000-000000000001\"", "\"sellerId\":\"nope\""),
        })
            Assert.Null(HttpOfferSource.Parse(J(bad)));
        Assert.Null(HttpOfferSource.Parse(J("{}")));
    }

    // ---------- versions and tombstones ----------

    [Fact]
    public async Task A_new_offer_is_projected_a_newer_version_replaces_it_and_an_older_one_changes_nothing()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var id = Guid.NewGuid();
        src.Upsert(Doc(id, price: 90));
        Assert.Equal(1, (await Sync(f)).Applied);
        var v1 = Row(f, id);
        Assert.Equal((1L, true, "products"), (v1.Version, v1.Visible, v1.Scope));
        Assert.Contains("\"UnitPriceCents\":90", v1.TiersJson);
        src.Upsert(Doc(id, price: 85));
        await Sync(f);
        var v2 = Row(f, id);
        Assert.Equal(2L, v2.Version); Assert.Contains("\"UnitPriceCents\":85", v2.TiersJson);
        // a replayed older document (a stale page) must not roll it back
        using var scope = f.Services.CreateScope();
        var held = WithDb(f, db => db.CatalogOffers.Count());
        var older = Doc(id, price: 95) with { Version = 1 };
        var svc = Service(scope, new OneShotSource(older));
        await svc.RunAsync();
        Assert.Equal(2L, Row(f, id).Version); Assert.Contains("\"UnitPriceCents\":85", Row(f, id).TiersJson); Assert.Equal(held, WithDb(f, db => db.CatalogOffers.Count()));
    }

    [Fact]
    public async Task Two_offers_first_seen_in_one_sync_keep_the_feed_order_so_the_tie_break_never_falls_to_the_id()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var first = Guid.Parse("ffffffff-0000-0000-0000-000000000001"); var second = Guid.Parse("00000000-0000-0000-0000-000000000002");   // the id order is the OPPOSITE of the feed order
        src.Upsert(Doc(first, scope: "all")); src.Upsert(Doc(second, scope: "all"));
        Assert.Equal(2, (await Sync(f)).Applied);
        Assert.True(Row(f, first).FirstSeenAt < Row(f, second).FirstSeenAt);
        using var scope = f.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<OfferLoader>().ActiveAsync([Seller], DateTimeOffset.UtcNow, default);
        var line = new OfferLineInput(Guid.NewGuid(), P1, Seller, Brand, [], 100, 20);
        var priced = OfferPricing.Price([line], loaded.Select(o => o.Def).ToList());
        Assert.Equal(first, Assert.Single(priced.Offers).OfferId);                       // the earlier one wins, as the portal's older-offer rule says
    }

    [Fact]
    public async Task The_portals_createdAt_is_kept_to_the_tick_and_never_changed_by_a_later_version()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var id = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 11, 1, 8, 30, 15, TimeSpan.Zero).AddTicks(1234567 % 10_000_000);
        src.Upsert(Doc(id) with { CreatedAt = created });
        await Sync(f);
        Assert.Equal(created.UtcTicks, Row(f, id).CreatedAtTicks);                          // 100 ns: PostgreSQL timestamps would cut it to microseconds, so it is stored as ticks
        src.Upsert(Doc(id, price: 85) with { CreatedAt = created.AddDays(3) });              // a portal never sends another one; a later value is simply the newest document
        await Sync(f);
        Assert.NotNull(Row(f, id).CreatedAtTicks);
        var old = Guid.NewGuid();
        src.Upsert(Doc(old));                                                                // an older portal sends none: null, and the first-seen time stands in
        await Sync(f);
        Assert.Null(Row(f, old).CreatedAtTicks);
    }

    [Fact]
    public async Task An_ended_offer_stops_applying_and_a_new_version_brings_it_back_a_deleted_one_never_returns()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var id = Guid.NewGuid(); var gone = Guid.NewGuid();
        src.Upsert(Doc(id)); src.Upsert(Doc(gone));
        await Sync(f);
        src.End(id, "ended");
        await Sync(f);
        Assert.False(Row(f, id).Visible);
        src.Upsert(Doc(id, price: 80));                                                                                  // re-activated as a new version
        await Sync(f);
        Assert.True(Row(f, id).Visible);
        src.End(gone, "deleted");
        await Sync(f);
        Assert.False(Row(f, gone).Visible);
        src.Upsert(Doc(gone));
        await Sync(f);
        Assert.False(Row(f, gone).Visible);                                                                              // gone for good
        Assert.Equal(long.MaxValue, Row(f, gone).UnlistedVersion);
    }

    [Fact]
    public async Task Demo_offers_are_never_visible_and_unknown_ids_in_a_tombstone_are_ignored()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var demo = Guid.NewGuid();
        src.Upsert(Doc(demo, demo: true)); src.End(Guid.NewGuid(), "ended");
        Assert.True((await Sync(f)).Ran);
        Assert.False(Row(f, demo).Visible);
        Assert.Equal(1, WithDb(f, db => db.CatalogOffers.Count()));
    }

    // ---------- events ----------

    [Fact]
    public async Task An_ended_event_hides_at_once_and_a_stale_page_of_that_version_cannot_show_it_again()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var id = Guid.NewGuid();
        src.Upsert(Doc(id));
        await Sync(f);
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Event(f, c, "offer.ended", id, 1)).Status);
        Assert.False(Row(f, id).Visible);
        // the feed still shows version 1 (a stale or lagging page): it must not re-list the offer
        using var scope = f.Services.CreateScope();
        await Service(scope, new OneShotSource(Doc(id) with { Version = 1 })).RunAsync();
        Assert.False(Row(f, id).Visible);
        // a newer version re-lists it
        await Service(scope, new OneShotSource(Doc(id) with { Version = 2 })).RunAsync();
        Assert.True(Row(f, id).Visible);
    }

    [Fact]
    public async Task An_updated_event_for_an_unknown_offer_makes_a_hidden_stub_and_the_sync_fetches_the_document()
    {
        using var f = new TestFactory();
        var src = f.Services.GetRequiredService<FixtureOfferSource>();
        var id = Guid.NewGuid();
        var doc = src.Upsert(Doc(id));
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Event(f, c, "offer.updated", id, doc.Version)).Status);
        var stub = Row(f, id);
        Assert.Equal((false, 0L, doc.Version), (stub.Visible, stub.Version, stub.KnownVersion));                         // known to exist, not yet read
        await Sync(f);
        var row = Row(f, id);
        Assert.Equal((true, doc.Version), (row.Visible, row.Version));
        Assert.Equal((HttpStatusCode.OK, true), ((await Event(f, c, "offer.updated", id, 1)).Status, true));            // a repeat is harmless
    }

    [Fact]
    public async Task A_malformed_offer_event_is_acknowledged_and_ignored()
    {
        using var f = new TestFactory();
        using var c = f.CreateClient();
        var res = await Post(c, Request(f, Body("evt-bad-" + Guid.NewGuid().ToString("N"), "offer.updated", DateTimeOffset.UtcNow, new { offerId = "nope" })));
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Equal(0, WithDb(f, db => db.CatalogOffers.Count()));
    }

    /// <summary>A source that answers one fixed page (a stale or lagging feed in a test).</summary>
    private sealed class OneShotSource(OfferSnapshot doc) : IOfferSource
    {
        public Task<OfferPage> GetOffersAsync(string? since, int limit, CancellationToken ct) => Task.FromResult(new OfferPage([doc], [], "1", false));
        public Task<OfferSnapshot?> GetOfferAsync(Guid id, CancellationToken ct) => Task.FromResult<OfferSnapshot?>(doc);
    }
}
