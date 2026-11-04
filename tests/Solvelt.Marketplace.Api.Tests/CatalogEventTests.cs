using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogEventTests
{
    private const string Slug = "chocolate-babka";
    private static Guid Babka => ListingId("babka-chocolate");

    private static async Task<bool> InList(HttpClient c, string slug = Slug)
        => Slugs(await Json(c, "/api/v1/catalog/listings?pageSize=100")).Contains(slug);

    [Fact]
    public async Task An_unlist_event_hides_the_listing_immediately_without_waiting_for_a_sync()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        Assert.True(await InList(c));
        await Json(c, $"/api/v1/catalog/listings/{Slug}");
        var v = Row(f, Babka).Version;
        var res = await Event(f, c, "listing.unlisted", Babka, v);
        Assert.Equal(("applied", HttpStatusCode.OK), (StatusOf(res.Json), res.Status));
        Assert.False(await InList(c));                                                             // gone from lists ...
        await Json(c, $"/api/v1/catalog/listings/{Slug}", HttpStatusCode.NotFound);               // ... and from the detail page
        await Json(c, $"/api/v1/catalog/listings/{Babka}", HttpStatusCode.NotFound);
        Assert.False(Row(f, Babka).Visible);
        Assert.Equal(40 - 1, (await Json(c, "/api/v1/catalog/listings")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_stale_poll_page_cannot_show_what_an_unlist_event_hid()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var v = Row(f, Babka).Version;
        await Event(f, c, "listing.unlisted", Babka, v);
        // the portal feed still carries the listing at that same version: a re-poll from the very start must not re-show it
        WithDb(f, db => db.CatalogSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.Cursor, (string?)null)));
        var r = await Sync(f);
        Assert.Equal(0, r.Applied);
        Assert.False(await InList(c));
        Assert.False(Row(f, Babka).Visible);
    }

    [Fact]
    public async Task A_newer_version_after_an_unlist_shows_the_listing_again_once_it_is_fetched()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var v = Row(f, Babka).Version;
        await Event(f, c, "listing.unlisted", Babka, v);
        var relisted = Fixture(f).Upsert(Babka, l => l with { Title = "Chocolate Babka, Back Again" });
        await Event(f, c, "listing.upserted", Babka, relisted.Version);
        // the event only records that a newer version exists: still hidden until the snapshot is fetched
        Assert.Equal((v, relisted.Version, false), (Row(f, Babka).Version, Row(f, Babka).KnownVersion, Row(f, Babka).Visible));
        Assert.False(await InList(c));
        var probe = new ProbeSource(Fixture(f));
        using (var scope = f.Services.CreateScope())
        {
            var r = await Service(scope, probe, f.Clock).RunAsync();
            Assert.Equal(0, r.Pending);
        }
        Assert.True(await InList(c, Slug));                                                        // same slug, visible again
        Assert.Equal("Chocolate Babka, Back Again", Row(f, Babka).Title);
    }

    [Fact]
    public async Task An_unlist_event_older_than_the_applied_snapshot_does_not_hide_it()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var v = Row(f, Babka).Version;
        await Event(f, c, "listing.unlisted", Babka, v - 1);                                       // delivered late: the listing was relisted since
        Assert.True(await InList(c));
        Assert.True(Row(f, Babka).Visible);
        Assert.Equal(v - 1, Row(f, Babka).UnlistedVersion);
    }

    [Fact]
    public async Task An_event_for_an_unknown_listing_creates_a_hidden_stub_that_an_older_snapshot_cannot_reveal()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        var babka = Fixture(f).All().First(l => l.Id == Babka);
        // the unlist arrives before we have ever seen the listing; the feed still carries it at its (older or equal) version
        await Event(f, c, "listing.unlisted", Babka, babka.Version);
        var stub = Row(f, Babka);
        Assert.Equal((true, false, 0L), (stub.IsStub, stub.Visible, stub.Version));
        Assert.StartsWith("~", stub.Slug);
        await Sync(f);
        Assert.False(await InList(c));                                                             // applied, but hidden by the unlist
        var row = Row(f, Babka);
        Assert.Equal((false, babka.Version, false), (row.IsStub, row.Version, row.Visible));
        Assert.Equal("chocolate-babka", row.Slug);                                                 // the placeholder slug was replaced
        // a genuinely newer version shows it again
        var newer = Fixture(f).Upsert(Babka, l => l);
        await Sync(f);
        Assert.True(await InList(c));
        Assert.Equal(newer.Version, Row(f, Babka).Version);
    }

    [Fact]
    public async Task Two_stubs_do_not_collide_on_the_unique_slug()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Event(f, c, "listing.upserted", Guid.NewGuid(), 5)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Event(f, c, "listing.upserted", Guid.NewGuid(), 6)).Status);
        Assert.Equal(2, WithDb(f, db => db.CatalogListings.Count(l => l.IsStub)));
    }

    [Fact]
    public async Task An_upsert_event_only_records_the_version_and_the_next_sync_fetches_just_that_listing()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var changed = Fixture(f).Upsert(ListingId("honey-cake"), l => l with { Summary = "Now with extra honey." });
        // the feed would also deliver it by cursor; mark it as already seen by the cursor so only the event can explain the fetch
        WithDb(f, db => db.CatalogSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.Cursor, $"{changed.Version}:{changed.Id}")));
        await Event(f, c, "listing.upserted", changed.Id, changed.Version);
        Assert.Equal(1, WithDb(f, db => db.CatalogListings.Count(l => l.KnownVersion > l.Version)));
        var probe = new ProbeSource(Fixture(f));
        CatalogSyncResult r;
        using (var scope = f.Services.CreateScope()) r = await Service(scope, probe, f.Clock).RunAsync();
        Assert.Equal((1, 1, 0), (r.Fetched, probe.GetListingCalls, r.Pending));
        Assert.Equal("Now with extra honey.", Row(f, changed.Id).Summary);
    }

    [Fact]
    public async Task The_event_handler_never_calls_the_catalog_source()
    {
        using var f = new TestFactory(services: s =>
        {
            s.AddSingleton<ForbiddenSource>();
            s.AddSingleton<ICatalogSource>(sp => sp.GetRequiredService<ForbiddenSource>());
        });
        var c = f.CreateClient();
        Assert.Equal(("applied", HttpStatusCode.OK), (StatusOf((await Event(f, c, "listing.upserted", Guid.NewGuid(), 3)).Json), HttpStatusCode.OK));
        Assert.Equal("applied", StatusOf((await Event(f, c, "listing.unlisted", Guid.NewGuid(), 3)).Json));
    }

    [Theory]
    [InlineData("""{"version":3}""")]
    [InlineData("""{"listingId":"not-a-guid","version":3}""")]
    [InlineData("""{"listingId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""")]
    [InlineData("""{"listingId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","version":0}""")]
    [InlineData("""{"listingId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","version":"3"}""")]
    [InlineData("""[]""")]
    public async Task A_malformed_listing_event_is_skipped_and_creates_nothing(string data)
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        var body = $$"""{"id":"evt-bad","type":"listing.unlisted","createdAt":"2026-11-04T12:00:00Z","data":{{data}}}""";
        var res = await Post(c, Request(f, body));
        Assert.Equal(("skipped", HttpStatusCode.OK), (StatusOf(res.Json), res.Status));
        Assert.Equal(0, WithDb(f, db => db.CatalogListings.Count()));
    }

    [Fact]
    public async Task A_replayed_event_changes_nothing()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var v = Row(f, Babka).Version;
        var first = await Event(f, c, "listing.unlisted", Babka, v, id: "evt-same-id");
        var second = await Event(f, c, "listing.unlisted", Babka, v, id: "evt-same-id");
        Assert.Equal(("applied", "duplicate"), (StatusOf(first.Json), StatusOf(second.Json)));
        Assert.Equal(1, WithDb(f, db => db.PortalInboundEvents.Count(e => e.Id == "evt-same-id")));
        Assert.Equal(v, Row(f, Babka).UnlistedVersion);
    }

    [Fact]
    public async Task Parallel_events_for_one_listing_end_with_the_highest_version_and_never_regress_it()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var v = Row(f, Babka).Version;
        async Task<HttpStatusCode> Deliver(int i)
        {
            for (var attempt = 0; ; attempt++)
            {
                var r = await Event(f, c, "listing.unlisted", Babka, v + i, id: $"evt-par-{i}");
                if ((int)r.Status < 500 || attempt >= 15) return r.Status;                         // the portal retries 5xx
                await Task.Delay(10);
            }
        }
        var codes = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => Task.Run(() => Deliver(i))));
        Assert.All(codes, s => Assert.Equal(HttpStatusCode.OK, s));
        var row = Row(f, Babka);
        Assert.Equal((v + 10, v + 10, false), (row.UnlistedVersion, row.KnownVersion, row.Visible));
    }

    [Fact]
    public async Task A_sync_running_while_events_arrive_keeps_both_changes()
    {
        using var f = new TestFactory();
        await Sync(f);
        var c = f.CreateClient();
        var other = ListingId("honey-cake");
        var v = Row(f, Babka).Version;
        var changed = Fixture(f).Upsert(Babka, l => l with { Summary = "Updated while events fly." });
        var sync = Task.Run(() => Sync(f));
        var events = Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 15; attempt++)
            {
                var r = await Event(f, c, "listing.upserted", other, 1000 + i);
                if ((int)r.Status < 500) return;
                await Task.Delay(10);
            }
        })));
        await Task.WhenAll(sync, events);
        await Sync(f);   // a conflict makes one run fail and the next catches up
        Assert.Equal("Updated while events fly.", Row(f, Babka).Summary);
        Assert.Equal(changed.Version, Row(f, Babka).Version);
        Assert.Equal(1005, Row(f, other).KnownVersion);
    }
}
