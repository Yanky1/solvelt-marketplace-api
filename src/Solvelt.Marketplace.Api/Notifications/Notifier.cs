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
        => AddForAccount(db, outbox, time, order.AccountId, kind, eventId, order.Number, group?.SellerOrderNumber, new Dictionary<string, object?> { ["sellerName"] = group?.SellerName }.Concat(Flatten(parameters)));

    /// <summary>
    /// The same for a booking (called by M4's projection, in its own transaction, after the state change). <paramref name="marketplaceBookingId"/> is our <c>BK-…</c> id; the entry is in the
    /// <c>bookings</c> email category (switchable), the in-app entry is always kept.
    /// </summary>
    public static Notification AddBooking(MarketplaceDb db, OutboxWriter outbox, TimeProvider time, Guid accountId, string kind, string eventId, string bookingNumber, string? marketplaceBookingId, object? parameters = null)
        => AddForAccount(db, outbox, time, accountId, kind, eventId, null, null,
            new Dictionary<string, object?> { ["bookingNumber"] = bookingNumber, ["marketplaceBookingId"] = marketplaceBookingId }.Concat(Flatten(parameters)));

    public static Notification AddForAccount(MarketplaceDb db, OutboxWriter outbox, TimeProvider time, Guid accountId, string kind, string eventId, string? orderNumber, string? sellerOrderNumber, IEnumerable<KeyValuePair<string, object?>> parameters)
    {
        var n = new Notification
        {
            Id = Guid.NewGuid(), AccountId = accountId, Kind = kind, OrderNumber = orderNumber, SellerOrderNumber = sellerOrderNumber,
            ParamsJson = JsonSerializer.Serialize(parameters.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value), PortalJson.Options),
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
/// Unsubscribe links: <c>&lt;account id N&gt;.&lt;category|all&gt;.&lt;expiry, unix seconds&gt;.&lt;base64url HMAC-SHA256(Notifications:UnsubscribeKey, "unsub|id|category|exp")&gt;</c>. Its own key
/// (M5-08): a leaked unsubscribe link can switch mail off for one category (or all) of one account and does nothing else; it is never a session or an email-verification link. It
/// expires after <see cref="Lifetime"/> (M5-17), so an old mail forwarded or leaked years later does nothing; the signed-in preferences page is always there.
/// </summary>
public sealed class UnsubscribeTokens(IOptions<NotificationOptions> options, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);
    public enum State { Invalid, Expired, Ok }

    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.UnsubscribeKey ?? throw new InvalidOperationException("Notifications:UnsubscribeKey is not configured."));

    public string Make(Guid accountId, string category)
    {
        var exp = time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        return $"{accountId:N}.{category}.{exp}.{Mac(accountId, category, exp)}";
    }

    /// <summary>Expired is reported only for a token whose signature is right, so the answer reveals nothing about accounts.</summary>
    public (State State, Guid AccountId, string Category) Parse(string? token)
    {
        var bad = (State.Invalid, Guid.Empty, "");
        if (string.IsNullOrEmpty(token) || token.Length > 200) return bad;
        var parts = token.Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var id) || parts[0] != id.ToString("N")) return bad;   // the canonical (lower-case) id only, M5 review P4
        if (parts[1] != NotificationCategories.All && !NotificationCategories.Switchable.Contains(parts[1])) return bad;
        if (!long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var exp) || parts[2] != exp.ToString(System.Globalization.CultureInfo.InvariantCulture)) return bad;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(id, parts[1], exp)), Encoding.ASCII.GetBytes(parts[3]))) return bad;
        return time.GetUtcNow().ToUnixTimeSeconds() >= exp ? (State.Expired, id, parts[1]) : (State.Ok, id, parts[1]);
    }

    private string Mac(Guid id, string category, long exp)
        => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"unsub|{id:N}|{category}|{exp}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Sends one notification mail. The outbox payload is the notification id only; the address, the text and the unsubscribe link are produced here, when it goes out. Nothing is sent
/// to a deleted or unverified account, for a category the buyer switched off, or twice.
/// </summary>
public sealed class NotificationMailHandler(MarketplaceDb db, IEmailSender sender, UnsubscribeTokens tokens, IOptions<MarketplaceOptions> marketplace, IOptions<EmailOptions> email, IOptions<NotificationOptions> notifications) : IOutboxHandler
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
        if (category != NotificationCategories.Transactional && prefs is not null && !prefs.Wants(category)) return OutboxResult.Delivered();   // transactional mail ignores the preferences (O-21)
        var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
        EmailMessage mail;
        if (category == NotificationCategories.Transactional)
            mail = NotificationEmails.Build(account.Email!, account.DisplayName, n, baseUrl, null, null);   // required mail has nothing to unsubscribe from: no link, no List-Unsubscribe header (M5-25, review X3)
        else
        {
            var catToken = tokens.Make(n.AccountId, category);
            var apiBase = (notifications.Value.PublicApiBaseUrl ?? baseUrl).TrimEnd('/');
            mail = NotificationEmails.Build(account.Email!, account.DisplayName, n, baseUrl, $"{baseUrl}/unsubscribe?token={catToken}", $"{baseUrl}/unsubscribe?token={tokens.Make(n.AccountId, NotificationCategories.All)}",
                $"{apiBase}/api/v1/notifications/unsubscribe?token={catToken}");
        }
        await EmailSending.SendAsync(sender, mail, email.Value, ct);   // a failure or timeout throws: the outbox retries with backoff
        return OutboxResult.Delivered();
    }
}

public static class NotificationEmails
{
    private static string BookingSubject(string kind, string number) => kind switch
    {
        NotificationKinds.BookingConfirmed => $"Booking {number} confirmed", NotificationKinds.BookingDeclined => $"Booking {number} declined", NotificationKinds.BookingTimeProposed => $"New time proposed for booking {number}",
        NotificationKinds.BookingRescheduled => $"Booking {number} rescheduled", NotificationKinds.BookingCancelled => $"Booking {number} cancelled", NotificationKinds.BookingCompleted => $"Booking {number} completed",
        NotificationKinds.BookingReminder => $"Reminder: booking {number}", NotificationKinds.BookingExtraChargeRequested => $"Extra charge requested for booking {number}", NotificationKinds.BookingNoShow => $"Booking {number}: no-show",
        _ => $"Update on booking {number}",
    };

    public static EmailMessage Build(string to, string name, Notification n, string baseUrl, string? unsubscribeCategory, string? unsubscribeAll, string? oneClickUrl = null)
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
            NotificationKinds.MessageReceived => ($"New message from {seller}", $"{seller} sent you a message about {(P("subject") is { Length: > 0 } sub ? sub : "your order")}. Open it in your Solvelt messages."),
            var k when k.StartsWith("booking_") => (BookingSubject(k, P("bookingNumber")), $"There is an update on your booking {P("bookingNumber")}: {BookingSubject(k, P("bookingNumber")).ToLowerInvariant()}."),
            _ => ($"Update on order {order}", $"There is an update on your order {order}."),
        };
        subject = System.Text.RegularExpressions.Regex.Replace(subject, @"[\p{Cc}\u2028\u2029]+", " ").Trim();   // a seller/listing name can never add a header line (review P7)
        if (subject.Length > 200) subject = subject[..(char.IsHighSurrogate(subject[199]) ? 199 : 200)];   // never cut a surrogate pair in half (review X7)
        var link = n.Kind == NotificationKinds.MessageReceived ? $"{baseUrl}/account/messages/{P("threadId")}" : n.Kind.StartsWith("booking_") ? $"{baseUrl}/account/bookings" : $"{baseUrl}/account/orders/{n.OrderNumber}";
        var footer = unsubscribeCategory is null || unsubscribeAll is null
            ? $"This email is required for your orders and account, so it cannot be switched off. You can choose which other emails you get at {baseUrl}/account/notifications/preferences.\n"
            : $"You get this email because of your notification settings ({baseUrl}/account/notifications).\nStop these emails: {unsubscribeCategory}\nStop all notification emails: {unsubscribeAll}\n";
        var body = $"Hello {name},\n\n{line}\n\nSee the details: {link}\n\n" + footer;
        // RFC 8058: a mail client shows its own unsubscribe button and POSTs "List-Unsubscribe=One-Click" to the URL, with no page and no cookie (review P3)
        var headers = oneClickUrl is null ? null : new Dictionary<string, string> { ["List-Unsubscribe"] = $"<{oneClickUrl}>", ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click" };
        return new EmailMessage(to, subject, body, Headers: headers);
    }
}
