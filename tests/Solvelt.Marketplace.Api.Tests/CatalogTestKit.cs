using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

public static class CatalogKit
{
    public static Guid ListingId(string fixtureSlug) => FixtureCatalogSource.Id("listing:" + fixtureSlug);
    /// <summary>A copy of a listing under a new id with new variant and image ids (child ids are primary keys, so copies must not share them).</summary>
    public static ListingSnapshot Copy(ListingSnapshot t, Guid id, string? title = null) => t with
    {
        Id = id, Title = title ?? t.Title,
        Variants = t.Variants.Select(v => v with { Id = Guid.NewGuid() }).ToList(),
        Images = t.Images.Select(i => i with { Id = Guid.NewGuid() }).ToList(),
    };

    public static FixtureCatalogSource Fixture(TestFactory f) => f.Services.GetRequiredService<FixtureCatalogSource>();

    public static async Task<CatalogSyncResult> Sync(TestFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogSyncService>().RunAsync();
    }

    public static CatalogSyncService Service(IServiceScope scope, ICatalogSource source, TimeProvider clock, int pageSize = 100)
        => new(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), source, clock, Options.Create(new CatalogOptions { PageSize = pageSize }), NullLogger<CatalogSyncService>.Instance);

    public static async Task<JsonElement> Json(HttpClient c, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await c.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"GET {url} -> {(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<(HttpStatusCode Status, string Json)> Event(TestFactory f, HttpClient c, string type, Guid listingId, long version, string? id = null)
        => await Post(c, Request(f, Body(id ?? $"evt-{type}-{listingId:N}-{version}-{Guid.NewGuid():N}"[..60], type, T0, new { listingId, version })));

    public static CatalogListing Row(TestFactory f, Guid id) => WithDb(f, db => db.CatalogListings.AsNoTracking().Include(l => l.Variants).Include(l => l.Images).Include(l => l.Attributes).Include(l => l.Faqs).Single(l => l.Id == id));

    public static List<string> Slugs(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToList();
}

/// <summary>An <see cref="ICatalogSource"/> that records and can fail or block calls, wrapping the fixture.</summary>
public sealed class ProbeSource(ICatalogSource inner) : ICatalogSource
{
    public int ListingCalls, GetListingCalls, ReferenceCalls;
    public Func<int, bool>? FailListingCall;
    public bool FailReferenceData;
    public TaskCompletionSource? Gate;
    public TaskCompletionSource Entered { get; } = new();

    public async Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref ListingCalls);
        Entered.TrySetResult();
        if (Gate is not null) await Gate.Task;
        if (FailListingCall?.Invoke(n) == true) throw new InvalidOperationException("simulated portal failure");
        return await inner.GetListingsAsync(since, limit, ct);
    }

    public Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct) { Interlocked.Increment(ref GetListingCalls); return inner.GetListingAsync(id, ct); }

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ReferenceCalls);
        if (FailReferenceData) throw new InvalidOperationException("simulated portal failure");
        return inner.GetCategoriesAsync(ct);
    }

    public Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct) => inner.GetBrandsAsync(ct);
}

/// <summary>Fails on every call: proves a code path never reaches the network.</summary>
public sealed class ForbiddenSource : ICatalogSource
{
    private static Exception Boom() => new InvalidOperationException("the catalog source must not be called here");
    public Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct) => throw Boom();
    public Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct) => throw Boom();
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct) => throw Boom();
    public Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct) => throw Boom();
}
