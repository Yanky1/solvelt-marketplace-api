using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Outbox;

public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

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

/// <summary>SMTP through the framework client. The password is configuration only and never logged.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        using var client = new System.Net.Mail.SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.SmtpUseTls, Timeout = Math.Max(1, o.SendTimeoutSeconds) * 1000 };
        if (!string.IsNullOrEmpty(o.SmtpUserName)) client.Credentials = new System.Net.NetworkCredential(o.SmtpUserName, o.SmtpPassword);
        using var mail = new System.Net.Mail.MailMessage(o.From!, message.To, message.Subject, message.TextBody);
        if (message.HtmlBody is { } html) mail.AlternateViews.Add(System.Net.Mail.AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Every send is cut off after <see cref="EmailOptions.SendTimeoutSeconds"/>, well inside the outbox lease (the start-up validator keeps it so).</summary>
public static class EmailSending
{
    public static async Task SendAsync(IEmailSender sender, EmailMessage mail, EmailOptions options, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.SendTimeoutSeconds)));
        try { await sender.SendAsync(mail, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The email send timed out."); }
    }
}

public sealed class EmailOutboxHandler(IEmailSender sender, IOptions<EmailOptions> email) : IOutboxHandler
{
    public const string Type = "email.send";
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        EmailMessage? mail;
        try { mail = message.Payload is null ? null : JsonSerializer.Deserialize<EmailMessage>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (mail is null || string.IsNullOrWhiteSpace(mail.To) || mail.Subject is null || mail.TextBody is null) return OutboxResult.Dead("invalid_payload");
        await EmailSending.SendAsync(sender, mail, email.Value, ct);
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
