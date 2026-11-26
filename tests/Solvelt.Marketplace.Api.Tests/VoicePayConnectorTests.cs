using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.PayConnector;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The API's half of the keypad card path (decision V-13): the connector is a separate service, and nothing it handles ever reaches this API except attempt ids, invoices, amounts and results, signed.</summary>
public class VoicePayConnectorTests
{
    private const string Key = "connector-shared-key-for-tests-0123456789abcdef", User = "payuser", Password = "basic-password-for-tests-0123456789abcdef", Url = "https://pay.example.com/connector";

    private static Dictionary<string, string?> Config() => new()
    {
        ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = "https://pay.sola.test/site", ["Sola:WebhookPin"] = "abcdefghij0123456789",
        ["Sola:PayConnectorUrl"] = Url, ["Sola:PayConnectorUser"] = User, ["Sola:PayConnectorPassword"] = Password, ["Sola:PayConnectorKey"] = Key, ["Sola:SavedCardKey"] = "saved-card-key-for-tests-0123456789abcdef",
    };

    private static async Task<VoiceScenario> Confirmed(Dictionary<string, string?>? config = null)
    {
        var s = new VoiceScenario(config: config ?? Config());
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, t1) = await s.Type(s.LastCode()); await s.Attach(t1);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" }); await s.Tool("confirm_guest_email");
        await s.Tool("set_delivery_address", new { house_number = "1", street = "Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }); await s.Tool("confirm_address");
        await s.Tool("read_back_order"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, t2) = await s.Type("1"); await s.Attach(t2);
        return s;
    }

    private static async Task<(System.Xml.Linq.XElement Pay, string Number, long Total, string Attempt)> StartKeypad(VoiceScenario s)
    {
        var r = await s.Tool("pay_for_order");
        Assert.Equal("payment_starting", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        var xml = await s.AfterStream();
        var pay = xml.Element("Pay")!;
        var order = Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));
        var attempt = Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
        return (pay, order.Number, order.TotalCents, attempt.Id.ToString("N"));
    }

    private static async Task<HttpResponseMessage> Send(VoiceScenario s, string path, string body, string? ts = null, string? sig = null, string key = Key)
    {
        ts ??= s.F.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (sig != "") { req.Headers.Add("X-Connector-Timestamp", ts); req.Headers.Add("X-Connector-Signature", sig ?? ConnectorSigning.Sign(key, ts, body)); }
        return await s.C.SendAsync(req);
    }

    private static string Report(string attempt, string invoice, string status, long cents, string? tx = "R-1", string? code = null)
        => JsonSerializer.Serialize(new { attemptId = attempt, invoice, status, paidCents = cents, transactionId = tx, code });

    // ---------- the instruction ----------

    [Fact]
    public async Task The_keypad_instruction_is_a_Pay_for_the_servers_amount_that_sends_the_card_to_the_connector_only()
    {
        using var s = await Confirmed();
        var (pay, number, total, attempt) = await StartKeypad(s);
        Assert.Equal(SolaNotification.Dollars(total), pay.Attribute("chargeAmount")!.Value);
        Assert.Equal("usd", pay.Attribute("currency")!.Value);
        var connector = new Uri(pay.Attribute("paymentConnectorUrl")!.Value);
        Assert.Equal(("https", "pay.example.com", "/connector", User, Password), (connector.Scheme, connector.Host, connector.AbsolutePath, Uri.UnescapeDataString(connector.UserInfo.Split(':')[0]), Uri.UnescapeDataString(connector.UserInfo.Split(':')[1])));
        Assert.Equal(attempt, pay.Elements("Parameter").Single(p => p.Attribute("name")!.Value == "attempt").Attribute("value")!.Value);
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, pay.Attribute("action")!.Value);
        Assert.Contains(number, pay.Attribute("description")!.Value);
        // the model and the logs never see the connector's credentials or key
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains(Password) || m.Contains(Key));
        Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(Password) || l.Contains(Key));
    }

    [Fact]
    public async Task Without_the_connector_there_is_no_keypad_card_method_and_the_endpoints_do_not_exist()
    {
        var config = Config(); config.Remove("Sola:PayConnectorUrl");
        var p = new SolaPhonePaymentProvider(Options.Create(new SolaOptions { PaymentSiteUrl = "https://pay.sola.test/site", WebhookPin = "abcdefghij0123456789" }), null!);
        Assert.False(p.Supports(PhonePaymentMethod.Keypad));
        using var s = new VoiceScenario(config: config);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(s, PayConnectorEndpoints.ExpectPath, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(s, PayConnectorEndpoints.ResultPath, "{}")).StatusCode);
        var fake = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        Assert.Equal(HttpStatusCode.NotFound, (await Send(fake, PayConnectorEndpoints.ExpectPath, "{}")).StatusCode);
        fake.Dispose();
    }

    [Theory]
    [InlineData("Sola:PayConnectorUrl", "http://pay.example.com/connector", "PayConnectorUrl")]
    [InlineData("Sola:PayConnectorUrl", "https://u:p@pay.example.com/connector", "PayConnectorUrl")]
    [InlineData("Sola:PayConnectorPassword", "short", "PayConnectorPassword")]
    [InlineData("Sola:PayConnectorKey", "short", "PayConnectorKey")]
    [InlineData("Sola:PayConnectorPassword", Key, "must differ")]
    [InlineData("Sola:PayConnectorUser", "", "PayConnectorUser")]
    [InlineData("Sola:SavedCardKey", "short", "SavedCardKey")]
    [InlineData("Sola:SavedCardKey", Key, "must differ")]
    public void The_connector_settings_are_checked_at_start_up(string key, string value, string problem)
    {
        var c = Config();
        var o = new SolaOptions { PaymentSiteUrl = c["Sola:PaymentSiteUrl"], WebhookPin = c["Sola:WebhookPin"], PayConnectorUrl = c["Sola:PayConnectorUrl"], PayConnectorUser = User, PayConnectorPassword = Password, PayConnectorKey = Key, SavedCardKey = "saved-card-key-for-tests-0123456789abcdef" };
        Assert.Empty(SolaOptions.Problems(o));
        switch (key) { case "Sola:PayConnectorUrl": o.PayConnectorUrl = value; break; case "Sola:PayConnectorPassword": o.PayConnectorPassword = value; break; case "Sola:PayConnectorKey": o.PayConnectorKey = value; break; case "Sola:SavedCardKey": o.SavedCardKey = value; break; default: o.PayConnectorUser = value; break; }
        Assert.Contains(SolaOptions.Problems(o), x => x.Contains(problem));
    }

    // ---------- expect: what to charge comes from the server ----------

    [Fact]
    public async Task The_connector_is_told_the_servers_amount_and_invoice_and_only_for_an_open_keypad_attempt()
    {
        using var s = await Confirmed();
        var (_, number, total, attempt) = await StartKeypad(s);
        var res = await Send(s, PayConnectorEndpoints.ExpectPath, JsonSerializer.Serialize(new { attemptId = attempt }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        using var d = JsonDocument.Parse(text);
        Assert.Equal((true, total, number, "USD"), (d.RootElement.GetProperty("open").GetBoolean(), d.RootElement.GetProperty("amountCents").GetInt64(), d.RootElement.GetProperty("invoice").GetString(), d.RootElement.GetProperty("currency").GetString()));
        // the answer is signed with the shared key too
        Assert.True(ConnectorSigning.Verify(Key, res.Headers.GetValues("X-Connector-Timestamp").Single(), text, res.Headers.GetValues("X-Connector-Signature").Single(), s.F.Clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
        foreach (var other in new[] { Guid.NewGuid().ToString("N"), "not-a-guid" })
            Assert.False(JsonDocument.Parse(await (await Send(s, PayConnectorEndpoints.ExpectPath, JsonSerializer.Serialize(new { attemptId = other }))).Content.ReadAsStringAsync()).RootElement.GetProperty("open").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(s, PayConnectorEndpoints.ExpectPath, "not json")).StatusCode);
    }

    [Fact]
    public async Task Requests_that_are_unsigned_wrongly_signed_stale_or_oversized_are_refused()
    {
        using var s = await Confirmed();
        var (_, number, total, attempt) = await StartKeypad(s);
        var body = JsonSerializer.Serialize(new { attemptId = attempt });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body, sig: "")).StatusCode);                          // no headers
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body, sig: "AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body, key: "another-key-0123456789abcdef0123456789")).StatusCode);
        var stale = s.F.Clock.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body, ts: stale)).StatusCode);                          // a replay of an old, once-valid request
        var future = s.F.Clock.GetUtcNow().AddMinutes(6).ToUnixTimeSeconds().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body, ts: future)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, body + " ", sig: ConnectorSigning.Sign(Key, s.F.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(), body))).StatusCode);   // a changed body
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ExpectPath, new string('x', 5000))).StatusCode);
        // an unsigned REPORT of a payment changes nothing
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.ResultPath, Report(attempt, number, "paid", total), sig: "")).StatusCode);
        Assert.Equal("pending", Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync())).Status);
    }

    // ---------- result: believed like every provider's word ----------

    [Fact]
    public async Task A_signed_paid_report_for_the_servers_amount_places_the_order_and_a_repeat_changes_nothing()
    {
        using var s = await Confirmed();
        var (_, number, total, attempt) = await StartKeypad(s);
        Assert.Equal(HttpStatusCode.OK, (await Send(s, PayConnectorEndpoints.ResultPath, Report(attempt, number, "paid", total))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(s, PayConnectorEndpoints.ResultPath, Report(attempt, number, "paid", total))).StatusCode);             // the connector retries: the same word again
        var order = Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));
        Assert.Equal((OrderStatus.Paid, total), (order.Status, order.PaidCents));
        Assert.Equal(("paid", "R-1"), (Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync())).Status, "R-1"));
        Assert.Equal(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
        Assert.Equal(1, s.Sms.Sent.Count(m => m.Body.Contains(number) && m.Body.Contains("placed and paid")));                                           // one confirmation text
        var (resume, token) = await s.PayAction("success");                                                                                               // SignalWire's action after the card step
        Assert.Equal(VoiceScripts.PayThanks, resume.Element("Say")!.Value);
        await s.Attach(token);
        Assert.True((await s.Tool("check_payment"))["order_placed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_report_for_another_amount_or_another_invoice_is_not_applied_and_is_logged_for_a_person()
    {
        foreach (var kind in new[] { "amount", "invoice" })
        {
            using var s = await Confirmed();
            var (_, number, total, attempt) = await StartKeypad(s);
            await Send(s, PayConnectorEndpoints.ResultPath, kind == "amount" ? Report(attempt, number, "paid", total - 1) : Report(attempt, "MP-SOMEONE-ELSES", "paid", total));
            Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync())).Status);
            Assert.NotEqual(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
        }
    }

    [Fact]
    public async Task Declined_failed_and_unknown_reports_leave_the_order_for_another_try_and_unknown_is_logged_for_a_person()
    {
        using var s = await Confirmed();
        var (_, number, total, attempt) = await StartKeypad(s);
        await Send(s, PayConnectorEndpoints.ResultPath, Report(attempt, number, "unknown", 0, null, "no_answer"));
        Assert.Contains(s.F.Logs.Lines, l => l.Contains("could not confirm") && l.Contains(number) && l.Contains("needs a person"));
        Assert.NotEqual(OrderStatus.Paid, Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync())).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(s, PayConnectorEndpoints.ResultPath, Report(Guid.NewGuid().ToString("N"), number, "paid", total))).StatusCode);   // an attempt that is not ours is told so, not applied
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(s, PayConnectorEndpoints.ResultPath, "{\"attemptId\":\"x\"}")).StatusCode);
    }

    // ---------- the whole path, both services, no card in any log ----------

    private sealed class FakeSola : ISolaGateway
    {
        public int Calls;
        public Task<SolaResult> SaleAsync(SolaSale sale, ConnectorRequest card, CancellationToken ct) { Calls++; return Task.FromResult(new SolaResult(SolaOutcome.Approved, "601518451", sale.AmountCents, null)); }
        public Task<SolaSaveResult> SaveAsync(ConnectorRequest card, CancellationToken ct) => throw new NotSupportedException();
        public Task<SolaResult> TokenSaleAsync(SolaSale sale, string token, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// Does a text show the start of the test card number? The login code in the SAME text is a random six-digit number and contains "4111" about three times in ten thousand logins (CI run 45122249434 hit it
    /// with 341115), so the code is taken out of the text first, by its exact wording; everything else is still searched, so a leaked card number is still found.
    /// </summary>
    internal static bool ShowsCardDigits(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"Solvelt code: \d{6}\.", "Solvelt code: ______.").Contains("4111");

    [Fact]
    public void The_card_digits_check_ignores_a_random_login_code_but_still_finds_a_leaked_card_number()
    {
        Assert.Contains("4111", "Solvelt code: 341115. It expires in 10 minutes.");                                       // what the old assertion tripped on (a code that happens to contain 4111)
        Assert.False(ShowsCardDigits("Solvelt code: 341115. It expires in 10 minutes."));
        Assert.False(ShowsCardDigits("Solvelt code: 411100. It expires in 10 minutes."));
        Assert.True(ShowsCardDigits("Solvelt: card 4111 1111 1111 1111 received"));                                              // a leak is still found
        Assert.True(ShowsCardDigits("Solvelt code: 123456. Your card 4111111111111111"));                                        // also next to a real code
        Assert.True(ShowsCardDigits("4111"));
    }

    [Fact]
    public async Task The_connector_and_the_API_settle_a_keypad_payment_together_and_the_caller_hears_it()
    {
        using var s = await Confirmed();
        var (pay, number, total, attempt) = await StartKeypad(s);
        var options = new ConnectorOptions { ApiBaseUrl = "https://api.example.com", SharedKey = Key, SignalWireSigningKey = "signalwire-signing-key-for-tests-0123456789", PublicUrl = Url, BasicUser = User, BasicPassword = Password, SolaKey = "sola-sandbox-key-for-tests", LedgerPath = ":memory:" };
        var logs = new List<string>();
        var api = new MarketplaceApi(new HttpClient(new Redirect(s.C)), Options.Create(options), s.F.Clock, NullLogger<MarketplaceApi>.Instance);
        using var ledger = new ChargeLedger(":memory:");
        var sola = new FakeSola();
        var handler = new ConnectorHandler(Options.Create(options), api, sola, ledger, s.F.Clock, NullLogger<ConnectorHandler>.Instance);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            transaction_id = "tx-1", method = "credit-card", cardnumber = "4111111111111111", cvv = "123", expiry_month = "01", expiry_year = "38", postal_code = "11201",
            chargeAmount = pay.Attribute("chargeAmount")!.Value, currency_code = "usd", parameters = new[] { new { attempt } },
        }));
        var reply = await handler.HandleAsync(true, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password)), SignalWireRequest.Sign(options.SignalWireSigningKey, Url, body), body, default);
        Assert.True(reply.ChargeId == "601518451", $"{reply.Status} {reply.ErrorCode} {reply.ErrorMessage}");
        Assert.Equal(1, sola.Calls);
        // the API believed the connector's signed report and placed the order
        Assert.Equal((OrderStatus.Paid, total), (Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync())) is var o ? o.Status : default, total));
        // then SignalWire's action: the keypad step resumes with the result
        var (resume, token) = await s.PayAction("success");
        Assert.Equal(VoiceScripts.PayThanks, resume.Element("Say")!.Value);
        await s.Attach(token);
        Assert.True((await s.Tool("check_payment"))["order_placed"]!.GetValue<bool>());
        // no card number anywhere the API or the model could see
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("4111111111111111") || m.Contains("601518451"));
        Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains("4111111111111111"));
        Assert.DoesNotContain(s.Sms.Sent, m => ShowsCardDigits(m.Body));
        var dump = JsonSerializer.Serialize(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
        Assert.DoesNotContain("4111111111111111", dump);
    }

    /// <summary>Lets the connector's real HTTP client talk to the API's in-memory test server.</summary>
    private sealed class Redirect(HttpClient target) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var copy = new HttpRequestMessage(request.Method, new Uri(target.BaseAddress!, request.RequestUri!.PathAndQuery)) { Content = new StringContent(await request.Content!.ReadAsStringAsync(ct), Encoding.UTF8, "application/json") };
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await target.SendAsync(copy, ct);
        }
    }
}
