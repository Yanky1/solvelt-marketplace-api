using System.Text;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

public static class WebhookEndpoint
{
    public const string Route = "/api/portal/webhooks";

    public static IEndpointRouteBuilder MapPortalWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, Handle).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> Handle(HttpContext ctx, IOptions<PortalOptions> options, TimeProvider time, PortalEventProcessor processor)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return Problems.Json(503, "webhook_not_configured", "The webhook receiver is not configured.");

        // 1. Raw body, capped at 256 KB (checked on the declared length and while reading).
        if (ctx.Request.ContentLength > WebhookSignature.MaxBodyBytes) return Problems.Json(413, "too_large", "Request body too large.");
        var body = await ReadCapped(ctx.Request.Body, WebhookSignature.MaxBodyBytes, ctx.RequestAborted);
        if (body is null) return Problems.Json(413, "too_large", "Request body too large.");

        // 2. Verify BEFORE parsing anything.
        var sig = WebhookSignature.Verify(secret, ctx.Request.Headers[WebhookSignature.TimestampHeader].ToString(),
            ctx.Request.Headers[WebhookSignature.SignatureHeader].ToString(), body, time.GetUtcNow());
        if (!sig.Ok) return Problems.Json(sig.Status, sig.Code, sig.Message);

        // 3. Parse the signed envelope.
        if (!PortalEvent.TryParse(body, out var evt, out var error)) return Problems.Json(400, "invalid_envelope", error);

        // 4. The signature does not cover the event headers. Idempotency and typing come from the signed body; a header that
        //    disagrees (a captured request replayed with a fresh event id) is refused, never used to bypass the duplicate guard.
        if (ctx.Request.Headers[WebhookSignature.EventIdHeader].ToString() != evt!.Id) return Problems.Json(400, "event_id_mismatch", "X-Solvelt-Event-Id does not match the event id in the body.");
        var typeHeader = ctx.Request.Headers[WebhookSignature.EventTypeHeader].ToString();
        if (typeHeader.Length > 0 && typeHeader != evt.Type) return Problems.Json(400, "event_type_mismatch", "X-Solvelt-Event-Type does not match the event type in the body.");

        // 5. Apply in one transaction; failures propagate as 5xx (ProblemExceptionMiddleware) so the portal retries.
        var outcome = await processor.ProcessAsync(evt, Encoding.UTF8.GetString(body), ctx.RequestAborted);
        return Results.Json(new { status = outcome.ToString().ToLowerInvariant() });
    }

    private static async Task<byte[]?> ReadCapped(Stream s, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > cap) return null;
        }
        return ms.ToArray();
    }
}
