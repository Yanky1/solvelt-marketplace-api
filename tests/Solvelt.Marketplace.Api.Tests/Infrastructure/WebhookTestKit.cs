using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>A scripted projection handler. Its side effect is a real database row (a "marker" state), so rollbacks and duplicates are observable.</summary>
public sealed class ScriptedHandler(params string[] types) : IProjectionHandler
{
    public IReadOnlyCollection<string> EventTypes { get; } = types;
    public int Calls;
    public Func<ProjectionContext, Task<HandlerOutcome>>? Script { get; set; }
    public HashSet<string> KnownOrders { get; } = [];

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Script is not null) return await Script(c);
        // default: a stand-in order handler. Unknown order -> parked; otherwise ordered, terminal-aware.
        var mid = c.Event.Data.GetProperty("order").GetProperty("marketplaceOrderId").GetString()!;
        lock (KnownOrders) { if (!KnownOrders.Contains(mid)) return HandlerOutcome.Parked("order", mid); }
        var terminal = c.Event.Type is "order.cancelled" or "order.delivered" or "refund.issued" ? c.Event.Type : null;
        if (!await c.Store.TryAdvanceAsync("order", mid, "status", c.Event.CreatedAt, terminal, ct)) return HandlerOutcome.Skipped("older or terminal");
        c.Db.ProjectionStates.Add(new ProjectionState { AggregateType = "marker", AggregateKey = c.Event.Id, FieldGroup = "applied", LastAppliedAt = c.Event.CreatedAt });
        return HandlerOutcome.Applied();
    }
}

public static class WebhookTestKit
{
    public const string Secret = DevDefaults.PortalWebhookSecret;   // what the Testing app is configured with
    public static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public static string Body(string id, string type, DateTimeOffset createdAt, object? data = null)
        => JsonSerializer.Serialize(new { id, type, createdAt, data = data ?? new { } });

    public static object Order(string mid, Guid? soid = null) => new { order = new { sellerOrderId = soid ?? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), sellerOrderNumber = "SO-2026-000001", marketplaceOrderId = mid } };

    /// <summary>Independent test-side signer: v1=hex HMAC-SHA256(secret, "&lt;ts&gt;.&lt;raw body&gt;").</summary>
    public static string Sign(string secret, string ts, string body)
        => "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + body))).ToLowerInvariant();

    public static HttpRequestMessage Request(TestFactory f, string body, string? eventId = null, string? type = null, string? secret = null,
        string? ts = null, string? sig = null, bool signed = true)
    {
        ts ??= f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, WebhookEndpoint.Route) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!signed) return req;
        using var doc = JsonDocument.Parse(body.Length > 0 && body.TrimStart().StartsWith('{') ? body : "{}");
        eventId ??= doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : "evt-none";
        type ??= doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "x";
        req.Headers.Add("X-Solvelt-Timestamp", ts);
        req.Headers.Add("X-Solvelt-Event-Id", eventId);
        req.Headers.Add("X-Solvelt-Event-Type", type);
        req.Headers.Add("X-Solvelt-Signature", sig ?? Sign(secret ?? Secret, ts, body));
        return req;
    }

    public static async Task<(HttpStatusCode Status, string Json)> Post(HttpClient c, HttpRequestMessage req)
    {
        var res = await c.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    public static T WithDb<T>(TestFactory f, Func<MarketplaceDb, T> read)
    {
        using var scope = f.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<MarketplaceDb>());
    }

    public static string StatusOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;
    public static string CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("code").GetString()!;
}
