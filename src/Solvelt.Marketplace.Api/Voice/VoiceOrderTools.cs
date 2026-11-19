using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;

using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Runs the EXISTING public catalog handlers in process (the same visibility rules, search and ranking as the web), so the voice tools duplicate no catalog logic.
/// </summary>
public sealed class InProcessCatalog(IServiceProvider sp)
{
    private async Task<JsonDocument?> RunAsync(Func<HttpContext, Task<IResult>> handler, QueryString? query, CancellationToken ct)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        if (query is { } q) ctx.Request.QueryString = q;
        ctx.Response.Body = new MemoryStream();
        var result = await handler(ctx);
        await result.ExecuteAsync(ctx);
        if (ctx.Response.StatusCode != 200) return null;
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body, cancellationToken: ct);
    }

    /// <summary>The category tree as the web shows it, but counting only products that can be bought now (a category with none is left out).</summary>
    public Task<JsonDocument?> CategoriesAsync(CancellationToken ct)
        => RunAsync(_ => CatalogEndpoints.CategoriesAsync(sp.GetRequiredService<MarketplaceDb>(), sp.GetRequiredService<CategoryMap>(), true, ct), null, ct);

    public Task<JsonDocument?> SearchAsync(string text, int pageSize, long? priceMaxCents, CancellationToken ct, string? categorySlug = null)
    {
        var q = new Dictionary<string, string?> { ["q"] = text, ["type"] = "product", ["available"] = "true", ["pageSize"] = pageSize.ToString(), ["sort"] = "relevance" };
        if (priceMaxCents is > 0) q["priceMax"] = priceMaxCents.Value.ToString();
        if (!string.IsNullOrEmpty(categorySlug)) q["category"] = categorySlug;
        return RunAsync(ctx => CatalogEndpoints.Listings(ctx.Request, sp.GetRequiredService<MarketplaceDb>(), sp.GetRequiredService<ISearchIndex>(), sp.GetRequiredService<CategoryMap>(), ct),
            QueryString.Create(q), ct);
    }

    public Task<JsonDocument?> DetailAsync(Guid listingId, CancellationToken ct)
        => RunAsync(_ => CatalogEndpoints.Listing(sp.GetRequiredService<MarketplaceDb>(), sp.GetRequiredService<CategoryMap>(), sp.GetRequiredService<TimeProvider>(), listingId.ToString(), ct), null, ct);
}

internal static class ToolKit
{
    public static VoiceToolResult Json(JsonObject o) => new(o.ToJsonString());
    public static VoiceToolResult Error(string code, string instruction) => new(new JsonObject { ["error"] = code, ["instruction"] = instruction }.ToJsonString());
    public static int Int(JsonElement a, string name, int fallback) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
    public static string? Str(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonObject Def(string name, string description, JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "function", ["name"] = name, ["description"] = description,
        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject(), ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()), ["additionalProperties"] = false },
    };

    public static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
}

/// <summary>
/// Starts identification: texts a six-digit code to the caller's number (or a number they ask for) and puts the caller on the keypad step. The answer is the same whether or not an account
/// uses the number, so the tool cannot be used to find out who is a customer; the account is only connected after the code is typed right.
/// </summary>
public sealed class IdentifyCallerTool(PhoneCodeService codes, PhoneIdentity identity) : IVoiceTool
{
    public string Name => "identify_caller";
    public JsonObject Definition => ToolKit.Def(Name,
        "Verify the caller by text message. Texts a six-digit code to the number they are calling from (or another number if they ask) and moves the call to the keypad, where they type it. Never ask for the code aloud.",
        new JsonObject { ["other_number"] = ToolKit.Prop("string", "Only if the caller wants the code sent to a different number than they are calling from.") });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (s.Level == CallerLevel.Verified && s.VerifiedInThisCall) return ToolKit.Json(new JsonObject { ["status"] = "already_verified", ["instruction"] = "The caller is already verified in this call." });
        var requested = ToolKit.Str(args, "other_number");
        var number = requested is null ? s.CallerNumber : PhoneNumbers.ToE164(requested);
        if (number is null) return ToolKit.Error("no_number", requested is null ? "No number is known for this call: ask which number to text, then call again with other_number." : "That is not a phone number: ask the caller to say it again, digit by digit.");
        var match = await identity.FindAsync(number, ct);
        var sent = await codes.SendAsync(s, number, match is null ? "guest" : "recognise", match?.AccountId, ct);
        switch (sent)
        {
            case SendResult.TooMany: return ToolKit.Error("too_many_codes", "No more codes can be sent right now. Offer to connect a person, or suggest ordering on the web site.");
            case SendResult.Failed: return ToolKit.Error("sms_failed", "The text could not be sent. Offer to try once more, or to connect a person.");
        }
        s.SetTask("verification");
        s.BeginKeypadStep("sms_code");
        return new(new JsonObject
        {
            ["status"] = "code_sent",
            ["instruction"] = "Tell the caller, in one or two short sentences: you are texting a six digit code to their phone now, and after you stop talking they should type the code on the keypad, then press pound. Do not ask them to say it. Then stop talking.",
        }.ToJsonString(), EndCall: true);
    }
}

public sealed class SetGuestNameTool : IVoiceTool
{
    public string Name => "set_guest_name";
    public JsonObject Definition => ToolKit.Def(Name, "Record the name of a caller who orders without an account.", new JsonObject { ["name"] = ToolKit.Prop("string", "The caller's full name as they said it.") }, "name");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(ToolKit.Str(args, "name") ?? "", @"\s+", " ").Trim();
        if (name.Length is < 2 or > 60 || name.Any(char.IsControl) || name.Any(char.IsDigit))
            return Task.FromResult(ToolKit.Error("bad_name", "That did not sound like a name: ask them to say it again."));
        ctx.State.SetGuestName(name);
        return Task.FromResult(ToolKit.Json(new JsonObject { ["status"] = "saved", ["instruction"] = "Read the name back once to be sure." }));
    }
}

/// <summary>Reads the category tree through the same handler as the web (see <see cref="InProcessCatalog.CategoriesAsync"/>): only categories that have something to buy, in the tree's own order.</summary>
internal static class VoiceCategories
{
    internal sealed record Node(string Slug, string Name, int Count, List<Node> Children);

    public static async Task<List<Node>?> LoadAsync(InProcessCatalog catalog, CancellationToken ct)
    {
        using var doc = await catalog.CategoriesAsync(ct);
        return doc is null ? null : doc.RootElement.EnumerateArray().Select(Read).ToList();
    }

    private static Node Read(JsonElement e) => new(e.GetProperty("slug").GetString() ?? "", e.GetProperty("name").GetString() ?? "", e.GetProperty("listingCount").GetInt32(),
        e.GetProperty("children").EnumerateArray().Select(Read).ToList());

    /// <summary>The node whose slug or spoken name matches (letters and digits only, any case), searching the whole tree; null when nothing with products matches.</summary>
    public static Node? Find(List<Node> tree, string asked)
    {
        var want = Norm(asked);
        if (want.Length == 0) return null;
        Node? byName = null;
        foreach (var n in Walk(tree))
        {
            if (Norm(n.Slug) == want) return n;
            if (byName is null && Norm(n.Name) == want) byName = n;
        }
        return byName;
    }

    public static async Task<string?> ResolveAsync(InProcessCatalog catalog, string asked, CancellationToken ct)
        => await LoadAsync(catalog, ct) is { } tree ? Find(tree, asked)?.Slug : null;

    private static IEnumerable<Node> Walk(IEnumerable<Node> nodes) { foreach (var n in nodes) { yield return n; foreach (var c in Walk(n.Children)) yield return c; } }
    private static string Norm(string s) => new(s.Replace("&", " and ").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());   // "Food & Drink" is what a caller says as "food and drink"
}

/// <summary>What Solvelt sells, by category: the top level first, then one level down on request. Only categories with something that can be bought now.</summary>
public sealed class ListCategoriesTool(InProcessCatalog catalog) : IVoiceTool
{
    private const int PerAnswer = 3;
    public string Name => "list_categories";
    public JsonObject Definition => ToolKit.Def(Name,
        "List the kinds of products Solvelt sells, by category. With no parent it gives the top-level categories; with a parent (a slug from an earlier answer, or the name the caller used) it gives the categories inside it. Returns at most three at a time; use skip to hear more.",
        new JsonObject
        {
            ["parent"] = ToolKit.Prop("string", "Only to look inside one category: its slug from an earlier list_categories answer, or the name as the caller said it."),
            ["skip"] = ToolKit.Prop("integer", "How many of the categories already read out to skip, to hear the next ones. Leave out the first time."),
        });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var tree = await VoiceCategories.LoadAsync(catalog, ct);
        if (tree is null) return ToolKit.Error("categories_unavailable", "Say the list did not load and offer to try again, or to search for something by name.");
        var parentAsked = ToolKit.Str(args, "parent")?.Trim();
        VoiceCategories.Node? parent = null;
        var level = tree;
        if (!string.IsNullOrEmpty(parentAsked))
        {
            parent = VoiceCategories.Find(tree, parentAsked);
            if (parent is null) return ToolKit.Error("unknown_category", "There is no such category with products for sale. Offer the top-level list instead (call list_categories with no parent).");
            level = parent.Children;
        }
        var skip = Math.Max(0, ToolKit.Int(args, "skip", 0));
        var page = level.Skip(skip).Take(PerAnswer).ToList();
        var more = Math.Max(0, level.Count - skip - page.Count);
        ctx.State.SetTask("shopping");
        var items = new JsonArray();
        foreach (var n in page) items.Add(new JsonObject { ["slug"] = n.Slug, ["name"] = VoiceSpeech.Plain(n.Name, 60), ["has_subcategories"] = n.Children.Count > 0 });
        string instruction;
        if (level.Count == 0) instruction = parent is null ? "Say there is nothing for sale right now." : "Say this category has no subcategories; offer to search it (search_products with this category).";
        else if (page.Count == 0) instruction = "That was all of them: say so and ask what they would like.";
        else instruction = "Say at most these names, then" + (more > 0 ? $" offer to read more (there are {more} more; call again with skip {skip + page.Count})," : "") + " ask which one they want to explore. To see what is in one, search_products with its slug as category, or list_categories with it as parent for its subcategories.";
        return ToolKit.Json(new JsonObject
        {
            ["parent"] = parent is null ? null : VoiceSpeech.Plain(parent.Name, 60),
            ["total"] = level.Count,
            ["categories"] = items,
            ["say"] = page.Count == 0 ? "" : VoiceSpeech.List(page.Select(n => VoiceSpeech.Plain(n.Name, 60)).ToList()),
            ["more"] = more,
            ["instruction"] = instruction,
        });
    }
}

public sealed class SearchProductsTool(InProcessCatalog catalog) : IVoiceTool
{
    public string Name => "search_products";
    public JsonObject Definition => ToolKit.Def(Name, "Search the marketplace's products. Returns at most three, with positions the caller can refer to.",
        new JsonObject
        {
            ["query"] = ToolKit.Prop("string", "What the caller is looking for, in their words. May be left out when the caller only named a category (\"what do you have in kitchen?\")."),
            ["category"] = ToolKit.Prop("string", "Only to search inside one category: its slug from list_categories, or the name as the caller said it."),
            ["max_price_dollars"] = ToolKit.Prop("integer", "Only if the caller named a ceiling in whole dollars."),
        });

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var text = ToolKit.Str(args, "query")?.Trim() ?? "";
        var categoryAsked = ToolKit.Str(args, "category")?.Trim();
        if (text.Length > 100 || (text.Length == 0 && string.IsNullOrEmpty(categoryAsked))) return ToolKit.Error("bad_query", "Ask what they are looking for, or which category.");
        string? slug = null;
        if (!string.IsNullOrEmpty(categoryAsked))
        {
            slug = await VoiceCategories.ResolveAsync(catalog, categoryAsked, ct);
            if (slug is null) return ToolKit.Error("unknown_category", "There is no such category with products for sale. Call list_categories to see the real ones, or search without a category.");
        }
        var maxDollars = ToolKit.Int(args, "max_price_dollars", 0);
        using var page = await catalog.SearchAsync(text, 3, maxDollars > 0 ? maxDollars * 100L : null, ct, slug);
        if (page is null) return ToolKit.Error("search_unavailable", "Say the search did not work and offer to try again.");
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToList();
        var total = page.RootElement.GetProperty("total").GetInt32();
        var refs = items.Select(i => new ProductRef(i.GetProperty("id").GetGuid(), i.GetProperty("slug").GetString() ?? "", i.GetProperty("title").GetString() ?? "")).ToList();
        ctx.State.SetResults(refs);
        ctx.State.SetTask("shopping");
        var results = new JsonArray();
        var lines = new List<string>();
        for (var n = 0; n < items.Count; n++)
        {
            var from = items[n].GetProperty("priceFromCents").GetInt64(); var to = items[n].GetProperty("priceToCents").GetInt64();
            var price = from == to ? VoiceSpeech.Money(from) : "from " + VoiceSpeech.Money(from);
            var title = VoiceSpeech.Plain(refs[n].Title, 80);
            results.Add(new JsonObject { ["position"] = n + 1, ["name"] = title, ["price_spoken"] = price, ["seller"] = VoiceSpeech.Plain(items[n].GetProperty("seller").GetProperty("displayName").GetString(), 40) });
            lines.Add($"{title}, {price}");
        }
        var more = total - items.Count;
        return ToolKit.Json(new JsonObject
        {
            ["found"] = total,
            ["results"] = results,
            ["say"] = items.Count == 0 ? "I couldn't find anything for that." : VoiceSpeech.List(lines) + (more > 0 && total > 3 ? "" : ""),
            ["instruction"] = items.Count == 0 ? "Say nothing was found and ask for different words." : "Read at most these options by name and price, then ask which one they want to hear more about. Offer more only if found is larger than the number listed.",
        });
    }
}

public sealed class DescribeProductTool(InProcessCatalog catalog) : IVoiceTool
{
    public string Name => "describe_product";
    public JsonObject Definition => ToolKit.Def(Name, "Describe one of the products last read out: what it is, its options and prices, and whether it is in stock.",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "The position of the product in the last search results (1, 2 or 3).") }, "position");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var pos = ToolKit.Int(args, "position", 0);
        var results = ctx.State.LastResults;
        if (pos < 1 || pos > results.Count) return ToolKit.Error("no_such_position", "Ask which of the products they mean, or search again.");
        var product = results[pos - 1];
        using var detail = await catalog.DetailAsync(product.ListingId, ct);
        if (detail is null) return ToolKit.Error("not_available", "Say that product is no longer available.");
        var d = detail.RootElement;
        var variants = d.GetProperty("variants").EnumerateArray().Select(v => new VariantRef(v.GetProperty("id").GetGuid(), v.GetProperty("name").GetString() ?? "", v.GetProperty("priceCents").GetInt64(), v.GetProperty("available").GetBoolean())).ToList();
        ctx.State.SetCurrentProduct(product, variants);
        var options = new JsonArray();
        for (var n = 0; n < Math.Min(variants.Count, 5); n++)
            options.Add(new JsonObject { ["position"] = n + 1, ["name"] = VoiceSpeech.Plain(variants[n].Name, 60), ["price_spoken"] = VoiceSpeech.Money(variants[n].PriceCents), ["in_stock"] = variants[n].Available });
        return ToolKit.Json(new JsonObject
        {
            ["name"] = VoiceSpeech.Plain(product.Title, 80),
            ["about"] = VoiceSpeech.Plain(d.GetProperty("summary").GetString(), 240),
            ["in_stock"] = d.GetProperty("available").GetBoolean(),
            ["options"] = options,
            ["instruction"] = "Describe it in one or two short sentences. If there is more than one option, name at most three with prices and ask which. If it is not in stock, say so.",
        });
    }
}

/// <summary>The caller's cart, through the existing cart service: an account cart once verified in this call, otherwise a guest cart held by a token the server keeps.</summary>
internal static class CartAccess
{
    public static CartOwner Owner(VoiceCallState s) => s.Level == CallerLevel.Verified && s.VerifiedInThisCall && s.AccountId is { } id ? new CartOwner(id, null) : new CartOwner(null, s.CartToken);

    public static void Remember(VoiceCallState s, CartView v)
    {
        if (v.CartToken is { } t && s.Level != CallerLevel.Verified) s.SetCartToken(t);
        s.SetCart("cart", v.ItemCount);
    }

    public static IReadOnlyList<CartLineView> Lines(CartView v) => v.Groups.SelectMany(g => g.Lines).ToList();

    public static JsonObject Describe(CartView v)
    {
        var lines = Lines(v);
        var arr = new JsonArray();
        for (var n = 0; n < Math.Min(lines.Count, 3); n++)
            arr.Add(new JsonObject
            {
                ["position"] = n + 1, ["name"] = VoiceSpeech.Plain(lines[n].Title, 80) + (string.IsNullOrWhiteSpace(lines[n].VariantName) ? "" : ", " + VoiceSpeech.Plain(lines[n].VariantName, 40)),
                ["quantity"] = lines[n].Quantity, ["price_each_spoken"] = VoiceSpeech.Money(lines[n].UnitPriceCents), ["available"] = lines[n].Status == CartLineStatus.Ok,
            });
        return new JsonObject
        {
            ["items_in_cart"] = v.ItemCount, ["lines"] = arr, ["lines_not_listed"] = Math.Max(0, lines.Count - 3),
            ["subtotal_spoken"] = VoiceSpeech.Money(v.SubtotalCents),
            ["instruction"] = "Subtotal only: delivery and tax come later. Say the item count and subtotal in one sentence.",
        };
    }
}

public sealed class AddToCartTool(CartService cart, InProcessCatalog catalog, IOptions<VoiceOptions> options) : IVoiceTool
{
    public string Name => "add_to_cart";
    public JsonObject Definition => ToolKit.Def(Name, "Add a product to the cart. Use the position from the last search; if the product has several options, also the option position from describe_product.",
        new JsonObject { ["position"] = ToolKit.Prop("integer", "Position of the product in the last search results."), ["option_position"] = ToolKit.Prop("integer", "Position of the option, when the product has several."), ["quantity"] = ToolKit.Prop("integer", "How many; 1 if the caller did not say.") }, "position");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        var pos = ToolKit.Int(args, "position", 0);
        var qty = ToolKit.Int(args, "quantity", 1);
        if (pos < 1 || pos > s.LastResults.Count) return ToolKit.Error("no_such_position", "Ask which product they mean, or search again.");
        if (qty is < 1 or > CartLimits.MaxQuantityPerLine) return ToolKit.Error("bad_quantity", $"Quantity must be from one to {CartLimits.MaxQuantityPerLine}.");
        var product = s.LastResults[pos - 1];
        if (s.CurrentProduct?.ListingId != product.ListingId)
        {
            using var detail = await catalog.DetailAsync(product.ListingId, ct);
            if (detail is null) return ToolKit.Error("not_available", "Say that product is no longer available.");
            s.SetCurrentProduct(product, detail.RootElement.GetProperty("variants").EnumerateArray().Select(v => new VariantRef(v.GetProperty("id").GetGuid(), v.GetProperty("name").GetString() ?? "", v.GetProperty("priceCents").GetInt64(), v.GetProperty("available").GetBoolean())).ToList());
        }
        var variants = s.CurrentVariants;
        var vpos = ToolKit.Int(args, "option_position", variants.Count == 1 ? 1 : 0);
        if (vpos < 1 || vpos > variants.Count) return ToolKit.Error("option_needed", "This product has several options: use describe_product, read at most three of them, and ask which one.");
        var variant = variants[vpos - 1];
        if (!variant.Available) return ToolKit.Error("out_of_stock", "Say that option is not in stock and offer another option or product.");

        var owner = CartAccess.Owner(s);
        var current = await cart.GetAsync(owner, ct);
        var max = Math.Max(1, options.Value.MaxItems);
        if (current.ItemCount + qty > max) return ToolKit.Error("order_limit", $"A phone order can hold at most {VoiceSpeech.Number(max)} items. Say so and suggest finishing this order, or ordering the rest on the web site.");
        var (status, view) = await cart.AddAsync(owner, variant.VariantId, qty, ct);
        if (status != AddStatus.Ok || view is null)
            return ToolKit.Error(status.ToString().ToLowerInvariant(), status is AddStatus.NotFound or AddStatus.NotPurchasable ? "Say that item cannot be bought right now." : "Say it did not work, and offer to try again.");
        CartAccess.Remember(s, view);
        s.SetTask("shopping");
        var o = CartAccess.Describe(view);
        o["added"] = $"{qty} x {VoiceSpeech.Plain(product.Title, 80)}";
        return ToolKit.Json(o);
    }
}

public sealed class ViewCartTool(CartService cart) : IVoiceTool
{
    public string Name => "view_cart";
    public JsonObject Definition => ToolKit.Def(Name, "Read back what is in the cart now.");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var view = await cart.GetAsync(CartAccess.Owner(ctx.State), ct);
        CartAccess.Remember(ctx.State, view);
        return ToolKit.Json(CartAccess.Describe(view));
    }
}

public sealed class ChangeCartTool(CartService cart, IOptions<VoiceOptions> options) : IVoiceTool
{
    public string Name => "change_cart";
    public JsonObject Definition => ToolKit.Def(Name, "Change the quantity of a line in the cart, or remove it with quantity 0. Positions are those of view_cart.",
        new JsonObject { ["line_position"] = ToolKit.Prop("integer", "Position of the line in the cart as last read out."), ["quantity"] = ToolKit.Prop("integer", "The new quantity; 0 removes the line.") }, "line_position", "quantity");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        var owner = CartAccess.Owner(s);
        var before = await cart.GetAsync(owner, ct);
        var lines = CartAccess.Lines(before);
        var pos = ToolKit.Int(args, "line_position", 0);
        var qty = ToolKit.Int(args, "quantity", -1);
        if (pos < 1 || pos > lines.Count) return ToolKit.Error("no_such_line", "Read the cart again and ask which line they mean.");
        if (qty < 0 || qty > CartLimits.MaxQuantityPerLine) return ToolKit.Error("bad_quantity", $"Quantity must be from zero to {CartLimits.MaxQuantityPerLine}.");
        if (before.ItemCount - lines[pos - 1].Quantity + qty > Math.Max(1, options.Value.MaxItems))
            return ToolKit.Error("order_limit", $"A phone order can hold at most {VoiceSpeech.Number(Math.Max(1, options.Value.MaxItems))} items. Say so.");
        var view = await cart.SetQuantityAsync(owner, lines[pos - 1].Id, qty, ct);
        if (view is null) return ToolKit.Error("no_such_line", "Read the cart again.");
        CartAccess.Remember(s, view);
        var o = CartAccess.Describe(view);
        o["changed"] = qty == 0 ? "removed" : $"now {qty}";
        return ToolKit.Json(o);
    }
}
