using System.Text.Json;
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
        var messages = (await db.ThreadMessages.AsNoTracking().Include(m => m.Attachments).Where(m => ids.Contains(m.ThreadId)).OrderBy(m => m.SentAt).ToListAsync(ct)).ToLookup(m => m.ThreadId);
        return threads.Select(t => new
        {
            kind = t.Kind, subject = t.Subject, sellerName = t.SellerName, createdAt = t.CreatedAt, demo = t.IsDemo, closed = t.RedactedAt != null,
            messages = messages[t.Id].Select(m => new
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
        var rows = await db.Notifications.AsNoTracking().Where(n => n.AccountId == accountId).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => new { n.Kind, n.OrderNumber, n.SellerOrderNumber, n.ParamsJson, n.CreatedAt, n.ReadAt, n.IsDemo }).ToListAsync(ct);
        var items = rows.Select(n => new { n.Kind, n.OrderNumber, n.SellerOrderNumber, parameters = Allowed(n.ParamsJson), n.CreatedAt, n.ReadAt, demo = n.IsDemo }).ToList();
        var p = await db.NotificationPreferences.AsNoTracking().Where(x => x.AccountId == accountId).FirstOrDefaultAsync(ct);
        return new
        {
            notifications = items,
            emailSettings = new Dictionary<string, bool>(NotificationCategories.Switchable.Select(c => new KeyValuePair<string, bool>(c, p?.Wants(c) ?? true))),
        };
    }

    /// <summary>The parameters a notification carries that mean something to the buyer. The column also holds our own ids (thread, booking), so only these keys are exported (review L8): a key added later is not in the file until someone decides it belongs there.</summary>
    internal static readonly string[] ExportedParameters = ["sellerName", "subject", "carrier", "amountCents", "bookingNumber"];

    internal static Dictionary<string, object?> Allowed(string? json)
    {
        var o = new Dictionary<string, object?>();
        if (json is null) return o;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return o;
            foreach (var k in ExportedParameters)
                if (d.RootElement.TryGetProperty(k, out var v)) o[k] = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.TryGetInt64(out var n) ? (object)n : v.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (JsonException) { }
        return o;
    }
}
