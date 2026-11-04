using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>What the web does: ask for a quote, then send its hash with the checkout. A body that already carries <c>quoteHash</c> is sent as is; when the quote itself is refused the checkout goes without a hash (so its own error shows).</summary>
public static class QuoteHashes
{
    public static async Task<JsonContent> WithHashAsync(HttpClient http, string? session, object body)
    {
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (node.ContainsKey("quoteHash") || node["address"] is null) return JsonContent.Create(node);
        var quoteBody = new JsonObject { ["address"] = node["address"]!.DeepClone() };
        if (node["selections"] is { } sel) quoteBody["selections"] = sel.DeepClone();
        var q = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/quote") { Content = JsonContent.Create(quoteBody) };
        if (session != null) q.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        var res = await http.SendAsync(q);
        if (res.IsSuccessStatusCode && JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.TryGetProperty("quoteHash", out var h)) node["quoteHash"] = h.GetString();
        return JsonContent.Create(node);
    }
}
