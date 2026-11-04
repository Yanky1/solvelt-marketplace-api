using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Checkout;

public sealed record QuoteAddress(string? Country, string? Region, string? PostalCode, double? Lat = null, double? Lng = null);
/// <summary>What the buyer CHOSE for a group. There is deliberately no money field anywhere in a quote request.</summary>
public sealed record GroupSelection(Guid SellerId, Guid BrandId, string? Speed, Guid? LocalWindowId, string? LocalDate);
public sealed record QuoteRequest(QuoteAddress? Address, IReadOnlyList<GroupSelection>? Selections);

public sealed record LocalOption(bool Eligible, string? Reason, long? FeeCents, string? TimeZone, IReadOnlyList<DeliveryWindow> Windows);
public sealed record SelectedDelivery(string Kind, string? Speed, Guid? WindowId, string? Date, long ShippingCents);
public sealed record QuoteLine(Guid LineId, Guid ListingId, Guid VariantId, string Title, string VariantName, int Quantity, long UnitPriceCents, long LineTotalCents, long TaxCents);
public sealed record QuoteGroup(Guid SellerId, string SellerName, Guid BrandId, string BrandName, IReadOnlyList<QuoteLine> Lines, long ItemsCents,
    IReadOnlyList<ShippingOption> ShippingOptions, IReadOnlyList<UnavailableSpeed> UnavailableSpeeds, LocalOption? Local, SelectedDelivery? Selected, long ShippingCents, long ShippingTaxCents, long TaxCents, long TotalCents);
public sealed record QuoteView(string Currency, IReadOnlyList<QuoteGroup> Groups, long ItemsCents, long ShippingCents, long TaxCents, long TotalCents, bool CanPay, string? QuoteHash = null, DateTimeOffset? ExpiresAt = null);

/// <summary>Canonical text for hashing: every field length-prefixed, so no value can contain a separator that shifts a neighbour (a name with a `|` can never collide with another split of the same characters).</summary>
public static class Canon
{
    public static StringBuilder Field(this StringBuilder sb, object? value)
    {
        var text = value switch { null => "\u0000", string t => t, IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        return sb.Append(text.Length).Append(':').Append(text).Append(';');
    }
}

/// <summary>
/// Binds a quote to a checkout (decisions M3-22 and M3-30). The token is <c>{expiry}.{HMAC-SHA256(Checkout:QuoteKey, expiry + everything priced)}</c>: it covers every line, unit price, quantity, tax, the
/// chosen delivery, the shipping and the destination, so any change gives another value, and because it is keyed the client cannot compute a digest of its own (or move the expiry). Checkout recomputes the
/// quote and compares in constant time; a token that differs or is past its expiry gets 409 price_changed with the fresh quote. It is consent, never a price.
/// </summary>
public sealed class QuoteSigner(IOptions<CheckoutOptions> options)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public string Digest(QuoteView v, string country, string? region, string postal, long exp)
    {
        var sb = new StringBuilder().Field(exp).Field(v.Currency).Field(country).Field(region).Field(postal.Trim().ToUpperInvariant()).Field(v.TotalCents);
        foreach (var g in v.Groups.OrderBy(g => g.SellerId).ThenBy(g => g.BrandId))
        {
            sb.Field("G").Field(g.SellerId).Field(g.BrandId).Field(g.Selected?.Kind).Field(g.Selected?.Speed).Field(g.Selected?.WindowId).Field(g.Selected?.Date)
              .Field(g.ShippingCents).Field(g.ShippingTaxCents).Field(g.TaxCents);
            foreach (var l in g.Lines.OrderBy(l => l.VariantId)) sb.Field("L").Field(l.VariantId).Field(l.Quantity).Field(l.UnitPriceCents).Field(l.TaxCents);
        }
        var key = Encoding.UTF8.GetBytes(options.Value.QuoteKey ?? throw new InvalidOperationException("Checkout:QuoteKey is not configured."));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public string Make(QuoteView v, string country, string? region, string postal, DateTimeOffset now)
    {
        var exp = now.Add(Lifetime).ToUnixTimeSeconds();
        return exp + "." + Digest(v, country, region, postal, exp);
    }

    public bool Valid(string? token, QuoteView v, string country, string? region, string postal, DateTimeOffset now)
    {
        if (token is null || token.Length > 100) return false;
        var dot = token.IndexOf('.');
        if (dot < 1 || !long.TryParse(token[..dot], out var exp) || exp < now.ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token[(dot + 1)..]), Encoding.ASCII.GetBytes(Digest(v, country, region, postal, exp)));
    }
}

public enum QuoteStatus { Ok, EmptyOrUnavailableCart, InvalidAddress, InvalidSelection, ShippingUnavailable, TotalTooLarge }

/// <summary>
/// The server-side price of a checkout: items from the catalog (re-read now), shipping from the portal's settings, tax from <see cref="ITaxCalculator"/>.
/// Everything is computed here from ids and the buyer's choices; no price, total, tax or time a client sends is read. Money is <c>long</c> cents; the order total
/// is the exact sum of the parts, and the same function builds the order and the payment amount at checkout (the next slice).
/// </summary>
public sealed class QuoteService(MarketplaceDb db, CartService carts, ShippingSettingsCache settings, ISignedPortalClient portal, ITaxCalculator tax,
    IOptions<ShippingOptions> shippingOptions, IOptions<TaxOptions> taxOptions, QuoteSigner signer, TimeProvider time)
{
    public async Task<(QuoteStatus Status, QuoteView? View, string? Detail)> QuoteAsync(CartOwner owner, QuoteRequest req, CancellationToken ct)
    {
        var a = req.Address;
        var country = a?.Country?.Trim().ToUpperInvariant();
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetterUpper) || string.IsNullOrWhiteSpace(a?.PostalCode) || a.PostalCode.Length > 20
            || (a.Region is { } r && (r.Length is < 2 or > 3 || !r.All(char.IsAsciiLetter)))
            || (country == "US" && !UsStates.Codes.Contains(a.Region?.Trim().ToUpperInvariant() ?? "")))
            return (QuoteStatus.InvalidAddress, null, null);
        var region = a.Region?.Trim().ToUpperInvariant();

        var cart = await carts.GetAsync(owner, ct);
        if (!cart.CanCheckout || cart.Groups.Count == 0) return (QuoteStatus.EmptyOrUnavailableCart, null, null);

        var picks = req.Selections ?? [];
        if (picks.GroupBy(p => (p.SellerId, p.BrandId)).Any(g => g.Count() > 1) || picks.Any(p => !cart.Groups.Any(g => g.SellerId == p.SellerId && g.BrandId == p.BrandId)))
            return (QuoteStatus.InvalidSelection, null, "unknown_group");

        var listingIds = cart.Groups.SelectMany(g => g.Lines).Select(l => l.ListingId).Distinct().ToList();
        var facts = await db.CatalogListings.AsNoTracking().Where(l => listingIds.Contains(l.Id)).Select(l => new { l.Id, l.CategoryId }).ToListAsync(ct);
        var variantIds = cart.Groups.SelectMany(g => g.Lines).Select(l => l.VariantId).Distinct().ToList();
        var variantWeights = await db.CatalogVariants.AsNoTracking().Where(v => variantIds.Contains(v.Id)).Select(v => new { v.Id, v.WeightGrams }).ToDictionaryAsync(v => v.Id, v => v.WeightGrams, ct);
        var attrs = await db.CatalogAttributes.AsNoTracking().Where(x => listingIds.Contains(x.ListingId) && (x.Key == "weight" || x.Key == "delivery")).ToListAsync(ct);
        var categories = await db.CatalogCategories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.Slug }).ToDictionaryAsync(c => c.Id, ct);
        var exemptRoots = taxOptions.Value.ExemptRootCategories;
        string RootSlug(Guid id) { var c = categories.GetValueOrDefault(id); var guard = 0; while (c?.ParentId is { } p && categories.TryGetValue(p, out var up) && guard++ < 20) c = up; return c?.Slug ?? ""; }
        bool Taxable(Guid listingId) => !exemptRoots.Contains(RootSlug(facts.First(f => f.Id == listingId).CategoryId), StringComparer.OrdinalIgnoreCase);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var built = new List<(CartGroupView G, IReadOnlyList<ShippingOption> Opts, IReadOnlyList<UnavailableSpeed> Unavailable, LocalOption? Local, SelectedDelivery? Sel, List<TaxLineInput> TaxLines)>();
        foreach (var g in cart.Groups)
        {
            ShippingSettings s;
            try { s = await settings.GetAsync(g.SellerId, g.BrandId, ct); }
            catch (ShippingUnavailableException) { return (QuoteStatus.ShippingUnavailable, null, null); }
            // The group's weight is known only when EVERY line's is (decision O-15); one unknown makes weight-based speeds unavailable.
            // The weight comes from the variant (portal C7: the product's weight on listing and variant, null = unknown, never 0); the older "weight" attribute is the fallback while a snapshot has none.
            var lineWeights = g.Lines.Select(l => (variantWeights.GetValueOrDefault(l.VariantId) is > 0 and var vw ? vw
                : ShippingRates.Grams(attrs.FirstOrDefault(x => x.ListingId == l.ListingId && x.Key == "weight")?.Value)) is { } gr ? gr * l.Quantity : (long?)null).ToList();
            long? weight = lineWeights.All(w => w is not null) ? lineWeights.Sum(w => w!.Value) : null;
            var priced = ShippingRates.Options(s, country, g.SubtotalCents, weight, today, shippingOptions.Value);
            var opts = priced.Options;

            LocalOption? local = null;
            var allLocal = g.Lines.All(l => attrs.Any(x => x.ListingId == l.ListingId && x.Key == "delivery" && x.Value == "local"));
            // A group whose listings say "delivery: local" and never "ships" is not shipped by carrier (decision M3-22): without an eligible local window there is nothing to pay with, so the quote cannot be paid.
            // Any line that is local-only (says "local" and never "ships") rules carrier delivery out for the WHOLE group (review K2; the alternative, splitting such a line into its own delivery group, is a later
            // product decision): the group is paid with a local window or not at all, so a carrier can never be asked to ship a line it must not.
            bool Has(Guid listing, string value) => attrs.Any(x => x.ListingId == listing && x.Key == "delivery" && x.Value == value);
            if (g.Lines.Any(l => Has(l.ListingId, "local") && !Has(l.ListingId, "ships"))) opts = [];
            if (allLocal)
            {
                try
                {
                    var e = await portal.CheckLocalDeliveryEligibilityAsync(new LocalDeliveryEligibilityRequest(g.SellerId, g.BrandId, g.SubtotalCents, new EligibilityAddress(a.PostalCode.Trim(), country, a.Lat, a.Lng)), ct);
                    if (e.IsSuccess && e.Value is { } v && (v.FeeCents is null || v.FeeCents is >= 0 and <= CartLimits.MaxTotalCents)) local = new LocalOption(v.Eligible, v.Reason, v.FeeCents, v.TimeZone, v.Eligible ? v.Windows ?? [] : []);
                }
                catch (Exception ex) when (ex is PortalUnavailableException or PortalNotConfiguredException or PortalResponseTooLargeException) { /* local delivery is optional: just not offered now */ }
            }

            var pick = picks.FirstOrDefault(p => p.SellerId == g.SellerId && p.BrandId == g.BrandId);
            SelectedDelivery? sel = null;
            if (pick?.LocalWindowId is { } wid)
            {
                var w = local?.Windows.FirstOrDefault(x => x.WindowId == wid && x.Date == pick.LocalDate && x.Remaining > 0);
                if (local is not { Eligible: true, FeeCents: { } fee } || w is null || pick.Speed is not null) return (QuoteStatus.InvalidSelection, null, "local_window_unavailable");
                sel = new SelectedDelivery("local", null, wid, w.Date, fee);
            }
            else if (pick?.Speed is { } speed)
            {
                var o = opts.FirstOrDefault(x => string.Equals(x.Speed, speed, StringComparison.OrdinalIgnoreCase));
                if (o is null) return (QuoteStatus.InvalidSelection, null, "speed_not_offered");
                sel = new SelectedDelivery("carrier", o.Speed, null, null, o.PriceCents);
            }
            else if (opts.Count > 0) sel = new SelectedDelivery("carrier", opts[0].Speed, null, null, opts[0].PriceCents);   // cheapest by default
            built.Add((g, opts, priced.Unavailable, local, sel, g.Lines.Select(l => new TaxLineInput(l.Id.ToString(), l.LineTotalCents, Taxable(l.ListingId))).ToList()));
        }

        var taxes = tax.Calculate(country, region, built.Select(b => new TaxGroupInput(b.G.SellerId + ":" + b.G.BrandId, b.Sel?.ShippingCents ?? 0, b.TaxLines)).ToList());
        var groups = new List<QuoteGroup>();
        foreach (var (g, opts, unavailable, local, sel, _) in built)
        {
            var lines = g.Lines.Select(l => new QuoteLine(l.Id, l.ListingId, l.VariantId, l.Title, l.VariantName, l.Quantity, l.UnitPriceCents, l.LineTotalCents, taxes.LineTaxCents[l.Id.ToString()])).ToList();
            var shipping = sel?.ShippingCents ?? 0;
            var shipTax = taxes.ShippingTaxCents[g.SellerId + ":" + g.BrandId];
            var groupTax = lines.Sum(l => l.TaxCents) + shipTax;
            groups.Add(new QuoteGroup(g.SellerId, g.SellerName, g.BrandId, g.BrandName, lines, g.SubtotalCents, opts, unavailable, local, sel, shipping, shipTax, groupTax, g.SubtotalCents + shipping + groupTax));
        }
        var view = new QuoteView(cart.Currency, groups, groups.Sum(x => x.ItemsCents), groups.Sum(x => x.ShippingCents), groups.Sum(x => x.TaxCents), groups.Sum(x => x.TotalCents), groups.All(x => x.Selected is not null));
        if (view.TotalCents > CartLimits.MaxTotalCents) return (QuoteStatus.TotalTooLarge, null, null);
        var now = time.GetUtcNow();
        return (QuoteStatus.Ok, view with { QuoteHash = signer.Make(view, country, region, a.PostalCode, now), ExpiresAt = now.Add(QuoteSigner.Lifetime) }, null);
    }

}

public static class UsStates
{
    public static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","DC","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY",
        "AS","FM","GU","MH","MP","PW","PR","VI","AA","AE","AP",
    };
}
