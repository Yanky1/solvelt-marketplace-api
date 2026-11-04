using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogSyncTests
{
    private static CatalogSyncState State(TestFactory f) => WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single());

    [Fact]
    public async Task A_first_sync_loads_everything_and_a_second_changes_nothing()
    {
        using var f = new TestFactory();
        var r = await Sync(f);
        Assert.True(r.Ran);
        Assert.Equal((43, 0, null), (r.Applied, r.Skipped, r.Error));
        Assert.Equal((43, 13, 10, 6), WithDb(f, db => (db.CatalogListings.Count(), db.CatalogCategories.Count(), db.CatalogBrands.Count(), db.CatalogSellers.Count())));
        Assert.Equal(42, WithDb(f, db => db.CatalogListings.Count(l => l.Visible)));   // only the archived one is flagged hidden; the suspended seller is joined at query time
        var last = Fixture(f).All()[^1];
        Assert.Equal($"{last.Version}:{last.Id}", State(f).Cursor);
        var again = await Sync(f);
        Assert.Equal((0, 0), (again.Applied, again.Skipped));
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count()));
        Assert.NotNull(State(f).LastSuccessAt);
        Assert.Null(State(f).LeaseUntil);
    }

    [Fact]
    public async Task Slugs_come_from_the_title_stay_when_the_title_changes_and_never_collide()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("challah-classic");
        Assert.Equal("classic-braided-challah", Row(f, id).Slug);
        Fixture(f).Upsert(id, l => l with { Title = "Classic Braided Challah (Large Batch)" });
        // a brand-new listing whose title collides with an existing slug gets a suffix from its id
        var clash = Fixture(f).Add(Copy(Fixture(f).All().First(l => l.Id == ListingId("babka-chocolate")), Guid.Parse("00000000-0000-0000-0000-00000000abcd")));
        await Sync(f);
        Assert.Equal("classic-braided-challah", Row(f, id).Slug);                                  // stable for SEO
        Assert.Equal("Classic Braided Challah (Large Batch)", Row(f, id).Title);
        var clashed = Row(f, clash.Id);
        Assert.Equal("chocolate-babka-000000", clashed.Slug);
        Assert.Equal(WithDb(f, db => db.CatalogListings.Count()), WithDb(f, db => db.CatalogListings.Select(l => l.Slug).Distinct().Count()));
    }

    [Fact]
    public async Task A_changed_listing_replaces_its_variants_attributes_and_faqs_and_recomputes_prices()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("challah-classic");
        var before = Row(f, id);
        Assert.Equal((650, 950), (before.PriceFromCents, before.PriceToCents));
        var small = Fixture(f).All().First(l => l.Id == id).Variants[0];
        var updated = Fixture(f).Upsert(id, l => l with
        {
            Variants = [small with { PriceCents = 700, CompareAtCents = null }, new VariantDto(Guid.NewGuid(), "Jumbo", 1500, 1800, true)],   // "Large" is gone, "Jumbo" is new
            Attributes = [new AttributeDto("kosher-certification", "Star-K")],
            Faqs = [],
        });
        var r = await Sync(f);
        Assert.Equal((1, 0, null), (r.Applied, r.Skipped, r.Error));
        var after = Row(f, id);
        Assert.Equal(updated.Version, after.Version);
        Assert.Equal(["Small", "Jumbo"], after.Variants.OrderBy(v => v.SortOrder).Select(v => v.Name));
        Assert.Equal((700, 1500, 1800L), (after.PriceFromCents, after.PriceToCents, after.CompareAtFromCents));
        Assert.Equal([("kosher-certification", "Star-K")], after.Attributes.Select(a => (a.Key, a.Value)));
        Assert.Empty(after.Faqs);
        Assert.Equal(1, WithDb(f, db => db.CatalogVariants.Count(v => v.ListingId == id && v.Name == "Small")));   // updated in place, not duplicated
    }

    [Fact]
    public async Task Price_range_ignores_sold_out_variants_unless_everything_is_sold_out()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("challah-classic");
        var vs = Fixture(f).All().First(l => l.Id == id).Variants;
        Fixture(f).Upsert(id, l => l with { Variants = [vs[0] with { Available = false, PriceCents = 100 }, vs[1] with { PriceCents = 900 }] });
        await Sync(f);
        Assert.Equal((900, 900, true), (Row(f, id).PriceFromCents, Row(f, id).PriceToCents, Row(f, id).Available));
        Fixture(f).Upsert(id, l => l with { Variants = [vs[0] with { Available = false, PriceCents = 100 }, vs[1] with { Available = false, PriceCents = 900 }] });
        await Sync(f);
        Assert.Equal((100, 900, false), (Row(f, id).PriceFromCents, Row(f, id).PriceToCents, Row(f, id).Available));
    }

    [Fact]
    public async Task A_stale_or_duplicate_snapshot_is_ignored_and_the_order_of_arrival_does_not_matter()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("babka-chocolate");
        var current = Fixture(f).All().First(l => l.Id == id);
        var v = current.Version;
        async Task<bool> Apply(ListingSnapshot s)
        {
            using var scope = f.Services.CreateScope();
            var applied = await scope.ServiceProvider.GetRequiredService<CatalogSyncService>().ApplySnapshotAsync(s, default);
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
            return applied;
        }
        Assert.False(await Apply(current));                                                        // duplicate
        Assert.False(await Apply(current with { Version = v - 1, Title = "Older title" }));        // stale
        Assert.Equal("Chocolate Babka", Row(f, id).Title);
        // newer first, then older: the newer one wins both ways round
        Assert.True(await Apply(current with { Version = v + 5, Title = "Five" }));
        Assert.False(await Apply(current with { Version = v + 3, Title = "Three" }));
        Assert.Equal(("Five", v + 5), (Row(f, id).Title, Row(f, id).Version));
    }

    [Fact]
    public async Task A_failed_run_keeps_the_committed_pages_and_the_next_run_continues_from_the_cursor()
    {
        using var f = new TestFactory();
        var probe = new ProbeSource(Fixture(f)) { FailListingCall = n => n == 3 };
        CatalogSyncResult first;
        using (var scope = f.Services.CreateScope()) first = await Service(scope, probe, f.Clock, pageSize: 10).RunAsync();
        Assert.Equal(("InvalidOperationException", 20), (first.Error, first.Applied));             // two pages of ten committed, the third failed
        Assert.Equal(20, WithDb(f, db => db.CatalogListings.Count()));
        Assert.Equal("InvalidOperationException", State(f).LastError);
        Assert.Null(State(f).LastSuccessAt);
        Assert.Null(State(f).LeaseUntil);                                                          // released even on failure
        var cursorAfterFailure = State(f).Cursor;
        Assert.Equal($"{Fixture(f).All()[19].Version}:{Fixture(f).All()[19].Id}", cursorAfterFailure);
        CatalogSyncResult second;
        using (var scope = f.Services.CreateScope()) second = await Service(scope, new ProbeSource(Fixture(f)), f.Clock, pageSize: 10).RunAsync();
        Assert.Equal((23, null), (second.Applied, second.Error));
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count()));
        Assert.Null(State(f).LastError);
        Assert.NotNull(State(f).LastSuccessAt);
    }

    [Fact]
    public async Task A_failure_in_reference_data_is_recorded_but_does_not_stop_the_listings()      // replaces A_failure_in_reference_data_is_recorded_and_changes_no_listing (M2-11)
    {
        using var f = new TestFactory();
        var probe = new ProbeSource(Fixture(f)) { FailReferenceData = true };
        using var scope = f.Services.CreateScope();
        var r = await Service(scope, probe, f.Clock).RunAsync();
        Assert.Equal("InvalidOperationException", r.Error);                                          // recorded, with the exception type only
        Assert.DoesNotContain("simulated", State(f).LastError);                                       // the portal's message is never stored
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count()));                                // the poll went on: every listing is known ...
        Assert.Equal(0, WithDb(f, db => db.CatalogListings.Count(l => l.Visible)));                   // ... but none shows without its category: held, hidden
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count(l => l.IsStub && l.KnownVersion > l.Version)));
        Assert.Null(State(f).LeaseUntil);
        var last = Fixture(f).All()[^1];
        Assert.Equal($"{last.Version}:{last.Id}", State(f).Cursor);                                   // and the cursor moved on
    }

    [Fact]
    public async Task Listings_held_for_a_missing_category_appear_once_the_reference_data_works_again()
    {
        using var f = new TestFactory();
        var probe = new ProbeSource(Fixture(f)) { FailReferenceData = true };
        using (var scope = f.Services.CreateScope()) await Service(scope, probe, f.Clock).RunAsync();
        probe.FailReferenceData = false;
        using (var scope = f.Services.CreateScope()) await Service(scope, probe, f.Clock).RunAsync();
        Assert.Equal(0, WithDb(f, db => db.CatalogListings.Count(l => l.Visible)));                   // all of them are backing off for a minute
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var scope = f.Services.CreateScope()) { var r = await Service(scope, probe, f.Clock).RunAsync(); Assert.Null(r.Error); Assert.Equal(43, r.Fetched); }
        Assert.Equal(42, WithDb(f, db => db.CatalogListings.Count(l => l.Visible)));
        Assert.Equal(0, WithDb(f, db => db.CatalogListings.Count(l => l.IsStub)));
    }

    [Fact]
    public async Task Only_one_instance_runs_at_a_time_and_an_expired_lease_is_taken_over()
    {
        using var f = new TestFactory();
        var gate = new TaskCompletionSource();
        var slow = new ProbeSource(Fixture(f)) { Gate = gate };
        using var scopeA = f.Services.CreateScope();
        var running = Service(scopeA, slow, f.Clock).RunAsync();
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(5), State(f).LeaseUntil);                        // a running instance holds the lease for 5 minutes
        var blocked = await Sync(f);                                                                // a second instance while the first holds the lease
        Assert.False(blocked.Ran);
        gate.SetResult();
        Assert.True((await running).Ran);
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count()));

        // a crashed instance leaves its lease behind: it blocks until it expires (5 minutes)
        WithDb(f, db => db.CatalogSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.LeaseUntil, (DateTimeOffset?)f.Clock.GetUtcNow().AddMinutes(4))));
        Assert.False((await Sync(f)).Ran);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await Sync(f)).Ran);
    }

    [Fact]
    public async Task An_invalid_snapshot_is_skipped_and_does_not_block_the_cursor()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var template = fx.All()[0];
        var noVariants = fx.Add(Copy(template, Guid.NewGuid()) with { Variants = [] });
        var badType = fx.Add(Copy(template, Guid.NewGuid()) with { Type = "gadget" });
        var negative = fx.Add(Copy(template, Guid.NewGuid()) with { Variants = [new VariantDto(Guid.NewGuid(), "x", -5, null, true)] });
        var noTitle = fx.Add(Copy(template, Guid.NewGuid(), " "));
        var noCategory = fx.Add(Copy(template, Guid.NewGuid()) with { CategoryId = Guid.NewGuid() });
        var good = fx.Add(Copy(template, Guid.NewGuid(), "A Good One"));
        var r = await Sync(f);
        Assert.Null(r.Error);
        Assert.Equal((44, 5), (r.Applied, r.Skipped));
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("as a whole"));                        // rejected up front: the page was applied in one go, not record by record
        Assert.Equal(45, WithDb(f, db => db.CatalogListings.Count()));                             // 44 + the one held for its category (below)
        foreach (var bad in new[] { noVariants, badType, negative, noTitle })
            Assert.DoesNotContain(WithDb(f, db => db.CatalogListings.Select(l => l.Id).ToList()), id => id == bad.Id);
        var held = Row(f, noCategory.Id);                                                           // an unknown category is not a bad record: it is held, hidden, until the category exists (M2-11)
        Assert.True(held.IsStub);
        Assert.False(held.Visible);
        Assert.Equal(noCategory.Version, held.KnownVersion);
        Assert.Contains(WithDb(f, db => db.CatalogListings.Select(l => l.Id).ToList()), id => id == good.Id);
        Assert.Equal($"{good.Version}:{good.Id}", State(f).Cursor);                               // the cursor moved past the bad records
    }

    [Fact]
    public async Task A_seller_flag_change_on_one_snapshot_applies_to_the_sellers_other_listings()
    {
        using var f = new TestFactory();
        await Sync(f);
        var anyOfMazel = Fixture(f).All().First(l => l.Seller.DisplayName == "Mazel Bakery");
        Fixture(f).Upsert(anyOfMazel.Id, l => l with { Seller = l.Seller with { Active = false } });
        await Sync(f);
        Assert.False(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == anyOfMazel.Seller.Id).Active));
        Assert.Equal(8, WithDb(f, db => db.CatalogListings.Count(l => l.SellerId == anyOfMazel.Seller.Id)));
    }

    [Fact]
    public async Task A_listing_the_portal_no_longer_has_is_hidden_and_not_asked_for_again()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("babka-chocolate");
        var v = Row(f, id).Version;
        Fixture(f).Remove(id);
        var c = f.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Event(f, c, "listing.upserted", id, v + 1)).Status);   // the portal says there is a newer version ...
        var probe = new ProbeSource(Fixture(f));
        using (var scope = f.Services.CreateScope()) await Service(scope, probe, f.Clock).RunAsync();           // ... but fetching it finds nothing
        Assert.Equal(1, probe.GetListingCalls);
        var row = Row(f, id);
        Assert.Equal(("Removed", false, v + 1), (row.Status, row.Visible, row.Version));
        using (var scope = f.Services.CreateScope()) await Service(scope, probe, f.Clock).RunAsync();
        Assert.Equal(1, probe.GetListingCalls);                                                                  // settled: no repeated fetches
    }

    [Fact]
    public async Task One_record_that_breaks_a_database_rule_is_skipped_and_cannot_stall_the_cursor()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var template = fx.All()[0];
        // a portal bug: a second listing that reuses the first listing's variant id (variant ids are primary keys)
        var first = fx.Add(Copy(template, Guid.NewGuid(), "Shares a Variant Id"));
        var offender = fx.Add(Copy(template, Guid.NewGuid(), "Also Shares It") with { Variants = first.Variants });
        var after = fx.Add(Copy(template, Guid.NewGuid(), "Comes After"));
        var r = await Sync(f);
        Assert.Null(r.Error);
        Assert.Equal((45, 1), (r.Applied, r.Skipped));                                              // 43 + first + after; only the offender skipped
        var ids = WithDb(f, db => db.CatalogListings.Select(l => l.Id).ToList());
        Assert.Contains(first.Id, ids);
        Assert.Contains(after.Id, ids);
        Assert.DoesNotContain(offender.Id, ids);
        Assert.Equal($"{after.Version}:{after.Id}", State(f).Cursor);                               // the cursor moved past it
        var again = await Sync(f);
        Assert.Equal((0, 0, null), (again.Applied, again.Skipped, again.Error));
    }

    [Fact]
    public async Task A_compare_at_price_counts_only_when_it_is_above_the_price()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = ListingId("challah-classic");
        var vs = Fixture(f).All().First(l => l.Id == id).Variants;
        async Task<long?> CompareAtAfter(long? small, long? large)
        {
            Fixture(f).Upsert(id, l => l with { Variants = [vs[0] with { PriceCents = 650, CompareAtCents = small }, vs[1] with { PriceCents = 950, CompareAtCents = large }] });
            await Sync(f);
            return Row(f, id).CompareAtFromCents;
        }
        Assert.Equal(750L, await CompareAtAfter(750, null));        // above the price: shown
        Assert.Null(await CompareAtAfter(650, 950));                // equal to the price: not a sale
        Assert.Null(await CompareAtAfter(500, 800));                // below the price: not a sale
        Assert.Equal(1000L, await CompareAtAfter(600, 1000));       // only the variant that is really on sale counts
        Assert.Null(await CompareAtAfter(null, null));
    }

    [Theory]
    [InlineData(false, "Active", false, true)]
    [InlineData(false, "Archived", false, false)]
    [InlineData(false, "Active", true, false)]    // unlisted at or after this version
    [InlineData(true, "Active", false, false)]    // a stub is never visible, whatever its status says
    public void Visibility_is_active_and_not_unlisted_and_not_a_stub(bool stub, string status, bool unlisted, bool visible)
    {
        var l = new CatalogListing { Version = 5, Status = status, IsStub = stub, UnlistedVersion = unlisted ? 5 : null };
        l.RecomputeVisibility();
        Assert.Equal(visible, l.Visible);
    }
}

