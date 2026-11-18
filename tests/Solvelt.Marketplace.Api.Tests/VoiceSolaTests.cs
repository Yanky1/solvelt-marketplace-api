using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Sola's PaymentSITE as a text link and its signed webhook (slice 4b, decision V-07). Nothing here talks to Sola: the webhook is signed in the test the way Sola documents.</summary>
public class VoiceSolaTests
{
    private const string Pin = "abcdefghij0123456789", Site = "https://pay.sola.test/site";

    private static Dictionary<string, string?> Sola(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = Site, ["Sola:WebhookPin"] = Pin };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    // ---------- the signature, as documented (https://docs.solapayments.com/products/webhooks.md) ----------

    [Fact]
    public void The_signature_matches_known_answers_computed_outside_this_code()
    {
        var pairs = new Dictionary<string, string>
        {
            ["xAmount"] = "12.34", ["xCommand"] = "cc:sale", ["xEnteredDate"] = "01/02/2030 03:04:05 PM", ["xInvoice"] = "DEMO-INV-0001", ["xMaskedCardNumber"] = "XXXXXXXXXXXX0000",
            ["xName"] = "Example Person", ["xOrderID"] = "DEMO-ORDER-0001", ["xRefNum"] = "9999999999", ["xResponseResult"] = "Approved",
        };
        Assert.Equal("F13F9D51AAF67202EDF1CEEDE448FAEF", SolaSignature.Compute("TestPin0123456789ab", pairs));        // MD5 over the sorted values plus the PIN (Python hashlib)
        // names sort case-insensitively, a non-ASCII character becomes "?"
        var odd = new[] { KeyValuePair.Create("xName", "Café Müller"), KeyValuePair.Create("xAmount", "1.00"), KeyValuePair.Create("XInvoice", "a b") };
        Assert.Equal("5F9EF3562986A9F67279126E979B57E5", SolaSignature.Compute("TestPin0123456789ab", odd));
    }

    [Fact]
    public void Verification_is_constant_time_case_insensitive_on_the_digest_and_rejects_anything_unsigned()
    {
        var pairs = new Dictionary<string, string> { ["xAmount"] = "12.34", ["xInvoice"] = "MP-1" };
        var good = SolaSignature.Compute(Pin, pairs);
        Assert.True(SolaSignature.Verify(Pin, pairs, good));
        Assert.True(SolaSignature.Verify(Pin, pairs, good.ToLowerInvariant()));
        Assert.True(SolaSignature.Verify(Pin, pairs, " " + good + " "));
        Assert.False(SolaSignature.Verify(Pin, pairs, null));
        Assert.False(SolaSignature.Verify(Pin, pairs, ""));
        Assert.False(SolaSignature.Verify("", pairs, good));                                                          // no PIN configured: nothing is accepted
        Assert.False(SolaSignature.Verify(null, pairs, good));
        Assert.False(SolaSignature.Verify(Pin + "x", pairs, good));
        Assert.False(SolaSignature.Verify(Pin, new Dictionary<string, string> { ["xAmount"] = "0.01", ["xInvoice"] = "MP-1" }, good));   // a changed value
        Assert.False(SolaSignature.Verify(Pin, new Dictionary<string, string>(pairs) { ["xExtra"] = "1" }, good));    // an added parameter: EVERY parameter takes part
        Assert.False(SolaSignature.Verify(Pin, pairs, good[..^1] + (good[^1] == '0' ? "1" : "0")));
    }

    [Theory]
    [InlineData("12.34", 1234L)]
    [InlineData("12", 1200L)]
    [InlineData("12.3", 1230L)]
    [InlineData("0.01", 1L)]
    [InlineData("12.345", null)]
    [InlineData("-1.00", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Amounts_are_exact_cents_or_nothing(string? text, long? cents) => Assert.Equal(cents, SolaNotification.ToCents(text));

    private static IReadOnlyDictionary<string, string> Note(string result = "Approved", string command = "cc:sale", string? amount = "12.34", string? invoice = "MP-1", string? rf = "R1")
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["xCommand"] = command, ["xResponseResult"] = result };
        if (amount != null) d["xAmount"] = amount;
        if (invoice != null) d["xInvoice"] = invoice;
        if (rf != null) d["xRefNum"] = rf;
        return d;
    }

    [Fact]
    public void Only_an_approved_sale_with_an_amount_an_invoice_and_a_reference_is_a_payment()
    {
        var ok = SolaNotification.Parse(Note())!;
        Assert.Equal(("MP-1", PhonePaymentStatus.Paid, 1234L, "R1"), (ok.Invoice, ok.Outcome.Status, ok.Outcome.PaidCents, ok.Outcome.TransactionId));
        Assert.Equal(PhonePaymentStatus.Declined, SolaNotification.Parse(Note("Declined"))!.Outcome.Status);
        Assert.Equal(PhonePaymentStatus.Failed, SolaNotification.Parse(Note("Error"))!.Outcome.Status);
        Assert.Equal(PhonePaymentStatus.Failed, SolaNotification.Parse(Note(rf: null))!.Outcome.Status);              // an approval we cannot attribute
        Assert.Equal(PhonePaymentStatus.Failed, SolaNotification.Parse(Note(amount: "12.345"))!.Outcome.Status);
        Assert.Equal(PhonePaymentStatus.Failed, SolaNotification.Parse(Note(amount: null))!.Outcome.Status);
        Assert.Null(SolaNotification.Parse(Note(command: "cc:authonly")));                                              // reserves money, takes none
        Assert.Null(SolaNotification.Parse(Note(command: "cc:refund")));
        Assert.Null(SolaNotification.Parse(Note(invoice: null)));
        Assert.Null(SolaNotification.Parse(Note(invoice: " ")));
    }

    [Fact]
    public async Task The_link_carries_the_amount_and_the_invoice_and_there_is_no_keypad_method()
    {
        var p = new SolaPhonePaymentProvider(Options.Create(new SolaOptions { PaymentSiteUrl = Site, WebhookPin = Pin }));
        var start = await p.StartAsync(new PhonePaymentRequest("a1", "MP-20261117-ABC", 1805, "USD", PhonePaymentMethod.TextLink, "+15551234567"), "https://x/keypad", CancellationToken.None);
        Assert.Equal(Site + "?xamount=18.05&xinvoice=MP-20261117-ABC", start.LinkUrl);
        Assert.Null(start.Instruction);
        Assert.True(p.Supports(PhonePaymentMethod.TextLink)); Assert.False(p.Supports(PhonePaymentMethod.Keypad));
        await Assert.ThrowsAsync<NotSupportedException>(() => p.StartAsync(new PhonePaymentRequest("a1", "MP-1", 100, "USD", PhonePaymentMethod.Keypad, null), "u", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => p.CompleteKeypadAsync("a1", new Dictionary<string, string>(), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://pay.sola.test/site", Pin, "PaymentSiteUrl")]
    [InlineData("https://pay.sola.test/site?x=1", Pin, "PaymentSiteUrl")]
    [InlineData(null, Pin, "PaymentSiteUrl")]
    [InlineData(Site, "short", "WebhookPin")]
    [InlineData(Site, "has space 0123456789", "WebhookPin")]
    [InlineData(Site, null, "WebhookPin")]
    public void Sola_settings_are_checked_when_the_sola_provider_is_chosen(string? url, string? pin, string problem)
        => Assert.Contains(SolaOptions.Problems(new SolaOptions { PaymentSiteUrl = url, WebhookPin = pin }), x => x.Contains(problem));

    // ---------- a whole payment by text link ----------

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    private static async Task<VoiceScenario> Confirmed(Dictionary<string, string?>? extra = null)
    {
        var s = new VoiceScenario(config: Sola(extra));
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" }); await s.Tool("confirm_guest_email");
        await s.Tool("set_delivery_address", new { street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }); await s.Tool("confirm_address");
        await s.Tool("read_back_order"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type("1"); await s.Attach(token);
        return s;
    }

    private static string CkSignature(string pin, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var s = string.Concat(pairs.OrderBy(p => p.Key.ToLowerInvariant(), StringComparer.Ordinal).Select(p => p.Value)) + pin;
        return Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(new string(s.Select(c => c < 128 ? c : '?').ToArray()))));
    }

    private static async Task<HttpResponseMessage> Hook(VoiceScenario s, Dictionary<string, string> form, string? signature = null, bool sign = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, VoiceEndpoints.SolaWebhookPath) { Content = new FormUrlEncodedContent(form) };
        if (sign) req.Headers.Add("ck-signature", signature ?? CkSignature(Pin, form));
        return await s.C.SendAsync(req);
    }

    private static Dictionary<string, string> Approved(string order, long cents, string rf = "R-1", string name = "Dana Levi") => new()
    {
        ["xAmount"] = SolaNotification.Dollars(cents), ["xCommand"] = "cc:sale", ["xInvoice"] = order, ["xMaskedCardNumber"] = "XXXXXXXXXXXX0000", ["xName"] = name, ["xRefNum"] = rf, ["xResponseResult"] = "Approved",
    };

    private static Task<List<MarketplaceOrder>> Orders(VoiceScenario s) => s.K.Db(db => db.Orders.AsNoTracking().ToListAsync());
    private static Task<List<VoicePaymentAttempt>> Attempts(VoiceScenario s) => s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync());

    private static async Task<(string Order, long Total)> StartLink(VoiceScenario s)
    {
        var r = await s.Tool("pay_for_order", new { method = "text_link" });
        Assert.Equal("link_sent", r["status"]!.GetValue<string>());
        var order = Assert.Single(await Orders(s));
        return (order.Number, order.TotalCents);
    }

    [Fact]
    public async Task The_caller_is_texted_a_link_pays_on_solas_page_and_the_signed_notification_places_the_order()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        // the link went by text to the VERIFIED phone, with the exact amount and the order number as invoice
        var sms = s.Sms.Sent.Last();
        Assert.Equal("+15551234567", sms.To);
        Assert.Contains($"{Site}?xamount={SolaNotification.Dollars(total)}&xinvoice={number}", sms.Body);
        Assert.Null(s.State().KeypadStep);                                              // the call goes on while they pay
        var attempt = Assert.Single(await Attempts(s));
        Assert.Equal(("pending", total, number, PhonePaymentMethod.TextLink), (attempt.Status, attempt.AmountCents, attempt.OrderNumber, attempt.Method));
        Assert.False((await s.Tool("check_payment"))["order_placed"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total))).StatusCode);

        var paid = Assert.Single(await Orders(s));
        Assert.Equal((OrderStatus.Paid, total), (paid.Status, paid.PaidCents));
        Assert.Equal(("paid", "R-1"), ((await Attempts(s))[0].Status, (await Attempts(s))[0].ProviderRef));
        Assert.Equal(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
        var check = await s.Tool("check_payment");
        Assert.True(check["order_placed"]!.GetValue<bool>());
        Assert.Contains(number, s.Sms.Sent.Last().Body);                                // the confirmation text
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("XXXXXXXXXXXX0000") || m.Contains("R-1") || m.Contains("11201"));
        Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains("XXXXXXXXXXXX0000") || l.Contains("Dana Levi") || l.Contains(Pin));   // the notification is never logged
    }

    [Fact]
    public async Task Unsigned_wrongly_signed_or_tampered_notifications_do_nothing()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        var form = Approved(number, total);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(s, form, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(s, form, signature: new string('0', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(s, form, signature: SolaSignatureWith("some-other-pin-0123456789", form))).StatusCode);
        var good = CkSignature(Pin, form);
        var edited = new Dictionary<string, string>(form) { ["xAmount"] = "0.01" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(s, edited, signature: good)).StatusCode);              // signed for another amount
        var extra = new Dictionary<string, string>(form) { ["xNew"] = "1" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(s, extra, signature: good)).StatusCode);               // every parameter takes part
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);
        Assert.Equal("pending", Assert.Single(await Attempts(s)).Status);
    }

    private static string SolaSignatureWith(string pin, Dictionary<string, string> form) => CkSignature(pin, form);

    [Fact]
    public async Task A_customer_who_edits_the_amount_in_the_link_pays_nothing_we_accept()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        // Sola signs what it processed: the customer changed xamount in the link and paid less
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total - 1000))).StatusCode);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);           // never applied
        var attempt = Assert.Single(await Attempts(s));
        Assert.Equal("mismatch", attempt.Status);
        Assert.Contains(s.F.Logs.Lines, l => l.Contains("does not match the order") && l.Contains(number));   // for a person
        Assert.NotEqual(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
        Assert.False((await s.Tool("check_payment"))["order_placed"]!.GetValue<bool>());
        // a later correct payment for the same order is a new attempt, not this one
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total, rf: "R-2"))).StatusCode);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);           // the mismatched attempt is closed: nothing pending to settle
    }

    [Fact]
    public async Task Paying_too_much_or_for_another_invoice_is_not_applied_either()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        await Hook(s, Approved("MP-SOMEONE-ELSE", total));
        Assert.Equal("pending", Assert.Single(await Attempts(s)).Status);                          // no attempt for that invoice: ignored
        await Hook(s, Approved(number, total + 500));
        Assert.Equal("mismatch", Assert.Single(await Attempts(s)).Status);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);
    }

    [Fact]
    public async Task A_repeated_notification_is_the_same_payment()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        var form = Approved(number, total);
        var both = await Task.WhenAll(Hook(s, form), Hook(s, form), Hook(s, new Dictionary<string, string>(form)));
        Assert.All(both, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var paid = Assert.Single(await Orders(s));
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Single(await Attempts(s));
        Assert.Equal(1, await s.K.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == "ingest:" + number)));     // handed to the portal once
    }

    [Fact]
    public async Task Paying_the_same_link_twice_settles_once_and_the_second_charge_is_recorded_and_logged_for_a_person()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total, "R-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total, "R-2"))).StatusCode);     // a second, different sale for the same invoice
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total, "R-2"))).StatusCode);     // and its repeat

        var paid = Assert.Single(await Orders(s));
        Assert.Equal((OrderStatus.Paid, total), (paid.Status, paid.PaidCents));                         // the order is paid once
        var attempts = (await Attempts(s)).OrderBy(a => a.ProviderRef).ToList();
        Assert.Equal(new[] { ("paid", "R-1"), ("duplicate", "R-2") }, attempts.Select(a => (a.Status, a.ProviderRef!)).ToArray());   // the second charge is kept, once
        Assert.Equal(total, attempts[1].AmountCents);
        Assert.Single(s.F.Logs.Lines, l => l.Contains("paid twice") && l.Contains(number) && l.Contains("R-2"));   // a person is told
    }

    [Fact]
    public async Task A_declined_payment_leaves_the_order_for_another_try()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        var declined = Approved(number, total); declined["xResponseResult"] = "Declined";
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, declined)).StatusCode);
        Assert.Equal("declined", Assert.Single(await Attempts(s)).Status);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);
        // the caller asks for a new link: same order, a new attempt, and it can then succeed
        var again = await s.Tool("pay_for_order", new { method = "text_link" });
        Assert.Equal("link_sent", again["status"]!.GetValue<string>());
        Assert.Equal(2, (await Attempts(s)).Count);
        Assert.Single(await Orders(s));
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total, rf: "R-9"))).StatusCode);
        Assert.Equal(OrderStatus.Paid, Assert.Single(await Orders(s)).Status);
    }

    [Fact]
    public async Task Notifications_that_are_not_a_sale_or_not_ours_are_acknowledged_and_ignored()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        var auth = Approved(number, total); auth["xCommand"] = "cc:authonly";
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, auth)).StatusCode);
        var refund = Approved(number, total); refund["xCommand"] = "cc:refund";
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, refund)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, new Dictionary<string, string> { ["xCommand"] = "cc:sale", ["xResponseResult"] = "Approved" })).StatusCode);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);
        Assert.Equal("pending", Assert.Single(await Attempts(s)).Status);
    }

    [Fact]
    public async Task A_payment_made_after_the_caller_hung_up_still_places_the_order()
    {
        using var s = await Confirmed();
        var (number, total) = await StartLink(s);
        await s.Phone!.Send(new { @event = "stop" });
        await s.Phone.WaitClosed();
        await FakeRealtime.Poll(() => s.Row().Result.EndedAt is null ? null : "x", 5);
        Assert.Equal(VoiceOutcome.Abandoned, (await s.Row()).Outcome);
        Assert.Equal(HttpStatusCode.OK, (await Hook(s, Approved(number, total))).StatusCode);
        Assert.Equal(OrderStatus.Paid, Assert.Single(await Orders(s)).Status);
        Assert.Equal(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);                  // the cost report counts it
    }

    [Fact]
    public async Task The_sola_endpoint_does_not_exist_unless_sola_is_the_chosen_provider()
    {
        using var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        var form = Approved("MP-1", 100);
        Assert.Equal(HttpStatusCode.NotFound, (await Hook(s, form)).StatusCode);
        using var off = new VoiceScenario(config: Sola(new() { ["Voice:Enabled"] = "false" }));
        Assert.Equal(HttpStatusCode.NotFound, (await Hook(off, form)).StatusCode);
    }

    [Fact]
    public async Task The_keypad_method_is_not_offered_by_sola_and_a_text_needs_a_verified_phone()
    {
        using var s = await Confirmed();
        Assert.Equal("method_unavailable", (await s.Tool("pay_for_order", new { method = "keypad" }))["error"]!.GetValue<string>());
        Assert.Empty(await Attempts(s));
        Assert.Empty(await Orders(s));     // refused before any order was placed for it
        // the sms provider refusing the text leaves the attempt pending and tells the model
        s.Sms.Fail = true;
        Assert.Equal("sms_failed", (await s.Tool("pay_for_order", new { method = "text_link" }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sola_and_the_fake_are_refused_in_production_so_no_real_money_path_exists()
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Voice:Enabled"] = "true", ["Voice:OpenAiApiKey"] = "x", ["SignalWire:SigningKey"] = "sw-signing-key", ["Voice:StreamTokenKey"] = new string('k', 40), ["Voice:PhoneCodeKey"] = new string('c', 40), ["Voice:CallerHashKey"] = new string('h', 40),
            ["Voice:PublicBaseUrl"] = "https://api.example.com", ["SignalWire:ProjectId"] = "p", ["SignalWire:ApiToken"] = "t", ["SignalWire:Space"] = "s", ["SignalWire:FromNumber"] = "+15550002222",
            ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = Site, ["Sola:WebhookPin"] = Pin,
        };
        var problems = Solvelt.Marketplace.Api.Infrastructure.StartupValidator.VoiceProblems(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), new ProdEnv());
        Assert.Contains(problems, x => x.Contains("development and tests only"));
        await Task.CompletedTask;
    }

    private sealed class ProdEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
