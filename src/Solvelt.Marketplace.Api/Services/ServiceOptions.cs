using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Services;

public sealed record AddOnView(string Id, string Name, string? Description, long PriceCents, int ExtraMinutes);
public sealed record IntakeQuestionView(string Id, string Label, string Kind, bool Required, IReadOnlyList<string>? Choices, int? MaxLength);
public sealed record ProviderView(Guid ProviderId, string DisplayName, Guid? PhotoImageId);
public sealed record PolicyView(int? CancelFreeHours, int? CancelFeePercent, int? NoShowFeePercent, int? MaxReschedules, int? ApprovalHours);
public sealed record ServiceOptionsView(bool Available, string? Version, IReadOnlyList<AddOnView> AddOns, IReadOnlyList<IntakeQuestionView> IntakeQuestions, IReadOnlyList<ProviderView> Providers,
    string? Assignment, PolicyView? Policy, long? TravelFeeCents, IReadOnlyList<string>? ServiceAreaPostalCodes, double? ServiceRadiusKm)
{
    public static ServiceOptionsView None { get; } = new(false, null, [], [], [], null, null, null, null, null);
}

/// <summary>What the booking widget needs besides slots: add-ons, the intake form, who can be chosen, the cancellation policy (decision M4-11).</summary>
public static class ServiceOptionsReader
{
    private static readonly string[] Kinds = ["text", "longText", "choice", "yesNo", "number"];
    private static readonly string[] Assignments = ["leastBusy", "roundRobin", "buyerChoice"];
    private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
    private static bool SafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 100 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    private static int? Pct(int? v) => v is >= 0 and <= 100 ? v : null;
    private static int? Pos(int? v, int max) => v is >= 0 and <= 100_000 && v <= max ? v : null;

    /// <summary>The portal's document, cut down to what we show: bounded lists and strings, whitelisted kinds, plausible numbers. Nothing the portal sends is passed through unchecked.</summary>
    public static ServiceOptionsView From(ServiceOptionsDocument d) => new(true, Clip(d.Version, 100) is { Length: > 0 } v ? v : null,
        (d.AddOns ?? []).Where(a => SafeId(a.Id) && !string.IsNullOrWhiteSpace(a.Name) && a.PriceCents is >= 0 and <= 100_000_000 && a.ExtraMinutes is >= 0 and <= 24 * 60).Take(50)
            .Select(a => new AddOnView(a.Id!, Clip(a.Name, 120), a.Description is null ? null : Clip(a.Description, 500), a.PriceCents, a.ExtraMinutes)).ToList(),
        (d.IntakeQuestions ?? []).Where(q => SafeId(q.Id) && !string.IsNullOrWhiteSpace(q.Label)).Take(30)
            .Select(q => new IntakeQuestionView(q.Id!, Clip(q.Label, 300), Kinds.Contains(q.Kind) ? q.Kind! : "text", q.Required, q.Choices is { Count: > 0 } c ? c.Take(20).Select(x => Clip(x, 100)).ToList() : null, q.MaxLength is > 0 and <= 5000 ? q.MaxLength : null)).ToList(),
        (d.Providers ?? []).Where(p => p.ProviderId != Guid.Empty && !string.IsNullOrWhiteSpace(p.DisplayName)).Take(100).Select(p => new ProviderView(p.ProviderId, Clip(p.DisplayName, 100), p.PhotoImageId)).ToList(),
        d.Assignment is { } a1 && Assignments.Contains(a1) ? a1 : null,
        d.Policy is { } pol ? new PolicyView(Pos(pol.CancelFreeHours, 24 * 365), Pct(pol.CancelFeePercent), Pct(pol.NoShowFeePercent), Pos(pol.MaxReschedules, 100), Pos(pol.ApprovalHours, 24 * 30)) : null,
        d.Travel?.FeeCents is >= 0 and <= 100_000_000 ? d.Travel.FeeCents : null,
        d.Travel?.ServiceAreaPostalCodes is { Count: > 0 } z ? z.Where(x => !string.IsNullOrWhiteSpace(x)).Take(500).Select(x => Clip(x, 12)).ToList() : null,
        d.Travel?.ServiceRadiusKm is > 0 and <= 5000 ? d.Travel.ServiceRadiusKm : null);
}

/// <summary>
/// Service options, cached 5 minutes per service and fetched once for a burst (same single-flight rule as availability; the in-flight call has its own cancellation scope). A portal that
/// does not (yet) know the endpoint answers 404: that is cached for a minute as "no options", so shoppers never hammer it and the widget still works without add-ons or intake questions.
/// A failure is shared with the callers already waiting and never cached.
/// </summary>
public sealed class ServiceOptionsCache(IServiceScopeFactory scopes, TimeProvider time)
{
    private sealed record Entry(ServiceOptionsView View, DateTimeOffset At, TimeSpan Ttl);
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ServiceOptionsView?>>> _inflight = new();
    public int PortalCalls;
    public const int MaxEntries = 5000;
    public int Count => _cache.Count;
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5), Missing = TimeSpan.FromMinutes(1);

    /// <summary>The view, or null when the portal could not answer (callers say 503).</summary>
    public async Task<ServiceOptionsView?> GetAsync(Guid serviceId, CancellationToken ct)
    {
        if (_cache.TryGetValue(serviceId, out var hit) && time.GetUtcNow() - hit.At < hit.Ttl) return hit.View;
        var flight = _inflight.GetOrAdd(serviceId, _ => new Lazy<Task<ServiceOptionsView?>>(() => FetchAsync(serviceId), LazyThreadSafetyMode.ExecutionAndPublication));
        return await flight.Value.WaitAsync(ct);
    }

    private async Task<ServiceOptionsView?> FetchAsync(Guid serviceId)
    {
        try
        {
            if (_cache.TryGetValue(serviceId, out var hit) && time.GetUtcNow() - hit.At < hit.Ttl) return hit.View;
            Interlocked.Increment(ref PortalCalls);
            using var scope = scopes.CreateScope();
            var portal = scope.ServiceProvider.GetRequiredService<ISignedPortalClient>();
            PortalResult<ServiceOptionsDocument> r;
            try { r = await portal.GetServiceOptionsAsync(serviceId, CancellationToken.None); }
            catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException or HttpRequestException or System.Text.Json.JsonException) { return null; }
            if (r.IsSuccess && r.Value is { } doc) { var v = ServiceOptionsReader.From(doc); Trim(); _cache[serviceId] = new Entry(v, time.GetUtcNow(), Fresh); return v; }
            if (r.Status is 404 or 405 or 501) { Trim(); _cache[serviceId] = new Entry(ServiceOptionsView.None, time.GetUtcNow(), Missing); return ServiceOptionsView.None; }
            return null;
        }
        finally { _inflight.TryRemove(serviceId, out _); }
    }

    private void Trim()
    {
        if (_cache.Count < MaxEntries) return;
        var now = time.GetUtcNow();
        foreach (var kv in _cache.Where(k => now - k.Value.At >= k.Value.Ttl).ToList()) _cache.TryRemove(kv.Key, out _);
        if (_cache.Count >= MaxEntries) foreach (var kv in _cache.OrderBy(k => k.Value.At).Take(500).ToList()) _cache.TryRemove(kv.Key, out _);
    }
}

public static class ServiceOptionsEndpoint
{
    /// <summary><c>GET /api/v1/services/{listingId}/options</c>: public, visible service listings only.</summary>
    public static async Task<IResult> Get(Guid id, HttpContext http, MarketplaceDb db, ServiceOptionsCache cache, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";   // only the 200 below may be cached
        if (!await ServiceListings.Visible(db).AnyAsync(l => l.Id == id, ct)) return Problems.NotFound("This service is not available.");
        var view = await cache.GetAsync(id, ct);
        if (view is null) return Problems.Json(503, "options_unavailable", "This service's options can't be loaded right now. Try again in a moment.");
        http.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(view);
    }
}
