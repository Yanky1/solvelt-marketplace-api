using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogCacheControlTests : IAsyncLifetime
{
    private TestFactory _f = null!;
    private HttpClient _c = null!;
    private const string B = "/api/v1/catalog";
    public async Task InitializeAsync() { _f = new TestFactory(); await Sync(_f); _c = _f.CreateClient(); }
    public Task DisposeAsync() { _c.Dispose(); _f.Dispose(); return Task.CompletedTask; }

    private async Task<string?> CacheControl(string url, HttpStatusCode expected)
    {
        var res = await _c.GetAsync(url);
        Assert.Equal(expected, res.StatusCode);
        return res.Headers.CacheControl?.ToString();
    }

    [Theory]
    [InlineData("/categories")]
    [InlineData("/brands")]
    [InlineData("/listings")]
    [InlineData("/listings?q=challah&facets=true")]
    [InlineData("/listings/classic-braided-challah")]
    [InlineData("/brands/challah-corner")]
    [InlineData("/suggest?q=chal")]
    [InlineData("/sitemap/listings")]
    [InlineData("/sitemap/categories")]
    public async Task Successful_catalog_answers_may_be_cached_for_five_seconds_and_no_longer(string path)
        => Assert.Equal("public, max-age=5", await CacheControl(B + path, HttpStatusCode.OK));

    [Fact]
    public async Task Errors_are_never_cached()
    {
        Assert.Equal("no-store", await CacheControl($"{B}/listings/no-such-listing", HttpStatusCode.NotFound));
        Fixture(_f).Upsert(FixtureCatalogSource.Id("listing:babka-chocolate"), l => l with { Status = "Archived" });
        await Sync(_f);
        Assert.Equal("no-store", await CacheControl($"{B}/listings/chocolate-babka", HttpStatusCode.Gone));               // a 410 is final, but "final" is decided by us, not by a cache
        Assert.Equal("no-store", await CacheControl($"{B}/listings?sort=nonsense", HttpStatusCode.BadRequest));
        Assert.Equal("no-store", await CacheControl($"{B}/sitemap/nonsense", HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task A_rate_limited_answer_is_never_cached()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "1" });
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{B}/categories")).StatusCode);
        var limited = await c.GetAsync($"{B}/categories");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);                         // docs/API.md promises Retry-After; Clear() used to drop it
    }

    [Fact]
    public async Task An_unlisted_listing_is_gone_from_the_next_response_so_five_seconds_is_the_longest_it_can_linger()
    {
        var babka = Row(_f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        await Event(_f, _c, "listing.unlisted", babka.Id, babka.Version);
        Assert.Equal("no-store", await CacheControl($"{B}/listings/chocolate-babka", HttpStatusCode.Gone));
    }
}

public sealed class CategoryReadCounter : DbCommandInterceptor
{
    public int Reads;
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { if (command.CommandText.Contains("FROM \"catalog_categories\"") || command.CommandText.Contains("FROM catalog_categories")) Interlocked.Increment(ref Reads); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    { ReaderExecuting(command, eventData, result); return ValueTask.FromResult(result); }
}

public class CategoryMapTests
{
    [Fact]
    public async Task One_request_reads_the_category_table_once_however_many_parts_need_it()
    {
        var counter = new CategoryReadCounter();
        using var f = new TestFactory(services: s => s.ConfigureDbContext<MarketplaceDb>(o => o.AddInterceptors(counter)));
        await Sync(f);
        using var c = f.CreateClient();
        counter.Reads = 0;
        // category filter + facets (category tree) + cards (category of each card) all need categories
        await Json(c, "/api/v1/catalog/listings?category=food-drink&facets=true");
        Assert.Equal(1, counter.Reads);
        counter.Reads = 0;
        await Json(c, "/api/v1/catalog/listings/classic-braided-challah");
        Assert.Equal(1, counter.Reads);
        counter.Reads = 0;
        await Json(c, "/api/v1/catalog/suggest?q=bak");
        Assert.Equal(1, counter.Reads);
    }

    private static async Task<T> InScope<T>(TestFactory f, Func<CategoryMap, Task<T>> body)
    {
        using var scope = f.Services.CreateScope();
        return await body(scope.ServiceProvider.GetRequiredService<CategoryMap>());
    }

    [Fact]
    public async Task Descendants_include_the_category_and_everything_below_it_once()
    {
        using var f = new TestFactory();
        await Sync(f);
        var food = FixtureCatalogSource.Id("category:food-drink");
        var ids = await InScope(f, m => m.DescendantsAsync(food, default));
        Assert.Equal(food, ids[0]);
        Assert.Equal(5, ids.Count);                                                     // food-drink + bakery, meat, wine, pantry
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Single(await InScope(f, m => m.DescendantsAsync(FixtureCatalogSource.Id("category:bakery"), default)));
        Assert.Single(await InScope(f, m => m.DescendantsAsync(Guid.NewGuid(), default)));     // unknown: just itself, and so an empty page
    }

    [Fact]
    public async Task A_cycle_in_stored_categories_ends_the_walk_and_a_big_tree_is_fast()
    {
        using var f = new TestFactory();
        await Sync(f);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var rootOfBig = Guid.NewGuid();
        WithDb(f, db =>
        {
            // stored data that the sync would have repaired, written directly: the endpoints must still survive it
            db.CatalogCategories.AddRange(new CatalogCategory { Id = a, ParentId = b, Slug = "cyc-a", Name = "A" }, new CatalogCategory { Id = b, ParentId = a, Slug = "cyc-b", Name = "B" });
            db.CatalogCategories.Add(new CatalogCategory { Id = rootOfBig, Slug = "big-root", Name = "Big" });
            for (var i = 0; i < 6000; i++) db.CatalogCategories.Add(new CatalogCategory { Id = Guid.NewGuid(), ParentId = i < 60 ? rootOfBig : null, Slug = "big-" + i, Name = "Big " + i });
            return db.SaveChanges();
        });
        var cyc = await InScope(f, m => m.DescendantsAsync(a, default));
        Assert.Equal(2, cyc.Count);
        var sw = Stopwatch.StartNew();
        var big = await InScope(f, m => m.DescendantsAsync(rootOfBig, default));
        sw.Stop();
        Assert.Equal(61, big.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"descendants of a node in a 6000 category table took {sw.ElapsedMilliseconds} ms");
        using var c = f.CreateClient();
        await Json(c, "/api/v1/catalog/categories");                                                          // the tree endpoint survives a cycle
        Assert.Equal(0, (await Json(c, "/api/v1/catalog/listings?category=cyc-a")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_stored_chain_deeper_than_the_sync_allows_does_not_break_the_tree_json()
    {
        using var f = new TestFactory();
        await Sync(f);
        WithDb(f, db =>
        {
            Guid? parent = FixtureCatalogSource.Id("category:food-drink");
            for (var i = 0; i < 200; i++) { var id = Guid.NewGuid(); db.CatalogCategories.Add(new CatalogCategory { Id = id, ParentId = parent, Slug = "chain-" + i, Name = "Chain " + i }); parent = id; }
            db.SaveChanges();
            // a listing in the deepest category keeps the whole chain in the tree (categories with nothing below them are left out)
            return db.CatalogListings.Where(l => l.Id == FixtureCatalogSource.Id("listing:babka-chocolate")).ExecuteUpdate(s => s.SetProperty(l => l.CategoryId, parent!.Value));
        });
        using var c = f.CreateClient();
        var tree = await Json(c, "/api/v1/catalog/categories");              // used to be a 500 from about 33 levels (JSON depth)
        int Depth(JsonElement n) => 1 + n.GetProperty("children").EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max();
        Assert.True(tree.EnumerateArray().Max(Depth) <= CategoryRules.MaxDepth + 2);
    }

    [Fact]
    public async Task The_breadcrumb_of_a_listing_is_bounded_and_ends_at_a_repeat()
    {
        using var f = new TestFactory();
        await Sync(f);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var fx = Fixture(f);
        WithDb(f, db =>
        {
            db.CatalogCategories.AddRange(new CatalogCategory { Id = a, ParentId = b, Slug = "bc-a", Name = "A" }, new CatalogCategory { Id = b, ParentId = a, Slug = "bc-b", Name = "B" });
            return db.SaveChanges();
        });
        WithDb(f, db => db.CatalogListings.Where(l => l.Id == FixtureCatalogSource.Id("listing:babka-chocolate")).ExecuteUpdate(s => s.SetProperty(l => l.CategoryId, a)));
        using var c = f.CreateClient();
        var detail = await Json(c, "/api/v1/catalog/listings/chocolate-babka");
        Assert.Equal(2, detail.GetProperty("categoryPath").GetArrayLength());
    }
}

public class CatalogEventOrderTests
{
    [Fact]
    public async Task A_late_unlist_event_with_a_lower_version_never_lowers_the_unlisted_version()
    {
        using var f = new TestFactory();
        await Sync(f);
        using var c = f.CreateClient();
        var babka = Row(f, FixtureCatalogSource.Id("listing:babka-chocolate"));
        await Event(f, c, "listing.unlisted", babka.Id, babka.Version);                 // hides it (unlisted version = its version)
        await Event(f, c, "listing.unlisted", babka.Id, babka.Version - 1);             // delivered late: older
        Assert.Equal(babka.Version, WithDb(f, db => db.CatalogListings.Single(l => l.Id == babka.Id).UnlistedVersion));
        Assert.False(WithDb(f, db => db.CatalogListings.Single(l => l.Id == babka.Id).Visible));
        await Json(c, "/api/v1/catalog/listings/chocolate-babka", HttpStatusCode.Gone);   // still hidden
    }
}
