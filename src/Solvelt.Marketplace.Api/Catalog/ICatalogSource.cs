namespace Solvelt.Marketplace.Api.Catalog;

// PROVISIONAL: the shapes below are our request C1 (CONTRACT.md §2), not a portal contract yet. They are the only place that has to change
// when the portal publishes its real catalog feed; the read model, the sync job and the endpoints depend on this file, not on the portal.

public sealed record SellerRef(Guid Id, string DisplayName, bool Active);
public sealed record BrandRef(Guid Id, string Name);
/// <summary><see cref="IsDefault"/>: a synthetic line for a listing the portal sent with no variants. Its <see cref="Id"/> is the listing id, NOT a portal variant id: an order line for it sends productId with variantId null. <see cref="WeightGrams"/>: shipping weight in grams (portal C7), null when unknown, never 0.</summary>
public sealed record VariantDto(Guid Id, string Name, long PriceCents, long? CompareAtCents, bool Available, bool IsDefault = false, int? WeightGrams = null);
/// <summary>Product size in millimetres (portal C7); only ever built when all three sides are known.</summary>
public sealed record DimensionsMm(int L, int W, int H);
public sealed record ImageDto(Guid Id, int Width, int Height, string Alt);
public sealed record AttributeDto(string Key, string Value);
public sealed record FaqDto(string Question, string Answer);

/// <summary>One listing (product or service) with everything the read model stores. <c>SearchTerms</c> (seller keywords, optional; M2-01) feed the search index. <see cref="Version"/> is the portal's cursor.</summary>
public sealed record ListingSnapshot(
    Guid Id, long Version, string Type, string Status, string Title, string Summary, string Description,
    SellerRef Seller, BrandRef Brand, Guid CategoryId, string Currency,
    IReadOnlyList<VariantDto> Variants, IReadOnlyList<ImageDto> Images, IReadOnlyList<AttributeDto> Attributes, IReadOnlyList<FaqDto> Faqs,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? SearchTerms = null, DimensionsMm? Dimensions = null, bool Demo = false);

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Slug, string Name, int SortOrder);
public sealed record BrandDto(Guid Id, Guid SellerId, string Slug, string Name, Guid? LogoImageId);

/// <summary>A page of the feed in ascending (version, id) order. <see cref="NextCursor"/> is opaque; pass it back as <c>since</c>.</summary>
public sealed record ListingPage(IReadOnlyList<ListingSnapshot> Items, string? NextCursor, bool HasMore, IReadOnlyList<ListingTombstone>? Tombstones = null);

/// <summary>The feed says a listing we may know is no longer public (portal tombstone). <see cref="Reason"/> is informational (an unknown reason is treated as "unlisted").</summary>
public sealed record ListingTombstone(Guid Id, long Version, string Reason)
{
    /// <summary>The feed named the listing but its version could not be read: hide the listing at the version we hold (a newer snapshot still re-lists it).</summary>
    public const long UnknownVersion = 0;
}

/// <summary>Where listings come from: the fixture today, the portal's signed catalog feed (C1) later.</summary>
public interface ICatalogSource
{
    /// <summary>Listings with a (version, id) after <paramref name="since"/> (null = from the start), oldest first.</summary>
    Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct);
    /// <summary>One listing, or null when the portal no longer has it.</summary>
    Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct);
}

/// <summary>The source for environments with no catalog feed configured: an empty catalog.</summary>
public sealed class EmptyCatalogSource : ICatalogSource
{
    public Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct) => Task.FromResult(new ListingPage([], null, false));
    public Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct) => Task.FromResult<ListingSnapshot?>(null);
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CategoryDto>>([]);
    public Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BrandDto>>([]);
}

public static class CatalogSourceSelection
{
    /// <summary>The configured source name; when none is configured, the seed fixture in Development/Testing and the portal's signed feed (Http) everywhere else.</summary>
    public static string Resolve(string? configured, bool devLike)
        => string.IsNullOrWhiteSpace(configured) ? (devLike ? "Fixture" : "Http") : configured.Trim();
}
