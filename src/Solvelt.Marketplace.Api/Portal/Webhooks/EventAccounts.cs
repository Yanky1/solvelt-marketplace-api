using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

/// <summary>
/// Which buyer account a portal event is about (decision M1-82), found centrally from what the event itself names: the marketplace order number (<c>data.order.marketplaceOrderId</c>,
/// or <c>data.marketplaceOrderId</c> for messages), the booking, the quote or the conversation key. Done here, once, instead of in every handler, so a new handler cannot forget it.
/// A reference that names nothing we know, or a guest order, gives null.
/// </summary>
public static class EventAccounts
{
    public static async Task<Guid?> FindAsync(MarketplaceDb db, PortalEvent evt, CancellationToken ct)
    {
        var d = evt.Data;
        if (d.ValueKind != JsonValueKind.Object) return null;
        var orderNumber = EventRefs.Order(d)?.MarketplaceOrderId ?? Str(d, "marketplaceOrderId");
        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            var id = await db.Orders.AsNoTracking().Where(o => o.Number == orderNumber).Select(o => o.AccountId).FirstOrDefaultAsync(ct);
            if (id is not null) return id;
        }
        var booking = EventRefs.Booking(d)?.MarketplaceBookingId;
        if (!string.IsNullOrWhiteSpace(booking))
        {
            var id = await db.Bookings.AsNoTracking().Where(b => b.MarketplaceBookingId == booking).Select(b => (Guid?)b.AccountId).FirstOrDefaultAsync(ct);
            if (id is not null) return id;
        }
        if (d.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.Object && Str(q, "marketplaceQuoteId") is { Length: > 0 } quote)
        {
            var id = await db.Quotes.AsNoTracking().Where(x => x.MarketplaceQuoteId == quote).Select(x => (Guid?)x.AccountId).FirstOrDefaultAsync(ct);
            if (id is not null) return id;
        }
        if (Str(d, "marketplaceConversationId") is { Length: > 0 } conv)
            return await db.MessageThreads.AsNoTracking().Where(t => t.ConversationKey == conv).Select(t => (Guid?)t.AccountId).FirstOrDefaultAsync(ct);
        return null;
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// M1's part of deleting an account for the inbound event store (module "portal-inbound-events" of the PII registry, decision M1-82): the raw bodies of the events that are about the account's
/// orders, bookings, quotes and conversations (message text, relay identity, positions) are dropped. Parked events keep theirs until they are finished (they are replayed from it; a finished event of a
/// deleted account loses it in the same transaction), and the 30-day parked expiry bounds the wait. Row, type, time and status stay (the idempotency guard). Idempotent.
/// </summary>
public sealed class PortalEventsAccountEraser(MarketplaceDb db, TimeProvider time) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        await db.PortalInboundEvents.Where(e => e.AccountId == accountId && e.Status != InboundStatus.Parked && e.PayloadJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadJson, (string?)null).SetProperty(e => e.PayloadPurgedAt, (DateTimeOffset?)now), ct);
    }
}
