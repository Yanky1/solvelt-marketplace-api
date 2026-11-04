using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// <c>listing.upserted</c> / <c>listing.unlisted</c> (CONTRACT.md §2: <c>{listingId, version}</c>). Runs inside the webhook transaction and never
/// calls the network (the webhook must ack fast): an upsert only records the version we now know exists, and the sync job fetches the snapshot.
/// An unlist hides the listing immediately and keeps it hidden against any snapshot of that version or older, however stale the poll page is.
/// Both are ordered by the portal's version, not by event time; an event for a listing we have not seen creates a stub, so a later stale poll page
/// cannot show what an earlier unlist event hid.
/// </summary>
public sealed class CatalogEventHandler : IProjectionHandler
{
    public const string Upserted = "listing.upserted", Unlisted = "listing.unlisted";
    public IReadOnlyCollection<string> EventTypes { get; } = [Upserted, Unlisted];

    /// <summary>A unique placeholder (the slug column is unique); replaced by a real slug when the snapshot arrives.</summary>
    internal static string StubSlug(Guid id) => "~" + id.ToString("N");

    public async Task<HandlerOutcome> HandleAsync(ProjectionContext c, CancellationToken ct)
    {
        var data = c.Event.Data;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("listingId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !Guid.TryParse(idEl.GetString(), out var id)
            || !data.TryGetProperty("version", out var vEl) || vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt64(out var version) || version < 1)
            return HandlerOutcome.Skipped("invalid listing event payload");

        var now = c.Time.GetUtcNow().ToUniversalTime();
        var listing = await c.Db.CatalogListings.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
        {
            listing = new CatalogListing
            {
                Id = id, Status = "Unknown", IsStub = true, Visible = false, FirstSeenAt = now, CreatedAt = now, UpdatedAt = now, Slug = StubSlug(id), Title = "", TitleSort = "",
                KnownVersion = 0, Version = 0, Currency = "USD",
            };
            c.Db.CatalogListings.Add(listing);
        }
        listing.KnownVersion = Math.Max(listing.KnownVersion, version);
        if (c.Event.Type == Unlisted) listing.UnlistedVersion = Math.Max(listing.UnlistedVersion ?? 0, version);
        listing.RecomputeVisibility();
        listing.Row++;
        return HandlerOutcome.Applied();
    }
}
