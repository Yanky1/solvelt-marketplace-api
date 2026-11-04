using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Messaging;

public sealed record SendMessageInput(Guid? ThreadId, string? OrderNumber, string? SellerOrderNumber, Guid? ListingId, string? Text, bool AcknowledgeWarning);

/// <summary>
/// The buyer's side of messaging (M5): start or continue a thread, read it, mark it read (receipts to the portal), report a message. Every message is stored BEFORE the portal call (our
/// <c>messageId</c> is the idempotency id) and sent from the outbox. Another buyer's thread or message is a 404.
/// </summary>
public sealed class MessageService(MarketplaceDb db, OutboxWriter outbox, TimeProvider time)
{
    public const int MaxText = 4000;
    public const string SendType = "portal.message", ReceiptType = "portal.message-receipt", ReportType = "portal.content-report";
    public sealed record IdPayload(string Id);
    public sealed record ReceiptPayload(string PortalMessageId, string Status, string? SellerOrderNumber = null, Guid? SellerId = null);

    private static string NewKey(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();

    public async Task<ActionResult> SendAsync(BuyerPrincipal buyer, SendMessageInput input, CancellationToken ct)
    {
        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return ActionResult.Invalid("text", "Write a message.");
        if (text.Length > MaxText) return ActionResult.Invalid("text", $"Keep the message under {MaxText} characters.");
        var warnings = ContactDetails.Find(text);
        if (warnings.Count > 0 && !input.AcknowledgeWarning)
            return new ActionResult(409, Code: "contact_details_warning", Message: "Keep payment and contact details on Solvelt: it protects you. Send anyway?", Errors: warnings.ToDictionary(w => w, w => w));

        var account = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == buyer.AccountId && a.DeletedAt == null).Select(a => new { a.DisplayName }).FirstOrDefaultAsync(ct);
        if (account is null) return ActionResult.NotFound();

        for (var attempt = 0; ; attempt++)
        {
            var (thread, error) = await ResolveThreadAsync(buyer, input, ct);
            if (error is not null) return error;
            var now = time.GetUtcNow().ToUniversalTime();
            var isNew = db.Entry(thread!).State == EntityState.Added;
            var msg = new ThreadMessage { Id = Guid.NewGuid(), MessageKey = NewKey("MG-"), ThreadId = thread!.Id, Direction = MessageDirections.Buyer, Text = text, Status = MessageStatuses.Pending, SentAt = now };
            db.ThreadMessages.Add(msg);
            thread.LastMessageAt = now;
            outbox.Enqueue(SendType, new IdPayload(msg.MessageKey!), "message:" + msg.MessageKey);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) when (isNew && attempt < 2)
            {
                db.ChangeTracker.Clear();   // two first messages raced on the one-thread-per-context key: the second joins the winner's thread
                continue;
            }
            return new ActionResult(202, new { threadId = thread.Id, messageId = msg.Id, status = msg.Status });
        }
    }

    /// <summary>The thread the buyer means: an existing one of theirs, or the one for that seller order or listing (created if it is the first message).</summary>
    private async Task<(MessageThread? Thread, ActionResult? Error)> ResolveThreadAsync(BuyerPrincipal buyer, SendMessageInput i, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        if (i.ThreadId is { } tid)
        {
            var t = await db.MessageThreads.FirstOrDefaultAsync(x => x.Id == tid && x.AccountId == buyer.AccountId, ct);
            return t is null || t.RedactedAt is not null ? (null, ActionResult.NotFound()) : (t, null);
        }
        if (i.OrderNumber is not null)
        {
            var o = await db.Orders.AsNoTracking().Include(x => x.Groups).FirstOrDefaultAsync(x => x.Number == i.OrderNumber && x.AccountId == buyer.AccountId && (x.Status == OrderStatus.Placed || x.Status == OrderStatus.Paid), ct);
            if (o is null) return (null, ActionResult.NotFound());
            var groups = o.Groups.Where(g => g.SellerOrderId != null).ToList();
            var g = i.SellerOrderNumber is null ? (groups.Count == 1 ? groups[0] : null) : groups.FirstOrDefault(x => x.SellerOrderNumber == i.SellerOrderNumber);
            if (g is null) return (null, i.SellerOrderNumber is null && groups.Count > 1 ? ActionResult.Invalid("sellerOrderNumber", "Choose which seller you are writing to.") : ActionResult.NotFound());
            var existing = await db.MessageThreads.FirstOrDefaultAsync(x => x.AccountId == buyer.AccountId && x.GroupId == g.Id, ct);
            if (existing is not null) return existing.RedactedAt is null ? (existing, null) : (null, ActionResult.Conflict("thread_closed", "This conversation has been closed."));
            var t = new MessageThread { Id = Guid.NewGuid(), ConversationKey = NewKey("CV-"), AccountId = buyer.AccountId, Kind = ThreadKinds.Order, OrderId = o.Id, GroupId = g.Id, SellerName = g.SellerName, Subject = $"Order {o.Number}", CreatedAt = now, LastMessageAt = now };
            db.MessageThreads.Add(t);
            return (t, null);
        }
        if (i.ListingId is { } lid)
        {
            var l = await (from x in db.CatalogListings.AsNoTracking() join s in db.CatalogSellers.AsNoTracking() on x.SellerId equals s.Id where x.Id == lid && x.Visible && s.Active select new { x.Id, x.Title, s.DisplayName }).FirstOrDefaultAsync(ct);
            if (l is null) return (null, ActionResult.NotFound());
            var existing = await db.MessageThreads.FirstOrDefaultAsync(x => x.AccountId == buyer.AccountId && x.ListingId == lid, ct);
            if (existing is not null) return existing.RedactedAt is null ? (existing, null) : (null, ActionResult.Conflict("thread_closed", "This conversation has been closed."));
            var t = new MessageThread { Id = Guid.NewGuid(), ConversationKey = NewKey("CV-"), AccountId = buyer.AccountId, Kind = ThreadKinds.Product, ListingId = l.Id, SellerName = l.DisplayName, Subject = l.Title, CreatedAt = now, LastMessageAt = now };
            db.MessageThreads.Add(t);
            return (t, null);
        }
        return (null, ActionResult.Invalid("thread", "Say which order or product this is about."));
    }

    /// <summary>
    /// Marks the seller's messages of a thread read. Each message is claimed by one conditional UPDATE, so a double click or two devices send each <c>read</c> receipt once; the receipt is queued
    /// in the same transaction.
    /// </summary>
    public async Task<ActionResult> MarkReadAsync(BuyerPrincipal buyer, Guid threadId, CancellationToken ct)
    {
        if (!await db.MessageThreads.AnyAsync(t => t.Id == threadId && t.AccountId == buyer.AccountId, ct)) return ActionResult.NotFound();
        var now = time.GetUtcNow().ToUniversalTime();
        var unread = await db.ThreadMessages.AsNoTracking().Where(m => m.ThreadId == threadId && m.Direction == MessageDirections.Seller && m.ReadAt == null && m.RedactedAt == null).Select(m => new { m.Id, m.PortalMessageId }).ToListAsync(ct);
        if (unread.Count == 0) return new ActionResult(204);
        var (sellerOrderNumber, sellerId) = await SellerRefAsync(db, threadId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var m in unread)
        {
            var id = m.Id;
            var claimed = await db.ThreadMessages.Where(x => x.Id == id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, (DateTimeOffset?)now), ct);
            if (claimed == 1 && m.PortalMessageId is { } pid)
                await outbox.TryEnqueueAsync(ReceiptType, new ReceiptPayload(pid, "read", sellerOrderNumber, sellerId), $"receipt:{pid}:read", ct: ct);
        }
        await tx.CommitAsync(ct);
        return new ActionResult(204);
    }

    /// <summary>Reports a message of the buyer's own thread to the portal's moderation queue (idempotent: one report id per message).</summary>
    public async Task<ActionResult> ReportAsync(BuyerPrincipal buyer, Guid messageId, string? reason, string? note, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200) return ActionResult.Invalid("reason", "Say what is wrong (up to 200 characters).");
        if (note is { Length: > 1000 }) return ActionResult.Invalid("note", "Keep the note under 1000 characters.");
        var m = await (from x in db.ThreadMessages join t in db.MessageThreads on x.ThreadId equals t.Id where x.Id == messageId && t.AccountId == buyer.AccountId select x).FirstOrDefaultAsync(ct);
        if (m is null || m.RedactedAt is not null) return ActionResult.NotFound();
        if (m.ReportedAt is not null) return new ActionResult(202, new { reported = true });   // idempotent
        m.ReportedAt = time.GetUtcNow().ToUniversalTime();
        // the portal's target id is the marketplace message id: ours for a buyer message, the portal's own for a seller message
        var target = m.MessageKey ?? m.PortalMessageId!;
        var (sellerOrderNumber, sellerId) = await SellerRefAsync(db, m.ThreadId, ct);
        outbox.Enqueue(ReportType, new ReportPayload("MR-" + target, target, reason.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ReporterRef(buyer.AccountId), sellerOrderNumber, sellerId), "message-report:" + m.Id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // a parallel click reported it first
        return new ActionResult(202, new { reported = true });
    }

    public sealed record ReportPayload(string ReportId, string TargetId, string Reason, string? Note, string ReporterRef, string? SellerOrderNumber = null, Guid? SellerId = null);

    /// <summary>
    /// Message ids are unique per seller only (portal N3): a receipt or a report that names a message must also name its seller, or the portal can answer <c>422 ambiguous_message</c> /
    /// <c>ambiguous_target</c>. Order threads give the seller order number and the seller id of the seller order; product threads give the seller id of the listing.
    /// </summary>
    internal static async Task<(string? SellerOrderNumber, Guid? SellerId)> SellerRefAsync(MarketplaceDb db, Guid threadId, CancellationToken ct)
    {
        var t = await db.MessageThreads.AsNoTracking().Where(x => x.Id == threadId).Select(x => new { x.GroupId, x.ListingId }).FirstOrDefaultAsync(ct);
        if (t is null) return (null, null);
        if (t.GroupId is { } gid) { var g = await db.OrderGroups.AsNoTracking().Where(x => x.Id == gid).Select(x => new { x.SellerOrderNumber, x.SellerId }).FirstOrDefaultAsync(ct); return (g?.SellerOrderNumber, g?.SellerId); }
        if (t.ListingId is { } lid) return (null, await db.CatalogListings.AsNoTracking().Where(x => x.Id == lid).Select(x => (Guid?)x.SellerId).FirstOrDefaultAsync(ct));
        return (null, null);
    }

    /// <summary>An opaque, stable reference for the reporter (a hash of the account id: never a name or an address).</summary>
    public static string ReporterRef(Guid accountId) => "b-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("reporter|" + accountId.ToString("N"))))[..24].ToLowerInvariant();
}

// ---------------------------------------------------------------- outbox handlers (portal calls)

public sealed class MessageSendOutboxHandler(MarketplaceDb db, ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [MessageService.SendType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (Id(message) is not { } key) return OutboxResult.Dead("invalid_payload");
        var m = await db.ThreadMessages.FirstOrDefaultAsync(x => x.MessageKey == key, ct);
        if (m is null) return OutboxResult.Dead("message_missing");
        if (m.Status != MessageStatuses.Pending) return OutboxResult.Delivered();
        var t = await db.MessageThreads.AsNoTracking().FirstAsync(x => x.Id == m.ThreadId, ct);
        if (t.RedactedAt is not null || m.RedactedAt is not null) return OutboxResult.Delivered();
        var buyerName = (await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == t.AccountId).Select(a => a.DisplayName).FirstOrDefaultAsync(ct)) ?? "Buyer";
        string? mid = null, son = null; Guid? pid = null;
        if (t.Kind == ThreadKinds.Order)
        {
            var g = await db.OrderGroups.AsNoTracking().FirstAsync(x => x.Id == t.GroupId, ct);
            var o = await db.Orders.AsNoTracking().FirstAsync(x => x.Id == t.OrderId, ct);
            (mid, son) = (o.Number, g.SellerOrderNumber);
        }
        else pid = t.ListingId;
        var body = new BuyerMessageBody(m.MessageKey!, t.ConversationKey, t.Kind, mid, son, pid, buyerName, m.Text, m.SentAt);
        PortalResult<JsonElement> res;
        try { res = await portal.SendMessageAsync(body, ct); }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
        if (res.IsSuccess) { m.Status = MessageStatuses.Sent; await db.SaveChangesAsync(ct); return OutboxResult.Delivered(); }
        var outcome = PortalOutcome.From(res);
        if (outcome.Outcome == OutboxOutcome.Dead) { m.Status = MessageStatuses.Failed; await db.SaveChangesAsync(ct); }   // the buyer sees "not delivered"
        return outcome;
    }

    private static string? Id(OutboxMessage m) { try { return m.Payload is null ? null : JsonSerializer.Deserialize<MessageService.IdPayload>(m.Payload, PortalJson.Options)?.Id; } catch (JsonException) { return null; } }
}

public sealed class MessageReceiptOutboxHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [MessageService.ReceiptType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        MessageService.ReceiptPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<MessageService.ReceiptPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || p.Status is not ("delivered" or "read" or "failed")) return OutboxResult.Dead("invalid_payload");
        try { var res = await portal.SendMessageReceiptAsync(p.PortalMessageId, p.Status, ct, p.SellerOrderNumber, p.SellerId); return res.Status == 404 ? OutboxResult.Delivered() : PortalOutcome.From(res); }   // an unknown message: nothing to acknowledge
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}

public sealed class ContentReportOutboxHandler(ISignedPortalClient portal) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [MessageService.ReportType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        MessageService.ReportPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<MessageService.ReportPayload>(message.Payload, PortalJson.Options); } catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null) return OutboxResult.Dead("invalid_payload");
        try
        {
            var res = await portal.ReportContentAsync(new ContentReportBody(p.ReportId, "message", p.TargetId, p.Reason, p.Note, p.ReporterRef, p.SellerOrderNumber, p.SellerId), ct);
            // 429: the portal limits reports per (caller, target) at 60/min: wait the minute it asks for
            if (res.Status == 429) return OutboxResult.Retry("portal_429", TimeSpan.FromSeconds(60));
            if (res.Status == 409 && res.Error?.Code == "already_resolved") return OutboxResult.Delivered();   // a decision was already made on this report (portal N3): done, nothing to send again
            return PortalOutcome.From(res);   // 422 ambiguous_target / ambiguous_message and every other 4xx: permanent, dead-lettered (and so alerted), never retried
        }
        catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException) { return OutboxResult.Retry("portal_unavailable"); }
    }
}
