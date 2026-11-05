using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// "Add to calendar" for the buyer's own booking (RFC 5545). The file carries the service title, the booking number, the time (UTC), the KIND of place (online, at your address, at the
/// provider's) and a link to the booking page. It deliberately carries no address, no meeting link and no personal data: a calendar file is forwarded and synced to other devices,
/// and the confirmed buyer opens the booking page (signed in) for the details (decision M4-12).
/// </summary>
public static class BookingCalendar
{
    private static readonly string[] Included = [BookingStatus.Requested, BookingStatus.Confirmed, BookingStatus.Rescheduled, BookingStatus.InProgress];

    public static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Lines are folded at 75 octets (RFC 5545 §3.1) on character boundaries, with CRLF endings.</summary>
    public static string Fold(string line)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (count + bytes > (sb.Length == 0 || sb[^1] == ' ' && count == 1 ? 75 : 74) && count > 0) { sb.Append("\r\n "); count = 1; }
            sb.Append(rune.ToString()); count += bytes;
        }
        return sb.ToString();
    }

    public static string Build(Booking b, string title, string baseUrl, DateTimeOffset now)
    {
        string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var place = b.Location switch { "online" => "Online", "customer" => "At your address", _ => "At the provider" };
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Solvelt//Bookings//EN", "CALSCALE:GREGORIAN", "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{b.Id:N}@bookings.solvelt",
            $"DTSTAMP:{Utc(now)}",
            $"DTSTART:{Utc(b.StartAt!.Value)}",
            $"DTEND:{Utc(b.EndAt ?? b.StartAt.Value.AddMinutes(Math.Max(15, b.DurationMinutes)))}",
            $"SEQUENCE:{b.Version}",
            $"STATUS:{(b.Status == BookingStatus.Requested ? "TENTATIVE" : "CONFIRMED")}",
            $"SUMMARY:{Escape(title)}",
            $"LOCATION:{Escape(place)}",
            $"DESCRIPTION:{Escape($"Booking {b.Number ?? ""}".Trim() + $"\nDetails and changes: {baseUrl}/account/bookings/{b.Id}")}",
            $"URL:{baseUrl}/account/bookings/{b.Id}",
            "END:VEVENT", "END:VCALENDAR",
        };
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    public static async Task<IResult> Get(Guid id, HttpContext ctx, MarketplaceDb db, TimeProvider time, IOptions<MarketplaceOptions> marketplace, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AccountId == ctx.Buyer().AccountId, ct);
        if (b is null || !Included.Contains(b.Status) || b.StartAt is null) return Problems.NotFound();
        var title = await db.CatalogListings.AsNoTracking().Where(l => l.Id == b.ListingId).Select(l => l.Title).FirstOrDefaultAsync(ct) ?? "Booking";
        var baseUrl = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/');
        ctx.Response.Headers.CacheControl = "private, no-store";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"booking-{FileStem(b)}.ics\"";
        return Results.Text(Build(b, title, baseUrl, time.GetUtcNow()), "text/calendar; charset=utf-8");
    }

    /// <summary>The file name stem: only letters, digits and dashes of the number (it comes from the portal and goes into a header), else the start of the id.</summary>
    internal static string FileStem(Booking b)
    {
        var clean = new string((b.Number ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(40).ToArray());
        return clean.Length > 0 ? clean : b.Id.ToString("N")[..8];
    }
}
