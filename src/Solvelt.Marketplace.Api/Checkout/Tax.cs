namespace Solvelt.Marketplace.Api.Checkout;

/// <summary>
/// Tax policy (decision M3-04): US destinations only, one rate per state from configuration (<c>Tax:StateRates</c>, percent as a decimal),
/// no live tax provider. Items are taxable unless their category sits under an exempt root (<c>Tax:ExemptRootCategories</c>, default
/// <c>food-drink</c>: unprepared food). Shipping is taxed in the states listed in <c>Tax:ShippingTaxableStates</c> when the group has a taxable item.
/// Tax is computed per line (and per group for shipping) and rounded half up to the cent, so the order total is the exact sum of its parts.
/// </summary>
public sealed class TaxOptions
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, decimal> StateRates { get; set; } = new() { ["NY"] = 8.875m, ["NJ"] = 6.625m, ["CA"] = 7.25m, ["FL"] = 6m, ["TX"] = 6.25m, ["PA"] = 6m, ["OH"] = 5.75m, ["IL"] = 6.25m };
    public string[] ShippingTaxableStates { get; set; } = ["NY", "TX", "PA", "IL"];
    public string[] ExemptRootCategories { get; set; } = ["food-drink"];
}

public sealed record TaxLineInput(string Key, long AmountCents, bool Taxable);
public sealed record TaxGroupInput(string Key, long ShippingCents, IReadOnlyList<TaxLineInput> Lines);
public sealed record TaxResult(IReadOnlyDictionary<string, long> LineTaxCents, IReadOnlyDictionary<string, long> ShippingTaxCents)
{
    public long Total => LineTaxCents.Values.Sum() + ShippingTaxCents.Values.Sum();
}

public interface ITaxCalculator
{
    /// <summary>Tax for a destination. Never returns a negative amount; unknown places are 0.</summary>
    TaxResult Calculate(string country, string? region, IReadOnlyList<TaxGroupInput> groups);
}

public sealed class SimpleTaxCalculator(Microsoft.Extensions.Options.IOptions<TaxOptions> options) : ITaxCalculator
{
    public TaxResult Calculate(string country, string? region, IReadOnlyList<TaxGroupInput> groups)
    {
        var o = options.Value;
        var rate = o.Enabled && string.Equals(country, "US", StringComparison.OrdinalIgnoreCase) && region is { Length: 2 }
            && o.StateRates.FirstOrDefault(kv => string.Equals(kv.Key, region, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : 0m;
        var shippingTaxed = rate > 0 && o.ShippingTaxableStates.Contains(region!, StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, long>();
        var shipping = new Dictionary<string, long>();
        foreach (var g in groups)
        {
            foreach (var l in g.Lines) lines[l.Key] = l.Taxable ? Round(l.AmountCents, rate) : 0;
            shipping[g.Key] = shippingTaxed && g.Lines.Any(l => l.Taxable) ? Round(g.ShippingCents, rate) : 0;
        }
        return new TaxResult(lines, shipping);
    }

    /// <summary>amount x percent / 100, rounded half up (away from zero) to a whole cent, in exact decimal arithmetic (never floating point).</summary>
    public static long Round(long cents, decimal percent)
        => cents <= 0 || percent <= 0 ? 0 : (long)Math.Round(cents * percent / 100m, 0, MidpointRounding.AwayFromZero);
}
