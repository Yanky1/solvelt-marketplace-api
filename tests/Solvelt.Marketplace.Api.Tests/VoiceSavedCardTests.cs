using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.PayConnector;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Saved cards by phone (decision V-16): the card goes to the pay connector, this API keeps only the processor's token, the brand, the last four digits and the expiry, and nothing is charged.</summary>
public class VoiceSavedCardTests
{
    private const string SavedKey = "saved-card-key-for-tests-0123456789abcdef", Email = "jane@example.com", Key = "connector-shared-key-for-tests-0123456789abcdef", Token = "tok-9f8e7d6c5b4a39281706";

    private static Dictionary<string, string?> Config() => new()
    {
        ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = "https://pay.sola.test/site", ["Sola:WebhookPin"] = "abcdefghij0123456789",
        ["Sola:PayConnectorUrl"] = "https://pay.example.com/connector", ["Sola:PayConnectorUser"] = "payuser", ["Sola:PayConnectorPassword"] = "basic-password-for-tests-0123456789abcdef", ["Sola:PayConnectorKey"] = Key, ["Sola:SavedCardKey"] = SavedKey,
    };

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    private static async Task<(VoiceScenario S, Guid Jane, string Session)> AccountCall(Dictionary<string, string?>? config = null)
    {
        var s = new VoiceScenario(config: config ?? Config());
        await s.SyncCatalog(); s.PortalShipping();
        var session = await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        await s.Dial(); await Verify(s);
        return (s, jane, session);
    }

    private static async Task<HttpResponseMessage> Send(VoiceScenario s, string path, string body, string? sig = null, string? ts = null)
    {
        ts ??= s.F.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (sig != "") { req.Headers.Add("X-Connector-Timestamp", ts); req.Headers.Add("X-Connector-Signature", sig ?? ConnectorSigning.Sign(Key, ts, body)); }
        return await s.C.SendAsync(req);
    }

    private static string Report(string attempt, string status = "saved", string? token = Token, string brand = "Visa", string? last4 = "1111", string? exp = "0138")
        => JsonSerializer.Serialize(new { attemptId = attempt, status, token, brand, last4, exp, code = (string?)null });

    private static async Task<(string Attempt, System.Xml.Linq.XElement Pay)> StartSave(VoiceScenario s)
    {
        var r = await s.Tool("add_saved_card");
        Assert.Equal("card_step_starting", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        var pay = (await s.AfterStream()).Element("Pay")!;
        var attempt = Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().Where(a => a.Method == "save_card").ToListAsync()));
        return (attempt.Id.ToString("N"), pay);
    }

    private static Task<List<SavedCard>> Cards(VoiceScenario s) => s.K.Db(db => db.SavedCards.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync());

    // ---------- saving ----------

    [Fact]
    public async Task Saving_a_card_runs_a_zero_amount_reusable_Pay_into_the_connector_and_charges_nothing()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            var (attempt, pay) = await StartSave(s);
            Assert.Equal(("0", "reusable"), (pay.Attribute("chargeAmount")!.Value, pay.Attribute("tokenType")!.Value));
            Assert.Equal("pay.example.com", new Uri(pay.Attribute("paymentConnectorUrl")!.Value).Host);
            Assert.Equal(attempt, pay.Elements("Parameter").Single(p => p.Attribute("name")!.Value == "attempt").Attribute("value")!.Value);
            var row = Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
            Assert.Equal(("save_card", 0L, "cs-" + jane.ToString("N"), "pending"), (row.Method, row.AmountCents, row.OrderNumber, row.Status));
            Assert.Empty(await s.K.Db(db => db.Orders.AsNoTracking().ToListAsync()));                                   // no order, nothing charged
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains("connector") || m.Contains("pay.example.com"));
        }
    }

    [Fact]
    public async Task The_connector_is_told_it_is_a_save_attempt_and_its_signed_report_stores_only_token_brand_last_four_and_expiry()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            var (attempt, _) = await StartSave(s);
            using var d = JsonDocument.Parse(await (await Send(s, PayConnectorEndpoints.ExpectPath, JsonSerializer.Serialize(new { attemptId = attempt }))).Content.ReadAsStringAsync());
            Assert.Equal((true, 0L, "save_card"), (d.RootElement.GetProperty("open").GetBoolean(), d.RootElement.GetProperty("amountCents").GetInt64(), d.RootElement.GetProperty("purpose").GetString()));
            Assert.Equal(HttpStatusCode.OK, (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt))).StatusCode);   // a repeat: the first word stands
            var card = Assert.Single(await Cards(s));
            Assert.Equal((jane, "Visa", "1111", "01", "38"), (card.AccountId, card.Brand, card.Last4, card.ExpMonth, card.ExpYear));
            Assert.DoesNotContain(Token, card.Token);                                                                   // ENCRYPTED at rest: the database never holds the token itself
            var vault = s.F.Services.GetRequiredService<SavedCardVault>();
            Assert.Equal(vault.Fingerprint(Token), card.TokenHash);                                                     // the duplicate guard is a keyed fingerprint, not the token
            Assert.Equal(Token, vault.Unprotect(card.Token, jane));                                                     // the owner's key opens it
            Assert.Null(vault.Unprotect(card.Token, Guid.NewGuid()));                                                   // bound to the account: another account's id does not
            Assert.Equal("saved", (await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().SingleAsync())).Status);
            // after the report the attempt is closed: the connector is told it is not open any more
            using var closed = JsonDocument.Parse(await (await Send(s, PayConnectorEndpoints.ExpectPath, JsonSerializer.Serialize(new { attemptId = attempt }))).Content.ReadAsStringAsync());
            Assert.False(closed.RootElement.GetProperty("open").GetBoolean());
            // SignalWire's action resumes the call: the server says it is saved, and neither the token nor the card ever reaches the model, a text or a log
            var (resume, tokenForStream) = await s.PayAction("success");
            Assert.Equal(VoiceScripts.CardSaved, resume.Element("Say")!.Value);
            await s.Attach(tokenForStream);
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains(Token) || m.Contains("1111"));
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(Token));
            Assert.DoesNotContain(s.Sms.Sent, m => m.Body.Contains(Token));
        }
    }

    [Fact]
    public async Task A_report_that_is_unsigned_stale_for_another_attempt_or_in_a_bad_shape_stores_nothing()
    {
        var (s, _, _) = await AccountCall();
        using (s)
        {
            var (attempt, _) = await StartSave(s);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt), sig: "")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt), sig: "AAAA")).StatusCode);
            var stale = s.F.Clock.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds().ToString();
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt), ts: stale, sig: ConnectorSigning.Sign(Key, stale, Report(attempt)))).StatusCode);
            Assert.Contains("unknown_attempt", await (await Send(s, PayConnectorEndpoints.CardPath, Report(Guid.NewGuid().ToString("N")))).Content.ReadAsStringAsync());
            foreach (var bad in new[] { Report(attempt, last4: "11x1"), Report(attempt, exp: "1330"), Report(attempt, token: null), Report(attempt, token: new string('t', 101)), Report(attempt, brand: "") })
            {
                await Send(s, PayConnectorEndpoints.CardPath, bad);
                Assert.Empty(await Cards(s));
                // each bad one closed the attempt as failed: reopen by starting again is a new attempt, so reset the row for the next shape
                await s.K.Db(async db => { await db.VoicePaymentAttempts.ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, "pending")); return 0; });
            }
        }
    }

    [Fact]
    public async Task A_declined_or_failed_save_stores_nothing_and_the_caller_hears_it()
    {
        var (s, _, _) = await AccountCall();
        using (s)
        {
            var (attempt, _) = await StartSave(s);
            await Send(s, PayConnectorEndpoints.CardPath, Report(attempt, status: "declined", token: null, brand: "", last4: null, exp: null));
            Assert.Empty(await Cards(s));
            var (resume, token) = await s.PayAction("failed");
            Assert.Equal(VoiceScripts.CardFailed, resume.Element("Say")!.Value);
            await s.Attach(token);
        }
    }

    [Fact]
    public async Task A_report_still_on_its_way_is_a_pending_answer_and_an_account_holds_at_most_five_cards()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            var (attempt, _) = await StartSave(s);
            var (resume, token) = await s.PayAction("success");                                                         // the action arrived before the connector's report
            Assert.Equal(VoiceScripts.CardPending, resume.Element("Say")!.Value);
            await s.Attach(token);
            for (var i = 0; i < SavedCardLimits.MaxPerAccount; i++) await s.K.Db(async db => { db.SavedCards.Add(new SavedCard { TokenHash = Guid.NewGuid().ToString("N"), Id = Guid.NewGuid(), AccountId = jane, Token = "t" + i, Brand = "Visa", Last4 = "1111", ExpMonth = "01", ExpYear = "38", CreatedAt = s.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
            Assert.Equal("too_many_cards", (await s.Tool("add_saved_card"))["error"]!.GetValue<string>());                // the sixth is refused before the card step starts
            await Send(s, PayConnectorEndpoints.CardPath, Report(attempt));                                              // and a late report over the cap is not kept
            Assert.Equal(SavedCardLimits.MaxPerAccount, (await Cards(s)).Count);
            Assert.DoesNotContain(await Cards(s), c => c.Token == Token);
        }
    }

    [Fact]
    public async Task Only_a_verified_account_with_the_connector_configured_can_save_a_card()
    {
        using (var guest = new VoiceScenario(config: Config()))
        {
            await guest.SyncCatalog(); await guest.Dial(); await Verify(guest);                                         // a verified GUEST has no account
            Assert.Equal("need_verification", (await guest.Tool("add_saved_card"))["error"]!.GetValue<string>());
            Assert.Equal("need_verification", (await guest.Tool("list_saved_cards"))["error"]!.GetValue<string>());
        }
        var plain = Config(); plain.Remove("Sola:PayConnectorUrl");
        var (s, _, _) = await AccountCall(plain);
        using (s) Assert.Equal("cards_unavailable", (await s.Tool("add_saved_card"))["error"]!.GetValue<string>());
        var (t, _, _) = await AccountCall();
        using (t)
        {
            Assert.Equal("card_step_starting", (await t.Tool("add_saved_card"))["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_report_cannot_attach_a_card_to_another_account_than_the_calls_and_the_vault_refuses_tampering()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            var (attempt, _) = await StartSave(s);
            // the attempt names an account that is not the one signed in on the live call: refused, nothing stored, the attempt stays pending for the real report
            var other = Guid.NewGuid();
            await s.K.Db(async db => { await db.VoicePaymentAttempts.ExecuteUpdateAsync(x => x.SetProperty(a => a.OrderNumber, "cs-" + other.ToString("N"))); return 0; });
            Assert.Contains("wrong_account", await (await Send(s, PayConnectorEndpoints.CardPath, Report(attempt))).Content.ReadAsStringAsync());
            Assert.Empty(await Cards(s));
            Assert.Equal("pending", (await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().SingleAsync())).Status);
            await s.K.Db(async db => { await db.VoicePaymentAttempts.ExecuteUpdateAsync(x => x.SetProperty(a => a.OrderNumber, "cs-" + jane.ToString("N"))); return 0; });
            await Send(s, PayConnectorEndpoints.CardPath, Report(attempt));
            var stored = Assert.Single(await Cards(s)).Token;
            var vault = s.F.Services.GetRequiredService<SavedCardVault>();
            Assert.Null(vault.Unprotect(stored[..^4] + (stored[^4..] == "AAAA" ? "BBBB" : "AAAA"), jane));                 // an edited value does not decrypt
            Assert.Null(vault.Unprotect("v1.AAAA", jane)); Assert.Null(vault.Unprotect("plain-token", jane));
            // a wrong key fails closed: a vault with another key cannot open it
            var wrongKey = new SavedCardVault(Microsoft.Extensions.Options.Options.Create(new SolaOptions { SavedCardKey = "another-saved-card-key-0123456789abcdef" }));
            Assert.Null(wrongKey.Unprotect(stored, jane));
            Assert.Equal(vault.Fingerprint(Token), vault.Fingerprint(Token)); Assert.NotEqual(vault.Fingerprint(Token), wrongKey.Fingerprint(Token));   // stable per key, different under another
            Assert.NotEqual(vault.Protect(Token, jane), vault.Protect(Token, jane));                                     // a fresh nonce every time
        }
    }

    [Fact]
    public async Task The_same_card_saved_twice_is_one_saved_card()
    {
        var (s, _, _) = await AccountCall();
        using (s)
        {
            var (first, _) = await StartSave(s);
            await Send(s, PayConnectorEndpoints.CardPath, Report(first));
            var (_, tFirst) = await s.PayAction("success"); await s.Attach(tFirst);
            await s.Tool("add_saved_card"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var second = (await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().Where(a => a.Method == "save_card" && a.Status == "pending").ToListAsync())).Single().Id.ToString("N");
            await Send(s, PayConnectorEndpoints.CardPath, Report(second));                                              // Sola returns the same token for the same card
            Assert.Single(await Cards(s));                                                                              // the keyed fingerprint stops the duplicate; the attempt is closed
            Assert.Equal("saved", (await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == Guid.ParseExact(second, "N")))).Status);
        }
    }

    // ---------- listing and removing ----------

    private static async Task Seed(VoiceScenario s, Guid account, params (string Token, string Brand, string Last4)[] cards)
    {
        foreach (var c in cards)
            await s.K.Db(async db => { db.SavedCards.Add(new SavedCard { TokenHash = Guid.NewGuid().ToString("N"), Id = Guid.NewGuid(), AccountId = account, Token = c.Token, Brand = c.Brand, Last4 = c.Last4, ExpMonth = "07", ExpYear = "31", CreatedAt = s.F.Clock.GetUtcNow().AddSeconds(c.Last4[3])  }); await db.SaveChangesAsync(); return 0; });
    }

    [Fact]
    public async Task The_saved_cards_are_read_out_with_brand_last_four_and_expiry_and_never_the_token()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, ("tok-one", "Visa", "1111"), ("tok-two", "Mastercard", "4444"));
            var r = await s.Tool("list_saved_cards");
            Assert.Equal(2, r["count"]!.GetValue<int>());
            var text = r.ToJsonString();
            Assert.Contains("Visa card ending", text); Assert.Contains("Mastercard card ending", text);
            Assert.DoesNotContain("tok-one", text); Assert.DoesNotContain("tok-two", text);
        }
    }

    [Fact]
    public async Task Removing_a_card_is_read_back_by_the_server_and_only_the_yes_removes_it_and_only_the_accounts_own()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, ("tok-one", "Visa", "1111"), ("tok-two", "Mastercard", "4444"));
            var other = Guid.NewGuid();
            await s.K.Db(async db => { db.SavedCards.Add(new SavedCard { TokenHash = Guid.NewGuid().ToString("N"), Id = Guid.NewGuid(), AccountId = other, Token = "tok-other", Brand = "Visa", Last4 = "9999", ExpMonth = "01", ExpYear = "38", CreatedAt = s.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
            Assert.Equal("no_such_position", (await s.Tool("remove_saved_card", new { position = 1 }))["error"]!.GetValue<string>());   // the list was not read out yet
            await s.Tool("list_saved_cards");
            var r = await s.Tool("remove_saved_card", new { position = 2 });
            Assert.Equal("reading_back", r["status"]!.GetValue<string>());
            await s.EndStreamAfterGoodbye();
            var said = (await s.AfterStream()).Element("Gather")!.Element("Say")!.Value;
            Assert.Contains("Mastercard card ending", said); Assert.Contains(VoiceSpeech.Digits("4444"), said);
            Assert.Equal(3, (await s.K.Db(db => db.SavedCards.CountAsync())));                                          // asking removes nothing
            var (no, tNo) = await s.Type("2"); await s.Attach(tNo);
            Assert.Equal(VoiceScripts.AccountKept, no.Element("Say")!.Value);
            Assert.Equal(3, (await s.K.Db(db => db.SavedCards.CountAsync())));
            await s.Tool("remove_saved_card", new { position = 2 }); await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var (yes, tYes) = await s.Type("1"); await s.Attach(tYes);
            Assert.Equal(VoiceScripts.AccountSaved, yes.Element("Say")!.Value);
            Assert.Equal(new[] { "tok-one", "tok-other" }, (await Cards(s)).Select(c => c.Token).OrderBy(x => x).ToArray());   // only that one; another account's card untouched
        }
    }

    [Fact]
    public async Task Even_with_a_card_id_of_another_account_the_apply_step_removes_nothing_of_theirs()
    {
        var (s, jane, _) = await AccountCall();
        using (s)
        {
            var theirs = Guid.NewGuid();
            await s.K.Db(async db => { db.SavedCards.Add(new SavedCard { TokenHash = Guid.NewGuid().ToString("N"), Id = theirs, AccountId = Guid.NewGuid(), Token = "tok-theirs", Brand = "Visa", Last4 = "9999", ExpMonth = "01", ExpYear = "38", CreatedAt = s.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
            using var scope = s.F.Services.CreateScope();
            var applied = await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().ApplyAsync(s.State(), new AccountChangeRequest("card_remove", "x", CardId: theirs), default);
            Assert.Equal(AccountApply.Refused, applied);                                                                // the tools only ever offer the account's own ids; this is the second lock
            Assert.Single(await Cards(s));
        }
    }

    // ---------- erasure ----------

    [Fact]
    public async Task Deleting_the_account_erases_its_saved_cards_at_once()
    {
        var (s, jane, session) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, ("tok-one", "Visa", "1111"));
            var r = await s.K.Send(HttpMethod.Post, "/api/v1/account/delete", new { password = AuthKit.Password }, session);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            Assert.Empty(await Cards(s));
        }
    }
}
