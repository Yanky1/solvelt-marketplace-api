using System.Text.Json;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Outbox;

public sealed record PaymentEventPayload(string EventId, string Type, JsonElement Data);

/// <summary>Maps a portal answer to an outbox outcome. Reused by every handler that calls the portal.</summary>
public static class PortalOutcome
{
    public static OutboxResult From<T>(PortalResult<T> r)
    {
        if (r.IsSuccess) return OutboxResult.Delivered();
        // CONTRACT §1.1: never retry 401 or 413. A 401 means our key is wrong (an operator must fix it, then retry from the admin endpoint); a 413 never gets smaller.
        if (r.Status is 401 or 413) return OutboxResult.Dead($"portal_{r.Status}");
        // The portal's limiter is a one-minute fixed window, so a 429 is not tried again sooner than a minute.
        if (r.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
        // Worth sending again later: the contract's retryable answers, a timeout and server errors.
        if (r.Retryable || r.Status is 408 || r.Status >= 500) return OutboxResult.Retry($"portal_{r.Status}");
        // Any other 4xx will not change by waiting (413, 422 such as exceeds_sale, ...): dead-letter it for a person to look at.
        return OutboxResult.Dead($"portal_{r.Status}_{Sanitize(r.Error?.Code)}");
    }

    private static string Sanitize(string? code)
        => string.IsNullOrEmpty(code) ? "error" : new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(60).ToArray());
}

public sealed class PaymentEventOutboxHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public const string Type = "portal.payment-event";
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        PaymentEventPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<PaymentEventPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrWhiteSpace(p.EventId) || p.EventId.Length > 100 || string.IsNullOrWhiteSpace(p.Type) || p.Data.ValueKind != JsonValueKind.Object) return OutboxResult.Dead("invalid_payload");
        PortalResult<PaymentEventResponse> result;
        try { result = await portal.SendPaymentEventAsync(new PaymentEventRequest(p.EventId, p.Type, p.Data), ct); }
        catch (PortalUnavailableException) { return OutboxResult.Retry("portal_unavailable"); }
        catch (PortalNotConfiguredException) { return OutboxResult.Retry("portal_not_configured"); }
        return PortalOutcome.From(result);
    }
}
