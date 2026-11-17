using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Catalog and cart tools through whole scripted calls (slice 2). The model proposes a position and a quantity; the server decides the product, the price and the limits.</summary>
public class VoiceOrderToolTests
{
    private static async Task<VoiceScenario> Ready(Dictionary<string, string?>? config = null)
    {
        var s = new VoiceScenario(config: config);
        await s.SyncCatalog();
        await s.Dial();
        return s;
    }

    private static string Say(JsonNode n) => n.ToJsonString();

    [Fact]
    public async Task Searching_reads_out_at_most_three_real_products_with_prices_said_like_a_person()
    {
        using var s = await Ready();
        var r = await s.Tool("search_products", new { query = "challah" });
        var results = r["results"]!.AsArray();
        Assert.InRange(results.Count, 1, 3);
        Assert.True(r["found"]!.GetValue<int>() >= results.Count);
        Assert.Equal(1, results[0]!["position"]!.GetValue<int>());
        Assert.Contains("Challah", results[0]!["name"]!.GetValue<string>());
        Assert.All(results, x => Assert.True(VoiceSpeech.IsSpeakable(x!["price_spoken"]!.GetValue<string>() + " " + x["name"]!.GetValue<string>()), Say(x!)));   // no digits, symbols or markup to read out
        Assert.True(VoiceSpeech.IsSpeakable(r["say"]!.GetValue<string>()));
        Assert.Equal(results.Count, s.State().LastResults.Count);   // the server holds the ids behind the positions
    }

    [Fact]
    public async Task Nothing_found_and_bad_queries_are_told_plainly()
    {
        using var s = await Ready();
        Assert.Equal(0, (await s.Tool("search_products", new { query = "zzzqqq nothing like this" }))["found"]!.GetValue<int>());
        Assert.Equal("bad_query", (await s.Tool("search_products", new { query = "" }))["error"]!.GetValue<string>());
        Assert.Equal("bad_query", (await s.Tool("search_products", new { query = new string('x', 101) }))["error"]!.GetValue<string>());
        Assert.Equal("bad_query", (await s.Tool("search_products", new { }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_service_is_never_offered_as_a_product()
    {
        using var s = await Ready();
        var r = await s.Tool("search_products", new { query = "house cleaning" });
        Assert.DoesNotContain(r["results"]!.AsArray(), x => x!["name"]!.GetValue<string>().Contains("Cleaning", StringComparison.OrdinalIgnoreCase));   // services are booked, not carted
    }

    [Fact]
    public async Task Describing_a_product_gives_its_options_and_a_product_with_several_options_needs_one_chosen()
    {
        using var s = await Ready();
        await s.Tool("search_products", new { query = "classic braided challah" });
        var d = await s.Tool("describe_product", new { position = 1 });
        Assert.Equal("Classic Braided Challah", d["name"]!.GetValue<string>());
        var options = d["options"]!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.Equal(("six fifty", "nine fifty"), (options[0]!["price_spoken"]!.GetValue<string>(), options[1]!["price_spoken"]!.GetValue<string>()));
        Assert.True(VoiceSpeech.IsSpeakable(d["about"]!.GetValue<string>()));
        Assert.Equal("option_needed", (await s.Tool("add_to_cart", new { position = 1, quantity = 1 }))["error"]!.GetValue<string>());   // which size?
        Assert.Equal("no_such_position", (await s.Tool("describe_product", new { position = 9 }))["error"]!.GetValue<string>());
        Assert.Equal("no_such_position", (await s.Tool("describe_product", new { position = 0 }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_cart_is_built_by_position_and_the_price_comes_from_the_catalog_whatever_the_model_sends()
    {
        using var s = await Ready();
        await s.Tool("search_products", new { query = "classic braided challah" });
        // the model tries to pass a price, a variant id and a total: none of these are parameters, none are read
        var added = await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2, priceCents = 1, unitPriceCents = 1, totalCents = 2, variantId = Guid.NewGuid() });
        Assert.Equal(2, added["items_in_cart"]!.GetValue<int>());
        Assert.Equal("thirteen dollars", added["subtotal_spoken"]!.GetValue<string>());   // 2 x 650 cents, from the catalog
        var row = await s.K.Db(db => db.CartLines.AsNoTracking().SingleAsync());
        Assert.Equal((2, 650L), (row.Quantity, row.SeenUnitPriceCents));
        Assert.Equal(2, s.State().CartItemCount);

        var view = await s.Tool("view_cart");
        Assert.Equal("six fifty", view["lines"]![0]!["price_each_spoken"]!.GetValue<string>());
        var changed = await s.Tool("change_cart", new { line_position = 1, quantity = 1 });
        Assert.Equal("six fifty", changed["subtotal_spoken"]!.GetValue<string>());
        var removed = await s.Tool("change_cart", new { line_position = 1, quantity = 0 });
        Assert.Equal(0, removed["items_in_cart"]!.GetValue<int>());
        Assert.Equal(0, s.State().CartItemCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task A_bad_quantity_is_refused(int quantity)
    {
        using var s = await Ready();
        await s.Tool("search_products", new { query = "classic braided challah" });
        Assert.Equal("bad_quantity", (await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity }))["error"]!.GetValue<string>());
        Assert.Equal(0, await s.K.Db(db => db.CartLines.CountAsync()));
    }

    [Fact]
    public async Task A_phone_order_never_holds_more_than_the_item_limit()
    {
        using var s = await Ready(new() { ["Voice:MaxItems"] = "3" });
        await s.Tool("search_products", new { query = "classic braided challah" });
        Assert.Equal(3, (await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 3 }))["items_in_cart"]!.GetValue<int>());
        var over = await s.Tool("add_to_cart", new { position = 1, option_position = 2, quantity = 1 });
        Assert.Equal("order_limit", over["error"]!.GetValue<string>());
        var raise = await s.Tool("change_cart", new { line_position = 1, quantity = 4 });
        Assert.Equal("order_limit", raise["error"]!.GetValue<string>());
        Assert.Equal(3, await s.K.Db(db => db.CartLines.SumAsync(l => l.Quantity)));
        Assert.Equal(2, (await s.Tool("change_cart", new { line_position = 1, quantity = 2 }))["items_in_cart"]!.GetValue<int>());   // lowering is fine
    }

    [Fact]
    public async Task Positions_that_do_not_exist_change_nothing()
    {
        using var s = await Ready();
        Assert.Equal("no_such_position", (await s.Tool("add_to_cart", new { position = 1, quantity = 1 }))["error"]!.GetValue<string>());   // no search yet
        await s.Tool("search_products", new { query = "classic braided challah" });
        Assert.Equal("no_such_position", (await s.Tool("add_to_cart", new { position = 7, quantity = 1 }))["error"]!.GetValue<string>());
        Assert.Equal("option_needed", (await s.Tool("add_to_cart", new { position = 1, option_position = 5, quantity = 1 }))["error"]!.GetValue<string>());
        Assert.Equal("no_such_line", (await s.Tool("change_cart", new { line_position = 1, quantity = 1 }))["error"]!.GetValue<string>());
        Assert.Equal(0, await s.K.Db(db => db.CartLines.CountAsync()));
    }

    [Fact]
    public async Task A_changed_cart_drops_the_quote_and_the_confirmation_so_a_fresh_read_back_is_needed()
    {
        using var s = await Ready();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        var state = s.State();
        state.SetQuote("q-1", 650); state.ReadBackDone();
        Assert.Equal(ConfirmationStatus.ReadBack, state.Confirmation);
        await s.Tool("add_to_cart", new { position = 1, option_position = 2, quantity = 1 });
        Assert.Equal((null, ConfirmationStatus.None), (state.QuoteKey, state.Confirmation));
        Assert.False(state.Confirm());
    }

    [Fact]
    public async Task Search_results_and_tool_output_hold_no_markup_or_links()
    {
        using var s = await Ready();
        foreach (var q in new[] { "challah", "candlesticks", "kiddush cup" })
        {
            var r = await s.Tool("search_products", new { query = q });
            Assert.True(VoiceSpeech.IsSpeakable(r["say"]!.GetValue<string>()), r["say"]!.GetValue<string>());
        }
    }
}
