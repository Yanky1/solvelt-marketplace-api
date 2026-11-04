using System.Globalization;
using System.Text.Json;

namespace Solvelt.Marketplace.Api.Portal.Webhooks;

/// <summary>Envelope <c>{ id, type, createdAt, data }</c> (CONTRACT.md §1.2). <see cref="Data"/> is a detached clone, safe to keep.</summary>
public sealed record PortalEvent(string Id, string Type, DateTimeOffset CreatedAt, JsonElement Data)
{
    public const int MaxIdLength = 100, MaxTypeLength = 200;

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
            evt = new PortalEvent(id, type, at.ToUniversalTime(), data);
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
