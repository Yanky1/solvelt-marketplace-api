using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The image endpoint (M2-22): bytes of an image of a visible listing, from the portal, for the web tier's resizer.</summary>
public class CatalogImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>The seed fixture's listings, with the real portal image source (the default in Testing is the placeholder).</summary>
    private static TestFactory WithPortalImages() => new(services: s =>
    {
        s.RemoveAll<ICatalogImageSource>();
        s.AddSingleton<ICatalogImageSource>(sp => new PortalImageSource(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<PortalImageSource>>()));
    });

    private static HttpResponseMessage Image(byte[] bytes, string type) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(type) } } };
    private static Guid FirstImage(TestFactory f, string slug = "babka-chocolate") => Row(f, ListingId(slug)).Images.OrderBy(i => i.SortOrder).First().Id;
    private static int ImageCalls(TestFactory f) => f.Portal.VerifiedCalls.Count(c => c.PathAndQuery.Contains("/catalog/images/"));

    [Fact]
    public async Task A_visible_image_is_served_with_its_bytes_hardening_headers_and_a_signed_call_to_the_portal()
    {
        using var f = WithPortalImages();
        await Sync(f);
        var id = FirstImage(f);
        f.Portal.RespondAlways(_ => Image(Png, "image/png"));
        var res = await f.CreateClient().GetAsync($"/api/v1/catalog/images/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Png, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", res.Content.Headers.ContentDisposition?.DispositionType ?? string.Join("", res.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(ImageEndpoints.CacheControl, res.Headers.CacheControl!.ToString());
        Assert.StartsWith("\"", res.Headers.ETag!.Tag);
        var call = Assert.Single(f.Portal.VerifiedCalls);
        Assert.Equal(("GET", $"/api/internal/v1/catalog/images/{id:D}"), (call.Method, call.PathAndQuery));      // signed (the fake verified it), no body, the id only
        Assert.Empty(call.Body);
    }

    [Fact]
    public async Task A_matching_validator_gets_304_with_no_body_and_a_repeat_comes_from_the_cache_not_the_portal()
    {
        using var f = WithPortalImages();
        await Sync(f);
        var id = FirstImage(f);
        f.Portal.RespondAlways(_ => Image(Png, "image/png"));
        var c = f.CreateClient();
        var first = await c.GetAsync($"/api/v1/catalog/images/{id}");
        var etag = first.Headers.ETag!.Tag;
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/catalog/images/{id}");
        req.Headers.TryAddWithoutValidation("If-None-Match", $"W/{etag}");
        var cached = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, ImageCalls(f));                                                                        // the second answer never asked the portal
    }

    [Fact]
    public async Task An_unknown_image_is_404_and_the_portal_is_never_asked()
    {
        using var f = WithPortalImages();
        await Sync(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/catalog/images/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/catalog/images/not-an-id")).StatusCode);
        Assert.Equal(0, ImageCalls(f));
    }

    [Fact]
    public async Task An_image_of_a_listing_that_is_no_longer_visible_is_404_even_when_it_was_cached()
    {
        using var f = WithPortalImages();
        await Sync(f);
        var id = FirstImage(f);
        f.Portal.RespondAlways(_ => Image(Png, "image/png"));
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/catalog/images/{id}")).StatusCode);          // now in the cache
        var listing = ListingId("babka-chocolate");
        await Event(f, c, "listing.unlisted", listing, Row(f, listing).Version);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/catalog/images/{id}")).StatusCode);   // visibility is checked before the cache is asked
    }

    [Fact]
    public async Task What_the_portal_says_is_mapped_honestly_not_found_is_404_an_outage_is_503_and_anything_not_a_raster_picture_is_502()
    {
        using var f = WithPortalImages();
        await Sync(f);
        var c = f.CreateClient();
        var ids = WithDb(f, db => db.CatalogListings.Where(l => l.Visible).OrderBy(l => l.Id).Take(3).Select(l => l.Images.OrderBy(i => i.SortOrder).First().Id).ToList());   // the first photo of three listings
        Assert.Equal(3, ids.Distinct().Count());
        f.Portal.RespondAlways(r => r.PathAndQuery.Contains(ids[0].ToString("D")) ? FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}")
            : r.PathAndQuery.Contains(ids[1].ToString("D")) ? Image(Png, "image/svg+xml")                                    // a script could live in this
            : FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/catalog/images/{ids[0]}")).StatusCode);
        var svg = await c.GetAsync($"/api/v1/catalog/images/{ids[1]}");
        Assert.Equal(HttpStatusCode.BadGateway, svg.StatusCode);
        Assert.NotEqual("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        var down = await c.GetAsync($"/api/v1/catalog/images/{ids[2]}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("30", down.Headers.GetValues("Retry-After").Single());
        Assert.Equal("no-store", down.Headers.CacheControl!.ToString());                                                // an outage is never cached
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("image/svg+xml")]
    public async Task Only_jpeg_png_webp_gif_and_avif_are_passed_on(string type)
    {
        using var f = WithPortalImages();
        await Sync(f);
        f.Portal.RespondAlways(_ => Image(Png, type));
        Assert.Equal(HttpStatusCode.BadGateway, (await f.CreateClient().GetAsync($"/api/v1/catalog/images/{FirstImage(f)}")).StatusCode);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/gif")]
    [InlineData("image/avif")]
    public async Task The_raster_types_are_passed_on_as_they_are(string type)
    {
        using var f = WithPortalImages();
        await Sync(f);
        f.Portal.RespondAlways(_ => Image(Png, type));
        var res = await f.CreateClient().GetAsync($"/api/v1/catalog/images/{FirstImage(f)}");
        Assert.Equal((HttpStatusCode.OK, type), (res.StatusCode, res.Content.Headers.ContentType!.MediaType));
    }

    [Fact]
    public async Task An_oversized_original_is_refused_not_buffered_without_limit()
    {
        using var f = WithPortalImages();
        await Sync(f);
        f.Portal.RespondAlways(_ => Image(new byte[Solvelt.Marketplace.Api.Portal.SignedPortalClient.MaxImageBytes + 1], "image/png"));
        Assert.Equal(HttpStatusCode.BadGateway, (await f.CreateClient().GetAsync($"/api/v1/catalog/images/{FirstImage(f)}")).StatusCode);
    }

    [Fact]
    public async Task An_empty_body_is_not_a_picture()
    {
        using var f = WithPortalImages();
        await Sync(f);
        f.Portal.RespondAlways(_ => Image([], "image/png"));
        Assert.Equal(HttpStatusCode.BadGateway, (await f.CreateClient().GetAsync($"/api/v1/catalog/images/{FirstImage(f)}")).StatusCode);
    }

    // ---------- the seed fixture has no portal: a generated placeholder ----------
    [Fact]
    public async Task With_the_seed_fixture_the_placeholder_is_a_raster_picture_of_the_right_shape_made_from_the_id_alone()
    {
        using var f = new TestFactory();
        await Sync(f);
        var images = Row(f, ListingId("babka-chocolate")).Images.OrderBy(i => i.SortOrder).ToList();
        var image = images.First();
        var c = f.CreateClient();
        var res = await c.GetAsync($"/api/v1/catalog/images/{image.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);                                          // a raster: the web resizer refuses SVG on purpose (a 502 on every fixture image otherwise)
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);                                               // really a PNG, not a mislabelled something else
        using (var decoded = SkiaSharp.SKBitmap.Decode(bytes))
        {
            Assert.NotNull(decoded);
            Assert.Equal((Math.Min(image.Width, 1200), Math.Min(image.Height, 1200)), (decoded.Width, decoded.Height)); // the stored shape (a very large one is drawn smaller)
        }
        Assert.DoesNotContain(image.Alt, System.Text.Encoding.Latin1.GetString(bytes));   // nothing from the catalog's text goes into it
        Assert.Equal(bytes, await (await c.GetAsync($"/api/v1/catalog/images/{image.Id}")).Content.ReadAsByteArrayAsync());   // the same id, the same picture
        Assert.DoesNotContain(f.Portal.Calls, x => x.PathAndQuery.Contains("/catalog/images/"));                           // and no portal
        Assert.NotEqual(bytes, await (await c.GetAsync($"/api/v1/catalog/images/{images.Last().Id}")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_placeholder_too_is_only_for_visible_listings()
    {
        using var f = new TestFactory();
        await Sync(f);
        var id = Row(f, ListingId("babka-chocolate")).Images.First().Id;
        var c = f.CreateClient();
        var listing = ListingId("babka-chocolate");
        await Event(f, c, "listing.unlisted", listing, Row(f, listing).Version);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/catalog/images/{id}")).StatusCode);
    }
}
