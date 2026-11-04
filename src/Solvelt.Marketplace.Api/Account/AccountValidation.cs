using System.Text.RegularExpressions;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>Input rules for the profile and the address book. Every problem is returned by field so the page can show it where it belongs.</summary>
public static partial class AccountValidation
{
    public const int MaxAddresses = 20;

    // ISO 3166-1 alpha-2 (the officially assigned codes).
    private static readonly HashSet<string> Countries = new(StringComparer.Ordinal)
    {
        "AD","AE","AF","AG","AI","AL","AM","AO","AQ","AR","AS","AT","AU","AW","AX","AZ","BA","BB","BD","BE","BF","BG","BH","BI","BJ","BL","BM","BN","BO","BQ","BR","BS","BT","BV","BW","BY","BZ",
        "CA","CC","CD","CF","CG","CH","CI","CK","CL","CM","CN","CO","CR","CU","CV","CW","CX","CY","CZ","DE","DJ","DK","DM","DO","DZ","EC","EE","EG","EH","ER","ES","ET","FI","FJ","FK","FM","FO","FR",
        "GA","GB","GD","GE","GF","GG","GH","GI","GL","GM","GN","GP","GQ","GR","GS","GT","GU","GW","GY","HK","HM","HN","HR","HT","HU","ID","IE","IL","IM","IN","IO","IQ","IR","IS","IT","JE","JM","JO","JP",
        "KE","KG","KH","KI","KM","KN","KP","KR","KW","KY","KZ","LA","LB","LC","LI","LK","LR","LS","LT","LU","LV","LY","MA","MC","MD","ME","MF","MG","MH","MK","ML","MM","MN","MO","MP","MQ","MR","MS","MT",
        "MU","MV","MW","MX","MY","MZ","NA","NC","NE","NF","NG","NI","NL","NO","NP","NR","NU","NZ","OM","PA","PE","PF","PG","PH","PK","PL","PM","PN","PR","PS","PT","PW","PY","QA","RE","RO","RS","RU","RW",
        "SA","SB","SC","SD","SE","SG","SH","SI","SJ","SK","SL","SM","SN","SO","SR","SS","ST","SV","SX","SY","SZ","TC","TD","TF","TG","TH","TJ","TK","TL","TM","TN","TO","TR","TT","TV","TW","TZ","UA","UG",
        "UM","US","UY","UZ","VA","VC","VE","VG","VI","VN","VU","WF","WS","YE","YT","ZA","ZM","ZW",
    };

    public static bool IsCountry(string? code) => code is { Length: 2 } && Countries.Contains(code);

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")] private static partial Regex UsZip();
    [GeneratedRegex(@"^[A-Za-z]{2}$")] private static partial Regex UsState();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,18}[A-Za-z0-9]$|^[A-Za-z0-9]$")] private static partial Regex PostalCode();
    [GeneratedRegex(@"^[0-9+()\-. xX]{5,30}$")] private static partial Regex Phone();

    /// <summary>No control, format (zero-width, bidi overrides), surrogate-half or private-use characters: text that looks like something it is not, or breaks a mail or a label (L3). NFC normalising follows with the managed normaliser slice (M1-68).</summary>
    private static bool Clean(string? s) => s is not null && !Auth.AuthValidation.HasForbiddenCharacters(s);

    public static string? PhoneProblem(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var p = phone.Trim();
        return Phone().IsMatch(p) && p.Count(char.IsDigit) >= 5 ? null : "Enter a phone number with digits, spaces, + ( ) - . only.";
    }

    public static Dictionary<string, string> Profile(string? displayName, string? phone)
    {
        var errors = new Dictionary<string, string>();
        if (Auth.AuthValidation.DisplayNameProblem(displayName) is { } d) errors["displayName"] = d;
        if (PhoneProblem(phone) is { } p) errors["phone"] = p;
        return errors;
    }

    /// <summary>Checks an address body and returns the problems by field (empty when it is acceptable).</summary>
    public static Dictionary<string, string> Address(AddressBody? b)
    {
        var e = new Dictionary<string, string>();
        if (b is null) { e["fullName"] = "Enter the recipient's name."; return e; }
        void Required(string field, string? value, int max, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) e[field] = $"Enter {label}.";
            else if (value.Trim().Length > max) e[field] = $"Use at most {max} characters.";
            else if (!Clean(value)) e[field] = "That has characters that are not allowed.";
        }
        void Optional(string field, string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Trim().Length > max) e[field] = $"Use at most {max} characters.";
            else if (!Clean(value)) e[field] = "That has characters that are not allowed.";
        }
        Required("fullName", b.FullName, 100, "the recipient's name");
        Required("line1", b.Line1, 200, "the street address");
        Optional("line2", b.Line2, 200);
        Required("city", b.City, 100, "the city");
        Optional("region", b.Region, 100);
        var country = b.Country?.Trim().ToUpperInvariant();
        if (!IsCountry(country)) e["country"] = "Choose a country.";
        var postal = b.PostalCode?.Trim();
        if (string.IsNullOrWhiteSpace(postal)) e["postalCode"] = "Enter the postal code.";
        else if (country == "US" ? !UsZip().IsMatch(postal) : !PostalCode().IsMatch(postal)) e["postalCode"] = country == "US" ? "Enter a ZIP code like 12345 or 12345-6789." : "Enter a valid postal code.";
        if (country == "US")
        {
            if (string.IsNullOrWhiteSpace(b.Region)) e["region"] = "Enter the state.";
            else if (!UsState().IsMatch(b.Region.Trim())) e["region"] = "Use the two-letter state code, like NY.";
        }
        if (PhoneProblem(b.Phone) is { } p) e["phone"] = p;
        if (b.Lat is null != b.Lng is null) e["lat"] = "Give both latitude and longitude, or neither.";
        else if (b.Lat is { } lat && b.Lng is { } lng)
        {
            if (double.IsNaN(lat) || lat is < -90 or > 90) e["lat"] = "Latitude must be between -90 and 90.";
            if (double.IsNaN(lng) || lng is < -180 or > 180) e["lng"] = "Longitude must be between -180 and 180.";
        }
        return e;
    }
}

/// <summary>The address as the web sends it (<c>isDefault</c> may be true to make it the default).</summary>
public sealed record AddressBody(string? FullName, string? Line1, string? Line2, string? City, string? Region, string? PostalCode, string? Country, string? Phone, double? Lat = null, double? Lng = null, bool? IsDefault = null);
