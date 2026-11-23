using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;
using static Solvelt.Marketplace.Api.Tests.CatalogKit;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// W3-01 (proposal M6-11): a service the portal prices by quote is listed. It carries <c>priceMode: "quote"</c>, has no variants, sorts after every priced listing in BOTH price orders (stable by id,
/// also across cursors), is left out of price filters and of the price facet's range, can never be held (422 quote_required without a portal call) and is asked for through the existing quote flow.
/// </summary>
public sealed class QuotePricedListingTests : BookingFlowBase
{
    private const string L = "/api/v1/catalog/listings";
    private readonly List<Guid> _quotes = [];

    private async Task ReadyWithQuotes(int count = 3)
    {
        await Ready();
        var fx = Fixture(_k.F);
        var template = fx.All().First(l => l.Id == Service);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            _quotes.Add(id);
            fx.Add(Copy(template, id, $"Custom Renovation Quote {i + 1}") with { Variants = [], QuotePriced = true, QuoteAvailable = true });
        }
        await Sync(_k.F);
    }

    private Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK) => CatalogKit.Json(_k.Http, url, expected);
    private static IEnumerable<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray();
    private static string Mode(JsonElement card) => card.GetProperty("priceMode").GetString()!;

    [Fact]
    public async Task Cards_and_the_detail_say_quote_with_no_variants_and_priced_listings_say_fixed()
    {
        await ReadyWithQuotes(1);
        var all = Items(await Get($"{L}?pageSize=100")).ToList();
        var quote = all.Single(i => i.GetProperty("id").GetGuid() == _quotes[0]);
        Assert.Equal(("quote", 0L, 0L, "service"), (Mode(quote), quote.GetProperty("priceFromCents").GetInt64(), quote.GetProperty("priceToCents").GetInt64(), quote.GetProperty("type").GetString()));
        Assert.All(all.Where(i => i.GetProperty("id").GetGuid() != _quotes[0]), i => Assert.Equal("fixed", Mode(i)));

        var detail = await Get($"{L}/{quote.GetProperty("slug").GetString()}");
        Assert.Equal("quote", Mode(detail));
        Assert.Empty(detail.GetProperty("variants").EnumerateArray());                     // nothing to cart or hold
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("compareAtFromCents").ValueKind);
        Assert.Equal("fixed", Mode(await Get($"{L}/{Service}")));
    }

    [Theory]
    [InlineData("price_asc")]
    [InlineData("price_desc")]
    public async Task Quote_priced_listings_come_after_every_priced_one_in_both_price_orders_and_cursors_walk_the_same_order(string sort)
    {
        await ReadyWithQuotes(3);
        var page = Items(await Get($"{L}?sort={sort}&pageSize=100")).ToList();
        var modes = page.Select(Mode).ToList();
        var firstQuote = modes.IndexOf("quote");
        Assert.Equal(page.Count - 3, firstQuote);                                            // the last three, in both directions
        Assert.All(modes.Skip(firstQuote), m => Assert.Equal("quote", m));
        Assert.Equal(_quotes.Order().ToList(), page.Skip(firstQuote).Select(i => i.GetProperty("id").GetGuid()).ToList());   // the tail is in id order
        var prices = page.Take(firstQuote).Select(i => i.GetProperty("priceFromCents").GetInt64()).ToList();
        Assert.Equal(sort == "price_asc" ? prices.Order().ToList() : prices.OrderDescending().ToList(), prices);
        Assert.DoesNotContain(0L, prices);

        var slugs = page.Select(i => i.GetProperty("slug").GetString()!).ToList();
        foreach (var size in new[] { 1, 2, 5 })
        {
            var walked = new List<string>();
            string? cursor = null;
            do
            {
                var p = await Get($"{L}?sort={sort}&pageSize={size}" + (cursor is null ? "" : $"&cursor={cursor}"));
                walked.AddRange(Slugs(p));
                cursor = p.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : p.GetProperty("nextCursor").GetString();
                Assert.True(walked.Count <= slugs.Count);
            } while (cursor is not null);
            Assert.Equal(slugs, walked);                                                     // across the priced-to-quote boundary and inside the tail
        }
        foreach (var size in new[] { 2, 7 })                                                 // page numbers give the same order
        {
            var paged = new List<string>();
            for (var n = 1; n <= (int)Math.Ceiling(slugs.Count / (double)size); n++) paged.AddRange(Slugs(await Get($"{L}?sort={sort}&pageSize={size}&page={n}")));
            Assert.Equal(slugs, paged);
        }
    }

    [Fact]
    public async Task A_cursor_inside_the_quote_tail_is_accepted_and_a_forged_key_is_still_refused()
    {
        await ReadyWithQuotes(3);
        var total = Items(await Get($"{L}?sort=price_asc&pageSize=100")).Count();
        var p = await Get($"{L}?sort=price_asc&pageSize={total - 2}");                    // ends on the first quote listing
        var c = ListingCursor.Decode(p.GetProperty("nextCursor").GetString()!)!;
        Assert.Equal(ListingCursor.QuoteKey, c.Key);
        var rest = Items(await Get($"{L}?sort=price_asc&pageSize=10&cursor={p.GetProperty("nextCursor").GetString()}")).ToList();
        Assert.Equal(2, rest.Count);
        Assert.All(rest, i => Assert.Equal("quote", Mode(i)));
        Assert.Equal(HttpStatusCode.BadRequest, (await _k.Http.GetAsync($"{L}?sort=price_asc&cursor={(c with { Key = "qq" }).Encode()}")).StatusCode);
    }

    [Fact]
    public async Task Price_filters_leave_quote_listings_out_and_the_price_facet_never_starts_at_zero()
    {
        await ReadyWithQuotes(2);
        bool HasQuote(JsonElement page) => Items(page).Any(i => Mode(i) == "quote");
        Assert.True(HasQuote(await Get($"{L}?pageSize=100")));                              // no price filter: they are listed
        Assert.True(HasQuote(await Get($"{L}?pageSize=100&type=service")));
        Assert.False(HasQuote(await Get($"{L}?pageSize=100&priceMin=0")));                  // a quote listing has no price to compare
        Assert.False(HasQuote(await Get($"{L}?pageSize=100&priceMax=100000000")));
        Assert.False(HasQuote(await Get($"{L}?pageSize=100&priceMax=0")));

        var price = (await Get($"{L}?pageSize=1&type=service&facets=true")).GetProperty("facets").GetProperty("price");
        var cheapest = WithDb(_k.F, db => db.CatalogListings.AsNoTracking().Where(l => l.Visible && l.Type == ListingTypes.Service && !l.IsQuotePriced).Min(l => l.PriceFromCents));
        Assert.Equal(cheapest, price.GetProperty("minCents").GetInt64());
        Assert.True(price.GetProperty("minCents").GetInt64() > 0);
    }

    [Fact]
    public async Task A_result_of_only_quote_priced_listings_has_an_empty_price_range()
    {
        await ReadyWithQuotes(2);
        var page = await Get($"{L}?pageSize=10&q=renovation&facets=true");
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        var price = page.GetProperty("facets").GetProperty("price");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (price.GetProperty("minCents").ValueKind, price.GetProperty("maxCents").ValueKind));
    }

    [Fact]
    public async Task A_hold_on_a_quote_priced_service_is_refused_with_quote_required_without_calling_the_portal_and_a_quote_request_works()
    {
        await ReadyWithQuotes(1);
        var res = await Send(HttpMethod.Post, "/api/v1/bookings/holds", _jane, new { listingId = _quotes[0], start = Slot(), location = "online", durationMinutes = 60 }, "quote-hold-key-1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("quote_required", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(0, _portal.HoldCalls);
        Assert.Equal(0, await _k.Db(db => db.Bookings.CountAsync()));

        var quote = await Send(HttpMethod.Post, "/api/v1/quotes", _jane, new { listingId = _quotes[0], location = "online", details = "Full kitchen renovation, about 12 square metres.", phone = "+1 555 000 1111" }, "quote-request-key-1");
        Assert.True(quote.IsSuccessStatusCode, await quote.Content.ReadAsStringAsync());
        Assert.Single(_portal.Quotes);
    }

    [Fact]
    public async Task Changing_from_fixed_to_quote_removes_the_variants_so_nothing_stale_can_be_carted_or_held()
    {
        await Ready();
        var fx = Fixture(_k.F);
        var before = CatalogKit.Row(_k.F, Service);
        Assert.False(before.IsQuotePriced);
        Assert.NotEmpty(before.Variants);
        fx.Add(fx.All().First(l => l.Id == Service) with { Variants = [], QuotePriced = true });
        await Sync(_k.F);
        var after = CatalogKit.Row(_k.F, Service);
        Assert.Equal((true, 0, 0L), (after.IsQuotePriced, after.Variants.Count, after.PriceFromCents));
        Assert.Equal(0, WithDb(_k.F, db => db.CatalogVariants.Count(v => v.ListingId == Service)));
    }
}
