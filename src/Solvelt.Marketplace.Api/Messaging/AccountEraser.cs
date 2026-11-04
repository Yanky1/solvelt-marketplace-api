using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// M5's part of deleting an account (M1's <see cref="IAccountEraser"/>, decision M5-27; review G5): the account's notifications and email preferences are deleted, and every one of its message
/// threads is redacted AT ONCE, whatever the state of the order (an order thread whose seller order never ends would otherwise keep the texts for ever): texts emptied, files and their names
/// deleted, the thread closed. Safe to run again.
/// </summary>
public sealed class MessagingAccountEraser(MarketplaceDb db, TimeProvider time) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        await db.Notifications.Where(n => n.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.NotificationPreferences.Where(p => p.AccountId == accountId).ExecuteDeleteAsync(ct);
        var threads = db.MessageThreads.Where(t => t.AccountId == accountId);
        var messages = db.ThreadMessages.Where(m => threads.Any(t => t.Id == m.ThreadId));
        await db.MessageAttachmentData.Where(d => db.MessageAttachments.Any(a => a.Id == d.AttachmentId && messages.Any(m => m.Id == a.MessageId))).ExecuteDeleteAsync(ct);
        await db.MessageAttachments.Where(a => messages.Any(m => m.Id == a.MessageId)).ExecuteDeleteAsync(ct);
        await messages.ExecuteUpdateAsync(s => s.SetProperty(m => m.Text, "").SetProperty(m => m.RedactedAt, m => m.RedactedAt ?? now).SetProperty(m => m.ModerationNote, (string?)null), ct);
        await threads.Where(t => t.RedactedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RedactedAt, (DateTimeOffset?)now), ct);
    }
}
