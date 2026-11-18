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
    /// <summary>
    /// The keypad card connector (method A, V-13), a SEPARATE service that is the only place a card number is handled: its public https URL WITHOUT credentials (the path is <c>/connector</c>). Empty: the keypad method
    /// is not offered. Setting it needs <see cref="PayConnectorUser"/>, <see cref="PayConnectorPassword"/> and <see cref="PayConnectorKey"/>.
    /// </summary>
    public string? PayConnectorUrl { get; set; }
    /// <summary>The Basic credentials written into the <c>paymentConnectorUrl</c> of the <c>&lt;Pay&gt;</c> (a second layer next to SignalWire's signature). The password is a secret.</summary>
    public string? PayConnectorUser { get; set; }
    public string? PayConnectorPassword { get; set; }
    /// <summary>The key shared with the connector: signs what the connector and this API tell each other (HMAC-SHA256, both directions). At least 32 characters, a key of its own. Secret.</summary>
    public string? PayConnectorKey { get; set; }
    /// <summary>The key that encrypts saved-card tokens at rest (V-16): AES-256-GCM, its own purpose, bound to the account. A <b>secret</b>; required with the connector, at least 32 characters. Losing it makes saved cards unreadable (they are saved again).</summary>
    public string? SavedCardKey { get; set; }
    public bool KeypadEnabled => !string.IsNullOrWhiteSpace(PayConnectorUrl);

    public static IReadOnlyList<string> Problems(SolaOptions o)
    {
        var p = new List<string>();
        if (o.KeypadEnabled)
        {
            if (!Uri.TryCreate(o.PayConnectorUrl, UriKind.Absolute, out var cu) || cu.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(cu.UserInfo) || !string.IsNullOrEmpty(cu.Query) || !string.IsNullOrEmpty(cu.Fragment))
                p.Add("Sola:PayConnectorUrl (env Sola__PayConnectorUrl) must be the https URL of the pay connector, without credentials, a query string or a fragment.");
            if (string.IsNullOrWhiteSpace(o.PayConnectorUser) || o.PayConnectorPassword is not { Length: >= 24 }) p.Add("Sola:PayConnectorUser and Sola:PayConnectorPassword (at least 24 characters) are required with the pay connector.");
            if (o.PayConnectorKey is not { Length: >= 32 }) p.Add("Sola:PayConnectorKey (env Sola__PayConnectorKey) must be at least 32 characters.");
            else if (o.PayConnectorKey == o.PayConnectorPassword || o.PayConnectorKey == o.WebhookPin || o.PayConnectorKey == o.ApiKey) p.Add("Sola:PayConnectorKey must differ from every other secret (one key per purpose).");
            if (o.SavedCardKey is not { Length: >= 32 }) p.Add("Sola:SavedCardKey (env Sola__SavedCardKey) must be at least 32 characters: it encrypts saved-card tokens.");
            else if (o.SavedCardKey == o.PayConnectorKey || o.SavedCardKey == o.PayConnectorPassword || o.SavedCardKey == o.WebhookPin || o.SavedCardKey == o.ApiKey) p.Add("Sola:SavedCardKey must differ from every other secret (one key per purpose).");
        }
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
/// <summary>A provider that can save a card for later by phone (V-16): the keypad step asks the connector for a TOKEN (nothing is charged).</summary>
public interface ICardSavingProvider
{
    bool CanSaveCards { get; }
    XElement StartSaveCard(string attemptId, string keypadActionUrl);
}

public sealed class SolaPhonePaymentProvider(IOptions<SolaOptions> options, IServiceScopeFactory scopes) : IPhonePaymentProvider, ICardSavingProvider
{
    public string Name => "sola";
    /// <summary>The text link always; the keypad only when the pay connector (our own, V-13) is configured: Sola itself documents no IVR connector.</summary>
    public bool Supports(string method) => method == PhonePaymentMethod.TextLink || (method == PhonePaymentMethod.Keypad && options.Value.KeypadEnabled);

    public Task<PhonePaymentStart> StartAsync(PhonePaymentRequest request, string keypadActionUrl, CancellationToken ct)
    {
        if (request.Method == PhonePaymentMethod.Keypad && options.Value.KeypadEnabled) return Task.FromResult(StartKeypad(request, keypadActionUrl));
        if (request.Method != PhonePaymentMethod.TextLink) throw new NotSupportedException("The keypad method needs the pay connector (Sola:PayConnectorUrl).");
        var url = options.Value.PaymentSiteUrl ?? throw new InvalidOperationException("Sola:PaymentSiteUrl is not set.");
        var link = url + "?xamount=" + SolaNotification.Dollars(request.AmountCents) + "&xinvoice=" + Uri.EscapeDataString(request.OrderNumber);
        return Task.FromResult(new PhonePaymentStart(request.Method, null, link, "sola-" + request.AttemptId));
    }

    /// <summary>
    /// The keypad payment: SignalWire collects the card digits and sends them to OUR connector, never to this API. The server's side of it is only this instruction: the amount and the attempt come from the
    /// server's order, the connector URL carries the connector's credentials, and the connector asks THIS API what to charge (it does not trust the amount in the request).
    /// </summary>
    public bool CanSaveCards => options.Value.KeypadEnabled;

    /// <summary><c>&lt;Pay chargeAmount="0" tokenType="reusable"&gt;</c>: SignalWire collects the card and sends it to OUR connector, which asks Sola for a token (<c>cc:save</c>). The attempt id is the only thing this API puts in it.</summary>
    public XElement StartSaveCard(string attemptId, string keypadActionUrl)
    {
        var o = options.Value;
        var connector = new UriBuilder(o.PayConnectorUrl!) { UserName = Uri.EscapeDataString(o.PayConnectorUser!), Password = Uri.EscapeDataString(o.PayConnectorPassword!) }.Uri.ToString();
        return new XElement("Pay",
            new XAttribute("chargeAmount", "0"), new XAttribute("tokenType", "reusable"), new XAttribute("currency", "usd"), new XAttribute("paymentConnectorUrl", connector),
            new XAttribute("action", keypadActionUrl), new XAttribute("maxAttempts", 2), new XAttribute("timeout", 8), new XAttribute("postalCode", "true"), new XAttribute("securityCode", "true"),
            new XAttribute("description", "Save a card to your Solvelt account"),
            new XElement("Parameter", new XAttribute("name", "attempt"), new XAttribute("value", attemptId)));
    }

    private PhonePaymentStart StartKeypad(PhonePaymentRequest request, string keypadActionUrl)
    {
        var o = options.Value;
        var connector = new UriBuilder(o.PayConnectorUrl!) { UserName = Uri.EscapeDataString(o.PayConnectorUser!), Password = Uri.EscapeDataString(o.PayConnectorPassword!) }.Uri.ToString();
        var pay = new XElement("Pay",
            new XAttribute("chargeAmount", SolaNotification.Dollars(request.AmountCents)), new XAttribute("currency", "usd"), new XAttribute("paymentConnectorUrl", connector),
            new XAttribute("action", keypadActionUrl), new XAttribute("maxAttempts", 2), new XAttribute("timeout", 8), new XAttribute("postalCode", "true"), new XAttribute("securityCode", "true"),
            new XAttribute("description", "Solvelt order " + request.OrderNumber),
            new XElement("Parameter", new XAttribute("name", "attempt"), new XAttribute("value", request.AttemptId)));
        return new PhonePaymentStart(request.Method, pay, null, "sola-keypad-" + request.AttemptId);
    }

    /// <summary>
    /// SignalWire's <c>action</c> after the card step. What it says is NOT what settles the order: the connector's signed report does (see <see cref="PayConnectorEndpoints"/>, which runs the amount and invoice checks).
    /// This reads the persisted attempt: paid and declined are final; still pending with <c>PayResult=success</c> means the connector's report has not arrived yet (<see cref="PhonePaymentStatus.Pending"/>); any
    /// other result is a failure. The masked card fields SignalWire adds (last four digits) are ignored, never stored or logged.
    /// </summary>
    public async Task<PhonePaymentOutcome> CompleteKeypadAsync(string attemptId, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        if (!options.Value.KeypadEnabled) throw new NotSupportedException("The keypad method needs the pay connector (Sola:PayConnectorUrl).");
        if (!Guid.TryParseExact(attemptId, "N", out var id)) return new PhonePaymentOutcome(PhonePaymentStatus.Failed, 0, null, null, "unknown_attempt");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.MarketplaceDb>();
        var a = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.VoicePaymentAttempts.Where(x => x.Id == id && x.Method == PhonePaymentMethod.Keypad), ct);
        if (a is null) return new PhonePaymentOutcome(PhonePaymentStatus.Failed, 0, null, null, "unknown_attempt");
        form.TryGetValue("PayResult", out var result);
        return a.Status switch
        {
            "paid" => new PhonePaymentOutcome(PhonePaymentStatus.Paid, a.AmountCents, a.OrderNumber, a.ProviderRef),
            "declined" => new PhonePaymentOutcome(PhonePaymentStatus.Declined, 0, a.OrderNumber, null, "declined"),
            "pending" when result == "success" => new PhonePaymentOutcome(PhonePaymentStatus.Pending, 0, a.OrderNumber, null, "awaiting_connector_report"),
            _ => new PhonePaymentOutcome(PhonePaymentStatus.Failed, 0, a.OrderNumber, null, result ?? "no_result"),
        };
    }
}
