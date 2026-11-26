using System.Globalization;
using Solvelt.Marketplace.Api.Checkout;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>The latitude and longitude box Google gives for a ZIP code. It is the only location data kept (in memory, for seven days at most).</summary>
public sealed record GeoViewport(double SouthwestLat, double SouthwestLng, double NortheastLat, double NortheastLng);

/// <summary>What a ZIP code is: its city and state (city as Google names it, state as a two-letter code) and, when Google gave one, its viewport.</summary>
public sealed record ZipInfo(string City, string State, GeoViewport? Viewport);

/// <summary>The lookup could not give an answer (not configured, refused, over its limit, unreachable). It carries nothing of the vendor's answer: a ZIP that does not exist is a null answer, not this.</summary>
public sealed class LookupUnavailableException : Exception
{
    public LookupUnavailableException() : base("The address lookup is not available.") { }
}

/// <summary>A ZIP code to its city, state and viewport (decision V-63).</summary>
public interface IPostcodeLookup
{
    bool IsConfigured { get; }
    /// <summary>Null when the ZIP code does not exist; <see cref="LookupUnavailableException"/> when no answer could be had.</summary>
    Task<ZipInfo?> FindAsync(string zip5, CancellationToken ct);
}

/// <summary>Street names that start with what the caller said, inside a viewport.</summary>
public interface IStreetSuggest
{
    bool IsConfigured { get; }
    /// <summary>Up to five different street names; <see cref="LookupUnavailableException"/> when no answer could be had.</summary>
    Task<IReadOnlyList<string>> StreetsAsync(string partial, GeoViewport? viewport, CancellationToken ct);
}

public sealed record AddressMissing(string Field, string Problem);
public sealed record AddressCorrection(string Field, string Submitted, string Suggested);

/// <summary>
/// The one rule for "is this address complete" (decision V-63), used by <c>check_address</c> AND at save time, so the two cannot drift: a house number, a street, a city, a state, and a ZIP code of five digits (only the first
/// five count: ZIP+4 is fine).
/// </summary>
public static class AddressShape
{
    /// <summary>
    /// The one street line that is stored: "{house_number} {street}". The two are taken as SEPARATE arguments, never split out of one line (decision V-63): "123-45 Main St" (Queens), "12B", "1/2" and "One Main St"
    /// have no number that a rule on the first word could find.
    /// </summary>
    public static string Line1(string? houseNumber, string? street)
        => string.Join(' ', $"{houseNumber} {street}".Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The first five digits of a ZIP code, or null when there are fewer than five digits (a ZIP+4 passes).</summary>
    public static string? ZipFive(string? zip)
    {
        var m = System.Text.RegularExpressions.Regex.Match(zip ?? "", @"^\s*(\d{5})(?:[-\s]?\d{4})?\s*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return m.Success ? m.Groups[1].Value : null;
    }

    public static IReadOnlyList<AddressMissing> Check(string? houseNumber, string? street, string? city, string? state, string? zip)
    {
        var missing = new List<AddressMissing>();
        if (string.IsNullOrWhiteSpace(houseNumber)) missing.Add(new("house_number", "missing"));
        if (string.IsNullOrWhiteSpace(street)) missing.Add(new("street", "missing"));
        if (string.IsNullOrWhiteSpace(city)) missing.Add(new("city", "missing"));
        if (string.IsNullOrWhiteSpace(state)) missing.Add(new("state", "missing"));
        if (string.IsNullOrWhiteSpace(zip)) missing.Add(new("zip", "missing"));
        else if (ZipFive(zip) is null) missing.Add(new("zip", "must be five digits"));
        return missing;
    }

    /// <summary>
    /// The check for an address. When the caller gave the house number or the street, BOTH must be there. When neither was given (an edit that leaves the street as it is), the stored line only has to exist:
    /// it passed this check when it was saved, and no line is ever taken apart to find a number.
    /// </summary>
    public static IReadOnlyList<AddressMissing> Check(CheckoutAddress a, string? houseNumber, string? street)
    {
        if (houseNumber is null && street is null)
        {
            var rest = Check("-", "-", a.City, a.Region, a.PostalCode).ToList();
            if (string.IsNullOrWhiteSpace(a.Line1)) rest.Insert(0, new("street", "missing"));
            return rest;
        }
        return Check(houseNumber, street, a.City, a.Region, a.PostalCode);
    }

    /// <summary>The missing parts as a person says them: "the house number and the ZIP code".</summary>
    public static string Say(IReadOnlyList<AddressMissing> missing)
        => VoiceSpeech.List(missing.Select(m => m.Field switch { "house_number" => "the house number", "street" => "the street", "city" => "the city", "state" => "the state", "zip" => m.Problem == "missing" ? "the ZIP code" : "a ZIP code of five digits", _ => m.Field }).ToList(), 5);
}

public enum AddressStatus { Valid, Corrected, Inconsistent, Incomplete, Unverified }

/// <summary>
/// What the save-time check found. <see cref="Normalized"/> is the address to read back (Google's city and state when they differ from what was said). <see cref="ProceededAfterRetries"/>: the caller did not settle after
/// <see cref="AddressValidator.MaxAttempts"/> corrections, so the address AS GIVEN goes on to the usual read-back (never for an incomplete address).
/// </summary>
public sealed record AddressValidation(AddressStatus Status, string Message, CheckoutAddress? Normalized, IReadOnlyList<AddressCorrection> Corrections, IReadOnlyList<AddressMissing> Missing, bool ProceededAfterRetries = false)
{
    /// <summary>True only for Valid and Unverified: nothing needs the caller's say-so beyond the usual read-back and confirm.</summary>
    public bool CanSaveWithoutConfirmation => Status is AddressStatus.Valid or AddressStatus.Unverified;
}

/// <summary>
/// The save-time check (decision V-63), a port of the telephone system's own rule WITHOUT the carrier half (no deliverability step exists here). Order: missing parts, then a ZIP that is not five digits (both
/// <c>Incomplete</c>), then the ZIP lookup: no such ZIP is <c>Inconsistent</c>; an unavailable lookup falls through and does not reject; a different city or state (case-insensitive) is <c>Corrected</c> with Google's
/// spelling; no difference is <c>Valid</c>; no lookup at all is <c>Unverified</c> and savable. Nothing here names a vendor or a system: the messages are for the caller's ears.
/// </summary>
public static class AddressValidator
{
    /// <summary>Corrections or inconsistencies a call may have before the address is taken as given.</summary>
    public const int MaxAttempts = 2;

    public static async Task<AddressValidation> ValidateAsync(CheckoutAddress address, string? houseNumber, string? street, int priorRetries, bool lookupConfigured, Func<string, CancellationToken, Task<ZipInfo?>> findZip, CancellationToken ct)
    {
        var missing = AddressShape.Check(address, houseNumber, street);
        if (missing.Count > 0) return new(AddressStatus.Incomplete, "Still needed: " + AddressShape.Say(missing) + ".", null, [], missing);
        if (address.Country != "US" || !lookupConfigured) return new(AddressStatus.Unverified, "", address, [], []);
        var zip = AddressShape.ZipFive(address.PostalCode)!;
        ZipInfo? info;
        try { info = await findZip(zip, ct); }
        catch (LookupUnavailableException) { return new(AddressStatus.Unverified, "", address, [], []); }   // an unavailable check never rejects an address
        AddressValidation Settle(AddressValidation v)
            => v.Status is AddressStatus.Corrected or AddressStatus.Inconsistent && priorRetries >= MaxAttempts
                ? new(v.Status, "", address, v.Corrections, [], ProceededAfterRetries: true)   // the caller will not settle: the address as given goes on to the read-back
                : v;
        if (info is null) return Settle(new(AddressStatus.Inconsistent, $"ZIP code {VoiceSpeech.Digits(zip)} does not appear to exist.", null, [], []));
        var corrections = new List<AddressCorrection>();
        if (!string.Equals(address.City?.Trim(), info.City, StringComparison.OrdinalIgnoreCase)) corrections.Add(new("city", address.City?.Trim() ?? "", info.City));
        if (!string.Equals(address.Region?.Trim(), info.State, StringComparison.OrdinalIgnoreCase)) corrections.Add(new("state", address.Region?.Trim() ?? "", info.State));
        if (corrections.Count == 0) return new(AddressStatus.Valid, "", address, [], []);
        var normalized = address with { City = info.City, Region = info.State };
        return Settle(new(AddressStatus.Corrected, $"That ZIP code is {info.City}, {VoiceSpeech.State(info.State)}. Read the corrected address back and ask them to confirm before saving.", normalized, corrections, []));
    }
}
