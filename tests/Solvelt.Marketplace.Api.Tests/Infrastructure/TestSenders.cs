using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>An email provider that is down: every send fails.</summary>
public sealed class FailingEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct) => throw new InvalidOperationException("provider down for " + message.To);
}

/// <summary>An email provider that never answers; the send ends only when its token is cancelled.</summary>
public sealed class HangingEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}
