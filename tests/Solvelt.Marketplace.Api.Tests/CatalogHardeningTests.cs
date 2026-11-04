using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class SyncErrorClassificationTests
{
    private static PostgresException Pg(string state) => new("m", "ERROR", "ERROR", state);

    [Theory]
    [InlineData("23505", true)]     // unique violation
    [InlineData("23503", true)]     // foreign key
    [InlineData("23502", true)]     // not null
    [InlineData("22001", true)]     // value too long
    [InlineData("22P02", true)]     // invalid text representation
    [InlineData("57014", false)]    // query cancelled (statement timeout)
    [InlineData("40001", false)]    // serialisation failure
    [InlineData("40P01", false)]    // deadlock
    [InlineData("08006", false)]    // connection failure
    [InlineData("53300", false)]    // too many connections
    [InlineData("42P01", false)]    // undefined table: a deployment problem, not a bad record
    public void A_PostgreSQL_error_is_deterministic_only_for_data_and_integrity_classes(string state, bool deterministic)
        => Assert.Equal(deterministic, SyncErrors.IsDeterministic(new DbUpdateException("x", Pg(state))));

    [Theory]
    [InlineData(19, true)]          // constraint
    [InlineData(5, false)]          // busy
    [InlineData(6, false)]          // locked
    [InlineData(10, false)]         // I/O error
    [InlineData(13, false)]         // disk full
    public void A_SQLite_error_is_deterministic_only_for_constraints(int code, bool deterministic)
        => Assert.Equal(deterministic, SyncErrors.IsDeterministic(new DbUpdateException("x", new SqliteException("m", code))));

    [Fact]
    public void Timeouts_and_dropped_connections_are_transient_even_when_wrapped_the_way_EF_wraps_them()
    {
        Assert.False(SyncErrors.IsDeterministic(new InvalidOperationException("x", new NpgsqlException("x", new TimeoutException()))));      // EF wraps a command timeout like this
        Assert.False(SyncErrors.IsDeterministic(new DbUpdateException("x", new TimeoutException())));
        Assert.False(SyncErrors.IsDeterministic(new DbUpdateException("x", new IOException())));
        Assert.False(SyncErrors.IsDeterministic(new InvalidOperationException("x", new System.Net.Sockets.SocketException())));
        Assert.False(SyncErrors.IsDeterministic(new NpgsqlException("connection reset")));
        Assert.False(SyncErrors.IsDeterministic(new DbUpdateConcurrencyException("someone changed the row")));
        Assert.False(SyncErrors.IsDeterministic(new TimeoutException()));
        Assert.False(SyncErrors.IsDeterministic(new ArgumentException("not ours to skip")));
    }

    [Fact]
    public void A_plain_EF_or_logic_failure_on_one_record_is_deterministic()
    {
        Assert.True(SyncErrors.IsDeterministic(new DbUpdateException("x")));
        Assert.True(SyncErrors.IsDeterministic(new InvalidOperationException("the instance of entity type cannot be tracked: duplicate key")));
    }
}

public class SyncRulesTests
{
    private static CategoryDto C(int n, int? parent = null, string? slug = null) => new(new Guid(n, 0, 0, new byte[8]), parent is null ? null : new Guid(parent.Value, 0, 0, new byte[8]), slug ?? $"c{n}", $"Category {n}", n);
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);

    [Fact]
    public void A_chain_deeper_than_ten_levels_is_attached_at_level_ten()
    {
        var feed = Enumerable.Range(1, 40).Select(i => C(i, i == 1 ? null : i - 1)).ToList();
        var result = CategoryRules.Normalise(feed).ToDictionary(c => c.Id);
        int Depth(Guid id) { var d = 1; for (var p = result[id].ParentId; p is { } x; p = result[x].ParentId) d++; return d; }
        Assert.Equal(10, Enumerable.Range(1, 40).Max(i => Depth(G(i))));
        Assert.Equal(1, Depth(G(1)));
        Assert.Equal(9, Depth(G(9)));
        Assert.Equal(10, Depth(G(10)));                      // exactly ten levels is allowed ...
        Assert.Equal(10, Depth(G(11)));                      // ... the eleventh moves up beside the tenth
        Assert.Equal(G(9), result[G(11)].ParentId);
        Assert.Equal(G(9), result[G(40)].ParentId);
        Assert.Equal(40, result.Count);                      // nothing is dropped
    }

    [Fact]
    public void Cycles_self_parents_and_unknown_parents_become_roots_without_looping()
    {
        var result = CategoryRules.Normalise([C(1, 2), C(2, 3), C(3, 1), C(4, 4), C(5, 99), C(6, 5)]).ToDictionary(c => c.Id);
        Assert.Equal(6, result.Count);
        int Walk(Guid id) { var n = 0; for (var p = result[id].ParentId; p is { } x; p = result[x].ParentId) { n++; Assert.True(n < 20); } return n; }
        foreach (var id in result.Keys) Walk(id);              // every chain ends
        Assert.Equal(1, result.Values.Count(c => c.Id is var i && (i == G(1) || i == G(2) || i == G(3)) && c.ParentId is null));     // the cycle is cut in exactly one place
        Assert.Null(result[G(4)].ParentId);
        Assert.Null(result[G(5)].ParentId);
        Assert.Equal(G(5), result[G(6)].ParentId);
    }

    [Fact]
    public void Records_without_a_slug_or_name_are_dropped_names_are_cut_and_duplicates_collapse()
    {
        var result = CategoryRules.Normalise([C(1) with { Slug = " " }, C(2) with { Name = "" }, C(3) with { Name = new string('n', 250), Slug = new string('s', 130) }, C(4), C(4) with { Name = "Second" }]);
        Assert.Equal([G(3), G(4)], result.Select(c => c.Id).Order());
        Assert.Equal((200, 120), (result.Single(c => c.Id == G(3)).Name.Length, result.Single(c => c.Id == G(3)).Slug.Length));
        Assert.Single(result, c => c.Id == G(4));
    }

    [Fact]
    public void Depth_and_length_limits_are_the_documented_literals()
        => Assert.Equal((10, 200, 50, 100, 50, 120), (CategoryRules.MaxDepth, SnapshotLimits.MaxVariants, SnapshotLimits.MaxImages, SnapshotLimits.MaxAttributes, SnapshotLimits.MaxFaqs, SnapshotLimits.Slug));

    [Fact]
    public void A_clashing_slug_keeps_the_existing_one_or_gets_an_id_suffix()
    {
        var a = G(1); var b = G(2);
        var used = new Dictionary<string, Guid> { ["fruit"] = a };
        Assert.Equal("fruit", SlugRules.Choose("fruit", a, "fruit", used));                          // ours
        Assert.Equal("veg", SlugRules.Choose("veg", b, null, used));                                  // free
        Assert.Equal("fruit-" + b.ToString("N")[..6], SlugRules.Choose("fruit", b, null, used));      // new row, taken: suffix
        Assert.Equal("old", SlugRules.Choose("fruit", b, "old", used));                               // existing row, taken: stable
        used["fruit-" + b.ToString("N")[..6]] = a;
        Assert.Equal("fruit-" + b.ToString("N")[..12], SlugRules.Choose("fruit", b, null, used));
        Assert.True(SlugRules.Choose(new string('x', 120), b, null, new Dictionary<string, Guid> { [new string('x', 120)] = a }).Length <= 120);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(9, 256)]
    [InlineData(10, 360)]
    [InlineData(50, 360)]
    public void Pending_fetches_back_off_exponentially_up_to_six_hours(int attempts, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), CatalogSyncService.Backoff(attempts));

    [Theory]
    [InlineData(60, 0, 60)]
    [InlineData(60, 1, 120)]
    [InlineData(60, 3, 480)]
    [InlineData(60, 4, 600)]
    [InlineData(60, 100, 600)]
    [InlineData(1, 0, 5)]
    [InlineData(5, 0, 5)]
    public void The_worker_waits_longer_after_each_failed_run_but_never_more_than_ten_minutes(int interval, int failures, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), CatalogSyncWorker.Delay(interval, failures));
}

public class CatalogReferenceDataTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    public Task InitializeAsync() { _f = new TestFactory(); return Task.CompletedTask; }
    public Task DisposeAsync() { _f.Dispose(); return Task.CompletedTask; }

    private static Guid Cat(string n) => FixtureCatalogSource.Id("category:" + n);

    [Fact]
    public async Task Two_categories_with_the_same_slug_both_arrive_and_listings_still_sync()
    {
        var fx = Fixture(_f);
        fx.SetCategory(new CategoryDto(Guid.NewGuid(), Cat("food-drink"), "bakery", "Bakery Again", 99));       // the portal keeps slugs unique per parent only
        var r = await Sync(_f);
        Assert.Null(r.Error);
        Assert.Equal(43, r.Applied);
        var slugs = WithDb(_f, db => db.CatalogCategories.Select(c => c.Slug).ToList());
        Assert.Equal(14, slugs.Count);
        Assert.Equal(14, slugs.Distinct().Count());
        Assert.Equal("bakery", WithDb(_f, db => db.CatalogCategories.Single(c => c.Id == Cat("bakery")).Slug));      // the existing one keeps its URL
    }

    [Fact]
    public async Task A_feed_brand_whose_slug_equals_a_generated_fallback_slug_does_not_stall_the_sync()
    {
        var fx = Fixture(_f);
        var template = fx.All().First();
        var orphanBrand = new BrandRef(Guid.NewGuid(), "Orphan Brand");                                    // not in the brand feed: a fallback brand with slug "orphan-brand"
        fx.Add(Copy(template, Guid.NewGuid(), "Orphan Product") with { Brand = orphanBrand });
        Assert.Null((await Sync(_f)).Error);
        fx.AddBrand(new BrandDto(Guid.NewGuid(), template.Seller.Id, "orphan-brand", "Orphan Brand (feed)", null));
        fx.Add(Copy(template, Guid.NewGuid(), "After The Clash"));
        var r = await Sync(_f);
        Assert.Null(r.Error);
        Assert.Equal(1, r.Applied);                                                                          // the cursor moved: the sync is not stuck
        var brandSlugs = WithDb(_f, db => db.CatalogBrands.Select(b => b.Slug).ToList());
        Assert.Equal(brandSlugs.Count, brandSlugs.Distinct().Count());
        Assert.Equal(1, brandSlugs.Count(s => s == "orphan-brand"));
    }

    [Fact]
    public async Task Oversized_names_and_slugs_are_cut_not_fatal()
    {
        var fx = Fixture(_f);
        fx.SetCategory(new CategoryDto(Guid.NewGuid(), null, new string('s', 150), new string('n', 250), 50));
        fx.AddBrand(new BrandDto(Guid.NewGuid(), fx.All()[0].Seller.Id, new string('b', 150), new string('B', 250), null));
        var r = await Sync(_f);
        Assert.Null(r.Error);
        Assert.Equal(43, r.Applied);
        Assert.Equal((120, 200), WithDb(_f, db => db.CatalogCategories.Where(c => c.Name.StartsWith("nnn")).Select(c => new { c.Slug.Length, NameLength = c.Name.Length }).AsEnumerable().Select(x => (x.Length, x.NameLength)).Single()));
        Assert.Equal((120, 200), WithDb(_f, db => db.CatalogBrands.Where(b => b.Name.StartsWith("BBB")).Select(b => new { b.Slug.Length, NameLength = b.Name.Length }).AsEnumerable().Select(x => (x.Length, x.NameLength)).Single()));
    }

    [Fact]
    public async Task A_deep_category_chain_is_flattened_and_the_categories_endpoint_still_answers()
    {
        var fx = Fixture(_f);
        var parent = Cat("food-drink");
        for (var i = 0; i < 60; i++) { var id = Guid.NewGuid(); fx.SetCategory(new CategoryDto(id, parent, $"deep-{i}", $"Deep {i}", 100 + i)); parent = id; }
        var template = fx.All().First(l => l.Type == "product");
        fx.Add(Copy(template, Guid.NewGuid(), "Deep Thing") with { CategoryId = parent });
        Assert.Null((await Sync(_f)).Error);
        using var c = _f.CreateClient();
        var tree = await Json(c, "/api/v1/catalog/categories");
        int Depth(System.Text.Json.JsonElement n) => 1 + (n.GetProperty("children").EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max());
        Assert.True(tree.EnumerateArray().Max(Depth) <= 10);
        Assert.Equal(40 + 1, tree.EnumerateArray().Sum(r => r.GetProperty("listingCount").GetInt32()));       // the listing in the deepest category is still counted
        var page = await Json(c, "/api/v1/catalog/listings?q=deep+thing");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_category_cycle_in_portal_data_does_not_break_anything()
    {
        var fx = Fixture(_f);
        var a = new Guid(1, 0, 0, new byte[8]); var b = new Guid(2, 0, 0, new byte[8]);          // fixed ids: the cycle is cut at a known place (the smaller id is visited first)
        fx.SetCategory(new CategoryDto(a, b, "cycle-a", "Cycle A", 90));
        fx.SetCategory(new CategoryDto(b, a, "cycle-b", "Cycle B", 91));
        fx.Add(Copy(fx.All().First(l => l.Type == "product"), Guid.NewGuid(), "In A Cycle") with { CategoryId = a });
        Assert.Null((await Sync(_f)).Error);
        using var c = _f.CreateClient();
        await Json(c, "/api/v1/catalog/categories");
        Assert.Equal(1, (await Json(c, "/api/v1/catalog/listings?category=cycle-a")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(c, "/api/v1/catalog/listings?category=cycle-b")).GetProperty("total").GetInt32());     // the cut makes B the root and A its child, so B includes A
    }

#pragma warning disable EF1002, EF1003   // DDL cannot be parameterised; every argument below is a constant written in this file
    /// <summary>A trigger that refuses one row the way a database rule does (SQLite: constraint; PostgreSQL: integrity violation).</summary>
    private void Poison(string table, string column, string value, string pgState)
    {
        if (_f.Provider == "postgres")
            WithDb(_f, db => db.Database.ExecuteSqlRaw(
                $"CREATE OR REPLACE FUNCTION poison_{table}() RETURNS trigger AS $$ BEGIN IF NEW.\"{column}\" = '{value}' THEN RAISE EXCEPTION 'poison' USING ERRCODE = '{pgState}'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql; " +
                $"CREATE TRIGGER poison_{table} BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION poison_{table}();"));
        else
            WithDb(_f, db => db.Database.ExecuteSqlRaw($"CREATE TRIGGER poison_{table} BEFORE INSERT ON {table} WHEN NEW.{column} = '{value}' BEGIN SELECT RAISE(ABORT, 'poison'); END;"));
    }

#pragma warning restore EF1002, EF1003

    [Fact]
    public async Task One_record_that_breaks_a_database_rule_is_skipped_and_the_rest_still_apply()
    {
        await Sync(_f);                                                    // creates the schema and the first state
        var fx = Fixture(_f);
        Poison("catalog_categories", "Name", "poison", "23514");
        fx.SetCategory(new CategoryDto(Guid.NewGuid(), null, "poison-cat", "poison", 70));
        fx.SetCategory(new CategoryDto(Guid.NewGuid(), null, "fine-cat", "Fine", 71));
        fx.Add(Copy(fx.All().First(), Guid.NewGuid(), "Still Arrives"));
        var r = await Sync(_f);
        Assert.Null(r.Error);                                              // skipped records are logged by id, not an error of the run
        Assert.Equal(1, r.Applied);
        Assert.True(WithDb(_f, db => db.CatalogCategories.Any(c => c.Slug == "fine-cat")));
        Assert.False(WithDb(_f, db => db.CatalogCategories.Any(c => c.Slug == "poison-cat")));
        Assert.Contains(_f.Logs.Lines, l => l.Contains("record by record"));
        Assert.DoesNotContain(_f.Logs.Lines, l => l.Contains("RAISE") || l.Contains("poison-cat"));      // no message text or slug in the log, only the id
        Assert.Null((await Sync(_f)).Error);                               // and the next run is as calm
    }
}

public class CatalogSyncHardeningTests
{
    private static CatalogSyncState State(TestFactory f) => WithDb(f, db => db.CatalogSyncStates.AsNoTracking().Single());
    private static async Task<CatalogSyncResult> Run(TestFactory f, ICatalogSource s, int pageSize = 100, int maxPending = 100, int retentionDays = 90)
    {
        using var scope = f.Services.CreateScope();
        return await Service(scope, s, f.Clock, pageSize, maxPending, retentionDays).RunAsync();
    }

    // ---------- lease owner and renewal (item 5) ----------
    [Fact]
    public async Task A_run_that_lost_its_lease_stops_at_the_next_page_and_never_releases_the_new_owners_lease()
    {
        using var f = new TestFactory();
        var gateA = new TaskCompletionSource(); var gateB = new TaskCompletionSource();
        var a = new ProbeSource(Fixture(f)) { Gate = gateA }; var b = new ProbeSource(Fixture(f)) { Gate = gateB };
        var runA = Run(f, a, pageSize: 10);
        await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance(TimeSpan.FromMinutes(6));                                   // A's five-minute lease has expired
        var runB = Run(f, b, pageSize: 10);
        await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ownerB = State(f).LeaseOwner;
        Assert.NotNull(ownerB);
        gateA.SetResult();
        var resultA = await runA;
        Assert.Equal(10, resultA.Applied);                                           // A finished the page it held, then its renewal failed: it stopped (renewal per page)
        Assert.Equal(ownerB, State(f).LeaseOwner);                                  // A did not release B's lease
        Assert.NotNull(State(f).LeaseUntil);
        Assert.Null(State(f).LastRunAt);                                            // and wrote nothing into the shared state
        gateB.SetResult();
        var resultB = await runB;
        Assert.Null(resultB.Error);
        Assert.Equal(43, WithDb(f, db => db.CatalogListings.Count()));
        Assert.Null(State(f).LeaseOwner);
        Assert.Null(State(f).LeaseUntil);
    }

    [Fact]
    public async Task A_running_instance_renews_its_lease_on_every_page()
    {
        using var f = new TestFactory();
        var probe = new ProbeSource(Fixture(f));
        probe.OnListingCall = n => { f.Clock.Advance(TimeSpan.FromMinutes(4)); };      // each page "takes" four minutes: five pages would outlive one five-minute lease
        var r = await Run(f, probe, pageSize: 10);
        Assert.Null(r.Error);
        Assert.Equal(43, r.Applied);                                                   // it kept the lease for the whole run
        Assert.Equal(5, probe.ListingCalls);
    }

    // ---------- pending fetches back off instead of starving good rows (item 6) ----------
    [Fact]
    public async Task Pending_fetches_that_never_apply_back_off_so_the_ones_behind_them_are_reached()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var template = fx.All().First(l => l.Type == "product");
        var missing = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();      // six listings in categories the portal has not published yet
        var added = missing.Select(c => fx.Add(Copy(template, Guid.NewGuid()) with { CategoryId = c })).ToList();
        var probe = new ProbeSource(fx);
        await Run(f, probe, maxPending: 3);
        Assert.Equal(3, probe.GetListingCalls);                                          // oldest three first
        await Run(f, probe, maxPending: 3);
        Assert.Equal(6, probe.GetListingCalls);                                          // the other three: the first three are backing off, not retried
        await Run(f, probe, maxPending: 3);
        Assert.Equal(6, probe.GetListingCalls);                                          // everything is waiting: no portal calls at all
        Assert.Equal(1, WithDb(f, db => db.CatalogListings.Where(l => l.Id == added[0].Id).Select(l => l.FetchAttempts).Single()));
        fx.SetCategory(new CategoryDto(missing[5], null, "late-category", "Late Category", 80));       // the sixth one's category is published
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Run(f, probe, maxPending: 3);                                               // back-off over: the first three are tried again and fail again ...
        Assert.Equal(9, probe.GetListingCalls);
        Assert.False(WithDb(f, db => db.CatalogListings.Single(l => l.Id == added[5].Id).Visible));
        await Run(f, probe, maxPending: 3);                                               // ... so this run reaches the other three, including the sixth, which now applies
        Assert.Equal(12, probe.GetListingCalls);
        Assert.True(WithDb(f, db => db.CatalogListings.Single(l => l.Id == added[5].Id).Visible));
        Assert.Equal(2, WithDb(f, db => db.CatalogListings.Where(l => l.Id == added[0].Id).Select(l => l.FetchAttempts).Single()));          // second failure: two minutes next time
        var next = WithDb(f, db => db.CatalogListings.Where(l => l.Id == added[0].Id).Select(l => l.NextFetchAt).Single());
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(2), next);
    }

    [Fact]
    public async Task A_newer_version_announced_by_an_event_is_fetched_at_once_even_while_backing_off()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var bad = fx.Add(Copy(fx.All().First(l => l.Type == "product"), Guid.NewGuid()) with { CategoryId = Guid.NewGuid() });
        var probe = new ProbeSource(fx);
        await Run(f, probe);
        Assert.Equal(1, WithDb(f, db => db.CatalogListings.Single(l => l.Id == bad.Id).FetchAttempts));
        using var c = f.CreateClient();
        await Event(f, c, "listing.upserted", bad.Id, bad.Version + 5);
        var row = WithDb(f, db => db.CatalogListings.Single(l => l.Id == bad.Id));
        Assert.Equal((0, null), (row.FetchAttempts, row.NextFetchAt));
    }

    // ---------- seller state is ordered (item 7) ----------
    [Fact]
    public async Task An_older_snapshot_cannot_reactivate_a_suspended_seller()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        await Sync(f);
        var mazel = fx.All().Where(l => l.Seller.DisplayName == "Mazel Bakery").ToList();
        var first = mazel[0];
        fx.Upsert(first.Id, l => l with { Seller = l.Seller with { Active = false } });                   // newer time: suspended
        await Sync(f);
        Assert.False(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == first.Seller.Id).Active));
        // a stale page: a snapshot stamped BEFORE the suspension that still says active
        var stale = fx.Add(Copy(first, Guid.NewGuid(), "Stale Page Item") with { UpdatedAt = first.UpdatedAt.AddDays(-10), Seller = first.Seller with { Active = true } });
        await Sync(f);
        Assert.False(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == first.Seller.Id).Active));     // still suspended
        Assert.Equal(0, WithDb(f, db => db.CatalogListings.Count(l => l.Id == stale.Id && !l.IsStub)) - 1);   // the listing itself was applied
        // a genuinely newer snapshot reactivates
        fx.Upsert(mazel[1].Id, l => l with { Seller = l.Seller with { Active = true } });
        await Sync(f);
        Assert.True(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == first.Seller.Id).Active));
    }

    [Fact]
    public async Task A_snapshot_with_the_same_time_applies_so_equal_stamps_cannot_wedge_a_seller()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        await Sync(f);
        var l0 = fx.All().First(l => l.Seller.DisplayName == "Mazel Bakery");
        var at = DateTimeOffset.UtcNow.AddDays(1);
        fx.Add(l0 with { UpdatedAt = at, Seller = l0.Seller with { Active = false } });          // Add keeps the time it is given (Upsert would bump an equal one)
        await Sync(f);
        fx.Add(l0 with { UpdatedAt = at, Seller = l0.Seller with { Active = true } });          // the very same time
        await Sync(f);
        Assert.True(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == l0.Seller.Id).Active));
    }

    // ---------- seller retention (item 11) ----------
    [Fact]
    public async Task A_seller_with_nothing_visible_is_deleted_ninety_days_and_a_second_after_it_was_last_visible()
    {
        using var f = new TestFactory();
        await Sync(f);
        var closed = FixtureCatalogSource.Id("seller:closed");
        Assert.True(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == closed)));                // suspended from the start: nothing visible
        f.Clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        await Sync(f);
        Assert.True(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == closed)));                // 89 days 23:59:59: kept
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Sync(f);
        Assert.True(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == closed)));                // exactly 90 days: kept
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Sync(f);
        Assert.False(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == closed)));               // one second more: deleted
        Assert.Equal(5, WithDb(f, db => db.CatalogSellers.Count()));                              // sellers with visible listings are untouched
        using var c = f.CreateClient();
        await Json(c, "/api/v1/catalog/listings");                                                // and the catalog still works
    }

    [Fact]
    public async Task A_seller_that_stays_visible_is_never_deleted_however_long_the_service_runs()
    {
        using var f = new TestFactory();
        await Sync(f);
        for (var i = 0; i < 4; i++) { f.Clock.Advance(TimeSpan.FromDays(60)); await Sync(f); }
        Assert.True(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == FixtureCatalogSource.Id("seller:mazel"))));
    }

    [Fact]
    public async Task A_deleted_seller_that_comes_back_is_recreated_by_its_next_snapshot()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        await Sync(f);
        var closed = FixtureCatalogSource.Id("seller:closed");
        f.Clock.Advance(TimeSpan.FromDays(91));
        await Sync(f);
        Assert.False(WithDb(f, db => db.CatalogSellers.Any(s => s.Id == closed)));
        var mug = fx.All().First(l => l.Seller.Id == closed);
        fx.Upsert(mug.Id, l => l with { Seller = l.Seller with { Active = true } });
        await Sync(f);
        Assert.True(WithDb(f, db => db.CatalogSellers.Single(s => s.Id == closed).Active));
    }

    // ---------- snapshot limits (items 9 and B-2) ----------
    private static ListingSnapshot Wide(ListingSnapshot t, int variants, int images, int attributes, int faqs) => Copy(t, Guid.NewGuid()) with
    {
        Variants = Enumerable.Range(0, variants).Select(i => new VariantDto(Guid.NewGuid(), $"V{i}", 100 + i, null, true)).ToList(),
        Images = Enumerable.Range(0, images).Select(i => new ImageDto(Guid.NewGuid(), 10, 10, $"i{i}")).ToList(),
        Attributes = Enumerable.Range(0, attributes).Select(i => new AttributeDto($"k{i}", $"v{i}")).ToList(),
        Faqs = Enumerable.Range(0, faqs).Select(i => new FaqDto($"q{i}", $"a{i}")).ToList(),
    };

    [Fact]
    public async Task Collections_are_capped_at_their_limits_exactly()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var t = fx.All().First(l => l.Type == "product");
        var atLimit = fx.Add(Wide(t, 200, 50, 100, 50));
        var over = fx.Add(Wide(t, 200, 51, 101, 51));
        var tooManyVariants = fx.Add(Wide(t, 201, 1, 1, 1));
        var r = await Run(f, fx);
        Assert.Equal(43 + 2, r.Applied);
        Assert.Equal(1, r.Skipped);                                                                                // 201 variants: invalid, skipped (a buyer must be able to buy every variant, so they are never cut)
        (int, int, int, int) Counts(Guid id) => WithDb(f, db => (db.CatalogVariants.Count(x => x.ListingId == id), db.CatalogImages.Count(x => x.ListingId == id), db.CatalogAttributes.Count(x => x.ListingId == id), db.CatalogFaqs.Count(x => x.ListingId == id)));
        Assert.Equal((200, 50, 100, 50), Counts(atLimit.Id));
        Assert.Equal((200, 50, 100, 50), Counts(over.Id));                                                         // images, attributes, FAQs are cut to the limit
        Assert.False(WithDb(f, db => db.CatalogListings.Any(l => l.Id == tooManyVariants.Id)));
    }

    [Fact]
    public async Task Oversized_text_is_cut_to_its_column_not_the_reason_to_drop_the_listing()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var t = fx.All().First(l => l.Type == "product");
        string S(int n) => new string('x', n);
        var exact = fx.Add(Copy(t, Guid.NewGuid(), S(300)) with { Summary = S(600), Variants = [new VariantDto(Guid.NewGuid(), S(300), 1, null, true)], Images = [new ImageDto(Guid.NewGuid(), 1, 1, S(300))],
            Attributes = [new AttributeDto(S(100), S(500))], Faqs = [new FaqDto(S(500), "a")] });
        var over = fx.Add(Copy(t, Guid.NewGuid(), S(301)) with { Summary = S(601), Variants = [new VariantDto(Guid.NewGuid(), S(301), 1, null, true)], Images = [new ImageDto(Guid.NewGuid(), 1, 1, S(301))],
            Attributes = [new AttributeDto(S(101), S(501))], Faqs = [new FaqDto(S(501), "a")], Seller = new SellerRef(Guid.NewGuid(), S(250), true) });
        var r = await Run(f, fx);
        Assert.Equal((45, 0, null), (r.Applied, r.Skipped, r.Error));
        foreach (var id in new[] { exact.Id, over.Id })
        {
            var (title, summary, vname, alt, key, value, q) = WithDb(f, db => (db.CatalogListings.Single(l => l.Id == id).Title, db.CatalogListings.Single(l => l.Id == id).Summary, db.CatalogVariants.Single(x => x.ListingId == id).Name,
                db.CatalogImages.Single(x => x.ListingId == id).Alt, db.CatalogAttributes.Single(x => x.ListingId == id).Key, db.CatalogAttributes.Single(x => x.ListingId == id).Value, db.CatalogFaqs.Single(x => x.ListingId == id).Question));
            Assert.Equal((300, 600, 300, 300, 100, 500, 500), (title.Length, summary.Length, vname.Length, alt.Length, key.Length, value.Length, q.Length));
        }
        Assert.Equal(200, WithDb(f, db => db.CatalogSellers.Single(s => s.Id == over.Seller.Id).DisplayName.Length));
    }

    [Fact]
    public async Task Updating_a_wide_listing_does_not_build_the_cartesian_product_of_its_collections()
    {
        using var f = new TestFactory();
        var fx = Fixture(f);
        var wide = fx.Add(Wide(fx.All().First(l => l.Type == "product"), 100, 30, 30, 20));
        await Run(f, fx);
        fx.Upsert(wide.Id, l => l with { Title = "Wide One Renamed" });
        var r = await Run(f, fx);                                                          // this read of the existing rows is the query that exploded
        Assert.Null(r.Error);
        Assert.Equal(1, r.Applied);
        // (In Testing, EF is configured to THROW on an unsplit multi-collection Include, in every test: a log line would be order dependent, since EF
        // warns only the first time a query shape is compiled in the process.)
    }

    // ---------- a transient database failure is not a bad record (B-2) ----------
#pragma warning disable EF1002, EF1003   // DDL cannot be parameterised; every argument is a constant in this file
    [Fact]
    public async Task A_transient_failure_keeps_the_cursor_and_loses_nothing_PostgreSQL()
    {
        using var f = new TestFactory();
        if (f.Provider != "postgres") return;                                              // the failure is simulated with a PostgreSQL trigger (query_canceled); CI runs this on PostgreSQL
        var fx = Fixture(f);
        await Run(f, fx);
        var cursorBefore = State(f).Cursor;
        var before = WithDb(f, db => db.CatalogListings.Count());
        WithDb(f, db => db.Database.ExecuteSqlRaw(
            "CREATE FUNCTION slow_listing() RETURNS trigger AS $$ BEGIN IF NEW.\"Title\" = 'Transient One' THEN RAISE EXCEPTION 'canceling statement due to statement timeout' USING ERRCODE = '57014'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql; " +
            "CREATE TRIGGER slow_listing BEFORE INSERT ON catalog_listings FOR EACH ROW EXECUTE FUNCTION slow_listing();"));
        var template = fx.All().First(l => l.Type == "product");
        var good = fx.Add(Copy(template, Guid.NewGuid(), "Before It"));
        var transient = fx.Add(Copy(template, Guid.NewGuid(), "Transient One"));
        var after = fx.Add(Copy(template, Guid.NewGuid(), "After It"));
        var failed = await Run(f, fx);
        Assert.Equal("DbUpdateException", failed.Error);                                    // the run fails: this is not a bad record
        Assert.Equal(cursorBefore, State(f).Cursor);                                        // the cursor did not move past it
        Assert.Equal(before, WithDb(f, db => db.CatalogListings.Count()));                  // and the page is atomic: nothing from it is half applied
        Assert.Null(State(f).LeaseOwner);
        WithDb(f, db => db.Database.ExecuteSqlRaw("DROP TRIGGER slow_listing ON catalog_listings"));
        var ok = await Run(f, fx);                                                           // the database is back: the next run applies all three
        Assert.Null(ok.Error);
        Assert.Equal(3, ok.Applied);
        Assert.True(WithDb(f, db => db.CatalogListings.Count(l => new[] { good.Id, transient.Id, after.Id }.Contains(l.Id) && l.Visible)) == 3);
    }
#pragma warning restore EF1002, EF1003
}
