using System.Text.Json;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Account;

public sealed record AccountRedactionPayload(Guid AccountId);

/// <summary>Runs the redaction of a deleted account through the outbox (at least once, with backoff), so an erasure that fails halfway is retried and never forgotten.</summary>
public sealed class AccountRedactionHandler(AccountService accounts, IEnumerable<IAccountEraser> erasers) : IOutboxHandler
{
    public const string Type = "account.redact";
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        AccountRedactionPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<AccountRedactionPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || p.AccountId == Guid.Empty) return OutboxResult.Dead("invalid_payload");
        await accounts.RedactAsync(p.AccountId, erasers, ct);   // already redacted or unknown: nothing to do, which also counts as done
        return OutboxResult.Delivered();
    }
}
