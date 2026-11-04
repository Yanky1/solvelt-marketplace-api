using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Notifications;

/// <summary>Creates a notification (always) and queues its email (the preference is checked when it is SENT, so an unsubscribe in between stops it).</summary>
public static class Notifier
{
    public const string MailType = "notification.mail";
    public sealed record MailPayload(Guid NotificationId);

    /// <summary>Adds the entry and its mail message to the caller's unit of work (nothing is saved here). <paramref name="eventId"/> + kind make it once-only.</summary>
    public static Notification Add(MarketplaceDb db, OutboxWriter outbox, TimeProvider time, MarketplaceOrder order, OrderGroup? group, string kind, string eventId, object? parameters = null)
    {
        // A guest order has no account to notify (its mails go to the checkout address, M3); the claim attaches it later.
        if (order.AccountId is not { } accountId) return new Notification { Id = Guid.NewGuid(), Kind = kind, OrderNumber = order.Number, DedupeKey = $"{eventId}:{kind}" };
        var n = new Notification
        {
            Id = Guid.NewGuid(), AccountId = accountId, Kind = kind, OrderNumber = order.Number, SellerOrderNumber = group?.SellerOrderNumber,
            ParamsJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["sellerName"] = group?.SellerName }.Concat(Flatten(parameters)).ToDictionary(kv => kv.Key, kv => kv.Value), PortalJson.Options),
            DedupeKey = $"{eventId}:{kind}", CreatedAt = time.GetUtcNow().ToUniversalTime(),
        };
        db.Notifications.Add(n);
        outbox.Enqueue(MailType, new MailPayload(n.Id), "notification-mail:" + n.Id);
        return n;
    }

    private static IEnumerable<KeyValuePair<string, object?>> Flatten(object? o)
        => o is null ? [] : o.GetType().GetProperties().Select(p => new KeyValuePair<string, object?>(char.ToLowerInvariant(p.Name[0]) + p.Name[1..], p.GetValue(o)));
}

/// <summary>
/// Unsubscribe links: <c>&lt;account id&gt;.&lt;category&gt;.&lt;base64url HMAC-SHA256(Notifications:UnsubscribeKey, "unsub|id|category")&gt;</c>. Its own key (M5-08): a leaked unsubscribe link can switch
/// mail off for one category (or all) of one account and does nothing else; it is never a session or an email-verification link.
/// </summary>
public sealed class UnsubscribeTokens(IOptions<NotificationOptions> options)
{
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.UnsubscribeKey ?? throw new InvalidOperationException("Notifications:UnsubscribeKey is not configured."));

    public string Make(Guid accountId, string category) => $"{accountId:N}.{category}.{Mac(accountId, category)}";

    public (Guid AccountId, string Category)? Parse(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var parts = token.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var id)) return null;
        if (parts[1] != NotificationCategories.All && !NotificationCategories.Switchable.Contains(parts[1])) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(id, parts[1])), Encoding.ASCII.GetBytes(parts[2])) ? (id, parts[1]) : null;
    }

    private string Mac(Guid id, string category)
        => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"unsub|{id:N}|{category}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Sends one notification mail. The outbox payload is the notification id only; the address, the text and the unsubscribe link are produced here, when it goes out. Nothing is sent
/// to a deleted or unverified account, for a category the buyer switched off, or twice.
/// </summary>
public sealed class NotificationMailHandler(MarketplaceDb db, IEmailSender sender, UnsubscribeTokens tokens, IOptions<MarketplaceOptions> marketplace, IOptions<EmailOptions> email) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [Notifier.MailType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Notifier.MailPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Notifier.MailPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        var n = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.NotificationId, ct);
        if (n is null) return OutboxResult.Delivered();
        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == n.AccountId && a.DeletedAt == null && a.Email != null && a.EmailVerifiedAt != null).Select(a => new { a.Email, a.DisplayName }).FirstOrDefaultAsync(ct);
        if (account is null) return OutboxResult.Delivered();
        var category = NotificationKinds.CategoryOf(n.Kind);
        var prefs = await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == n.AccountId, ct);
        if (prefs is not null && !prefs.Wants(category)) return OutboxResult.Delivered();
        var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
        var mail = NotificationEmails.Build(account.Email!, account.DisplayName, n, baseUrl, $"{baseUrl}/unsubscribe?token={tokens.Make(n.AccountId, category)}", $"{baseUrl}/unsubscribe?token={tokens.Make(n.AccountId, NotificationCategories.All)}");
        await EmailSending.SendAsync(sender, mail, email.Value, ct);   // a failure or timeout throws: the outbox retries with backoff
        return OutboxResult.Delivered();
    }
}

public static class NotificationEmails
{
    public static EmailMessage Build(string to, string name, Notification n, string baseUrl, string unsubscribeCategory, string unsubscribeAll)
    {
        string P(string key) { try { using var d = JsonDocument.Parse(n.ParamsJson ?? "{}"); return d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : ""; } catch (JsonException) { return ""; } }
        long Money() { try { using var d = JsonDocument.Parse(n.ParamsJson ?? "{}"); return d.RootElement.TryGetProperty("amountCents", out var v) && v.TryGetInt64(out var x) ? x : 0; } catch (JsonException) { return 0; } }
        var seller = P("sellerName") is { Length: > 0 } s ? s : "the seller";
        var order = n.OrderNumber ?? "your order";
        var (subject, line) = n.Kind switch
        {
            NotificationKinds.OrderAccepted => ($"{seller} accepted order {order}", $"{seller} accepted your order {order} and will pack it soon."),
            NotificationKinds.OrderShipped => ($"Your order {order} has shipped", $"{seller} shipped your order {order}{(P("carrier") is { Length: > 0 } c ? " with " + c : "")}."),
            NotificationKinds.OutForDelivery => ($"Your order {order} is out for delivery", $"Your order {order} from {seller} is out for delivery today."),
            NotificationKinds.OrderDelivered => ($"Your order {order} was delivered", $"Your order {order} from {seller} was delivered."),
            NotificationKinds.OrderCancelled => ($"Order {order} was cancelled", $"Your order {order} from {seller} was cancelled. Any refund due is returned to your original payment method."),
            NotificationKinds.CancellationRejected => ($"{seller} could not cancel order {order}", $"{seller} could not cancel your order {order}."),
            NotificationKinds.ReturnApproved => ($"Your return for {order} was approved", $"{seller} approved your return for order {order}. You can get the return label on the order page."),
            NotificationKinds.ReturnRejected => ($"Your return for {order} was declined", $"{seller} declined your return for order {order}."),
            NotificationKinds.ReturnReceived => ($"{seller} received your return", $"{seller} received your return for order {order}."),
            NotificationKinds.ReturnLabelReady => ($"Your return label for {order} is ready", $"Your return label for order {order} is ready on the order page."),
            NotificationKinds.DeliveryAttemptFailed => ($"A delivery attempt for {order} did not work", $"A delivery attempt for order {order} from {seller} was unsuccessful. The seller will try again or contact you."),
            NotificationKinds.RefundIssued => ($"A refund for order {order}", $"{seller} refunded ${Money() / 100m:0.00} for order {order}. It returns to your original payment method."),
            _ => ($"Update on order {order}", $"There is an update on your order {order}."),
        };
        var body = $"Hello {name},\n\n{line}\n\nSee the details: {baseUrl}/account/orders/{n.OrderNumber}\n\n" +
                   $"You get this email because of your notification settings ({baseUrl}/account/notifications).\nStop these emails: {unsubscribeCategory}\nStop all notification emails: {unsubscribeAll}\n";
        return new EmailMessage(to, subject, body);
    }
}
