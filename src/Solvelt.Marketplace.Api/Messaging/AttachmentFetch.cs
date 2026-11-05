using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// Fetches one seller attachment with a signed GET (<c>portal.attachment-fetch</c>), runs it through <see cref="AttachmentSanitizer"/> and stores the result. A file that fails the checks is marked
/// <c>rejected</c> (the buyer sees it was removed for safety); a busy portal is retried; a 404 means the portal no longer has it (rejected). The portal's declared type and size are never trusted.
/// </summary>
/// <summary>
/// Decision M5-22 (review G6): there is no virus scanner for now. Every file that passed the sanitizer is offered to this seam before it is stored; the default allows it. Files are still
/// served only as downloads with <c>nosniff</c> and a sandboxing CSP. A real scanner (ClamAV...) replaces <see cref="AllowAllAttachmentScanner"/> in DI. An exception retries the fetch.
/// </summary>
public interface IAttachmentScanner
{
    Task<bool> AllowsAsync(byte[] bytes, string contentType, CancellationToken ct);
}

public sealed class AllowAllAttachmentScanner : IAttachmentScanner
{
    public Task<bool> AllowsAsync(byte[] bytes, string contentType, CancellationToken ct) => Task.FromResult(true);
}

public sealed class AttachmentFetchOutboxHandler(MarketplaceDb db, ISignedPortalClient portal, IAttachmentScanner scanner) : IOutboxHandler
{
    public const string Type = "portal.attachment-fetch";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        MessageService.IdPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<MessageService.IdPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || !Guid.TryParse(p.Id, out var id)) return OutboxResult.Dead("invalid_payload");
        var a = await db.MessageAttachments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || a.Status != AttachmentStatuses.Pending) return OutboxResult.Delivered();   // gone with its message, or already done
        var hidden = await db.ThreadMessages.AnyAsync(m => m.Id == a.MessageId && (m.HiddenAt != null || m.RedactedAt != null), ct);
        if (hidden) { a.Status = AttachmentStatuses.Rejected; await db.SaveChangesAsync(ct); return OutboxResult.Delivered(); }

        PortalBytes res;
        try { res = await portal.GetAttachmentAsync(a.PortalAttachmentId, ct); }
        catch (PortalResponseTooLargeException) { return await Reject(a, "too_large", ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
        if (!res.IsSuccess)
            return res.Retryable ? OutboxResult.Retry($"portal_{res.Status}") : res.Status == 404 ? await Reject(a, "gone", ct) : OutboxResult.Dead($"portal_{res.Status}");
        // one picture at a time: decoding is the memory-hungry step, and a drain may hold several files (review P2)
        AttachmentSanitizer.Result r;
        await Gate.WaitAsync(ct);
        try { r = AttachmentSanitizer.Process(res.Body!); }
        finally { Gate.Release(); }
        if (r.Rejected is not null) return await Reject(a, r.Rejected, ct);
        // the seam for a virus scanner (decision M5-22): nothing is Stored before it has said yes
        if (!await scanner.AllowsAsync(r.Bytes!, r.ContentType!, ct)) return await Reject(a, "scanner_refused", ct);

        a.SniffedType = r.ContentType; a.Size = r.Bytes!.Length; a.Status = AttachmentStatuses.Stored;
        db.MessageAttachmentData.Add(new MessageAttachmentData { AttachmentId = a.Id, Bytes = r.Bytes });
        await db.SaveChangesAsync(ct);
        return OutboxResult.Delivered();
    }

    private async Task<OutboxResult> Reject(MessageAttachment a, string why, CancellationToken ct)
    {
        a.Status = AttachmentStatuses.Rejected;
        await db.SaveChangesAsync(ct);
        return OutboxResult.Delivered();   // a verdict, not a failure: nothing to retry
    }
}

public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/messages/attachments/{id:guid}", Get).RequireRateLimiting(RateLimitPolicies.Public).AddEndpointFilter<RequireBuyerFilter>();
        return app;
    }

    /// <summary>
    /// The buyer's own attachment. Always <c>Content-Disposition: attachment</c> (never rendered by our origin), the sniffed type, <c>nosniff</c>, a CSP that allows nothing and
    /// <c>private, no-store</c>; a hidden or redacted message's files are gone.
    /// </summary>
    private static async Task<IResult> Get(Guid id, HttpContext ctx, MarketplaceDb db, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        var buyer = ctx.Buyer();
        var a = await (from x in db.MessageAttachments.AsNoTracking()
                       join m in db.ThreadMessages.AsNoTracking() on x.MessageId equals m.Id
                       join t in db.MessageThreads.AsNoTracking() on m.ThreadId equals t.Id
                       where x.Id == id && t.AccountId == buyer.AccountId && x.Status == AttachmentStatuses.Stored && m.HiddenAt == null && m.RedactedAt == null && t.RedactedAt == null
                       select x).FirstOrDefaultAsync(ct);
        if (a?.SniffedType is null) return Problems.NotFound();
        var data = await db.MessageAttachmentData.AsNoTracking().Where(d => d.AttachmentId == id).Select(d => d.Bytes).FirstOrDefaultAsync(ct);
        if (data is null) return Problems.NotFound();
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        var ext = a.SniffedType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => ".pdf" };
        var name = System.IO.Path.GetFileNameWithoutExtension(a.FileName);
        return Results.File(data, a.SniffedType, (string.IsNullOrWhiteSpace(name) ? "attachment" : name) + ext, enableRangeProcessing: false);   // File(...) with a name is Content-Disposition: attachment
    }
}
