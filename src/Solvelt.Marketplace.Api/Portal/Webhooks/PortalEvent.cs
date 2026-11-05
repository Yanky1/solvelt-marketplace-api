using System.Globalization;
using System.Text.Json;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

/// <summary>Envelope <c>{ id, type, createdAt, data }</c> (CONTRACT.md §1.2). <see cref="Data"/> is a detached clone, safe to keep.</summary>
public sealed record PortalEvent(string Id, string Type, DateTimeOffset CreatedAt, JsonElement Data)
{
    public const int MaxIdLength = 100, MaxTypeLength = 200;
    /// <summary>How far past receipt time an event's <c>createdAt</c> may be before ordering clamps it (small clock differences are normal).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The portal's own <c>createdAt</c> as sent. <see cref="CreatedAt"/> is the ordering time handlers see (clamped, see <see cref="EffectiveTime"/>).</summary>
    public DateTimeOffset RawCreatedAt { get; init; } = CreatedAt;

    /// <summary>Top-level <c>predecessorDead</c> (portal ordering, C14/C15): an earlier event of the same aggregate went dead, so this one means "reconcile" (read the record and apply it).</summary>
    public bool PredecessorDead { get; init; }

    /// <summary>
    /// C17.1: the scope the portal numbers this event in (a string such as <c>review:R-1</c>) and its <c>sequence</c> in that scope (an integer that increases within it). Both or neither: an event without
    /// them (legacy, catalog), or with either malformed, has none, and its handler falls back to arrival order. Parsed here for every event type; a handler decides what to do with them.
    /// </summary>
    public string? SequenceScope { get; init; }
    public long? Sequence { get; init; }

    public const int MaxScopeLength = 200;

    /// <summary>The ordering time: <c>min(createdAt, receivedAt + 5 min)</c>. Dates in the past are never changed.</summary>
    public static DateTimeOffset EffectiveTime(DateTimeOffset raw, DateTimeOffset receivedAt)
    {
        var ceiling = receivedAt.ToUniversalTime() + FutureTolerance;
        var r = raw.ToUniversalTime();
        return r > ceiling ? ceiling : r;
    }

    public static bool TryParse(ReadOnlySpan<byte> body, out PortalEvent? evt, out string error)
    {
        evt = null; error = "";
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "The body must be a JSON object."; return false; }
            var id = Str(root, "id"); var type = Str(root, "type");
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength) { error = "id is required (at most 100 characters)."; return false; }
            if (string.IsNullOrWhiteSpace(type) || type.Length > MaxTypeLength) { error = "type is required (at most 200 characters)."; return false; }
            if (Str(root, "createdAt") is not { } created ||
                !DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            { error = "createdAt is required (ISO 8601)."; return false; }
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            var scope = Str(root, "sequenceScope");
            long? sequence = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number && sq.TryGetInt64(out var n) && n >= 0 ? n : null;
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > MaxScopeLength || sequence is null) { scope = null; sequence = null; }
            evt = new PortalEvent(id, type, at.ToUniversalTime(), data) { RawCreatedAt = at.ToUniversalTime(), PredecessorDead = root.TryGetProperty("predecessorDead", out var pd) && pd.ValueKind == JsonValueKind.True, SequenceScope = scope, Sequence = sequence };
            return true;
        }
        catch (JsonException) { error = "The body is not valid JSON."; return false; }
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Identity of an order or booking inside an event. The matching rule (CONTRACT.md §1.2, review hotspot): a record is matched by
/// the marketplace id we sent AND the portal id the event carries, never by tracking number, SKU or any external id alone.
/// </summary>
public sealed record OrderRef(string MarketplaceOrderId, Guid SellerOrderId, string? SellerOrderNumber);
public sealed record BookingRef(string MarketplaceBookingId, Guid BookingId, string? Number);

public static class EventRefs
{
    /// <summary>
    /// The matching rule as code (review hotspot): an event belongs to a stored order only when the marketplace id AND the portal id it carries are
    /// <b>equal</b> to the stored ones. A portal id that is merely present, or a tracking number or SKU that happens to match, is not a match: another
    /// seller's order could otherwise be altered by an event that names our marketplace order id. Every order handler must use this (or the booking twin).
    /// </summary>
    public static bool Matches(OrderRef? eventRef, string storedMarketplaceOrderId, Guid storedSellerOrderId)
        => eventRef is not null && string.Equals(eventRef.MarketplaceOrderId, storedMarketplaceOrderId, StringComparison.Ordinal) && eventRef.SellerOrderId == storedSellerOrderId;

    /// <summary>The booking twin of <see cref="Matches(OrderRef?, string, Guid)"/>.</summary>
    public static bool Matches(BookingRef? eventRef, string storedMarketplaceBookingId, Guid storedBookingId)
        => eventRef is not null && string.Equals(eventRef.MarketplaceBookingId, storedMarketplaceBookingId, StringComparison.Ordinal) && eventRef.BookingId == storedBookingId;

    /// <summary><c>data.order = {sellerOrderId, sellerOrderNumber, marketplaceOrderId}</c>; null unless both ids are present.</summary>
    public static OrderRef? Order(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("order", out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var mid = Str(o, "marketplaceOrderId");
        if (string.IsNullOrWhiteSpace(mid) || !Guid.TryParse(Str(o, "sellerOrderId"), out var soid)) return null;
        return new OrderRef(mid, soid, Str(o, "sellerOrderNumber"));
    }

    /// <summary><c>data.booking = {bookingId, number, marketplaceBookingId}</c>; null unless both ids are present.</summary>
    public static BookingRef? Booking(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("booking", out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var mid = Str(o, "marketplaceBookingId");
        if (string.IsNullOrWhiteSpace(mid) || !Guid.TryParse(Str(o, "bookingId"), out var bid)) return null;
        return new BookingRef(mid, bid, Str(o, "number"));
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
