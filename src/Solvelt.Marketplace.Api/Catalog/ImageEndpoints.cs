using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Catalog;

public static class ImageEndpoints
{
    /// <summary>How long a client or the web tier may keep an image. Short, because an unlisted listing's images must stop being served soon (the endpoint re-checks visibility on every request that reaches it).</summary>
    public const string CacheControl = "public, max-age=300";

    public static RouteGroupBuilder MapImages(this RouteGroupBuilder g)
    {
        g.MapGet("/images/{imageId:guid}", Image);
        return g;
    }

    /// <summary>
    /// <c>GET /api/v1/catalog/images/{imageId}</c>: the original bytes of an image of a <b>visible</b> listing, for the web tier's resizer (the browser never calls this).
    /// Anything else, whatever the reason (unknown id, hidden or unlisted listing, suspended seller, an image the portal no longer has), is the same 404.
    /// </summary>
    private static async Task<IResult> Image(Guid imageId, HttpContext http, MarketplaceDb db, ICatalogImageSource source, CancellationToken ct)
    {
        var size = await CatalogEndpoints.Visible(db).SelectMany(l => l.Images).Where(i => i.Id == imageId).Select(i => new { i.Width, i.Height }).FirstOrDefaultAsync(ct);
        if (size is null) return Problems.NotFound("Image not found.");
        var fetched = await source.GetAsync(imageId, size.Width, size.Height, ct);
        switch (fetched.Status)
        {
            case ImageFetchStatus.NotFound: return Problems.NotFound("Image not found.");
            case ImageFetchStatus.Invalid: return Problems.Json(502, "bad_image", "The image could not be served.");
            case ImageFetchStatus.Unavailable:
                http.Response.Headers.RetryAfter = "30";
                return Problems.Json(503, "image_unavailable", "Images are temporarily unavailable.");
        }
        var image = fetched.Image!;
        var etag = image.ETag;
        var res = http.Response;
        res.Headers.CacheControl = CacheControl;
        res.Headers.ETag = etag;
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers.ContentDisposition = "inline";
        res.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";           // a picture, never a page: whatever it holds cannot run or load anything
        var tags = http.Request.Headers.IfNoneMatch.ToString().Split(',').Select(t => t.Trim()).ToArray();
        if (tags.Contains("*") || tags.Select(t => t.Replace("W/", "")).Contains(etag)) return Results.StatusCode(304);   // "*" matches any current representation (RFC 9110 13.1.2)
        return Results.Bytes(image.Bytes, image.ContentType);
    }
}
