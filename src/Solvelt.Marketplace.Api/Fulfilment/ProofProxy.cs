using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// Fetches a proof-of-delivery image from the portal with its own short-lived signed link (no secret of ours is sent: the link is the credential). Only the portal origin we are configured
/// with is ever contacted and only a path under <see cref="FulfilmentProjector.ProofPrefix"/>, so a hostile event cannot point us at another host (SSRF). Size-capped, deadline 15 s.
/// </summary>
public sealed class ProofFetcher(HttpClient http)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public sealed record Proof(byte[] Bytes, string ContentType);

    public async Task<Proof?> FetchAsync(string pathAndQuery, CancellationToken ct)
    {
        if (http.BaseAddress is null || !pathAndQuery.StartsWith(FulfilmentProjector.ProofPrefix, StringComparison.Ordinal)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var res = await http.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength > MaxBytes) return null;
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes) return null;
            }
            var bytes = ms.ToArray();
            // The type comes from the bytes, never from the portal's header: only JPEG, PNG and WebP are served.
            return Sniff(bytes) is { } type ? new Proof(bytes, type) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    public static string? Sniff(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }
}

public static class ProofEndpoints
{
    public static IEndpointRouteBuilder MapProofs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/orders/{number}/shipments/{shipmentId:guid}/proof/{index:int}", Get)
            .RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        return app;
    }

    /// <summary>
    /// The buyer's own proof image, proxied. A link past its expiry is never used (and the job nulls it); the response is an inline image of a sniffed type with <c>nosniff</c>
    /// and a CSP that allows nothing, <c>private, no-store</c>: it is never cacheable by a shared cache and never stored by us.
    /// </summary>
    private static async Task<IResult> Get(string number, Guid shipmentId, int index, HttpContext ctx, MarketplaceDb db, ProofFetcher fetcher, TimeProvider time, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        var buyer = ctx.Buyer();
        var sh = await (from s in db.OrderShipments.AsNoTracking()
                        join g in db.OrderGroups.AsNoTracking() on s.GroupId equals g.Id
                        join o in db.Orders.AsNoTracking() on g.OrderId equals o.Id
                        where s.Id == shipmentId && o.Number == number && o.AccountId == buyer.AccountId select s).FirstOrDefaultAsync(ct);
        if (sh?.ProofJson is null || sh.ProofExpiresAt is not { } exp || exp <= time.GetUtcNow()) return Problems.NotFound();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sh.ProofJson) ?? [];
        if (index < 0 || index >= paths.Count) return Problems.NotFound();
        var proof = await fetcher.FetchAsync(paths[index], ct);
        if (proof is null) return Problems.Json(502, "proof_unavailable", "The proof of delivery is not available right now.");
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        ctx.Response.Headers["Content-Disposition"] = "inline; filename=\"proof\"";
        return Results.Bytes(proof.Bytes, proof.ContentType);
    }
}
