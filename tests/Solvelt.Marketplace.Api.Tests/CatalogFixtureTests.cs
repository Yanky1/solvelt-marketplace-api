using Solvelt.Marketplace.Api.Catalog;

namespace Solvelt.Marketplace.Api.Tests;

public class CatalogFixtureTests
{
    private static readonly FixtureCatalogData Data = FixtureCatalogData.Build();

    [Fact]
    public void The_seed_is_realistic_in_size_and_spread()
    {
        var visible = Data.Listings.Where(l => l.Status == "Active" && l.Seller.Active).ToList();
        Assert.Equal(40, visible.Count);
        Assert.Equal(43, Data.Listings.Count);
        Assert.Equal(28, visible.Count(l => l.Type == "product"));
        Assert.Equal(12, visible.Count(l => l.Type == "service"));
        Assert.Equal(6, Data.Listings.Select(l => l.Seller.Id).Distinct().Count());
        Assert.Equal(10, Data.Brands.Count);
        Assert.Equal(13, Data.Categories.Count);
        Assert.True(visible.Count(l => l.Attributes.Any(a => a.Key == "kosher-certification")) >= 20);   // kosher food
        Assert.Contains(visible, l => l.Attributes.Any(a => a is { Key: "kosher-status", Value: "meat" }));
        Assert.Contains(visible, l => l.Attributes.Any(a => a is { Key: "kosher-for-passover", Value: "yes" }));
    }

    [Fact]
    public void Some_listings_exercise_the_visibility_rule_and_the_sale_and_stock_flags()
    {
        Assert.Contains(Data.Listings, l => !l.Seller.Active);                       // a suspended seller
        Assert.Contains(Data.Listings, l => l.Status == "Archived");                 // a status that has left Active
        Assert.Contains(Data.Listings, l => l.Variants.All(v => !v.Available));      // sold out
        Assert.Contains(Data.Listings, l => l.Variants.Any(v => v.CompareAtCents > v.PriceCents));   // on sale
        Assert.Contains(Data.Listings, l => l.Variants.Count >= 3);
    }

    [Fact]
    public void Every_reference_resolves_and_every_record_is_well_formed()
    {
        var categories = Data.Categories.ToDictionary(c => c.Id);
        var brands = Data.Brands.ToDictionary(b => b.Id);
        Assert.Equal(Data.Categories.Count, categories.Count);
        Assert.All(Data.Categories, c => Assert.True(c.ParentId is null || categories.ContainsKey(c.ParentId.Value)));
        Assert.Equal(Data.Categories.Count, Data.Categories.Select(c => c.Slug).Distinct().Count());
        Assert.Equal(Data.Brands.Count, Data.Brands.Select(b => b.Slug).Distinct().Count());
        foreach (var l in Data.Listings)
        {
            Assert.True(categories.ContainsKey(l.CategoryId), l.Title);
            Assert.True(brands.ContainsKey(l.Brand.Id), l.Title);
            Assert.Equal(l.Seller.Id, brands[l.Brand.Id].SellerId);                  // a brand belongs to the listing's seller
            Assert.NotEmpty(l.Variants);
            Assert.All(l.Variants, v => Assert.True(v.PriceCents > 0));
            Assert.NotEmpty(l.Images);
            Assert.All(l.Images, i => Assert.True(i.Width > 0 && i.Height > 0));
            Assert.NotEmpty(l.Faqs);
            Assert.False(string.IsNullOrWhiteSpace(l.Description));
            Assert.Equal(l.Variants.Count, l.Variants.Select(v => v.Id).Distinct().Count());
        }
        Assert.Equal(Data.Listings.Count, Data.Listings.Select(l => l.Id).Distinct().Count());
        Assert.Equal(Data.Listings.Count, Data.Listings.Select(l => Slug.From(l.Title)).Distinct().Count());   // titles give unique slugs
        Assert.Equal(Data.Listings.Count, Data.Listings.Select(l => l.Version).Distinct().Count());
    }

    [Fact]
    public void Ids_are_deterministic_across_builds()
    {
        var again = FixtureCatalogData.Build();
        Assert.Equal(Data.Listings.Select(l => l.Id), again.Listings.Select(l => l.Id));
        Assert.Equal(Data.Categories.Select(c => c.Id), again.Categories.Select(c => c.Id));
    }

    [Fact]
    public async Task The_cursor_feed_pages_through_everything_once_in_version_order()
    {
        var source = new FixtureCatalogSource();
        var seen = new List<ListingSnapshot>();
        string? cursor = null;
        int pages = 0;
        while (true)
        {
            var page = await source.GetListingsAsync(cursor, 7, default);
            seen.AddRange(page.Items); pages++;
            cursor = page.NextCursor;
            if (!page.HasMore) break;
        }
        Assert.Equal(7, pages);   // 43 listings, 7 per page
        Assert.Equal(source.All().Select(l => l.Id), seen.Select(l => l.Id));
        Assert.Equal(seen.Select(l => l.Version).Order(), seen.Select(l => l.Version));
        var after = await source.GetListingsAsync(cursor, 7, default);
        Assert.Empty(after.Items);
        Assert.False(after.HasMore);
        Assert.Equal(cursor, after.NextCursor);   // nothing new: the cursor stays
    }

    [Fact]
    public async Task A_change_reappears_after_the_cursor_and_a_bad_cursor_is_refused()
    {
        var source = new FixtureCatalogSource();
        var first = await source.GetListingsAsync(null, 500, default);
        var id = CatalogKit.ListingId("babka-chocolate");
        var updated = source.Upsert(id, l => l with { Title = "Chocolate Babka, Large" });
        Assert.Equal(first.Items.Max(l => l.Version) + 1, updated.Version);
        var next = await source.GetListingsAsync(first.NextCursor, 10, default);
        Assert.Equal(id, Assert.Single(next.Items).Id);
        Assert.Equal("Chocolate Babka, Large", next.Items[0].Title);
        await Assert.ThrowsAsync<ArgumentException>(() => source.GetListingsAsync("not-a-cursor", 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => source.GetListingsAsync("5:not-a-guid", 10, default));
    }

    [Theory]
    [InlineData("Classic Braided Challah", "classic-braided-challah")]
    [InlineData("Glatt Brisket, First Cut (per lb)", "glatt-brisket-first-cut-per-lb")]
    [InlineData("Zoë's Café — Crème Brûlée!", "zoes-cafe-creme-brulee")]
    [InlineData("  --  ", "listing")]
    [InlineData("日本語", "listing")]
    public void Slugs_are_ascii_hyphenated_and_never_empty(string title, string expected) => Assert.Equal(expected, Slug.From(title));

    [Fact]
    public void Slugs_are_capped_at_100_characters_without_a_trailing_hyphen()
    {
        var slug = Slug.From(string.Join(" ", Enumerable.Repeat("word", 40)));
        Assert.True(slug.Length <= 100);
        Assert.False(slug.EndsWith('-'));
    }
}
