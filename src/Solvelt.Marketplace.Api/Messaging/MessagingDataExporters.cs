using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>M5's parts of "download my data" (decision M5-32): messages, notifications and email settings.</summary>
public sealed class MessagingDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "messages";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var threads = await db.MessageThreads.AsNoTracking().Where(t => t.AccountId == accountId).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var ids = threads.Select(t => t.Id).ToList();
        var messages = await db.ThreadMessages.AsNoTracking().Include(m => m.Attachments).Where(m => ids.Contains(m.ThreadId)).OrderBy(m => m.SentAt).ToListAsync(ct);
        return threads.Select(t => new
        {
            kind = t.Kind, subject = t.Subject, sellerName = t.SellerName, createdAt = t.CreatedAt, closed = t.RedactedAt != null,
            messages = messages.Where(m => m.ThreadId == t.Id).Select(m => new
            {
                from = m.Direction, sentAt = m.SentAt, text = m.HiddenAt != null || m.RedactedAt != null ? "" : m.Text, hiddenByModeration = m.HiddenAt != null,
                files = m.Attachments.Select(a => new { fileName = a.FileName, type = a.SniffedType, size = a.Size }),
            }),
        });
    }
}

public sealed class NotificationsDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "notifications";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var items = await db.Notifications.AsNoTracking().Where(n => n.AccountId == accountId).OrderBy(n => n.CreatedAt).Select(n => new { n.Kind, n.OrderNumber, n.SellerOrderNumber, parameters = n.ParamsJson, n.CreatedAt, n.ReadAt }).ToListAsync(ct);
        var p = await db.NotificationPreferences.AsNoTracking().Where(x => x.AccountId == accountId).FirstOrDefaultAsync(ct);
        return new
        {
            notifications = items,
            emailSettings = new Dictionary<string, bool>(NotificationCategories.Switchable.Select(c => new KeyValuePair<string, bool>(c, p?.Wants(c) ?? true))),
        };
    }
}
