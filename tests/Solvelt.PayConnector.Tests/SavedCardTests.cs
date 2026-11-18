using System.Text;

namespace Solvelt.PayConnector.Tests;

/// <summary>Saving a card (a tokenisation, <c>chargeAmount 0</c>) and charging a saved token, through the connector (V-16). No card data reaches the API; only a token, brand, last four digits and expiry do.</summary>
public class SavedCardTests
{
    private static readonly Expectation SaveAttempt = new(0, "", "USD", "save_card");
    private const string Token = "tok-abcdef0123456789";

    private static Rig SaveRig(Action<ConnectorOptions>? tweak = null) { var r = new Rig(tweak); r.Api.Expect = SaveAttempt; return r; }

    // ---------- saving ----------

    [Fact]
    public async Task A_card_is_saved_for_a_save_attempt_and_only_the_token_and_display_facts_go_to_the_API()
    {
        using var rig = SaveRig();
        var reply = await rig.Post(Kit.Body(tx: "s-1", amount: "0"));
        Assert.True(reply.IsToken); Assert.Null(reply.ErrorCode);
        Assert.Equal((Kit.Pan, Kit.Cvv), Assert.Single(rig.Sola.SaveCalls));
        Assert.Empty(rig.Sola.Calls);                                                                                  // no sale: nothing was charged
        var card = Assert.Single(rig.Api.Cards);
        Assert.Equal(new CardReport(Kit.Attempt, "saved", Token, "Visa", "1111", "0138", null), card);
        var row = rig.Ledger.Find("s-1")!;
        Assert.Equal(("approved", true, null), (row.State, row.Reported, row.Token));                                   // reported, and the token is erased from the ledger
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains(Token) || l.Contains(Kit.Pan));
    }

    [Fact]
    public async Task Only_a_save_attempt_with_amount_zero_saves_a_card()
    {
        using (var rig = new Rig())                                                                                    // a SALE attempt, but the request says 0
        {
            var reply = await rig.Post(Kit.Body(amount: "0"));
            Assert.Equal("amount_mismatch", reply.ErrorCode);
            Assert.Empty(rig.Sola.SaveCalls); Assert.Empty(rig.Sola.Calls);
        }
        using (var rig = SaveRig())                                                                                    // a SAVE attempt, but the request names an amount
        {
            Assert.Equal("amount_mismatch", (await rig.Post(Kit.Body(amount: "35.00"))).ErrorCode);
            Assert.Empty(rig.Sola.SaveCalls); Assert.Empty(rig.Api.Cards);
        }
        using (var rig = SaveRig())
        {
            rig.Api.Expect = new Expectation(0, "", "USD", "something_else");
            Assert.Equal("attempt_not_open", (await rig.Post(Kit.Body(amount: "0"))).ErrorCode);
            rig.Api.Expect = null;
            Assert.Equal("attempt_not_open", (await rig.Post(Kit.Body(amount: "0"))).ErrorCode);
            Assert.Empty(rig.Sola.SaveCalls);
        }
    }

    [Fact]
    public async Task The_same_transaction_saves_once_and_a_saved_attempt_takes_no_further_save()
    {
        using var rig = SaveRig();
        var first = await rig.Post(Kit.Body(tx: "same", amount: "0")); var again = await rig.Post(Kit.Body(tx: "same", amount: "0"));
        Assert.True(first.IsToken && again.IsToken);
        Assert.Single(rig.Sola.SaveCalls); Assert.Single(rig.Api.Cards);
        Assert.Equal("already_saved", (await rig.Post(Kit.Body(tx: "other", amount: "0"))).ErrorCode);                  // a new transaction for the same attempt
        Assert.Single(rig.Sola.SaveCalls);
    }

    [Theory]
    [InlineData(SolaOutcome.Declined, "declined", "declined")]
    [InlineData(SolaOutcome.Error, "gateway_error", "failed")]
    [InlineData(SolaOutcome.Unknown, "gateway_unknown", "unknown")]
    public async Task A_refused_or_unclear_save_is_told_in_our_words_and_reported_without_a_token(SolaOutcome outcome, string code, string status)
    {
        using var rig = SaveRig();
        rig.Sola.SaveResult = new SolaSaveResult(outcome, null, null, null, null, "x");
        var reply = await rig.Post(Kit.Body(amount: "0"));
        Assert.Equal((false, code), (reply.IsToken, reply.ErrorCode));
        var card = Assert.Single(rig.Api.Cards);
        Assert.Equal((status, null), (card.Status, card.Token));
        if (outcome == SolaOutcome.Unknown) Assert.Equal("already_saved", (await rig.Post(Kit.Body(tx: "retry", amount: "0"))).ErrorCode);   // an unclear save is not retried behind it
        Assert.Single(rig.Sola.SaveCalls);
    }

    [Fact]
    public async Task A_save_the_API_cannot_take_yet_keeps_the_token_in_the_ledger_until_it_can_and_then_erases_it()
    {
        using var rig = SaveRig();
        rig.Api.CardReportWorks = false;
        var posting = rig.Post(Kit.Body(tx: "late", amount: "0"));
        while (!posting.IsCompleted) { rig.Time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
        Assert.True((await posting).IsToken);                                                                          // the caller is told it was saved
        var row = rig.Ledger.Find("late")!;
        Assert.Equal(("approved", false, Token), (row.State, row.Reported, row.Token));
        rig.Api.CardReportWorks = true;
        var sweeper = new ReportRetryService(rig.Ledger, rig.Api, Microsoft.Extensions.Options.Options.Create(rig.Opts), rig.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportRetryService>.Instance);
        await sweeper.RunOnceAsync(default);
        Assert.Equal(Token, Assert.Single(rig.Api.Cards).Token);
        Assert.Equal((true, null), (rig.Ledger.Find("late")!.Reported, rig.Ledger.Find("late")!.Token));
    }

    [Fact]
    public async Task A_token_the_API_does_not_take_in_time_is_wiped_logged_for_a_person_and_reported_as_unclear()
    {
        using var rig = SaveRig(o => o.SavedTokenMinutes = 30);
        rig.Api.CardReportWorks = false;
        var posting = rig.Post(Kit.Body(tx: "stale", amount: "0"));
        while (!posting.IsCompleted) { rig.Time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
        await posting;
        var sweeper = new ReportRetryService(rig.Ledger, rig.Api, Microsoft.Extensions.Options.Options.Create(rig.Opts), rig.Time, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<ReportRetryService>(new Microsoft.Extensions.Logging.LoggerFactory([rig.Logs])));
        rig.Time.Advance(TimeSpan.FromMinutes(29));
        await sweeper.RunOnceAsync(default);
        Assert.Equal(Token, rig.Ledger.Find("stale")!.Token);                                                           // still within the window: kept for the retry
        rig.Api.CardReportWorks = true;
        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await sweeper.RunOnceAsync(default);
        var row = rig.Ledger.Find("stale")!;
        Assert.Equal((null, "abandoned", "token_not_delivered"), (row.Token, row.State, row.ErrorCode));                  // wiped
        Assert.Contains(rig.Logs.Lines, l => l.StartsWith("Error") && l.Contains("wiped") && l.Contains("needs a person"));
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains(Token));
        var report = Assert.Single(rig.Api.Cards);
        Assert.Equal(("unknown", null), (report.Status, report.Token));                                                 // the API is told it was unclear, never given the old token
    }

    [Fact]
    public void Sola_saves_are_read_strictly()
    {
        SolaSaveResult P(string json) => SolaGateway.ParseSave(Encoding.UTF8.GetBytes(json));
        var ok = P("""{"xResult":"A","xToken":"tok-1","xMaskedCardNumber":"4xxxxxxxxxxx1111","xCardType":"Visa","xExp":"1030"}""");
        Assert.Equal((SolaOutcome.Approved, "tok-1", "Visa", "1111", "1030"), (ok.Outcome, ok.Token, ok.Brand, ok.Last4, ok.Exp));
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xMaskedCardNumber":"4xxxxxxxxxxx1111","xExp":"1030"}""").Outcome);          // no token
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xToken":"t","xExp":"1030"}""").Outcome);                                        // no last four
        Assert.Equal(SolaOutcome.Unknown, P("""{"xResult":"A","xToken":"t","xMaskedCardNumber":"4xxxxxxxxxxx1111","xExp":"13"}""").Outcome);   // no usable expiry
        Assert.Equal(SolaOutcome.Declined, P("""{"xResult":"D"}""").Outcome); Assert.Equal(SolaOutcome.Error, P("""{"xResult":"E"}""").Outcome);
        Assert.Equal(SolaOutcome.Unknown, P("not json").Outcome);
        Assert.Equal("Card", P("""{"xResult":"A","xToken":"t","xMaskedCardNumber":"4xxxxxxxxxxx1111","xCardType":"<script>","xExp":"1030"}""").Brand);   // a brand that is not plain words is not echoed
    }

    [Fact]
    public async Task The_save_goes_to_Sola_as_cc_save_without_an_amount()
    {
        string? sent = null;
        var http = new HttpClient(new FuncHandler(async (req, ct) => { sent = await req.Content!.ReadAsStringAsync(ct); return new HttpResponseMessage { Content = new StringContent("""{"xResult":"A","xToken":"tok-1","xMaskedCardNumber":"4xxxxxxxxxxx1111","xCardType":"Visa","xExp":"0138"}""") }; }));
        var gateway = new SolaGateway(http, Microsoft.Extensions.Options.Options.Create(Kit.Options()));
        using var card = ConnectorRequest.TryParse(Kit.Body(amount: "0"))!;
        Assert.Equal(SolaOutcome.Approved, (await gateway.SaveAsync(card, default)).Outcome);
        using var d = System.Text.Json.JsonDocument.Parse(sent!);
        Assert.Equal(("cc:save", Kit.Pan, "0138"), (d.RootElement.GetProperty("xCommand").GetString(), d.RootElement.GetProperty("xCardNum").GetString(), d.RootElement.GetProperty("xExp").GetString()));
        Assert.False(d.RootElement.TryGetProperty("xAmount", out _));
    }

    // ---------- charging a saved token ----------

    [Fact]
    public async Task A_saved_token_is_charged_for_exactly_the_servers_amount_and_invoice()
    {
        using var rig = new Rig();
        var reply = await rig.Handler.ChargeTokenAsync(Kit.Attempt, Token, default);
        Assert.Equal(("601518451", null), (reply.ChargeId, reply.ErrorCode));
        var call = Assert.Single(rig.Sola.TokenCalls);
        Assert.Equal((3500L, Kit.Invoice, Token), (call.Sale.AmountCents, call.Sale.Invoice, call.Token));
        Assert.Equal(new ChargeReport(Kit.Attempt, Kit.Invoice, "paid", 3500, "601518451", null), Assert.Single(rig.Api.Reports));
        Assert.Empty(rig.Sola.Calls);                                                                                   // no card sale, no card data
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains(Token));
    }

    [Fact]
    public async Task A_token_sale_is_one_charge_per_attempt_and_refuses_anything_that_is_not_an_open_sale()
    {
        using var rig = new Rig();
        await rig.Handler.ChargeTokenAsync(Kit.Attempt, Token, default);
        Assert.Equal("601518451", (await rig.Handler.ChargeTokenAsync(Kit.Attempt, Token, default)).ChargeId);        // a repeat replays the stored answer
        Assert.Single(rig.Sola.TokenCalls);
        using (var other = new Rig()) { other.Api.Expect = null; Assert.Equal("attempt_not_open", (await other.Handler.ChargeTokenAsync(Kit.Attempt, Token, default)).ErrorCode); Assert.Empty(other.Sola.TokenCalls); }
        using (var save = SaveRig()) { Assert.Equal("attempt_not_open", (await save.Handler.ChargeTokenAsync(Kit.Attempt, Token, default)).ErrorCode); Assert.Empty(save.Sola.TokenCalls); }   // a save attempt is not a sale
        using (var big = new Rig(o => o.MaxChargeCents = 3000)) { Assert.Equal("over_limit", (await big.Handler.ChargeTokenAsync(Kit.Attempt, Token, default)).ErrorCode); Assert.Empty(big.Sola.TokenCalls); }
        using (var empty = new Rig()) { Assert.Equal("bad_request", (await empty.Handler.ChargeTokenAsync(Kit.Attempt, " ", default)).ErrorCode); Assert.Empty(empty.Sola.TokenCalls); }
    }

    [Fact]
    public async Task A_token_sale_that_throws_or_is_unclear_is_unknown_and_never_retried()
    {
        using var rig = new Rig();
        rig.Sola.Throw = new InvalidOperationException("boom " + Token);
        var reply = await rig.Handler.ChargeTokenAsync(Kit.Attempt, Token, default);
        Assert.Equal("gateway_unknown", reply.ErrorCode);
        Assert.Single(rig.Sola.TokenCalls);
        Assert.Equal("gateway_unknown", (await rig.Handler.ChargeTokenAsync(Kit.Attempt, Token, default)).ErrorCode);   // the same attempt again: the stored unknown, never a second call
        Assert.Single(rig.Sola.TokenCalls);
        Assert.Equal("unknown", rig.Ledger.Find("tok-" + Kit.Attempt)!.State);
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Contains("boom") || l.Contains(Token));
        Assert.Equal("unknown", Assert.Single(rig.Api.Reports).Status);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => f(request, cancellationToken);
    }
}
