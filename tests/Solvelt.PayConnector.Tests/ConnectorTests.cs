using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Solvelt.PayConnector.Tests;

/// <summary>The connector as a payment path under attack: every refusal happens BEFORE a card number goes anywhere, and the card number goes nowhere but into the one request to Sola.</summary>
public class ConnectorTests
{
    // ---------- who may call ----------

    [Fact]
    public void The_signature_is_the_documented_lowercase_hex_hmac_sha1_of_the_url_without_credentials_plus_the_raw_body()
    {
        var body = Kit.Body(tx: "t-1");
        var expected = Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Kit.SigningKey), Encoding.UTF8.GetBytes("https://pay.example.com/connector" + Encoding.UTF8.GetString(body))));
        Assert.Equal(expected, SignalWireRequest.Sign(Kit.SigningKey, Kit.PublicUrl, body));
        // the URL is normalised the way SignalWire signs it: lowercase scheme and host, dot segments resolved, "/" when there is no path, no credentials
        Assert.Equal("https://example.com/", SignalWireRequest.NormalisedUrl("HTTPS://Example.com"));
        Assert.Equal("https://example.com/connector", SignalWireRequest.NormalisedUrl("https://example.com/a/../connector"));
        Assert.Equal("https://example.com/connector", SignalWireRequest.NormalisedUrl("https://payuser:secret@example.com/connector"));
        Assert.True(SignalWireRequest.SignatureOk(Kit.SigningKey, Kit.PublicUrl, body, expected.ToUpperInvariant()));            // hex case does not matter
        Assert.False(SignalWireRequest.SignatureOk(Kit.SigningKey, Kit.PublicUrl, body, null));
        Assert.False(SignalWireRequest.SignatureOk(Kit.SigningKey, Kit.PublicUrl, body, ""));
        Assert.False(SignalWireRequest.SignatureOk("another-signing-key-0123456789abcdef", Kit.PublicUrl, body, expected));
        Assert.False(SignalWireRequest.SignatureOk(null, Kit.PublicUrl, body, expected));
        Assert.False(SignalWireRequest.SignatureOk(Kit.SigningKey, "https://evil.example.com/connector", body, expected));      // another URL
        body[10] ^= 1;
        Assert.False(SignalWireRequest.SignatureOk(Kit.SigningKey, Kit.PublicUrl, body, expected));                             // a changed body
    }

    [Fact]
    public void Basic_credentials_are_checked_in_constant_time_and_all_wrong_forms_fail()
    {
        Assert.True(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, Kit.BasicHeader()));
        Assert.False(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, Kit.BasicHeader(password: "wrong")));
        Assert.False(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, Kit.BasicHeader(user: "other")));
        Assert.False(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, null));
        Assert.False(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, "Bearer abc"));
        Assert.False(SignalWireRequest.BasicOk(Kit.BasicUser, Kit.BasicPassword, "Basic !!!not-base64"));
        Assert.False(SignalWireRequest.BasicOk(null, null, Kit.BasicHeader()));
    }

    [Fact]
    public async Task A_request_that_is_not_authentic_is_refused_before_anything_else_happens()
    {
        using var rig = new Rig();
        Assert.Equal(401, (await rig.Post(sign: false)).Status);                                                       // no signature
        Assert.Equal(401, (await rig.Post(signature: "0123456789abcdef0123456789abcdef01234567")).Status);              // a forged one
        Assert.Equal(401, (await rig.Post(auth: Kit.BasicHeader(password: "wrong-password-0123456789abcdef"))).Status);  // right signature, wrong Basic
        Assert.Equal(401, (await rig.Post(auth: "")).Status);
        var body = Kit.Body();
        Assert.Equal(401, (await rig.Handler.HandleAsync(true, Kit.BasicHeader(), Kit.Sign(body, key: "another-signing-key-0123456789abcdef"), body, default)).Status);
        Assert.Equal(0, rig.Api.ExpectCalls); Assert.Empty(rig.Sola.Calls);                                            // the API was not asked, Sola was not called
    }

    [Fact]
    public async Task Plain_http_and_bodies_of_the_wrong_size_are_refused()
    {
        using var rig = new Rig();
        Assert.Equal(403, (await rig.Post(https: false)).Status);
        Assert.Equal(400, (await rig.Handler.HandleAsync(true, Kit.BasicHeader(), "x", ReadOnlyMemory<byte>.Empty, default)).Status);
        var big = new byte[ConnectorHandler.MaxBodyBytes + 1];
        Assert.Equal(400, (await rig.Handler.HandleAsync(true, Kit.BasicHeader(), Kit.Sign(big), big, default)).Status);
        Assert.Empty(rig.Sola.Calls);
        using var relaxed = new Rig(o => o.RequireHttps = false);
        Assert.Equal(200, (await relaxed.Post(https: false)).Status);                                                   // only an explicit development setting allows http
    }

    // ---------- what is a valid card request ----------

    [Fact]
    public void A_valid_request_parses_and_the_card_is_held_only_in_pooled_byte_arrays()
    {
        using var r = ConnectorRequest.TryParse(Kit.Body(tx: "t-9"))!;
        Assert.Equal(("t-9", "35.00", "usd", Kit.Attempt, "01", "38", "11201"), (r.TransactionId, r.ChargeAmount, r.Currency, r.AttemptId, r.ExpiryMonth, r.ExpiryYear, r.PostalCode));
        Assert.Equal(Kit.Pan, Encoding.ASCII.GetString(r.Pan)); Assert.Equal(Kit.Cvv, Encoding.ASCII.GetString(r.Cvv));
        var fields = typeof(ConnectorRequest).GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Where(f => f.FieldType == typeof(string)).Select(f => f.Name).ToList();
        Assert.DoesNotContain(fields, n => n.Contains("pan", StringComparison.OrdinalIgnoreCase) || n.Contains("cvv", StringComparison.OrdinalIgnoreCase));   // no string field for the card number or the code
        Assert.DoesNotContain(typeof(ConnectorRequest).GetProperties().Where(p => p.PropertyType == typeof(string)), p => p.Name is "Pan" or "Cvv" or "CardNumber");
    }

    [Theory]
    [InlineData("4111111111111112")]          // fails the Luhn check
    [InlineData("411111111111")]              // one digit too short for a 12-digit minimum? (12 is allowed: this one fails Luhn)
    [InlineData("41111111111111111111")]      // too long
    [InlineData("4111 1111 1111 1111")]       // not digits only
    [InlineData("abcdefghijklmnop")]
    [InlineData("")]
    public void A_card_number_that_cannot_be_a_card_never_reaches_Sola(string pan)
    {
        Assert.Null(ConnectorRequest.TryParse(Kit.Body(pan: pan)));
    }

    [Theory]
    [InlineData("12")]       // too short
    [InlineData("12345")]    // too long
    [InlineData("12a")]
    [InlineData("")]
    public void A_bad_security_code_is_refused(string cvv) => Assert.Null(ConnectorRequest.TryParse(Kit.Body(cvv: cvv)));

    [Fact]
    public void Malformed_or_incomplete_requests_are_refused_without_saying_why()
    {
        foreach (var bad in new[] { "", "not json", "[]", "{", "{\"transaction_id\":\"x\"}", "{\"cardnumber\":\"4111111111111111\"}" })
            Assert.Null(ConnectorRequest.TryParse(Encoding.UTF8.GetBytes(bad)));
        Assert.Null(ConnectorRequest.TryParse(Kit.Body(currency: "eur")));
        Assert.Null(ConnectorRequest.TryParse(Kit.Body(attempt: "")));
        Assert.Null(ConnectorRequest.TryParse(Kit.Body(amount: "")));
        Assert.Null(ConnectorRequest.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Kit.Body()).Replace("\"expiry_month\":\"01\"", "\"expiry_month\":\"13\""))));
        Assert.Null(ConnectorRequest.TryParse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Kit.Body()).Replace("4111111111111111", "4111\\u0031111111111111"))));   // an escaped card number is not accepted
        Assert.True(ConnectorRequest.Luhn("4111111111111111"u8)); Assert.True(ConnectorRequest.Luhn("5500005555555559"u8)); Assert.False(ConnectorRequest.Luhn("4111111111111112"u8));
    }

    [Fact]
    public void The_card_buffers_are_zeroed_when_the_request_is_disposed()
    {
        var r = ConnectorRequest.TryParse(Kit.Body())!;
        var pan = (byte[])typeof(ConnectorRequest).GetField("_pan", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r)!;
        var cvv = (byte[])typeof(ConnectorRequest).GetField("_cvv", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r)!;
        Assert.Equal(Kit.Pan, Encoding.ASCII.GetString(pan, 0, 16)); Assert.Equal(Kit.Cvv, Encoding.ASCII.GetString(cvv, 0, 3));
        r.Dispose();
        Assert.All(pan, b => Assert.Equal(0, b)); Assert.All(cvv, b => Assert.Equal(0, b));                             // zeroed, not merely released
        Assert.Empty(r.Pan.ToArray()); r.Dispose();                                                                    // and a second Dispose is harmless
        // the one request buffer for Sola is zeroed too
        var buffer = new SecretBuffer();
        Encoding.ASCII.GetBytes(Kit.Pan).CopyTo(buffer.GetSpan(16)); buffer.Advance(16);
        var inner = (byte[])typeof(SecretBuffer).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
        Assert.Equal(Kit.Pan, Encoding.ASCII.GetString(inner, 0, 16));
        buffer.Dispose();
        Assert.All(inner, b => Assert.Equal(0, b));
    }

    // ---------- the amount comes from the SERVER ----------

    [Fact]
    public async Task A_valid_payment_charges_exactly_the_servers_amount_and_invoice_and_reports_it()
    {
        using var rig = new Rig();
        var reply = await rig.Post(Kit.Body(tx: "t-1"));
        Assert.Equal((200, "601518451", null), (reply.Status, reply.ChargeId, reply.ErrorCode));
        var call = Assert.Single(rig.Sola.Calls);
        Assert.Equal((3500L, Kit.Invoice), (call.Sale.AmountCents, call.Sale.Invoice));                                // what the SERVER said, not what the call said
        Assert.Equal((Kit.Pan, Kit.Cvv), (call.Pan, call.Cvv));
        var report = Assert.Single(rig.Api.Reports);
        Assert.Equal(new ChargeReport(Kit.Attempt, Kit.Invoice, "paid", 3500, "601518451", null), report);
        Assert.Equal("approved", rig.Ledger.Find("t-1")!.State); Assert.True(rig.Ledger.Find("t-1")!.Reported);
    }

    [Fact]
    public async Task A_request_whose_amount_differs_from_the_servers_is_refused_and_charges_nothing()
    {
        using var rig = new Rig();
        foreach (var amount in new[] { "34.99", "35.01", "0.01", "3500", "35", "35.000", "-35.00", "abc" })
        {
            var reply = await rig.Post(Kit.Body(amount: amount));
            Assert.Equal(("amount_mismatch", null), (reply.ErrorCode, reply.ChargeId)); Assert.Equal(200, reply.Status);
        }
        Assert.Empty(rig.Sola.Calls); Assert.Empty(rig.Api.Reports);
        Assert.Equal(8, rig.Logs.Lines.Count(l => l.StartsWith("Error") && l.Contains("another amount")));              // each one is logged for a person
    }

    [Fact]
    public async Task An_attempt_the_server_does_not_know_or_has_closed_charges_nothing()
    {
        using var rig = new Rig();
        rig.Api.Expect = null;
        Assert.Equal("attempt_not_open", (await rig.Post()).ErrorCode);
        Assert.Empty(rig.Sola.Calls);
    }

    [Fact]
    public async Task A_charge_over_the_connectors_limit_is_refused()
    {
        using var rig = new Rig(o => o.MaxChargeCents = 3000);
        Assert.Equal("over_limit", (await rig.Post()).ErrorCode);
        Assert.Empty(rig.Sola.Calls);
    }

    // ---------- idempotency and limits ----------

    [Fact]
    public async Task The_same_transaction_id_twice_is_one_charge_and_the_same_answer()
    {
        using var rig = new Rig();
        var body = Kit.Body(tx: "same");
        var first = await rig.Post(body); var second = await rig.Post(body);
        Assert.Equal(first, second);
        Assert.Single(rig.Sola.Calls); Assert.Single(rig.Api.Reports);
        var declined = new Rig(); declined.Sola.Result = new SolaResult(SolaOutcome.Declined, null, null, "05");
        var d1 = await declined.Post(Kit.Body(tx: "d")); var d2 = await declined.Post(Kit.Body(tx: "d"));
        Assert.Equal(("declined", "declined"), (d1.ErrorCode, d2.ErrorCode)); Assert.Single(declined.Sola.Calls);
        declined.Dispose();
    }

    [Fact]
    public async Task A_paid_attempt_takes_no_further_request_even_with_a_new_transaction_id()
    {
        using var rig = new Rig();
        Assert.Null((await rig.Post(Kit.Body(tx: "t-1"))).ErrorCode);
        var again = await rig.Post(Kit.Body(tx: "t-2"));                                                                // a second transaction for the SAME attempt after it was paid
        Assert.Equal("already_paid", again.ErrorCode);
        Assert.Single(rig.Sola.Calls);
    }

    [Fact]
    public async Task An_attempt_takes_a_limited_number_of_requests()
    {
        using var rig = new Rig(o => o.MaxRequestsPerAttempt = 2);
        rig.Sola.Result = new SolaResult(SolaOutcome.Declined, null, null, "05");
        Assert.Equal("declined", (await rig.Post(Kit.Body(tx: "a"))).ErrorCode);
        Assert.Equal("declined", (await rig.Post(Kit.Body(tx: "b"))).ErrorCode);
        Assert.Equal("too_many_attempts", (await rig.Post(Kit.Body(tx: "c"))).ErrorCode);                               // the third is not even sent to Sola
        Assert.Equal(2, rig.Sola.Calls.Count);
    }

    [Fact]
    public async Task Parallel_requests_for_one_transaction_charge_once()
    {
        using var rig = new Rig();
        var body = Kit.Body(tx: "race");
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rig.Post(body)));
        Assert.Single(rig.Sola.Calls);
        Assert.All(replies, r => Assert.True(r.ChargeId == "601518451" || r.ErrorCode == "in_progress"));      // the charge happened once; the others replay it or are told it is in progress
    }

    // ---------- what Sola says ----------

    [Fact]
    public async Task Declines_errors_and_unknown_answers_are_told_to_SignalWire_in_our_own_words()
    {
        foreach (var (outcome, code, status) in new[] { (SolaOutcome.Declined, "declined", "declined"), (SolaOutcome.Error, "gateway_error", "failed"), (SolaOutcome.Unknown, "gateway_unknown", "unknown") })
        {
            using var rig = new Rig();
            rig.Sola.Result = new SolaResult(outcome, null, null, "x");
            var reply = await rig.Post();
            Assert.Equal((200, code, null), (reply.Status, reply.ErrorCode, reply.ChargeId));
            Assert.Equal(status, Assert.Single(rig.Api.Reports).Status);
        }
    }

    [Fact]
    public async Task A_sale_whose_answer_is_unknown_is_never_retried_and_is_logged_for_a_person()
    {
        using var rig = new Rig();
        rig.Sola.Result = new SolaResult(SolaOutcome.Unknown, null, null, "no_answer");
        var reply = await rig.Post(Kit.Body(tx: "u-1"));
        Assert.Equal("gateway_unknown", reply.ErrorCode);
        Assert.Single(rig.Sola.Calls);                                                                                  // the card may have been charged: one call, never a second
        Assert.Equal("unknown", rig.Ledger.Find("u-1")!.State);
        Assert.Contains(rig.Logs.Lines, l => l.StartsWith("Error") && l.Contains("needs a person") && l.Contains("u-1"));
        Assert.Equal("already_paid", (await rig.Post(Kit.Body(tx: "u-2"))).ErrorCode);                                  // and the attempt takes no new charge while that is unresolved
    }

    [Fact]
    public async Task An_approval_for_another_amount_than_asked_is_not_reported_to_SignalWire_as_paid()
    {
        using var rig = new Rig();
        rig.Sola.Result = new SolaResult(SolaOutcome.Approved, "601518451", 3400, null);
        var reply = await rig.Post(Kit.Body(tx: "m-1"));
        Assert.Equal(("amount_mismatch", null), (reply.ErrorCode, reply.ChargeId));
        var report = Assert.Single(rig.Api.Reports);
        Assert.Equal(("paid", 3400L), (report.Status, report.PaidCents));                                               // the API is told what REALLY moved; its own check records a mismatch for a person
        Assert.Contains(rig.Logs.Lines, l => l.StartsWith("Error") && l.Contains("needs a person"));
    }

    [Fact]
    public async Task A_result_the_API_cannot_take_yet_is_kept_and_reported_later()
    {
        using var rig = new Rig();
        rig.Api.ReportWorks = false;
        var posting = rig.Post(Kit.Body(tx: "r-1"));
        while (!posting.IsCompleted) { rig.Time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }                // the retry delays run on the fake clock
        var reply = await posting;
        Assert.Equal("601518451", reply.ChargeId);                                                                      // the caller is told the truth meanwhile
        var pending = Assert.Single(rig.Ledger.Unreported());
        Assert.Equal(new ChargeReport(Kit.Attempt, Kit.Invoice, "paid", 3500, "601518451", null), ConnectorHandler.ReportOf(pending));
        rig.Api.ReportWorks = true;
        Assert.True(await rig.Api.ReportAsync(ConnectorHandler.ReportOf(pending), default)); rig.Ledger.MarkReported("r-1");
        Assert.Empty(rig.Ledger.Unreported());
    }

    // ---------- nothing stays "processing" for good ----------

    [Fact]
    public async Task Something_that_fails_after_the_sale_leaves_an_unknown_row_not_a_processing_one()
    {
        using var rig = new Rig();
        rig.Api.ThrowOnReport = new InvalidOperationException("boom 4111111111111111 cvv 123");
        var reply = await rig.Post(Kit.Body(tx: "after-1"));
        Assert.Equal(("gateway_unknown", null), (reply.ErrorCode, reply.ChargeId));
        var row = rig.Ledger.Find("after-1")!;
        Assert.Equal(("unknown", "601518451"), (row.State, row.ChargeId));                                             // kept with the reference: a person can find the sale
        Assert.Contains(rig.Logs.Lines, l => l.StartsWith("Error") && l.Contains("needs a person") && l.Contains("after-1") && l.Contains("601518451") && l.Contains(Kit.Attempt));
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains("boom") || l.Contains(Kit.Pan));                           // the exception type only
        Assert.Equal("already_paid", (await rig.Post(Kit.Body(tx: "after-2"))).ErrorCode);                              // and no new charge for the attempt
        Assert.Single(rig.Sola.Calls);
        Assert.Contains(rig.Ledger.Unreported(), r => r.TransactionId == "after-1");                                    // it is reported (for a person) by the retry service
    }

    [Fact]
    public async Task A_sale_call_that_throws_is_unknown_never_retried_and_logs_only_the_type()
    {
        using var rig = new Rig();
        rig.Sola.Throw = new InvalidOperationException("gateway blew up for 4111111111111111");
        var reply = await rig.Post(Kit.Body(tx: "throw-1"));
        Assert.Equal("gateway_unknown", reply.ErrorCode);
        Assert.Equal("unknown", rig.Ledger.Find("throw-1")!.State);
        Assert.Single(rig.Sola.Calls);
        Assert.Contains(rig.Logs.Lines, l => l.StartsWith("Error") && l.Contains("needs a person") && l.Contains("InvalidOperationException"));
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains("blew up") || l.Contains(Kit.Pan));
        Assert.Equal("unknown", Assert.Single(rig.Api.Reports).Status);
    }

    [Fact]
    public async Task A_row_stuck_in_processing_is_swept_to_unknown_and_reported_for_a_person()
    {
        using var rig = new Rig();
        Assert.Equal(BeginKind.Started, rig.Ledger.Begin("stuck-1", Kit.Attempt, Kit.Invoice, 3500, 3, rig.Time.GetUtcNow()).Kind);   // the request that owned it died
        var sweeper = new ReportRetryService(rig.Ledger, rig.Api, Microsoft.Extensions.Options.Options.Create(rig.Opts), rig.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportRetryService>.Instance);
        rig.Time.Advance(TimeSpan.FromSeconds(rig.Opts.SolaTimeoutSeconds + 30));
        await sweeper.RunOnceAsync(default);
        Assert.Equal("processing", rig.Ledger.Find("stuck-1")!.State);                                                  // still within the gateway timeout plus a minute: untouched
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await sweeper.RunOnceAsync(default);
        var row = rig.Ledger.Find("stuck-1")!;
        Assert.Equal(("unknown", "stuck_processing", true), (row.State, row.ErrorCode, row.Reported));
        Assert.Equal("unknown", Assert.Single(rig.Api.Reports).Status);                                                  // the API is told, so a person is
        Assert.Equal("already_paid", (await rig.Post(Kit.Body(tx: "next"))).ErrorCode);                                  // the attempt is not charged again behind a possible sale
    }

    // ---------- the API's answers must be signed too ----------

    [Fact]
    public async Task An_answer_from_the_API_with_a_bad_or_stale_signature_is_not_believed()
    {
        var o = Kit.Options();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        async Task<Expectation?> Ask(Func<string, (string Ts, string Sig)> sign)
        {
            const string json = """{"open":true,"amountCents":3500,"invoice":"MP-20261118-ABCD1234","currency":"USD"}""";
            var http = new HttpClient(new Handler((req, _) =>
            {
                var (ts, sig) = sign(json);
                var res = new HttpResponseMessage { Content = new StringContent(json) };
                res.Headers.Add(ConnectorSigning.TimestampHeader, ts); res.Headers.Add(ConnectorSigning.SignatureHeader, sig);
                return Task.FromResult(res);
            }));
            return await new MarketplaceApi(http, Microsoft.Extensions.Options.Options.Create(o), time, Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketplaceApi>.Instance).ExpectAsync(Kit.Attempt, default);
        }
        string Now() => time.GetUtcNow().ToUnixTimeSeconds().ToString();
        Assert.NotNull(await Ask(j => (Now(), ConnectorSigning.Sign(Kit.SharedKey, Now(), j))));                                          // control: a good answer is believed
        Assert.Null(await Ask(j => (Now(), ConnectorSigning.Sign("another-shared-key-0123456789abcdef0123", Now(), j))));               // signed with another key
        Assert.Null(await Ask(j => (Now(), "AAAA")));                                                                                    // garbage
        var stale = time.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds().ToString();
        Assert.Null(await Ask(j => (stale, ConnectorSigning.Sign(Kit.SharedKey, stale, j))));                                            // validly signed but old: a replayed answer
    }

    // ---------- Sola's wire format ----------

    [Fact]
    public async Task The_sale_goes_to_Sola_as_a_cc_sale_with_the_duplicate_guard_on()
    {
        string? sent = null; Uri? url = null;
        var http = new HttpClient(new Handler(async (req, ct) => { sent = await req.Content!.ReadAsStringAsync(ct); url = req.RequestUri; return new HttpResponseMessage { Content = new StringContent("""{"xResult":"A","xStatus":"Approved","xRefNum":"601518451","xAuthAmount":"35.00","xMaskedCardNumber":"4xxxxxxxxxxx1111","xErrorCode":"00000"}""") }; }));
        var gateway = new SolaGateway(http, Microsoft.Extensions.Options.Options.Create(Kit.Options()));
        using var card = ConnectorRequest.TryParse(Kit.Body())!;
        var result = await gateway.SaleAsync(new SolaSale(Kit.Invoice, 3500, "11201"), card, default);
        Assert.Equal((SolaOutcome.Approved, "601518451", 3500L), (result.Outcome, result.RefNum, result.AuthorisedCents));
        Assert.Equal("https://x1.cardknox.com/gatewayjson", url!.ToString());
        using var d = System.Text.Json.JsonDocument.Parse(sent!);
        string S(string n) => d.RootElement.GetProperty(n).GetString()!;
        Assert.Equal(("cc:sale", "5.0.0", "35.00", Kit.Invoice, "false", "0138", "11201"), (S("xCommand"), S("xVersion"), S("xAmount"), S("xInvoice"), S("xAllowDuplicate"), S("xExp"), S("xZip")));
        Assert.Equal((Kit.Pan, Kit.Cvv, "sola-sandbox-key-for-tests"), (S("xCardNum"), S("xCVV"), S("xKey")));
    }

    [Fact]
    public void Sola_answers_are_read_strictly()
    {
        SolaResult P(string json) => SolaGateway.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(SolaOutcome.Approved, P("""{"xResult":"A","xRefNum":"1","xAuthAmount":"12.50"}""").Outcome);
        Assert.Equal(1250, P("""{"xResult":"A","xRefNum":"1","xAuthAmount":"12.50"}""").AuthorisedCents);
        Assert.Equal(SolaOutcome.Declined, P("""{"xResult":"D","xErrorCode":"05"}""").Outcome);
        Assert.Equal(SolaOutcome.Error, P("""{"xResult":"E","xErrorCode":"00001"}""").Outcome);
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xAuthAmount":"12.50"}""").Outcome);                       // an approval without a reference is not a payment we can report
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xRefNum":"1","xAuthAmount":"12.345"}""").Outcome);       // 12.345 is not an amount of ours
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xRefNum":"1"}""").Outcome);
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"Z"}""").Outcome);
        Assert.Equal(SolaOutcome.Unknown, P("not json").Outcome);
        Assert.Equal("35.00", SolaGateway.Dollars(3500)); Assert.Equal("0.05", SolaGateway.Dollars(5));
    }

    [Fact]
    public async Task A_gateway_that_does_not_answer_is_unknown_not_declined()
    {
        var gateway = new SolaGateway(new HttpClient(new Handler((_, _) => throw new HttpRequestException("boom 4111111111111111"))), Microsoft.Extensions.Options.Options.Create(Kit.Options()));
        using var card = ConnectorRequest.TryParse(Kit.Body())!;
        var result = await gateway.SaleAsync(new SolaSale(Kit.Invoice, 3500, null), card, default);
        Assert.Equal((SolaOutcome.Unknown, "no_answer"), (result.Outcome, result.ErrorCode));                           // and the exception text (which could hold anything) is not carried
        var http500 = new SolaGateway(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)))), Microsoft.Extensions.Options.Options.Create(Kit.Options()));
        Assert.Equal(SolaOutcome.Unknown, (await http500.SaleAsync(new SolaSale(Kit.Invoice, 3500, null), card, default)).Outcome);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => f(request, cancellationToken);
    }

    // ---------- nothing about a card is ever logged ----------

    [Fact]
    public async Task No_log_line_ever_holds_card_data_whatever_goes_wrong()
    {
        var cards = new[] { "4111111111111111", "5500005555555559", "4111111111111112", "378282246310005", "4111111111111111111", "41111111" };
        foreach (var pan in cards)
        {
            using var rig = new Rig();
            // malformed bodies with the number in odd places, a bad signature, a mismatched amount, a gateway that throws with the number in its message, an API that does the same
            await rig.Post(Encoding.UTF8.GetBytes($"{{\"cardnumber\":\"{pan}\",\"cvv\":\"987\",\"oops\":"), sign: true);
            await rig.Post(Kit.Body(pan: pan, cvv: "987"), sign: false);
            await rig.Post(Kit.Body(pan: pan, cvv: "987", amount: "1.00"));
            rig.Sola.Throw = new InvalidOperationException($"gateway failed for {pan} cvv 987");
            try { await rig.Post(Kit.Body(tx: "x-tx", pan: pan, cvv: "987")); } catch (InvalidOperationException) { }
            rig.Api.ThrowOnExpect = new InvalidOperationException($"api failed for {pan}");
            try { await rig.Post(Kit.Body(tx: "y-tx", pan: pan, cvv: "987")); } catch (InvalidOperationException) { }
            var all = string.Join("\n", rig.Logs.Lines);
            Assert.DoesNotMatch(new Regex(@"\d{9,}"), all.Replace("601518451", "").Replace(Kit.Attempt, ""));                                    // no long digit run: no card number in any form (the one reference id of the fake is allowed)
            Assert.DoesNotContain("987", all); Assert.DoesNotContain(pan, all);
            Assert.DoesNotContain(Kit.SigningKey, all); Assert.DoesNotContain(Kit.BasicPassword, all); Assert.DoesNotContain("sola-sandbox-key", all); Assert.DoesNotContain(Kit.SharedKey, all);
        }
    }

    [Fact]
    public async Task The_ledger_holds_no_card_data()
    {
        using var rig = new Rig();
        await rig.Post(Kit.Body(tx: "l-1"));
        var row = rig.Ledger.Find("l-1")!;
        var text = string.Join("|", row.TransactionId, row.AttemptId, row.Invoice, row.Cents, row.State, row.ChargeId, row.ErrorCode);
        Assert.DoesNotContain(Kit.Pan, text); Assert.DoesNotContain(Kit.Cvv + "|", text);
        Assert.Equal(new[] { "TransactionId", "AttemptId", "Invoice", "Cents", "State", "ChargeId", "ErrorCode", "Reported" }, typeof(LedgerRow).GetProperties().Select(p => p.Name).ToArray());   // the row has no card fields at all
    }

    // ---------- start-up ----------

    [Fact]
    public void The_connector_refuses_to_start_in_production_and_without_its_secrets()
    {
        Assert.Empty(ConnectorOptions.Problems(Kit.Options(), production: false));
        Assert.Contains(ConnectorOptions.Problems(Kit.Options(), production: true), p => p.Contains("sandbox-only") && p.Contains("Production"));
        foreach (var (tweak, needle) in new (Action<ConnectorOptions>, string)[]
        {
            (o => o.SharedKey = "short", "Connector:SharedKey"), (o => o.SignalWireSigningKey = null, "SignalWireSigningKey"), (o => o.SharedKey = Kit.SigningKey, "must differ"),
            (o => o.PublicUrl = "http://pay.example.com/connector", "PublicUrl"), (o => o.PublicUrl = "https://u:p@pay.example.com/connector", "PublicUrl"), (o => o.BasicPassword = "short", "BasicPassword"),
            (o => o.BasicPassword = Kit.SharedKey, "must differ"), (o => o.SolaKey = null, "SolaKey"), (o => o.SolaUrl = "http://x1.cardknox.com/gatewayjson", "SolaUrl"), (o => o.ApiBaseUrl = "http://api.example.com", "ApiBaseUrl"),
            (o => o.MaxChargeCents = 0, "MaxChargeCents"), (o => o.MaxRequestsPerAttempt = 9, "MaxRequestsPerAttempt"),
        })
            Assert.Contains(ConnectorOptions.Problems(Kit.Options(tweak), false), p => p.Contains(needle));
    }
}
