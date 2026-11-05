using System.Globalization;
using System.Text.Json;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// The portal's signed catalog feed (docs/CATALOG.md in the seller portal) as an <see cref="ICatalogSource"/>. This file is the only place that knows the
/// portal's document shape: it reads the raw JSON the signed client returns and maps it onto the read model's own snapshot types, so a contract change
/// touches this file and nothing else. Images are not fetched here (their bytes are served by a later, server-side-only route through the same client).
/// </summary>
public sealed class HttpCatalogSource(ISignedPortalClient portal, TimeProvider time, ILogger<HttpCatalogSource> log) : ICatalogSource
{
    /// <summary>The portal's page limit is 1..200; a page that is bigger than the response cap is retried with half the limit.</summary>
    public const int MaxPageSize = 100;
    private const int MaxBrandPages = 200;
    /// <summary>Search keyword caps: the portal sends whatever the seller typed. Per term, number of terms, and characters over all terms.</summary>
    public const int MaxSearchTermLength = 60, MaxSearchTerms = 30, MaxSearchTermsTotal = 600;
    public const int MaxHighlights = 10, MaxQuestions = 20;

    public async Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct)
    {
        var size = Math.Clamp(limit, 1, MaxPageSize);
        while (true)
        {
            try
            {
                var root = Ok(await portal.GetCatalogListingsAsync(since, size, ct), "listings");
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The catalog feed page has no items array.");
                var listings = new List<ListingSnapshot>(); var tombstones = new List<ListingTombstone>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("listed", out var l) && l.ValueKind == JsonValueKind.False)
                    {
                        if (Tombstone(item) is { } t) tombstones.Add(t); else log.LogWarning("Skipping an unreadable catalog tombstone in the feed");
                    }
                    else if (Map(item, time.GetUtcNow()) is { } s) listings.Add(s);
                    // one document we cannot read never blocks the page: it is logged by id inside Map and the cursor moves on
                }
                var next = root.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : since;
                var more = root.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
                return new ListingPage(listings, next, more, tombstones);
            }
            catch (PortalResponseTooLargeException) when (size > 1) { size = Math.Max(1, size / 2); }
        }
    }

    public async Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct)
    {
        var result = await portal.GetCatalogListingAsync(id, ct);
        if (result.Status == 404) return null;                       // not visible: unknown, draft, hidden, moderated and unlisted all look the same
        var root = Ok(result, "listing");
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("listed", out var l) && l.ValueKind == JsonValueKind.False) return null;
        return Map(root, time.GetUtcNow()) ?? throw new InvalidDataException("The portal's listing document could not be read.");   // not "removed": retried with backoff
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct)
    {
        var root = Ok(await portal.GetCatalogCategoriesAsync(ct), "categories");
        var list = new List<CategoryDto>();
        foreach (var e in Items(root))
        {
            if (Guid_(e, "categoryId") is not { } id || Str(e, "slug") is not { } slug || Str(e, "name") is not { } name) continue;
            list.Add(new CategoryDto(id, Guid_(e, "parentId"), slug, name, Int(e, "sortOrder") ?? 0));
        }
        return list;
    }

    public async Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct)
    {
        var list = new List<BrandDto>();
        Guid? since = null;
        for (var page = 0; page < MaxBrandPages; page++)
        {
            var root = Ok(await portal.GetCatalogBrandsAsync(since, 200, ct), "brands");
            Guid? last = null;
            foreach (var e in Items(root))
            {
                if (Guid_(e, "brandId") is not { } id) continue;
                last = id;
                if (Guid_(e, "sellerId") is not { } sellerId || Str(e, "slug") is not { } slug || Str(e, "name") is not { } name) continue;
                list.Add(new BrandDto(id, sellerId, slug, name, null));
            }
            var more = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
            if (!more || last is null) return list;
            since = last;
        }
        log.LogWarning("The portal has more than {Max} pages of brands; the rest are ignored this run", MaxBrandPages);
        return list;
    }

    // ---------- mapping (the one place that knows the portal's document shape) ----------

    /// <summary>The snapshot for one feed document, or null (logged by listing id only) when it cannot be read. Sale prices are judged on <paramref name="now"/> (our clock).</summary>
    public ListingSnapshot? Map(JsonElement d, DateTimeOffset now)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var id = Guid_(d, "listingId");
        try
        {
            var type = Str(d, "type");
            if (id is not { } listingId || Long(d, "version") is not { } version || Str(d, "title") is not { } title || type is not ("product" or "service")) return Fail(id, "required field");
            if (!d.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Object || Long(price, "amountMinor") is not { } amount || Str(price, "currency") is not { } currency) return Fail(id, "price");
            if (!d.TryGetProperty("seller", out var seller) || Guid_(seller, "sellerId") is not { } sellerId) return Fail(id, "seller");
            if (!d.TryGetProperty("brand", out var brand) || Guid_(brand, "brandId") is not { } brandId) return Fail(id, "brand");
            if (!d.TryGetProperty("category", out var category) || Guid_(category, "id") is not { } categoryId) return Fail(id, "category");

            // sale and compare-at, on our clock: a window that has not started or has ended does not apply
            var compareAt = Long(price, "compareAtMinor");
            long? saleAmount = null;
            if (price.TryGetProperty("sale", out var sale) && sale.ValueKind == JsonValueKind.Object && Long(sale, "amountMinor") is { } sa && sa >= 0 && sa < amount
                && (Time(sale, "startsAt") is not { } starts || starts <= now) && (Time(sale, "endsAt") is not { } ends || now < ends))
                saleAmount = sa;
            var listingState = State(d);
            var weight = Int(d, "weightGrams") is > 0 and var wg ? wg : (int?)null;      // C7: null or a non-positive weight is "unknown", never 0 (O-15)
            var variants = new List<VariantDto>();
            if (d.TryGetProperty("variants", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                {
                    if (Guid_(v, "variantId") is not { } vid) continue;
                    var own = v.TryGetProperty("price", out var vp) ? Long(vp, "amountMinor") : null;
                    var onListingPrice = own is null || own == amount;                      // a variant with its own price is not part of the listing's sale
                    var cents = onListingPrice ? saleAmount ?? amount : own!.Value;
                    long? was = onListingPrice ? (saleAmount is not null ? Math.Max(amount, compareAt ?? 0) : compareAt) : null;
                    var values = v.TryGetProperty("values", out var vv) && vv.ValueKind == JsonValueKind.Array
                        ? string.Join(" / ", vv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)) : "";
                    variants.Add(new VariantDto(vid, values.Length > 0 ? values : "Standard", cents, was > cents ? was : null, Available(State(v) ?? listingState), WeightGrams: Int(v, "weightGrams") is > 0 and var vw ? vw : weight));   // a variant without its own weight carries the product's
                }
            if (variants.Count == 0)                                                      // no variants: one buyable line on the listing price (IsDefault: its id is the listing id, never a portal variant id)
                variants.Add(new VariantDto(listingId, "Standard", saleAmount ?? amount, (saleAmount is not null ? Math.Max(amount, compareAt ?? 0) : compareAt) is { } w && w > (saleAmount ?? amount) ? w : null, Available(listingState), IsDefault: true, WeightGrams: weight));

            var images = new List<ImageDto>();
            if (d.TryGetProperty("images", out var ims) && ims.ValueKind == JsonValueKind.Array)
                foreach (var im in ims.EnumerateArray().OrderByDescending(x => x.TryGetProperty("isMain", out var m) && m.ValueKind == JsonValueKind.True).ThenBy(x => Int(x, "sortOrder") ?? 0))
                    if (Guid_(im, "imageId") is { } imageId) images.Add(new ImageDto(imageId, Int(im, "width") ?? 0, Int(im, "height") ?? 0, Str(im, "alt") ?? ""));

            var description = Str(d, "description") ?? "";                               // the Markdown source: never descriptionHtml, so no portal HTML reaches our pages
            var bullets = Strings(d, "bullets").Take(MaxHighlights).ToList();
            var summary = bullets.FirstOrDefault() ?? FirstLine(description);

            var attributes = new List<AttributeDto>();
            foreach (var dv in Delivery(d, type)) attributes.Add(new AttributeDto("delivery", dv));
            if (d.TryGetProperty("attributes", out var at) && at.ValueKind == JsonValueKind.Array)
                foreach (var a in at.EnumerateArray())
                    if (Str(a, "key") is { } key && Str(a, "value") is { } value) attributes.Add(new AttributeDto(key, Str(a, "unit") is { } unit ? $"{value} {unit}" : value));
            if (Str(d, "condition") is { } cond && cond != "new") attributes.Add(new AttributeDto("condition", Str(d, "conditionNote") is { } note ? $"{cond}: {note}" : cond));
            if (listingState is "low_stock" or "backorder") attributes.Add(new AttributeDto("availability", listingState.Replace('_', '-')));
            if (d.TryGetProperty("policies", out var pol) && pol.ValueKind == JsonValueKind.Object)
            {
                if (Int(pol, "returnsDays") is { } rd) attributes.Add(new AttributeDto("returns-days", rd.ToString(CultureInfo.InvariantCulture)));
                if (Str(pol, "returnsCondition") is { } rc) attributes.Add(new AttributeDto("returns-condition", rc));
                if (Int(pol, "warrantyMonths") is { } wm) attributes.Add(new AttributeDto("warranty-months", wm.ToString(CultureInfo.InvariantCulture)));
                if (Str(pol, "warrantyNote") is { } wn) attributes.Add(new AttributeDto("warranty-note", wn));
                if (Int(pol, "handlingDays") is { } hd) attributes.Add(new AttributeDto("handling-days", hd.ToString(CultureInfo.InvariantCulture)));
            }
            if (type == "service" && d.TryGetProperty("service", out var svc) && svc.ValueKind == JsonValueKind.Object)
            {
                if (Str(svc, "pricingModel") is { } pm) attributes.Add(new AttributeDto("pricing-model", pm));
                if ((Int(svc, "defaultDurationMinutes") ?? Int(svc, "minMinutes")) is { } mins) attributes.Add(new AttributeDto("duration", $"{mins} min"));
                if (Str(svc, "bookingMode") is { } bm) attributes.Add(new AttributeDto("booking-mode", bm));
            }
            foreach (var b in bullets) attributes.Add(new AttributeDto("highlight", b));

            var faqs = new List<FaqDto>();
            if (d.TryGetProperty("faqs", out var fq) && fq.ValueKind == JsonValueKind.Array)
                foreach (var f in fq.EnumerateArray()) if (Str(f, "question") is { } q) faqs.Add(new FaqDto(q, Str(f, "answer") ?? ""));
            if (d.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (var f in qs.EnumerateArray().Take(MaxQuestions)) if (Str(f, "question") is { } q && Str(f, "answer") is { } ans) faqs.Add(new FaqDto(q, ans));   // published answers only; the asker is never named

            var updated = Time(d, "updatedAt") ?? now;
            return new ListingSnapshot(listingId, version, type, "Active", title, summary, description,
                new SellerRef(sellerId, Str(seller, "displayName") ?? "", true), new BrandRef(brandId, Str(brand, "name") ?? ""), categoryId, currency.ToUpperInvariant(),
                variants, images, attributes, faqs, Time(d, "publishedAt") ?? updated, updated, SearchTerms(d), Dimensions(d), d.TryGetProperty("demo", out var demoFlag) && demoFlag.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            return Fail(id, ex.GetType().Name);
        }
    }

    /// <summary>The seller's keywords, cut per term, in number and in total (the portal sends what was typed).</summary>
    private static IReadOnlyList<string> SearchTerms(JsonElement d)
    {
        var terms = new List<string>(); var total = 0;
        foreach (var raw in Strings(d, "searchTerms"))
        {
            if (terms.Count >= MaxSearchTerms) break;
            var t = SearchText.Clip(raw, MaxSearchTermLength);
            if (t.Length == 0) continue;
            if (total + t.Length > MaxSearchTermsTotal) { t = SearchText.Clip(t, MaxSearchTermsTotal - total); if (t.Length == 0) break; }
            terms.Add(t); total += t.Length;
        }
        return terms;
    }

    /// <summary>
    /// Delivery options as the read model's <c>delivery</c> attribute (ships, local, at-home, online, at-seller). The portal's explicit <c>delivery</c> object
    /// (C5) wins; only a document without it (an older portal) falls back to the service's own location flags, and products get nothing guessed.
    /// </summary>
    private static IEnumerable<string> Delivery(JsonElement d, string type)
    {
        if (d.TryGetProperty("delivery", out var del) && del.ValueKind == JsonValueKind.Object)
        {
            if (del.TryGetProperty("ships", out var s) && s.ValueKind == JsonValueKind.True) yield return "ships";
            if (del.TryGetProperty("localDelivery", out var l) && l.ValueKind == JsonValueKind.True) yield return "local";
            foreach (var loc in Strings(del, "serviceLocations"))
            {
                var v = loc switch { "online" => "online", "customer" => "at-home", "seller" => "at-seller", _ => null };
                if (v is not null) yield return v;
            }
            yield break;
        }
        if (type == "service" && d.TryGetProperty("service", out var svc) && svc.ValueKind == JsonValueKind.Object)
        {
            if (svc.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True) yield return "online";
            if (svc.TryGetProperty("atCustomer", out var c) && c.ValueKind == JsonValueKind.True) yield return "at-home";
            if (svc.TryGetProperty("atSeller", out var a) && a.ValueKind == JsonValueKind.True) yield return "at-seller";
        }
    }

    private static ListingTombstone? Tombstone(JsonElement e)
        => Guid_(e, "listingId") is { } id && Long(e, "version") is { } v && v >= 1 ? new ListingTombstone(id, v, Str(e, "reason") ?? "unlisted") : null;   // any reason, known or not, means "unlisted"

    private ListingSnapshot? Fail(Guid? id, string why) { log.LogWarning("Skipping a catalog document that could not be read ({Why}) for listing {ListingId}", why, id); return null; }

    // ---------- small readers ----------
    private static JsonElement Ok(PortalResult<JsonElement> r, string what)
    {
        if (r.Status is < 200 or >= 300) throw new PortalUnavailableException($"The portal catalog {what} call answered {r.Status}.");
        return r.Value.ValueKind == JsonValueKind.Undefined ? throw new InvalidDataException($"The portal catalog {what} answer is not JSON.") : r.Value;
    }
    private static IEnumerable<JsonElement> Items(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object) : [];
    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static IEnumerable<string> Strings(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0) : [];
    private static long? Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    /// <summary>C7: all three sides, each a positive whole number of millimetres, or null.</summary>
    private static DimensionsMm? Dimensions(JsonElement d)
        => d.TryGetProperty("dimensionsMm", out var m) && m.ValueKind == JsonValueKind.Object && Int(m, "l") is > 0 and var l && Int(m, "w") is > 0 and var w && Int(m, "h") is > 0 and var h ? new DimensionsMm(l, w, h) : null;
    private static int? Int(JsonElement e, string name) => Long(e, name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
    private static Guid? Guid_(JsonElement e, string name) => Str(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
    private static DateTimeOffset? Time(JsonElement e, string name) => Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    private static string? State(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("availability", out var a) ? Str(a, "state") : null;
    /// <summary>Everything but out_of_stock can be bought (in_stock, low_stock, backorder, and a service's available); an unknown state is not guessed as available.</summary>
    private static bool Available(string? state) => state is "in_stock" or "low_stock" or "backorder" or "available";
    private static string FirstLine(string description)
    {
        foreach (var line in description.Split('\n')) { var t = line.Trim().TrimStart('#', '*', '-', '>', ' ').Trim(); if (t.Length > 0) return SearchText.Clip(t, SnapshotLimits.Summary); }
        return "";
    }
}
