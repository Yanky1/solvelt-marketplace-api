using System.Collections.Concurrent;
using System.Text.Json;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Outbox;

public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

public class EmailNotConfiguredException() : Exception("No email provider is configured.");

public interface IEmailSender
{
    /// <summary>Throws on failure; the outbox retries with backoff.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Development/Testing only: keeps the last messages in memory so a developer or a test can read a verification link. Never registered elsewhere.</summary>
public sealed class DevEmailSink : IEmailSender
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _sent.Enqueue(message);
        while (_sent.Count > Capacity && _sent.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public void Clear() { while (_sent.TryDequeue(out _)) { } }
}

/// <summary>Production placeholder until a provider is chosen (decision M1-50): every send fails, so messages wait in the outbox and are delivered once one is configured.</summary>
public sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct) => throw new EmailNotConfiguredException();
}

public sealed class EmailOutboxHandler(IEmailSender sender) : IOutboxHandler
{
    public const string Type = "email.send";
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        EmailMessage? email;
        try { email = message.Payload is null ? null : JsonSerializer.Deserialize<EmailMessage>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (email is null || string.IsNullOrWhiteSpace(email.To) || email.Subject is null || email.TextBody is null) return OutboxResult.Dead("invalid_payload");
        try { await sender.SendAsync(email, ct); }
        catch (EmailNotConfiguredException) { return OutboxResult.Retry("email_not_configured"); }
        return OutboxResult.Delivered();
    }
}

public static class OutboxWriterExtensions
{
    /// <summary>Queues an email. Not saved: the caller's own SaveChanges commits it with the change that causes it. <paramref name="dedupeKey"/> stops the same mail being queued twice.</summary>
    public static OutboxMessage EnqueueEmail(this OutboxWriter w, EmailMessage email, string? dedupeKey = null)
        => w.Enqueue(EmailOutboxHandler.Type, email, dedupeKey);

    /// <summary>Queues a payment event for the portal. The event id is ours and is the dedupe key, so one event is queued once and the portal dedupes on it too.</summary>
    public static OutboxMessage EnqueuePaymentEvent(this OutboxWriter w, string eventId, string type, object data, TimeSpan? delay = null)
        => w.Enqueue(PaymentEventOutboxHandler.Type, new PaymentEventPayload(eventId, type, JsonSerializer.SerializeToElement(data, PortalJson.Options)), $"payment:{eventId}", delay);
}
