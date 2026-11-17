using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Solvelt.Marketplace.Api.Voice;

public static class PhonePaymentMethod
{
    public const string Keypad = "keypad", TextLink = "text_link";
}

/// <summary>
/// What the server asks a phone payment provider to collect. Everything here comes from the SERVER's order (amount, invoice): nothing the caller or the model said, and no card data in either direction
/// through this interface (the card is entered on the keypad into the provider's own certified path, or on the provider's own page).
/// </summary>
public sealed record PhonePaymentRequest(string AttemptId, string OrderNumber, long AmountCents, string Currency, string Method, string? CallerNumberE164);

/// <summary>How the caller is to pay: a cXML instruction to run on the call (keypad), or a link to text them (text link).</summary>
public sealed record PhonePaymentStart(string Method, XElement? Instruction, string? LinkUrl, string ProviderReference);

public enum PhonePaymentStatus { Pending, Paid, Declined, Failed }

/// <summary>The provider's word on one attempt. The server checks <see cref="PaidCents"/> and <see cref="InvoiceRef"/> against its own order before it believes a <see cref="PhonePaymentStatus.Paid"/>.</summary>
public sealed record PhonePaymentOutcome(PhonePaymentStatus Status, long PaidCents, string? InvoiceRef, string? TransactionId, string? Code = null);

/// <summary>
/// A way for a caller to pay by phone (decision V-06). Implementations: the fake (development and tests), and later Sola's keypad connector and text link. Nothing real is connected: with
/// <c>Voice:PhonePaymentProvider=none</c> (the default) nobody can pay by phone, and no card number ever reaches the marketplace API or the model through this interface.
/// </summary>
public interface IPhonePaymentProvider
{
    string Name { get; }
    bool Supports(string method);
    /// <summary>Opens an attempt for exactly <see cref="PhonePaymentRequest.AmountCents"/> against <see cref="PhonePaymentRequest.OrderNumber"/>. <paramref name="keypadActionUrl"/> is where SignalWire posts the keypad step's result.</summary>
    Task<PhonePaymentStart> StartAsync(PhonePaymentRequest request, string keypadActionUrl, CancellationToken ct);
    /// <summary>The result of the keypad step (the form SignalWire posted to our signed keypad endpoint). Card data is never in it for a provider that is certified to take it elsewhere.</summary>
    Task<PhonePaymentOutcome> CompleteKeypadAsync(string attemptId, IReadOnlyDictionary<string, string> form, CancellationToken ct);
}

/// <summary>
/// The fake: no card, no money, no network. The keypad step asks the tester to press 1 (pays) or 2 (declined); nothing a caller types here is a card number. Test hooks let a test make
/// the provider report the wrong amount or invoice, to prove the server refuses it. Development and tests only (start-up refuses it in production).
/// </summary>
public sealed class FakePhonePaymentProvider : IPhonePaymentProvider
{
    public string Name => "fake";
    public bool Supports(string method) => method is PhonePaymentMethod.Keypad;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PhonePaymentRequest> _attempts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _settled = new();
    public IReadOnlyCollection<PhonePaymentRequest> Attempts => _attempts.Values.ToList();
    /// <summary>Test hook: report this amount instead of the real one.</summary>
    public long? PaidCentsOverride { get; set; }
    /// <summary>Test hook: report this invoice instead of the order number.</summary>
    public string? InvoiceOverride { get; set; }
    public bool Fail { get; set; }

    public Task<PhonePaymentStart> StartAsync(PhonePaymentRequest request, string keypadActionUrl, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("fake provider down");
        _attempts[request.AttemptId] = request;
        var gather = new XElement("Gather", new XAttribute("input", "dtmf"), new XAttribute("numDigits", 1), new XAttribute("timeout", 15), new XAttribute("actionOnEmptyResult", "true"),
            new XAttribute("method", "POST"), new XAttribute("action", keypadActionUrl), new XElement("Say", VoiceScripts.FakePayPrompt));
        return Task.FromResult(new PhonePaymentStart(request.Method, gather, null, "fake-" + request.AttemptId));
    }

    public Task<PhonePaymentOutcome> CompleteKeypadAsync(string attemptId, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        if (!_attempts.TryGetValue(attemptId, out var a)) return Task.FromResult(new PhonePaymentOutcome(PhonePaymentStatus.Failed, 0, null, null, "unknown_attempt"));
        form.TryGetValue("Digits", out var digits);
        if (digits == "2") return Task.FromResult(new PhonePaymentOutcome(PhonePaymentStatus.Declined, 0, a.OrderNumber, null, "declined"));
        if (digits != "1") return Task.FromResult(new PhonePaymentOutcome(PhonePaymentStatus.Failed, 0, a.OrderNumber, null, "no_input"));
        // a repeated callback for an attempt that was already settled is the same transaction, not a second one
        var first = _settled.TryAdd(attemptId, true);
        return Task.FromResult(new PhonePaymentOutcome(PhonePaymentStatus.Paid, PaidCentsOverride ?? a.AmountCents, InvoiceOverride ?? a.OrderNumber, "fake-tx-" + attemptId, first ? null : "repeat"));
    }
}

/// <summary>The provider chosen by configuration. <c>none</c> means there is none: the tools tell the model that phone payment is not available.</summary>
public sealed class PhonePayments(IOptions<VoiceOptions> options, FakePhonePaymentProvider fake, SolaPhonePaymentProvider sola)
{
    public IPhonePaymentProvider? Provider => options.Value.PhonePaymentProvider switch { "fake" => fake, "sola" => sola, _ => null };
}
