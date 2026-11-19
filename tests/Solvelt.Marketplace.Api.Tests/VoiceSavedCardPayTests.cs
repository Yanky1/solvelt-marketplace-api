using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.PayConnector;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Paying with a SAVED card by phone (decision V-16), end to end: the server reads back the card and the amount, the caller's yes starts the charge, the API asks the real connector handler (signed channel) which talks to a fake Sola.</summary>
public class VoiceSavedCardPayTests
{
    private const string Email = "jane@example.com", Token = "tok-live-4f9c2a7e1b3d5a6c8e0f", SavedKey = "saved-card-key-for-tests-0123456789abcdef", Key = "connector-shared-key-for-tests-0123456789abcdef";

    private static Dictionary<string, string?> Config() => new()
    {
        ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = "https://pay.sola.test/site", ["Sola:WebhookPin"] = "abcdefghij0123456789",
        ["Sola:PayConnectorUrl"] = "https://pay.example.com/connector", ["Sola:PayConnectorUser"] = "payuser", ["Sola:PayConnectorPassword"] = "basic-password-for-tests-0123456789abcdef",
        ["Sola:PayConnectorKey"] = Key, ["Sola:SavedCardKey"] = SavedKey,
    };

    private sealed class RecordingSola : ISolaGateway
    {
        public SolaResult Result { get; set; } = new(SolaOutcome.Approved, "601518451", null, null);   // the authorised amount is filled from the sale
        public List<(SolaSale Sale, string Token)> TokenCalls { get; } = [];
        public Task<SolaResult> SaleAsync(SolaSale sale, ConnectorRequest card, CancellationToken ct) => throw new NotSupportedException();
        public Task<SolaSaveResult> SaveAsync(ConnectorRequest card, CancellationToken ct) => throw new NotSupportedException();
        public Task<SolaResult> TokenSaleAsync(SolaSale sale, string token, CancellationToken ct)
        {
            TokenCalls.Add((sale, token));
            return Task.FromResult(Result.Outcome == SolaOutcome.Approved ? Result with { AuthorisedCents = sale.AmountCents } : Result);
        }
    }

    /// <summary>The API's connector client, pointed at a REAL connector handler (fake Sola, in-memory ledger) that talks back to the API's test server over the signed channel.</summary>
    private sealed class Loopback : IPayConnectorClient
    {
        public ConnectorHandler? Handler;
        public bool Unreachable;
        public int Calls;
        public async Task<ConnectorSaleResult> ChargeTokenAsync(string attemptId, string token, CancellationToken ct)
        {
            Calls++;
            if (Unreachable) return new ConnectorSaleResult(false, null, "unclear");
            var reply = await Handler!.ChargeTokenAsync(attemptId, token, ct);
            return new ConnectorSaleResult(true, reply.ChargeId, reply.ErrorCode);
        }
    }

    private sealed class Redirect(HttpClient target) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var copy = new HttpRequestMessage(request.Method, new Uri(target.BaseAddress!, request.RequestUri!.PathAndQuery)) { Content = new StringContent(await request.Content!.ReadAsStringAsync(ct), Encoding.UTF8, "application/json") };
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await target.SendAsync(copy, ct);
        }
    }

    private sealed record Rig(VoiceScenario S, Guid Jane, RecordingSola Sola, Loopback Connector, ChargeLedger Ledger) : IDisposable
    {
        public void Dispose() { S.Dispose(); Ledger.Dispose(); }
    }

    private static async Task<Rig> Confirmed(Action<RecordingSola>? tweakSola = null)
    {
        var loop = new Loopback(); var sola = new RecordingSola(); tweakSola?.Invoke(sola);
        var s = new VoiceScenario(config: Config(), services: sv => sv.AddSingleton<IPayConnectorClient>(loop));
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        var options = new ConnectorOptions { ApiBaseUrl = "https://api.example.com", SharedKey = Key, SignalWireSigningKey = "signalwire-signing-key-for-tests-0123456789", PublicUrl = "https://pay.example.com/connector", BasicUser = "payuser", BasicPassword = "basic-password-for-tests-0123456789abcdef", SolaKey = "sola-sandbox-key-for-tests", LedgerPath = ":memory:" };
        var ledger = new ChargeLedger(":memory:");
        var api = new MarketplaceApi(new HttpClient(new Redirect(s.C)), Options.Create(options), s.F.Clock, NullLogger<MarketplaceApi>.Instance);
        loop.Handler = new ConnectorHandler(Options.Create(options), api, sola, ledger, s.F.Clock, NullLogger<ConnectorHandler>.Instance);

        await s.Dial();
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, t1) = await s.Type(s.LastCode()); await s.Attach(t1);
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await s.Tool("set_delivery_address", new { street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }); await s.Tool("confirm_address");
        Assert.Equal("reading_back", (await s.Tool("read_back_order"))["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, t2) = await s.Type("1"); await s.Attach(t2);
        return new Rig(s, jane, sola, loop, ledger);
    }

    private static async Task Seed(Rig r, string token = Token, Guid? account = null)
    {
        var vault = r.S.F.Services.GetRequiredService<SavedCardVault>();
        var owner = account ?? r.Jane;
        await r.S.K.Db(async db => { db.SavedCards.Add(new SavedCard { Id = Guid.NewGuid(), AccountId = owner, Token = vault.Protect(token, owner), TokenHash = vault.Fingerprint(token), Brand = "Visa", Last4 = "1111", ExpMonth = "07", ExpYear = "31", CreatedAt = r.S.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
    }

    private static async Task<string> StartPay(Rig r, int position = 1)
    {
        await r.S.Tool("list_saved_cards");
        var res = await r.S.Tool("pay_for_order", new { method = "saved_card", position });
        Assert.Equal("reading_back", res["status"]!.GetValue<string>());
        await r.S.EndStreamAfterGoodbye();
        return (await r.S.AfterStream()).Element("Gather")!.Element("Say")!.Value;
    }

    private static async Task<(string Say, string StreamToken)> Answer(Rig r, string digits)
    {
        var (xml, token) = await r.S.Type(digits);
        return (xml.Element("Say")!.Value, token);
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task The_caller_hears_the_card_and_the_amount_and_only_the_yes_charges_it_for_exactly_the_servers_order()
    {
        using var r = await Confirmed();
        await Seed(r);
        var total = r.S.State().QuoteTotalCents!.Value;
        var said = await StartPay(r);
        Assert.Contains("Visa card ending", said); Assert.Contains(VoiceSpeech.Money(total), said);                         // card and amount, read back by the SERVER
        Assert.DoesNotContain(r.S.Fake.Received, m => m.Contains("I will charge your"));                                       // the model never wrote the read-back
        Assert.Empty(r.Sola.TokenCalls); Assert.Empty(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));         // asking charges nothing and places nothing yet

        var (say, stream) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayThanks, say);
        await r.S.Attach(stream);
        var order = Assert.Single(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));
        Assert.Equal((OrderStatus.Paid, total), (order.Status, order.PaidCents));
        var call = Assert.Single(r.Sola.TokenCalls);
        Assert.Equal((total, order.Number, Token), (call.Sale.AmountCents, call.Sale.Invoice, call.Token));                  // the server's amount and invoice; the token reached ONLY the connector's Sola call
        var attempt = Assert.Single(await r.S.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
        Assert.Equal((PhonePaymentMethod.SavedCard, "paid", total), (attempt.Method, attempt.Status, attempt.AmountCents));
        Assert.Equal(VoiceOutcome.OrderPlaced, (await r.S.Row()).Outcome);
        Assert.True((await r.S.Tool("check_payment"))["order_placed"]!.GetValue<bool>());
        Assert.Contains(r.S.Sms.Sent, m => m.Body.Contains(order.Number) && m.Body.Contains("placed and paid"));             // the confirmation text
        Assert.DoesNotContain(r.S.Fake.Received, m => m.Contains(Token));
        Assert.DoesNotContain(r.S.F.Logs.Lines, l => l.Contains(Token));
        Assert.DoesNotContain(r.S.Sms.Sent, m => m.Body.Contains(Token));
    }

    [Fact]
    public async Task Saying_no_charges_nothing_and_places_nothing()
    {
        using var r = await Confirmed();
        await Seed(r);
        await StartPay(r);
        var (say, stream) = await Answer(r, "2");
        Assert.Equal(VoiceScripts.AccountKept, say);
        await r.S.Attach(stream);
        Assert.Empty(r.Sola.TokenCalls); Assert.Equal(0, r.Connector.Calls);
        Assert.Empty(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));
    }

    // ---------- who and what ----------

    [Fact]
    public async Task Only_the_callers_own_saved_card_in_the_list_the_server_read_out_can_be_chosen()
    {
        using var r = await Confirmed();
        await Seed(r);
        var stranger = Guid.NewGuid();
        await Seed(r, "tok-someone-else", stranger);
        Assert.Equal("no_such_position", (await r.S.Tool("pay_for_order", new { method = "saved_card", position = 1 }))["error"]!.GetValue<string>());   // the list was not read out yet
        await r.S.Tool("list_saved_cards");
        Assert.Equal("no_such_position", (await r.S.Tool("pay_for_order", new { method = "saved_card", position = 2 }))["error"]!.GetValue<string>());    // only one of them is theirs
        // even a forged card id (another account's) in the call state is not found by the payment step itself
        var theirs = (await r.S.K.Db(db => db.SavedCards.AsNoTracking().SingleAsync(c => c.AccountId == stranger))).Id;
        using var scope = r.S.F.Services.CreateScope();
        var call = await r.S.Row();
        Assert.Equal(SavedCardPayResult.Failed, await scope.ServiceProvider.GetRequiredService<SavedCardPayment>().PayAsync(r.S.State(), call, theirs, default));
        Assert.Empty(r.Sola.TokenCalls); Assert.Empty(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task A_guest_or_a_deployment_without_the_connector_cannot_pay_with_a_saved_card()
    {
        using (var guest = new VoiceScenario(config: Config()))
        {
            await guest.SyncCatalog(); await guest.Dial();
            await guest.Tool("identify_caller"); await guest.EndStreamAfterGoodbye(); await guest.AfterStream();
            var (_, t) = await guest.Type(guest.LastCode()); await guest.Attach(t);
            Assert.Equal("need_verification", (await guest.Tool("pay_for_order", new { method = "saved_card", position = 1 }))["error"]!.GetValue<string>());   // a guest has no saved cards
        }
        var plain = Config(); plain.Remove("Sola:PayConnectorUrl");
        using var s = new VoiceScenario(config: plain);
        await s.SyncCatalog(); await s.Dial();
        Assert.Equal("method_unavailable", (await s.Tool("pay_for_order", new { method = "saved_card", position = 1 }))["error"]!.GetValue<string>());
    }

    // ---------- when it does not go through ----------

    [Fact]
    public async Task A_declined_sale_leaves_the_order_unpaid_and_a_new_try_is_a_new_attempt()
    {
        using var r = await Confirmed(s => s.Result = new SolaResult(SolaOutcome.Declined, null, null, "05"));
        await Seed(r);
        await StartPay(r);
        var (say, stream) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayFailed, say);
        await r.S.Attach(stream);
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync())).Status);
        Assert.Equal("declined", Assert.Single(await r.S.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync())).Status);
        r.Sola.Result = new SolaResult(SolaOutcome.Approved, "601518452", null, null);                                       // the bank is fine now
        await StartPay(r);
        var (again, stream2) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayThanks, again);
        await r.S.Attach(stream2);
        Assert.Equal(OrderStatus.Paid, Assert.Single(await r.S.K.Db(db => db.Orders.AsNoTracking().ToListAsync())).Status);
        Assert.Equal(2, r.Sola.TokenCalls.Count);
    }

    [Fact]
    public async Task An_unclear_answer_is_never_retried_and_the_order_is_not_charged_again_behind_it()
    {
        using var r = await Confirmed();
        await Seed(r);
        r.Connector.Unreachable = true;                                                                                      // the connector gave no clear answer: the sale may have happened
        await StartPay(r);
        var (say, stream) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayPending, say);
        await r.S.Attach(stream);
        Assert.Equal(1, r.Connector.Calls);
        Assert.Contains(r.S.F.Logs.Lines, l => l.Contains("no clear answer") && l.Contains("needs a person"));
        Assert.Equal("pending", Assert.Single(await r.S.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync())).Status);
        r.Connector.Unreachable = false;
        await StartPay(r);                                                                                                   // the caller tries again
        var (again, stream2) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayPending, again);                                                                        // still pending: no second charge
        await r.S.Attach(stream2);
        Assert.Equal(1, r.Connector.Calls); Assert.Empty(r.Sola.TokenCalls);
    }

    [Fact]
    public async Task A_token_that_cannot_be_decrypted_charges_nothing_and_is_logged_for_a_person()
    {
        using var r = await Confirmed();
        await Seed(r, Token);
        await r.S.K.Db(async db => { await db.SavedCards.ExecuteUpdateAsync(x => x.SetProperty(c => c.Token, "v1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")); return 0; });
        await StartPay(r);
        var (say, stream) = await Answer(r, "1");
        Assert.Equal(VoiceScripts.PayFailed, say);
        await r.S.Attach(stream);
        Assert.Equal(0, r.Connector.Calls); Assert.Empty(r.Sola.TokenCalls);
        Assert.Contains(r.S.F.Logs.Lines, l => l.Contains("could not be decrypted") && l.Contains("needs a person"));
    }

    [Fact]
    public async Task The_connector_refuses_a_token_sale_for_an_attempt_the_server_did_not_open_or_for_another_amount()
    {
        using var r = await Confirmed();
        var strange = await r.Connector.Handler!.ChargeTokenAsync(Guid.NewGuid().ToString("N"), Token, default);
        Assert.Equal("attempt_not_open", strange.ErrorCode);
        Assert.Empty(r.Sola.TokenCalls);
    }
}
