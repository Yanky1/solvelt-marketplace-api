using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using System.Text.Json;

namespace Solvelt.Marketplace.Api.Auth;

public static class AuthMailKinds
{
    public const string Verify = "verify", Reset = "reset", Exists = "exists", PasswordChanged = "password-changed";
}

/// <summary>What the outbox holds for an account mail: ids only. No address, name or link: those are read and computed when the mail is sent.</summary>
public sealed record AuthMailPayload(string Kind, Guid AccountId, Guid? TokenId = null);

/// <summary>
/// Sends verify, reset and "already registered" mail. The outbox payload carries ids only, so a pending, dead or backed-up message holds no address and no
/// working link; the link is computed here from <c>Auth:EmailLinkKey</c> when the mail goes out. A link that is already used, replaced or expired, or an account
/// that is gone, is not mailed at all (the message counts as delivered).
/// </summary>
public sealed class AuthMailHandler(MarketplaceDb db, LinkTokens links, IEmailSender sender, TimeProvider time, IOptions<MarketplaceOptions> marketplace, IOptions<EmailOptions> email) : IOutboxHandler
{
    public const string Type = "auth.mail";
    /// <summary>Placeholder rows only (decision M1-65): written already delivered, never dispatched, purged within the hour.</summary>
    public const string DecoyType = "auth.decoy";
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        AuthMailPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<AuthMailPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || p.Kind is not (AuthMailKinds.Verify or AuthMailKinds.Reset or AuthMailKinds.Exists or AuthMailKinds.PasswordChanged)) return OutboxResult.Dead("invalid_payload");

        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == p.AccountId && a.DeletedAt == null && a.Email != null).Select(a => new { a.Email, a.DisplayName }).FirstOrDefaultAsync(ct);
        if (account is null) return OutboxResult.Delivered();   // deleted since: nothing to send
        var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
        EmailMessage mail;
        if (p.Kind == AuthMailKinds.Exists) mail = AuthEmails.AlreadyRegistered(account.Email!, baseUrl);
        else if (p.Kind == AuthMailKinds.PasswordChanged) mail = AuthEmails.PasswordChanged(account.Email!, baseUrl);
        else
        {
            if (p.TokenId is not { } tokenId) return OutboxResult.Dead("invalid_payload");
            var purpose = p.Kind == AuthMailKinds.Verify ? EmailTokenPurpose.Verify : EmailTokenPurpose.Reset;
            var now = time.GetUtcNow();
            var t = await db.EmailTokens.AsNoTracking().Where(x => x.Id == tokenId && x.AccountId == p.AccountId && x.Purpose == purpose && x.UsedAt == null && x.ExpiresAt > now)
                .Select(x => new { x.CreatedAt, x.ExpiresAt }).FirstOrDefaultAsync(ct);
            if (t is null) return OutboxResult.Delivered();     // replaced, used or expired while it waited: do not mail a dead link
            var token = links.Make(purpose, tokenId);
            mail = p.Kind == AuthMailKinds.Verify
                ? AuthEmails.VerifyEmail(account.Email!, account.DisplayName, baseUrl, token, (int)Math.Round((t.ExpiresAt - t.CreatedAt).TotalHours))
                : AuthEmails.PasswordReset(account.Email!, baseUrl, token, (int)Math.Round((t.ExpiresAt - t.CreatedAt).TotalMinutes));
        }
        await EmailSending.SendAsync(sender, mail, email.Value, ct);
        return OutboxResult.Delivered();
    }
}
