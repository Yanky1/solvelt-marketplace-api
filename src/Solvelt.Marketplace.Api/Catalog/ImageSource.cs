using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>The bytes of one image. The strong ETag is computed once, when the object is made (the instances are cached, so a request never hashes the bytes again).</summary>
public sealed record ImageBytes(byte[] Bytes, string ContentType)
{
    public string ETag { get; } = $"\"{Convert.ToHexStringLower(SHA256.HashData(Bytes))[..32]}\"";
}
public enum ImageFetchStatus { Ok, NotFound, Unavailable, Invalid }
public sealed record ImageFetch(ImageFetchStatus Status, ImageBytes? Image = null);

/// <summary>Where the bytes of a catalog image come from: the seller portal (signed, server-side only) or, with the seed fixture, a generated placeholder.</summary>
public interface ICatalogImageSource
{
    Task<ImageFetch> GetAsync(Guid imageId, int width, int height, CancellationToken ct);
}

/// <summary>
/// The portal's stored original (<c>GET /catalog/images/{id}</c>, signed like every portal call). Only raster formats are passed on: whatever else the portal
/// says (or nothing) is <see cref="ImageFetchStatus.Invalid"/>, so an image endpoint can never serve a script or a document under our origin. Answers are
/// kept in a small bounded memory cache (an image is immutable by id; the endpoint still checks that its listing is visible before it asks).
/// </summary>
public sealed class PortalImageSource(IServiceScopeFactory scopes, ILogger<PortalImageSource> log) : ICatalogImageSource
{
    public static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp", "image/gif", "image/avif" };
    public const long CacheBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = CacheBytes });

    public async Task<ImageFetch> GetAsync(Guid imageId, int width, int height, CancellationToken ct)
    {
        if (cache.TryGetValue(imageId, out ImageBytes? hit) && hit is not null) return new(ImageFetchStatus.Ok, hit);
        PortalImageResult res;
        try { using var scope = scopes.CreateScope(); res = await scope.ServiceProvider.GetRequiredService<ISignedPortalClient>().GetCatalogImageAsync(imageId, ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException)
        {
            log.LogWarning("Portal image {ImageId} could not be fetched ({ExceptionType})", imageId, ex.GetType().Name);   // never the message
            return new(ex is PortalResponseTooLargeException ? ImageFetchStatus.Invalid : ImageFetchStatus.Unavailable);
        }
        if (res.Status == 404) return new(ImageFetchStatus.NotFound);
        if (!res.IsSuccess) return new(ImageFetchStatus.Unavailable);
        if (res.ContentType is null || !AllowedTypes.Contains(res.ContentType) || res.Bytes!.Length == 0) return new(ImageFetchStatus.Invalid);
        var image = new ImageBytes(res.Bytes, res.ContentType.ToLowerInvariant());
        cache.Set(imageId, image, new MemoryCacheEntryOptions { Size = image.Bytes.Length, AbsoluteExpirationRelativeToNow = CacheFor });
        return new(ImageFetchStatus.Ok, image);
    }
}

/// <summary>
/// For the seed fixture (no portal): a plain coloured picture of the right shape, made from the image id alone (no text from the catalog goes into it).
/// It is a PNG, not an SVG: the web resizer only accepts raster pictures (a seller's SVG is refused on purpose), so an SVG placeholder made every fixture
/// image a 502 on the site. Drawn with SkiaSharp, which the API already needs for message attachments.
/// </summary>
public sealed class PlaceholderImageSource : ICatalogImageSource
{
    private const int MaxSide = 1200;                                              // the web shows these at most ~1200 px wide; a bigger fixture size is drawn smaller

    public Task<ImageFetch> GetAsync(Guid imageId, int width, int height, CancellationToken ct)
    {
        var h = SHA256.HashData(imageId.ToByteArray());
        var w = Math.Clamp(width, 1, MaxSide); var ht = Math.Clamp(height, 1, MaxSide);
        SKColor Colour(int i, float light) => SKColor.FromHsl(h[i] * 360f / 256f, 35f, light);
        using var surface = SKSurface.Create(new SKImageInfo(w, ht, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var paint = new SKPaint { IsAntialias = false, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, ht), [Colour(0, 82), Colour(1, 68)], SKShaderTileMode.Clamp) };
        surface.Canvas.DrawRect(0, 0, w, ht, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Task.FromResult(new ImageFetch(ImageFetchStatus.Ok, new ImageBytes(data.ToArray(), "image/png")));
    }
}
