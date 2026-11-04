using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The portal catalog feed (docs/CATALOG.md in the portal) through the signed client, against <see cref="FakePortal"/> (M2-17).</summary>
public class HttpCatalogSourceTests
{
    private const string Secret = "unit-test-inbound-secret-0123456789abcdef";
    private static readonly Guid SellerId = Guid.Parse("a0000000-0000-0000-0000-000000000001"), BrandId = Guid.Parse("b0000000-0000-0000-0000-000000000001"), CategoryId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);

    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new(Start);
        public FakePortal Portal { get; }
        public HttpCatalogSource Source { get; }
        public Rig()
        {
            Portal = new FakePortal(Secret, Clock);
            var client = new SignedPortalClient(new HttpClient(Portal) { BaseAddress = new Uri("https://portal.test"), Timeout = Timeout.InfiniteTimeSpan },
                Options.Create(new PortalOptions { BaseUrl = "https://portal.test", InboundSecret = Secret, RetryBaseDelayMs = 0, MaxAttempts = 1 }), Clock);
            Source = new HttpCatalogSource(client, Clock, NullLogger<HttpCatalogSource>.Instance);
        }
        public Rig Feed(string json) { Portal.RespondAlways(_ => FakePortal.Json(HttpStatusCode.OK, json)); return this; }
    }

    private static Dictionary<string, object?> Doc(int n, long version = 10, Action<Dictionary<string, object?>>? edit = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["listingId"] = G(n), ["version"] = version, ["listed"] = true, ["type"] = "product", ["title"] = $"Mug {n}", ["handle"] = $"mug-{n}", ["condition"] = "new",
            ["description"] = "# Hand made\nA **lovely** mug.", ["descriptionHtml"] = "<script>alert(1)</script><h1>Hand made</h1>",
            ["price"] = new { amountMinor = 2000L, currency = "USD" },
            ["availability"] = new { state = "in_stock" },
            ["category"] = new { id = CategoryId, name = "Kitchen", slug = "kitchen", path = Array.Empty<object>() },
            ["brand"] = new { brandId = BrandId, name = "Clay", slug = "clay" }, ["seller"] = new { sellerId = SellerId, displayName = "Clay Ltd", country = "US" },
            ["variants"] = Array.Empty<object>(), ["images"] = Array.Empty<object>(), ["attributes"] = Array.Empty<object>(), ["faqs"] = Array.Empty<object>(),
            ["delivery"] = new { ships = true, localDelivery = false, serviceLocations = Array.Empty<string>() },
            ["publishedAt"] = "2026-10-01T00:00:00Z", ["updatedAt"] = "2026-11-01T00:00:00Z",
        };
        edit?.Invoke(d);
        return d;
    }

    private static string Page(IEnumerable<object> items, string cursor = "10", bool more = false) => JsonSerializer.Serialize(new { items, nextCursor = cursor, hasMore = more });
    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);
    private static ListingSnapshot Mapped(Rig r, Dictionary<string, object?> d) => r.Source.Map(El(d), r.Clock.GetUtcNow())!;
    private static string[] Delivery(ListingSnapshot s) => s.Attributes.Where(a => a.Key == "delivery").Select(a => a.Value).ToArray();

    // ---------- signed calls ----------
    [Fact]
    public async Task The_feed_is_read_with_signed_get_calls_and_the_cursor_is_escaped_into_the_signed_query()
    {
        var rig = new Rig().Feed(Page([Doc(1)], cursor: "11"));
        var page = await rig.Source.GetListingsAsync("a b+1", 500, CancellationToken.None);
        var call = Assert.Single(rig.Portal.Calls);
        Assert.True(call.Verified);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/internal/v1/catalog/listings?since=a%20b%2B1&limit=100", call.PathAndQuery);     // our page size is capped at 100 (the portal allows 200)
        Assert.Equal("11", page.NextCursor);
        Assert.Single(page.Items);
        await rig.Source.GetListingsAsync(null, 10, CancellationToken.None);
        Assert.Equal("/api/internal/v1/catalog/listings?limit=10", rig.Portal.Calls[1].PathAndQuery);
    }

    [Fact]
    public async Task One_listing_one_categories_and_brands_use_their_portal_paths()
    {
        var rig = new Rig();
        rig.Portal.RespondAlways(c => c.PathAndQuery switch
        {
            var p when p.StartsWith("/api/internal/v1/catalog/listings/") => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(Doc(7))),
            "/api/internal/v1/catalog/categories" => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { categoryId = CategoryId, parentId = (Guid?)null, name = "Kitchen", slug = "kitchen", sortOrder = 3 } } })),
            _ => FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { brandId = BrandId, sellerId = SellerId, name = "Clay", slug = "clay" } }, nextCursor = BrandId, hasMore = false })),
        });
        Assert.Equal(G(7), (await rig.Source.GetListingAsync(G(7), CancellationToken.None))!.Id);
        var cat = Assert.Single(await rig.Source.GetCategoriesAsync(CancellationToken.None));
        Assert.Equal(("kitchen", 3, (Guid?)null), (cat.Slug, cat.SortOrder, cat.ParentId));
        var brand = Assert.Single(await rig.Source.GetBrandsAsync(CancellationToken.None));
        Assert.Equal((BrandId, SellerId, "clay"), (brand.Id, brand.SellerId, brand.Slug));
        Assert.All(rig.Portal.Calls, c => Assert.True(c.Verified));
        Assert.Equal($"/api/internal/v1/catalog/listings/{G(7):D}", rig.Portal.Calls[0].PathAndQuery);
    }

    [Fact]
    public async Task Brands_are_read_page_by_page_with_the_last_brand_id_as_the_cursor()
    {
        var rig = new Rig();
        var b1 = G(1); var b2 = G(2);
        rig.Portal.RespondAlways(c => FakePortal.Json(HttpStatusCode.OK, c.PathAndQuery.Contains("since=")
            ? JsonSerializer.Serialize(new { items = new[] { new { brandId = b2, sellerId = SellerId, name = "B", slug = "b" } }, nextCursor = b2, hasMore = false })
            : JsonSerializer.Serialize(new { items = new[] { new { brandId = b1, sellerId = SellerId, name = "A", slug = "a" } }, nextCursor = b1, hasMore = true })));
        Assert.Equal(["a", "b"], (await rig.Source.GetBrandsAsync(CancellationToken.None)).Select(b => b.Slug));
        Assert.Contains($"since={b1:D}", rig.Portal.Calls[1].PathAndQuery);
    }

    // ---------- mapping ----------
    [Fact]
    public void A_listing_maps_to_the_read_model_and_the_markdown_source_is_used_never_the_html()
    {
        var s = Mapped(new Rig(), Doc(1));
        Assert.Equal((G(1), 10L, "product", "Active", "Mug 1", "USD"), (s.Id, s.Version, s.Type, s.Status, s.Title, s.Currency));
        Assert.Equal("# Hand made\nA **lovely** mug.", s.Description);
        Assert.DoesNotContain("script", s.Description);
        Assert.Equal("Hand made", s.Summary);                                                       // no bullets: the first line of the description, markup marks removed
        Assert.Equal((SellerId, BrandId, CategoryId), (s.Seller.Id, s.Brand.Id, s.CategoryId));
        var v = Assert.Single(s.Variants);                                                          // no variants: one buyable line on the listing price
        Assert.Equal((G(1), 2000L, true, true), (v.Id, v.PriceCents, v.Available, v.IsDefault));      // marked synthetic: M3 sends productId with variantId null
    }

    [Fact]
    public void Variants_take_their_own_price_name_and_availability_and_the_best_state_comes_from_the_portal()
    {
        var rig = new Rig();
        var s = Mapped(rig, Doc(1, edit: d => d["variants"] = new object[]
        {
            new { variantId = G(101), values = new[] { "S", "Red" }, price = new { amountMinor = 1850L, currency = "USD" }, availability = new { state = "low_stock" } },
            new { variantId = G(102), values = new[] { "M" }, price = new { amountMinor = 2000L, currency = "USD" }, availability = new { state = "out_of_stock" } },
            new { variantId = G(103), values = Array.Empty<string>(), price = new { amountMinor = 2100L, currency = "USD" }, availability = new { state = "backorder" } },
        }));
        Assert.Equal(["S / Red", "M", "Standard"], s.Variants.Select(v => v.Name));
        Assert.Equal([true, false, true], s.Variants.Select(v => v.Available));
        Assert.Equal([1850L, 2000L, 2100L], s.Variants.Select(v => v.PriceCents));
        Assert.All(s.Variants, v => Assert.False(v.IsDefault));                                     // real portal variants are never marked
    }

    [Fact]
    public void A_sale_is_judged_on_our_clock_and_never_on_the_portals_active_flag()
    {
        var rig = new Rig();
        Dictionary<string, object?> Sale(string? starts, string? ends, bool portalSaysActive) => Doc(1, edit: d => d["price"] = new
        {
            amountMinor = 2000L, currency = "USD", compareAtMinor = 2500L, sale = new { amountMinor = 1700L, startsAt = starts, endsAt = ends, active = portalSaysActive },
        });
        var on = Mapped(rig, Sale("2026-11-01T00:00:00Z", "2026-12-01T00:00:00Z", false));          // the portal's flag says inactive: ignored
        Assert.Equal((1700L, 2500L), (on.Variants[0].PriceCents, on.Variants[0].CompareAtCents));
        var notYet = Mapped(rig, Sale("2026-11-10T00:00:00Z", null, true));                          // starts later than our clock: ignored even though the portal says active
        Assert.Equal((2000L, 2500L), (notYet.Variants[0].PriceCents, notYet.Variants[0].CompareAtCents));
        var over = Mapped(rig, Sale(null, "2026-11-04T11:59:59Z", true));
        Assert.Equal(2000L, over.Variants[0].PriceCents);
        rig.Clock.Advance(TimeSpan.FromDays(10));                                                    // the clock moves: the second sale now applies
        Assert.Equal(1700L, rig.Source.Map(El(Sale("2026-11-10T00:00:00Z", null, true)), rig.Clock.GetUtcNow())!.Variants[0].PriceCents);
        var variantOwn = Mapped(rig, Doc(1, edit: d => { d["price"] = new { amountMinor = 2000L, currency = "USD", sale = new { amountMinor = 1700L } };
            d["variants"] = new object[] { new { variantId = G(5), values = new[] { "XL" }, price = new { amountMinor = 2600L, currency = "USD" }, availability = new { state = "in_stock" } } }; }));
        Assert.Equal(2600L, variantOwn.Variants[0].PriceCents);                                      // a variant with its own price is not part of the listing's sale
    }

    [Fact]
    public void Delivery_comes_from_the_explicit_C5_field_and_only_an_old_portal_falls_back_to_the_service_flags()
    {
        var rig = new Rig();
        Assert.Equal(["ships", "local"], Delivery(Mapped(rig, Doc(1, edit: d => d["delivery"] = new { ships = true, localDelivery = true, serviceLocations = Array.Empty<string>() }))));
        Assert.Empty(Delivery(Mapped(rig, Doc(1, edit: d => d["delivery"] = new { ships = false, localDelivery = false, serviceLocations = Array.Empty<string>() }))));   // explicit nothing
        Assert.Equal(["online", "at-home", "at-seller"], Delivery(Mapped(rig, Doc(2, edit: d => { d["type"] = "service"; d["delivery"] = new { ships = false, localDelivery = false, serviceLocations = new[] { "online", "customer", "seller", "unknown" } }; }))));
        // the explicit field wins over the service flags
        Assert.Equal(["online"], Delivery(Mapped(rig, Doc(3, edit: d => { d["type"] = "service"; d["delivery"] = new { ships = false, localDelivery = false, serviceLocations = new[] { "online" } }; d["service"] = new { atCustomer = true, atSeller = true }; }))));
        // no delivery field: a product gets nothing guessed; a service uses its own flags
        Assert.Empty(Delivery(Mapped(rig, Doc(4, edit: d => d.Remove("delivery")))));
        Assert.Equal(["online", "at-home"], Delivery(Mapped(rig, Doc(5, edit: d => { d.Remove("delivery"); d["type"] = "service"; d["service"] = new { online = true, atCustomer = true, atSeller = false }; }))));
    }

    [Fact]
    public void Bullets_policies_service_details_availability_questions_and_attributes_are_mapped()
    {
        var s = Mapped(new Rig(), Doc(1, edit: d =>
        {
            d["type"] = "service"; d["condition"] = "refurbished"; d["conditionNote"] = "boxed";
            d["bullets"] = new[] { "Dishwasher safe", "Lead free" };
            d["availability"] = new { state = "low_stock" };
            d["attributes"] = new object[] { new { key = "material", label = "Material", unit = (string?)null, value = "stoneware" }, new { key = "volume", label = "Volume", unit = "ml", value = "350" } };
            d["policies"] = new { returnsDays = 30, returnsCondition = "unused", warrantyMonths = 12, warrantyNote = (string?)null, handlingDays = 2 };
            d["service"] = new { pricingModel = "hourly", defaultDurationMinutes = 90, bookingMode = "request" };
            d["faqs"] = new[] { new { question = "Dishwasher safe?", answer = "Yes." } };
            d["questions"] = new object[] { new { questionId = "Q-1", question = "Microwave safe?", answer = "Yes, 90 s.", askedAt = "x", answeredAt = "y" }, new { questionId = "Q-2", question = "Unanswered?", answer = (string?)null } };
        }));
        string? A(string key) => s.Attributes.FirstOrDefault(a => a.Key == key)?.Value;
        Assert.Equal("Dishwasher safe", s.Summary);
        Assert.Equal(["Dishwasher safe", "Lead free"], s.Attributes.Where(a => a.Key == "highlight").Select(a => a.Value));
        Assert.Equal(("stoneware", "350 ml", "refurbished: boxed", "low-stock"), (A("material"), A("volume"), A("condition"), A("availability")));
        Assert.Equal(("30", "unused", "12", "2"), (A("returns-days"), A("returns-condition"), A("warranty-months"), A("handling-days")));
        Assert.Equal(("hourly", "90 min", "request"), (A("pricing-model"), A("duration"), A("booking-mode")));
        Assert.Equal(["Dishwasher safe?", "Microwave safe?"], s.Faqs.Select(f => f.Question));      // published answers only
    }

    [Fact]
    public void Search_terms_are_cut_per_term_in_number_and_in_total()
    {
        var rig = new Rig();
        var many = Enumerable.Range(0, 100).Select(i => $"term{i}").ToArray();
        Assert.Equal(HttpCatalogSource.MaxSearchTerms, Mapped(rig, Doc(1, edit: d => d["searchTerms"] = many)).SearchTerms!.Count);
        var longTerm = Mapped(rig, Doc(1, edit: d => d["searchTerms"] = new[] { new string('x', 500), "ok" })).SearchTerms!;
        Assert.Equal(HttpCatalogSource.MaxSearchTermLength, longTerm[0].Length);
        var big = Enumerable.Range(0, 25).Select(i => new string((char)('a' + i), HttpCatalogSource.MaxSearchTermLength)).ToArray();    // 25 x 60 = 1500 characters
        var kept = Mapped(rig, Doc(1, edit: d => d["searchTerms"] = big)).SearchTerms!;
        Assert.True(kept.Sum(t => t.Length) <= HttpCatalogSource.MaxSearchTermsTotal);
        Assert.Equal(HttpCatalogSource.MaxSearchTermsTotal, kept.Sum(t => t.Length));              // filled to the budget, the last term cut to fit
        Assert.Equal(["mug", "coffee cup"], Mapped(rig, Doc(1, edit: d => d["searchTerms"] = new[] { " mug ", "", "coffee cup" })).SearchTerms);
        Assert.Empty(Mapped(rig, Doc(1)).SearchTerms!);
    }

    [Fact]
    public void Images_are_ordered_main_first_and_carry_only_ids_and_sizes()
    {
        var s = Mapped(new Rig(), Doc(1, edit: d => d["images"] = new object[]
        {
            new { imageId = G(21), width = 800, height = 600, alt = (string?)null, isMain = false, sortOrder = 0, path = "/api/internal/v1/catalog/images/x" },
            new { imageId = G(22), width = 1200, height = 1200, alt = "Front", isMain = true, sortOrder = 5, path = "/api/internal/v1/catalog/images/y" },
        }));
        Assert.Equal([G(22), G(21)], s.Images.Select(i => i.Id));
        Assert.Equal(("Front", 1200), (s.Images[0].Alt, s.Images[0].Width));
        Assert.Equal("", s.Images[1].Alt);
    }

    [Theory]
    [InlineData("title"), InlineData("price"), InlineData("seller"), InlineData("brand"), InlineData("category"), InlineData("version")]
    public void A_document_missing_a_required_part_is_unreadable_not_guessed(string part)
        => Assert.Null(new Rig().Source.Map(El(Doc(1, edit: d => d.Remove(part))), Start));

    [Fact]
    public async Task One_unreadable_document_is_skipped_and_the_page_and_cursor_still_move()
    {
        var bad = Doc(2, edit: d => d.Remove("price"));
        var rig = new Rig().Feed(Page([Doc(1), bad, Doc(3)], cursor: "99", more: true));
        var page = await rig.Source.GetListingsAsync(null, 50, CancellationToken.None);
        Assert.Equal([G(1), G(3)], page.Items.Select(i => i.Id));
        Assert.Equal(("99", true), (page.NextCursor, page.HasMore));
    }

    // ---------- failures ----------
    [Fact]
    public async Task A_missing_listing_is_null_but_a_portal_failure_or_a_garbled_answer_is_never_taken_as_removed()
    {
        var rig = new Rig();
        rig.Portal.Respond(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        Assert.Null(await rig.Source.GetListingAsync(G(1), CancellationToken.None));
        rig.Portal.Respond(HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<PortalUnavailableException>(() => rig.Source.GetListingAsync(G(1), CancellationToken.None));
        rig.Portal.Respond(HttpStatusCode.OK, "{\"listingId\":\"" + G(1) + "\",\"title\":\"no price\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Source.GetListingAsync(G(1), CancellationToken.None));
        rig.Portal.Respond(HttpStatusCode.OK, "not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Source.GetListingAsync(G(1), CancellationToken.None));
        await Assert.ThrowsAsync<PortalUnavailableException>(async () => { rig.Portal.Respond(HttpStatusCode.TooManyRequests, "{\"code\":\"rate_limited\"}"); await rig.Source.GetListingsAsync(null, 10, CancellationToken.None); });
    }

    [Fact]
    public async Task A_page_that_is_too_large_is_retried_with_half_the_page_size()
    {
        var rig = new Rig();
        var limits = new List<string>();
        rig.Portal.RespondAlways(c =>
        {
            var limit = c.PathAndQuery[(c.PathAndQuery.LastIndexOf("limit=", StringComparison.Ordinal) + 6)..];
            limits.Add(limit);
            if (int.Parse(limit) > 25) throw new PortalResponseTooLargeException();
            return FakePortal.Json(HttpStatusCode.OK, Page([Doc(1)]));
        });
        var page = await rig.Source.GetListingsAsync(null, 100, CancellationToken.None);
        Assert.Equal(["100", "50", "25"], limits);
        Assert.Single(page.Items);
    }

    // ---------- tombstones, end to end through the sync ----------
    private static async Task<CatalogSyncResult> Run(TestFactory f, ICatalogSource source)
    {
        using var scope = f.Services.CreateScope();
        return await CatalogKit.Service(scope, source, f.Clock).RunAsync();
    }

    private static Rig PortalFor(TestFactory f, Func<string?, string> feed)
    {
        var rig = new Rig();
        rig.Portal.RespondAlways(c => c.PathAndQuery.Contains("/catalog/categories") ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { categoryId = CategoryId, parentId = (Guid?)null, name = "Kitchen", slug = "kitchen", sortOrder = 1 } } }))
            : c.PathAndQuery.Contains("/catalog/brands") ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { brandId = BrandId, sellerId = SellerId, name = "Clay", slug = "clay" } }, nextCursor = BrandId, hasMore = false }))
            : FakePortal.Json(HttpStatusCode.OK, feed(c.PathAndQuery.Contains("since=") ? c.PathAndQuery.Split("since=")[1].Split('&')[0] : null)));
        return rig;
    }

    [Theory]
    [InlineData("moderated")]
    [InlineData("deleted")]
    [InlineData("a-reason-from-the-future")]
    public async Task A_feed_tombstone_hides_a_known_listing_whatever_its_reason_and_a_newer_snapshot_shows_it_again(string reason)
    {
        using var f = new TestFactory(new() { ["Catalog:Source"] = "None" });
        var published = true;
        long tombVersion = 20;
        var rig = PortalFor(f, since =>
            since is null ? Page([Doc(1, 10), Doc(2, 10)], "10")
            : since == "10" ? (published ? Page([], "10") : JsonSerializer.Serialize(new { items = new object[] { new { listingId = G(1), version = tombVersion, listed = false, reason } }, nextCursor = tombVersion.ToString(), hasMore = false }))
            : Page([Doc(1, 30, d => d["title"] = "Mug 1 again")], "30"));
        await Run(f, rig.Source);
        Assert.Equal(2, WithDb(f, db => db.CatalogListings.Count(l => l.Visible)));
        published = false;
        var r = await Run(f, rig.Source);
        Assert.Equal(1, r.Applied);                                                                  // the tombstone counts as applied
        var row = CatalogKit.Row(f, G(1));
        Assert.False(row.Visible);
        Assert.Equal((20L, 20L), (row.Version, row.KnownVersion));                                   // finished: nothing is left to fetch
        Assert.True(CatalogKit.Row(f, G(2)).Visible);
        Assert.Equal(0, (await Run(f, rig.Source)).Pending);
        // re-listed at a newer version: the next feed page carries the document and it shows again
        WithDb(f, db => db.CatalogSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.Cursor, "20")));
        await Run(f, rig.Source);
        Assert.True(CatalogKit.Row(f, G(1)).Visible);
        Assert.Equal("Mug 1 again", CatalogKit.Row(f, G(1)).Title);
    }

    [Fact]
    public async Task The_synthetic_default_variant_is_stored_flagged_so_checkout_can_tell_it_from_a_portal_variant()
    {
        using var f = new TestFactory(new() { ["Catalog:Source"] = "None" });
        var rig = PortalFor(f, _ => Page([Doc(1, 10), Doc(2, 10, d => d["variants"] = new object[] { new { variantId = G(50), values = new[] { "S" }, price = new { amountMinor = 2000L, currency = "USD" }, availability = new { state = "in_stock" } } })], "10"));
        await Run(f, rig.Source);
        Assert.True(Assert.Single(CatalogKit.Row(f, G(1)).Variants).IsDefault);
        Assert.False(Assert.Single(CatalogKit.Row(f, G(2)).Variants).IsDefault);
    }

    [Fact]
    public async Task A_tombstone_for_a_listing_we_never_held_creates_nothing_and_an_older_one_changes_nothing()
    {
        using var f = new TestFactory(new() { ["Catalog:Source"] = "None" });
        var step = 0;
        var rig = PortalFor(f, since => step++ switch
        {
            0 => Page([Doc(1, 10)], "10", more: true),
            _ => JsonSerializer.Serialize(new { items = new object[] { new { listingId = G(9), version = 11, listed = false, reason = "deleted" }, new { listingId = G(1), version = 5, listed = false, reason = "moderated" } }, nextCursor = "11", hasMore = false }),
        });
        await Run(f, rig.Source);
        Assert.Equal(1, WithDb(f, db => db.CatalogListings.Count()));                                // G(9) was never created
        Assert.True(CatalogKit.Row(f, G(1)).Visible);                                                // version 5 < 10: stale
    }

    [Fact]
    public async Task A_listing_that_the_portal_no_longer_serves_is_hidden_by_the_404_fallback()
    {
        using var f = new TestFactory(new() { ["Catalog:Source"] = "None" });
        var rig = PortalFor(f, _ => Page([Doc(1, 10)], "10"));
        await Run(f, rig.Source);
        // an event says version 15 exists, but the portal answers 404 for the document
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Post(c, Request(f, Body("evt-up-15", "listing.upserted", T0, new { listingId = G(1), version = 15 })))).Status);
        var feed = Page([Doc(1, 10)], "10");
        rig.Portal.RespondAlways(c => c.PathAndQuery.Contains("/catalog/listings/") ? FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}")      // only the single document is gone
            : c.PathAndQuery.Contains("/catalog/categories") ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { categoryId = CategoryId, parentId = (Guid?)null, name = "Kitchen", slug = "kitchen", sortOrder = 1 } } }))
            : c.PathAndQuery.Contains("/catalog/brands") ? FakePortal.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { new { brandId = BrandId, sellerId = SellerId, name = "Clay", slug = "clay" } }, nextCursor = BrandId, hasMore = false }))
            : FakePortal.Json(HttpStatusCode.OK, feed));
        await Run(f, rig.Source);
        Assert.False(CatalogKit.Row(f, G(1)).Visible);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("a-reason-from-the-future")]
    [InlineData("moderated")]
    public async Task An_unlisted_event_hides_the_listing_whatever_reason_it_carries(string reason)
    {
        using var f = new TestFactory();
        await CatalogKit.Sync(f);
        var c = f.CreateClient();
        var id = CatalogKit.ListingId("babka-chocolate");
        var v = CatalogKit.Row(f, id).Version;
        var res = await Post(c, Request(f, Body($"evt-unlist-{reason}", "listing.unlisted", T0, new { listingId = id, version = v, reason })));
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.False(CatalogKit.Row(f, id).Visible);
    }
}
