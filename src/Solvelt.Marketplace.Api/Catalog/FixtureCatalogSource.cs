using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// A realistic, deterministic seed (about 40 visible listings across 6 sellers, 10 brands and 13 categories, including kosher food and services)
/// behind <see cref="ICatalogSource"/>. It can also be changed at run time (<see cref="Upsert"/>, <see cref="Remove"/>) so tests and dev can
/// exercise updates, unlisting and stale pages. Ids are derived from names, so they are the same on every start.
/// </summary>
public sealed class FixtureCatalogSource : ICatalogSource
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ListingSnapshot> _listings;
    private readonly List<CategoryDto> _categories;
    private readonly List<BrandDto> _brands;
    private long _nextVersion;

    public FixtureCatalogSource() : this(FixtureCatalogData.Build()) { }

    public FixtureCatalogSource(FixtureCatalogData data)
    {
        _listings = data.Listings.ToDictionary(l => l.Id);
        _categories = [.. data.Categories];
        _brands = [.. data.Brands];
        _nextVersion = _listings.Values.Max(l => l.Version) + 1;
    }

    public static Guid Id(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("solvelt-fixture:" + name)));

    /// <summary>Applies a change and gives the listing the next version, as the portal does.</summary>
    public ListingSnapshot Upsert(Guid id, Func<ListingSnapshot, ListingSnapshot> change)
    {
        lock (_lock)
        {
            var updated = change(_listings[id]) with { Id = id, Version = _nextVersion++ };
            _listings[id] = updated;
            return updated;
        }
    }

    public ListingSnapshot Add(ListingSnapshot listing)
    {
        lock (_lock)
        {
            var added = listing with { Version = _nextVersion++ };
            _listings[added.Id] = added;
            return added;
        }
    }

    /// <summary>The portal no longer has it: <see cref="GetListingAsync"/> answers null and it leaves the feed.</summary>
    public void Remove(Guid id) { lock (_lock) _listings.Remove(id); }

    /// <summary>A reference-data change (the brand feed): the next sync sees the new name.</summary>
    public void RenameBrand(Guid id, string name) { lock (_lock) { var i = _brands.FindIndex(b => b.Id == id); _brands[i] = _brands[i] with { Name = name }; } }

    public IReadOnlyList<ListingSnapshot> All() { lock (_lock) return _listings.Values.OrderBy(l => l.Version).ThenBy(l => l.Id).ToList(); }

    public Task<ListingPage> GetListingsAsync(string? since, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        (long Version, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(since))
        {
            var parts = since.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var v) || !Guid.TryParse(parts[1], out var g))
                throw new ArgumentException("Invalid cursor.", nameof(since));
            after = (v, g);
        }
        lock (_lock)
        {
            var ordered = _listings.Values.OrderBy(l => l.Version).ThenBy(l => l.Id).Where(l => after is null || l.Version > after.Value.Version || (l.Version == after.Value.Version && l.Id.CompareTo(after.Value.Id) > 0)).ToList();
            var items = ordered.Take(limit).ToList();
            var more = ordered.Count > items.Count;
            var next = items.Count == 0 ? since : $"{items[^1].Version}:{items[^1].Id}";
            return Task.FromResult(new ListingPage(items, next, more));
        }
    }

    public Task<ListingSnapshot?> GetListingAsync(Guid id, CancellationToken ct)
    {
        lock (_lock) return Task.FromResult(_listings.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CategoryDto>>(_categories);
    public Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BrandDto>>(_brands);
}

public sealed record FixtureCatalogData(IReadOnlyList<ListingSnapshot> Listings, IReadOnlyList<CategoryDto> Categories, IReadOnlyList<BrandDto> Brands)
{
    private static Guid Id(string n) => FixtureCatalogSource.Id(n);

    // seller key -> (display name, active)
    private static readonly (string Key, string Name, bool Active)[] Sellers =
    [
        ("mazel", "Mazel Bakery", true), ("glatt", "Glatt Gourmet", true), ("cellar", "Kosher Wine Cellar", true),
        ("judaica", "Brooklyn Judaica", true), ("hudson", "Hudson Home Services", true), ("closed", "Closed Corner Shop", false),
    ];

    // brand key -> (seller key, name)
    private static readonly (string Key, string Seller, string Name)[] BrandRows =
    [
        ("mazel-bakery", "mazel", "Mazel Bakery"), ("challah-corner", "mazel", "Challah Corner"),
        ("glatt-gourmet", "glatt", "Glatt Gourmet"), ("shabbos-grill", "glatt", "Shabbos Grill"),
        ("hudson-valley-vines", "cellar", "Hudson Valley Vines"),
        ("brooklyn-judaica", "judaica", "Brooklyn Judaica"), ("shabbat-table", "judaica", "Shabbat Table Co"),
        ("hudson-home-pros", "hudson", "Hudson Home Pros"), ("torah-tutors", "hudson", "Torah Tutors NYC"),
        ("closed-corner", "closed", "Closed Corner"),
    ];

    // category key -> (parent key, name, sort)
    private static readonly (string Key, string? Parent, string Name)[] CategoryRows =
    [
        ("food-drink", null, "Food & Drink"), ("bakery", "food-drink", "Bakery"), ("meat-poultry", "food-drink", "Meat & Poultry"),
        ("wine-spirits", "food-drink", "Wine & Grape Juice"), ("pantry", "food-drink", "Pantry & Snacks"),
        ("home-judaica", null, "Home & Judaica"), ("candlesticks", "home-judaica", "Candles & Candlesticks"),
        ("kitchen-tableware", "home-judaica", "Kitchen & Tableware"), ("books", "home-judaica", "Books"),
        ("services", null, "Services"), ("cleaning", "services", "Cleaning"), ("repairs", "services", "Repairs & Handyman"), ("lessons", "services", "Lessons & Tutoring"),
    ];

    // slug | title | seller | brand | category | type | summary | variants ("name=cents;!name=cents" ! = unavailable) | attributes ("k:v;k:v") | images | status
    private const string Rows = """
        challah-classic|Classic Braided Challah|mazel|challah-corner|bakery|product|Hand-braided, baked fresh every Thursday.|Small=650;Large=950|kosher-certification:OU;kosher-status:pareve;weight:550 g|3|Active
        challah-whole-wheat|Whole Wheat Challah|mazel|challah-corner|bakery|product|Stone-ground whole wheat dough with a touch of honey.|Regular=750|kosher-certification:OU;kosher-status:pareve|2|Active
        challah-rolls-dozen|Challah Rolls, Dozen|mazel|challah-corner|bakery|product|Twelve soft rolls for the Shabbat table.|Dozen=1450|kosher-certification:OU;kosher-status:pareve|2|Active
        rugelach-assorted|Assorted Rugelach, 1 lb|mazel|mazel-bakery|bakery|product|Chocolate, cinnamon walnut and apricot.|1 lb=1800;2 lb=3400|kosher-certification:OU;kosher-status:dairy|3|Active
        babka-chocolate|Chocolate Babka|mazel|mazel-bakery|bakery|product|Twisted yeast cake with dark chocolate filling.|Loaf=1600|kosher-certification:OU;kosher-status:dairy|2|Active
        black-and-white-cookies|Black and White Cookies, 6 pack|mazel|mazel-bakery|bakery|product|New York classic, fondant and chocolate icing.|6 pack=1400|kosher-certification:OU;kosher-status:pareve|2|Active
        honey-cake|Honey Cake for Rosh Hashanah|mazel|mazel-bakery|bakery|product|Moist spiced honey cake, baked to order.|!Loaf=1550|kosher-certification:OU;kosher-status:pareve|2|Active
        hamantaschen-box|Hamantaschen Gift Box|mazel|mazel-bakery|bakery|product|Poppy seed, prune and raspberry, boxed for Purim.|Box of 12=1900|kosher-certification:OU;kosher-status:dairy|3|Active
        brisket-first-cut|Glatt Brisket, First Cut (per lb)|glatt|glatt-gourmet|meat-poultry|product|Trimmed first cut, vacuum sealed.|Per lb=1999|kosher-certification:Star-K;kosher-status:meat;glatt:yes|2|Active
        roasting-chicken|Whole Roasting Chicken|glatt|glatt-gourmet|meat-poultry|product|Air-chilled, about 4 lb.|About 4 lb=1299|kosher-certification:Star-K;kosher-status:meat;glatt:yes|2|Active
        kosher-hot-dogs|All-Beef Hot Dogs, 8 count|glatt|shabbos-grill|meat-poultry|product|Skinless, smoked, ready for the grill.|8 count=899|kosher-certification:OK;kosher-status:meat|1|Active
        beef-burgers-glatt|Glatt Beef Burgers, 6 pack|glatt|shabbos-grill|meat-poultry|product|Quarter-pound patties.|6 pack=1499|kosher-certification:OK;kosher-status:meat;glatt:yes|2|Active
        lamb-chops|Rib Lamb Chops (per lb)|glatt|glatt-gourmet|meat-poultry|product|Frenched rib chops, Badatz certified.|Per lb=3299|kosher-certification:Badatz;kosher-status:meat;glatt:yes|2|Active
        chicken-cutlets|Thin-Sliced Chicken Cutlets, 2 lb|glatt|glatt-gourmet|meat-poultry|product|Ready to bread and fry.|2 lb=1099|kosher-certification:Star-K;kosher-status:meat|1|Active
        cabernet-mevushal|Cabernet Sauvignon, Mevushal|cellar|hudson-valley-vines|wine-spirits|product|Medium-bodied red from the Hudson Valley.|750 ml=2499;1.5 L=4600|kosher-certification:OU;kosher-status:mevushal;vintage:2022|3|Active
        chardonnay-kosher|Chardonnay, Kosher for Passover|cellar|hudson-valley-vines|wine-spirits|product|Crisp, unoaked white.|750 ml=2199|kosher-certification:OU;kosher-for-passover:yes;vintage:2023|2|Active
        grape-juice-concord|Concord Grape Juice, 64 oz|cellar|hudson-valley-vines|wine-spirits|product|Not from concentrate.|64 oz=799|kosher-certification:OU;kosher-status:pareve|1|Active
        sparkling-moscato|Sparkling Moscato|cellar|hudson-valley-vines|wine-spirits|product|Lightly sweet and sparkling, for kiddush.|!750 ml=1899|kosher-certification:OU;kosher-status:mevushal|2|Active
        halva-pistachio|Pistachio Halva, 14 oz|judaica|shabbat-table|pantry|product|Sesame halva with whole pistachios.|14 oz=900|kosher-certification:OU;kosher-status:pareve|2|Active
        tahini-roasted|Roasted Tahini, 16 oz|judaica|shabbat-table|pantry|product|Stone-ground, single ingredient.|16 oz=1100|kosher-certification:OU;kosher-status:pareve|1|Active
        israeli-couscous|Israeli Couscous, 1 lb|judaica|shabbat-table|pantry|product|Toasted pearl couscous.|1 lb=600|kosher-certification:OU;kosher-status:pareve|1|Active
        shabbat-candlesticks-silver|Silver-Plated Shabbat Candlesticks|judaica|brooklyn-judaica|candlesticks|product|Classic pair, 10 inches tall.|Pair=8900|material:silver-plated brass;height:10 in|3|Active
        shabbat-candles-72|Shabbat Candles, 72 count|judaica|brooklyn-judaica|candlesticks|product|Four-hour burn, white.|72 count=899|burn-time:4 hours;color:white|1|Active
        travel-candlesticks|Folding Travel Candlesticks|judaica|brooklyn-judaica|candlesticks|product|Fold flat to fit a carry-on.|Pair=3400|material:brushed steel|2|Active
        kiddush-cup-pewter|Pewter Kiddush Cup|judaica|shabbat-table|kitchen-tableware|product|Hand-finished, holds 6 oz.|6 oz=4200|material:pewter;capacity:6 oz|3|Active
        challah-board-olive-wood|Olive Wood Challah Board|judaica|shabbat-table|kitchen-tableware|product|Hand-carved, with a matching knife.|Board and knife=3900|material:olive wood;size:14 in|2|Active
        netilat-yadayim-cup|Netilat Yadayim Washing Cup|judaica|shabbat-table|kitchen-tableware|product|Two-handled stainless steel cup.|Cup=2800|material:stainless steel|1|Active
        haggadah-illustrated|Illustrated Family Haggadah|judaica|brooklyn-judaica|books|product|Hebrew and English with commentary and illustrations.|Hardcover=2400|pages:160;language:Hebrew, English|2|Active
        deep-house-cleaning|Deep House Cleaning|hudson|hudson-home-pros|cleaning|service|Top-to-bottom clean by a two-person team.|2 bedrooms=18000;3 bedrooms=24000;4 bedrooms=30000|service-area:Brooklyn, Manhattan, Queens;duration:4 to 6 hours;insured:yes|3|Active
        pre-pesach-kitchen-cleaning|Pre-Pesach Kitchen Cleaning|hudson|hudson-home-pros|cleaning|service|Chametz-free kitchen cleaning, supervised on request.|Standard kitchen=25000|service-area:Brooklyn, Queens;duration:5 hours;insured:yes|2|Active
        window-cleaning|Window Cleaning|hudson|hudson-home-pros|cleaning|service|Inside and outside, up to 12 windows.|Up to 12 windows=9000|service-area:Brooklyn, Manhattan;duration:2 hours|2|Active
        move-out-cleaning|Move-Out Cleaning|hudson|hudson-home-pros|cleaning|service|Get the deposit back.|1 bedroom=22000;2 bedrooms=28000|service-area:Brooklyn, Queens;duration:4 hours|2|Active
        handyman-hourly|Handyman, Per Hour|hudson|hudson-home-pros|repairs|service|General repairs and installations, two hour minimum.|Per hour=7500|service-area:Brooklyn, Queens, Nassau;minimum:2 hours|2|Active
        furniture-assembly|Furniture Assembly|hudson|hudson-home-pros|repairs|service|Flat-pack furniture assembled and anchored.|Up to 3 items=6000;Up to 6 items=11000|service-area:Brooklyn, Manhattan;duration:2 hours|2|Active
        kitchen-faucet-install|Kitchen Faucet Installation|hudson|hudson-home-pros|repairs|service|Remove the old faucet, fit the new one.|Standard=12000|service-area:Brooklyn, Queens;duration:2 hours;licensed:yes|2|Active
        mezuzah-installation|Mezuzah Installation|hudson|hudson-home-pros|repairs|service|Install on every door, scrolls supplied by you.|Up to 8 doors=5000|service-area:Brooklyn, Queens, Manhattan;duration:1 hour|1|Active
        bar-mitzvah-tutoring|Bar Mitzvah Tutoring, 8 Lessons|hudson|torah-tutors|lessons|service|Torah reading, blessings and a speech, one on one.|8 lessons=48000|location:online or at home;duration:45 minutes each|2|Active
        hebrew-reading-lesson|Hebrew Reading Lesson, 30 Minutes|hudson|torah-tutors|lessons|service|From the alphabet to fluent davening.|Single lesson=4500;Pack of 5=20000|location:online;duration:30 minutes|2|Active
        gemara-chavrusa|Gemara Chavrusa Session|hudson|torah-tutors|lessons|service|Learn with a study partner, any level.|Per session=3500|location:online or in person;duration:60 minutes|1|Active
        torah-reading-lessons|Torah Reading and Trope Lessons|hudson|torah-tutors|lessons|service|Learn to read from the scroll with the cantillation marks.|Single lesson=4000;Pack of 4=15000|location:online;duration:45 minutes|1|Active
        closed-corner-mug|Corner Shop Coffee Mug|closed|closed-corner|kitchen-tableware|product|Ceramic mug (seller suspended: must stay hidden).|Mug=1200|material:ceramic|1|Active
        closed-corner-tote|Corner Shop Tote Bag|closed|closed-corner|books|product|Canvas tote (seller suspended: must stay hidden).|Tote=1800|material:canvas|1|Active
        retired-spice-set|Retired Spice Gift Set|judaica|shabbat-table|pantry|product|Archived by the seller (must stay hidden).|Set=3200|kosher-certification:OU|1|Archived
        """;

    // listings shown with a struck-through "compare at" price on their first variant: slug -> cents above the price
    private static readonly Dictionary<string, long> OnSale = new()
    {
        ["challah-classic"] = 100, ["shabbat-candlesticks-silver"] = 1100, ["kiddush-cup-pewter"] = 800, ["deep-house-cleaning"] = 2000,
    };

    // seller search keywords (ListingSnapshot.SearchTerms): slug -> terms; they rank below the title and the brand
    private static readonly Dictionary<string, string[]> Keywords = new()
    {
        ["challah-classic"] = ["hallah", "shabbat bread"], ["babka-chocolate"] = ["yeast cake"], ["deep-house-cleaning"] = ["maid service"],
        ["kiddush-cup-pewter"] = ["becher"], ["cabernet-mevushal"] = ["red wine"],
    };

    public static FixtureCatalogData Build()
    {
        var categories = CategoryRows.Select((c, i) => new CategoryDto(Id("category:" + c.Key), c.Parent is null ? null : Id("category:" + c.Parent), c.Key, c.Name, i)).ToList();
        var brands = BrandRows.Select(b => new BrandDto(Id("brand:" + b.Key), Id("seller:" + b.Seller), b.Key, b.Name, Id("brand-logo:" + b.Key))).ToList();
        var sellers = Sellers.ToDictionary(s => s.Key, s => new SellerRef(Id("seller:" + s.Key), s.Name, s.Active));
        var brandNames = BrandRows.ToDictionary(b => b.Key, b => b.Name);
        var created = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var listings = new List<ListingSnapshot>();
        foreach (var (line, index) in Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((l, i) => (l, i)))
        {
            var f = line.Split('|');
            var (slug, title, seller, brand, category, type, summary, variants, attrs, images, status) = (f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], int.Parse(f[9]), f[10]);
            var vs = variants.Split(';').Select((v, i) =>
            {
                var kv = v.TrimStart('!').Split('=');
                var price = long.Parse(kv[1]);
                return new VariantDto(Id($"variant:{slug}:{i}"), kv[0], price, OnSale.TryGetValue(slug, out var extra) && i == 0 ? price + extra : null, !v.StartsWith('!'));
            }).ToList();
            var attributes = attrs.Split(';').Select(a => a.Split(':', 2)).Select(a => new AttributeDto(a[0], a[1])).ToList();
            attributes.AddRange(Delivery(type, category, attributes).Select(d => new AttributeDto("delivery", d)));
            var imgs = Enumerable.Range(0, images).Select(i => new ImageDto(Id($"image:{slug}:{i}"), i == 0 ? 1200 : 1600, i == 0 ? 1200 : 1200, $"{title}, photo {i + 1}")).ToList();
            var cert = attributes.FirstOrDefault(a => a.Key == "kosher-certification")?.Value;
            var faqs = new List<FaqDto>();
            if (cert is not null) faqs.Add(new("Is this kosher?", $"Yes. It is certified by {cert}; the certification mark is on the packaging."));
            if (type == "service") faqs.Add(new("What areas do you serve?", attributes.FirstOrDefault(a => a.Key is "service-area" or "location")?.Value ?? "See the listing."));
            else faqs.Add(new("How is it shipped?", "Packed and shipped by the seller; delivery times are shown at checkout."));
            var description = $"{title}. {summary} " + string.Join(" ", attributes.Select(a => $"{Capitalise(a.Key)}: {a.Value}.")) +
                              (type == "service" ? " Book a time that suits you; the seller confirms the details after your request." : " Ships from the seller; see delivery options at checkout.");
            var at = created.AddDays(index);
            listings.Add(new ListingSnapshot(Id("listing:" + slug), index + 1, type, status, title, summary, description, sellers[seller],
                new BrandRef(Id("brand:" + brand), brandNames[brand]), Id("category:" + category), "USD", vs, imgs, attributes, faqs, at, at.AddDays(3), Keywords.GetValueOrDefault(slug)));
        }
        return new FixtureCatalogData(listings, categories, brands);
    }

    /// <summary>Delivery options (attribute key <c>delivery</c>; values ships, local, at-home, online): food is also delivered locally, services come to you or run online.</summary>
    private static IEnumerable<string> Delivery(string type, string category, List<AttributeDto> attributes)
    {
        if (type == "product")
        {
            yield return "ships";
            if (category is "bakery" or "meat-poultry") yield return "local";
            yield break;
        }
        var where = attributes.FirstOrDefault(a => a.Key == "location")?.Value ?? "";
        if (where.Contains("online")) yield return "online";
        if (where.Contains("home") || attributes.Any(a => a.Key == "service-area")) yield return "at-home";
    }

    private static string Capitalise(string key) => char.ToUpperInvariant(key[0]) + key[1..].Replace('-', ' ');
}
