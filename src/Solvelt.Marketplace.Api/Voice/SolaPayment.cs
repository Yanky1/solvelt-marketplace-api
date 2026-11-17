using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Sola Payments (formerly Cardknox). Settings only from the environment; nothing is committed. Sandbox accounts only until the owner decides about real money (V-07).</summary>
public sealed class SolaOptions
{
    /// <summary>The account's PaymentSITE page (https, no query string). Needs sign-up through Sola support.</summary>
    public string? PaymentSiteUrl { get; set; }
    /// <summary>The account webhook's PIN (letters and digits, at least 15): the key of the <c>ck-signature</c> check. Secret.</summary>
    public string? WebhookPin { get; set; }
    /// <summary>The API key of the account (reserved for a later reconciliation call and refunds; not used by this slice). Secret.</summary>
    public string? ApiKey { get; set; }

    public static IReadOnlyList<string> Problems(SolaOptions o)
    {
        var p = new List<string>();
        if (!Uri.TryCreate(o.PaymentSiteUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment))
            p.Add("Sola:PaymentSiteUrl (env Sola__PaymentSiteUrl) must be the https URL of the PaymentSITE page, without a query string.");
        if (o.WebhookPin is not { Length: >= 15 } pin || !pin.All(char.IsAsciiLetterOrDigit)) p.Add("Sola:WebhookPin (env Sola__WebhookPin) must be at least 15 letters and digits: it signs every Sola notification we accept.");
        return p;
    }
}

/// <summary>
/// <c>ck-signature</c> of a Sola account webhook (https://docs.solapayments.com/products/webhooks.md, read 2026-11-17): parse the form body, lower-case the names, sort by name, take the values
/// only, append the PIN, replace every non-ASCII character with <c>?</c>, MD5, uppercase hex. EVERY received parameter takes part (never a fixed list). MD5 is Sola's protocol, not our choice;
/// the PIN is the secret and the comparison is constant time. A missing PIN or signature rejects everything.
/// </summary>
public static class SolaSignature
{
    public static string Compute(string pin, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var kv in pairs.OrderBy(k => k.Key.ToLowerInvariant(), StringComparer.Ordinal)) sb.Append(kv.Value);
        sb.Append(pin);
        var ascii = new string(sb.ToString().Select(c => c > 127 ? '?' : c).ToArray());   // per character as Sola does: a supplementary character is two UTF-16 units here, as in a UTF-16 string
        return Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(ascii)));
    }

    public static bool Verify(string? pin, IEnumerable<KeyValuePair<string, string>> pairs, string? header)
    {
        if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(header)) return false;
        var expected = Encoding.ASCII.GetBytes(Compute(pin, pairs));
        var given = Encoding.ASCII.GetBytes(header.Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}

/// <summary>What a verified Sola notification means for us. Only an approved <c>cc:sale</c> can be a payment; the amount is parsed exactly (no float) and compared with OUR order by the caller.</summary>
public static class SolaNotification
{
    public sealed record Parsed(string Invoice, PhonePaymentOutcome Outcome);

    public static Parsed? Parse(IReadOnlyDictionary<string, string> p)
    {
        string? Get(string n) => p.FirstOrDefault(k => string.Equals(k.Key, n, StringComparison.OrdinalIgnoreCase)).Value;
        var command = Get("xCommand");
        var invoice = Get("xInvoice");
        if (string.IsNullOrWhiteSpace(invoice) || !string.Equals(command, "cc:sale", StringComparison.OrdinalIgnoreCase)) return null;   // not a payment for an order we asked about
        var result = Get("xResponseResult") ?? Get("xResult") ?? "";
        var status = result.Equals("Approved", StringComparison.OrdinalIgnoreCase) || result == "A" ? PhonePaymentStatus.Paid
            : result.Equals("Declined", StringComparison.OrdinalIgnoreCase) || result == "D" ? PhonePaymentStatus.Declined : PhonePaymentStatus.Failed;
        var cents = ToCents(Get("xAmount") ?? Get("xAuthAmount"));
        var rf = Get("xRefNum");
        if (status == PhonePaymentStatus.Paid && (cents is null || string.IsNullOrWhiteSpace(rf))) status = PhonePaymentStatus.Failed;   // an approval we cannot attribute is never a payment
        return new Parsed(invoice.Trim(), new PhonePaymentOutcome(status, cents ?? 0, invoice.Trim(), string.IsNullOrWhiteSpace(rf) ? null : rf.Trim(), result));
    }

    public static long? ToCents(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) || d < 0) return null;
        var c = d * 100m;
        return c == decimal.Truncate(c) && c <= long.MaxValue ? (long)c : null;   // 12.345 is not an amount of ours
    }

    public static string Dollars(long cents) => $"{cents / 100}.{cents % 100:D2}";
}

/// <summary>
/// Sola's PaymentSITE as a text link (method B). The page is Sola's: PCI scope stays with Sola and no card data touches this API. The link pre-fills the amount and the invoice, but Sola documents
/// no way to lock them (the customer can edit the query string), so NOTHING in the link is trusted: only a verified webhook that matches our own order counts (see <see cref="SolaNotification"/>
/// and <see cref="PhoneOrderService.SettleAsync"/>). There is no keypad method: Sola documents no IVR or SignalWire connector.
/// </summary>
public sealed class SolaPhonePaymentProvider(IOptions<SolaOptions> options) : IPhonePaymentProvider
{
    public string Name => "sola";
    public bool Supports(string method) => method == PhonePaymentMethod.TextLink;

    public Task<PhonePaymentStart> StartAsync(PhonePaymentRequest request, string keypadActionUrl, CancellationToken ct)
    {
        if (request.Method != PhonePaymentMethod.TextLink) throw new NotSupportedException("Sola has no keypad method.");
        var url = options.Value.PaymentSiteUrl ?? throw new InvalidOperationException("Sola:PaymentSiteUrl is not set.");
        var link = url + "?xamount=" + SolaNotification.Dollars(request.AmountCents) + "&xinvoice=" + Uri.EscapeDataString(request.OrderNumber);
        return Task.FromResult(new PhonePaymentStart(request.Method, null, link, "sola-" + request.AttemptId));
    }

    public Task<PhonePaymentOutcome> CompleteKeypadAsync(string attemptId, IReadOnlyDictionary<string, string> form, CancellationToken ct)
        => throw new NotSupportedException("Sola has no keypad method.");
}
