using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// <c>message.sent</c>: the seller wrote to the buyer (a reply, or a thread the seller started about an order). The thread is found by OUR ids only: the marketplace order number AND the portal's
/// seller order number we stored at ingest for an order thread; our <c>conversationId</c> (which must also agree with the order or listing) for a product question. A conversation id alone never
/// selects a thread. The message is stored once under the portal's message id; the seller's attachments are listed (the bytes are fetched later); a notification and a <c>delivered</c> receipt follow.
/// Booking messages are matched by M4's booking table (kind <c>booking</c>, not handled here yet).
/// </summary>
public sealed class MessageSentHandler : IProjectionHandler
{
    public const string Type = "message.sent";
    public IReadOnlyCollection<string> EventTypes { get; } = [Type];

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var d = c.Event.Data;
        var pmid = FulfilmentProjector.Str(d, "messageId");
        var text = FulfilmentProjector.Str(d, "text")?.Trim();
        var kind = FulfilmentProjector.Str(d, "kind");
        if (string.IsNullOrWhiteSpace(pmid) || pmid.Length > 100 || string.IsNullOrEmpty(text)) return HandlerOutcome.Skipped("invalid message");
        if (text.Length > MessageService.MaxText) text = text[..MessageService.MaxText];
        if (await c.Db.ThreadMessages.AnyAsync(m => m.PortalMessageId == pmid, ct)) return HandlerOutcome.Applied();   // stored already (the same message under another event id)
        var now = c.Time.GetUtcNow().ToUniversalTime();
        var convKey = FulfilmentProjector.Str(d, "marketplaceConversationId");

        MessageThread? thread;
        string? orderNumber = null, sellerOrderNumber = null;
        if (kind == "order")
        {
            var mid = FulfilmentProjector.Str(d, "marketplaceOrderId");
            var son = FulfilmentProjector.Str(d, "sellerOrderNumber");
            if (string.IsNullOrWhiteSpace(mid)) return HandlerOutcome.Skipped("order reference missing");
            var order = await c.Db.Orders.AsNoTracking().Include(o => o.Groups).FirstOrDefaultAsync(o => o.Number == mid, ct);
            if (order is null) return HandlerOutcome.Skipped("unknown marketplace order");
            if (order.Groups.Any(g => g.SellerOrderId is null)) return HandlerOutcome.Parked("order", order.Number, "seller orders not stored yet");
            var group = son is null ? (order.Groups.Count == 1 ? order.Groups[0] : null) : order.Groups.FirstOrDefault(g => g.SellerOrderNumber == son);   // equal numbers only
            if (group is null) return HandlerOutcome.Skipped("no such seller order on this order");
            thread = await c.Db.MessageThreads.FirstOrDefaultAsync(t => t.GroupId == group.Id && t.AccountId == order.AccountId, ct);
            if (thread is null)
            {
                // a seller-started thread: ours is created now; the buyer's first reply joins it
                thread = new MessageThread { Id = Guid.NewGuid(), ConversationKey = "CV-" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant(), AccountId = order.AccountId, Kind = ThreadKinds.Order, OrderId = order.Id, GroupId = group.Id,
                    SellerName = group.SellerName, Subject = $"Order {order.Number}", CreatedAt = now, LastMessageAt = now };
                c.Db.MessageThreads.Add(thread);
            }
            else if (convKey is not null && convKey != thread.ConversationKey) return HandlerOutcome.Skipped("conversation id does not match the order's thread");
            (orderNumber, sellerOrderNumber) = (order.Number, group.SellerOrderNumber);
        }
        else if (kind == "product")
        {
            if (string.IsNullOrWhiteSpace(convKey)) return HandlerOutcome.Skipped("conversation id missing");
            thread = await c.Db.MessageThreads.FirstOrDefaultAsync(t => t.ConversationKey == convKey && t.Kind == ThreadKinds.Product, ct);
            if (thread is null) return HandlerOutcome.Skipped("unknown product conversation");   // product threads are only ever started by the buyer
            if (FulfilmentProjector.Str(d, "productId") is { } pid && Guid.TryParse(pid, out var g) && thread.ListingId != g) return HandlerOutcome.Skipped("conversation is about another product");
        }
        else return HandlerOutcome.Skipped("unsupported message kind");
        if (thread.RedactedAt is not null) return HandlerOutcome.Skipped("thread closed");

        var sent = FulfilmentProjector.Time(d, "sentAt") is { } t0 && t0 <= now.AddMinutes(5) && t0 > now.AddYears(-1) ? t0 : now;
        var msg = new ThreadMessage { Id = Guid.NewGuid(), PortalMessageId = pmid, ThreadId = thread.Id, Direction = MessageDirections.Seller, Text = text, Status = MessageStatuses.Received, SentAt = sent };
        if (d.TryGetProperty("attachments", out var atts) && atts.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var a in atts.EnumerateArray().Take(5))
            {
                if (!Guid.TryParse(FulfilmentProjector.Str(a, "id"), out var aid)) continue;
                msg.Attachments.Add(new MessageAttachment { Id = Guid.NewGuid(), MessageId = msg.Id, PortalAttachmentId = aid, FileName = SafeName(FulfilmentProjector.Str(a, "fileName")),
                    DeclaredType = FulfilmentProjector.Cap(FulfilmentProjector.Str(a, "contentType"), 100), Size = FulfilmentProjector.Long(a, "size") is { } sz and >= 0 ? sz : 0, Status = AttachmentStatuses.Pending, CreatedAt = now });
            }
        c.Db.ThreadMessages.Add(msg);
        if (sent > thread.LastMessageAt) thread.LastMessageAt = sent;

        var outbox = new OutboxWriter(c.Db, c.Time);
        Notifier.AddForAccount(c.Db, outbox, c.Time, thread.AccountId, NotificationKinds.MessageReceived, c.Event.Id, orderNumber, sellerOrderNumber,
            new Dictionary<string, object?> { ["sellerName"] = thread.SellerName, ["subject"] = thread.Subject, ["threadId"] = thread.Id });
        outbox.Enqueue(MessageService.ReceiptType, new MessageService.ReceiptPayload(pmid, "delivered"), $"receipt:{pmid}:delivered");
        return HandlerOutcome.Applied();
    }

    /// <summary>The name shown for a file: the base name only, no path, no control characters, at most 120 characters.</summary>
    internal static string SafeName(string? name)
    {
        var n = new string((name ?? "").Where(ch => !char.IsControl(ch) && ch is not ('/' or '\\' or '"' or '<' or '>')).ToArray()).Trim();
        return n.Length == 0 ? "attachment" : n.Length > 120 ? n[..120] : n;
    }
}
