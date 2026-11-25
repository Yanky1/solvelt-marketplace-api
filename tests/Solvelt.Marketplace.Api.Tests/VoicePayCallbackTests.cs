using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The pay callback (decision V-60): the caller asks, the platform rings the number verified in that call, a person who is confirmed pays on the keypad, anyone else hears one neutral line.</summary>
public class VoicePayCallbackTests
{
    private const string Email = "jane@example.com", Phone = "+15551234567", Order = "MP-20261120-DDDD4444", From = "+15550001111";

    private static Dictionary<string, string?> Cfg(Dictionary<string, string?>? more = null)
    {
        var d = new Dictionary<string, string?> { ["Voice:PhonePaymentProvider"] = "fake", ["SignalWire:Space"] = "shop", ["SignalWire:ProjectId"] = "proj", ["SignalWire:ApiToken"] = "tok", ["SignalWire:FromNumber"] = From };
        if (more != null) foreach (var kv in more) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>A verified account call, with a placed order that is NOT paid yet.</summary>
    private static async Task<(VoiceScenario S, Guid Jane)> Ready(Dictionary<string, string?>? more = null, bool dial = true)
    {
        var s = new VoiceScenario(config: Cfg(more));
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        await Seed(s, jane, Order);
        if (dial)
        {
            await s.Dial();
            await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
        }
        return (s, jane);
    }

    private static async Task Seed(VoiceScenario s, Guid account, string number, string status = OrderStatus.PendingPayment, long total = 1805)
        => await s.K.Db(async db =>
        {
            var now = s.F.Clock.GetUtcNow();
            var id = Guid.NewGuid();
            var o = new MarketplaceOrder
            {
                Id = id, Number = number, AccountId = account, Status = status, Currency = "USD", ItemsCents = total, TotalCents = total, PaymentIntentId = "pi_" + number, BuyerName = "Jane", RelayEmail = "relay-x@relay.test",
                ShipName = "Jane Brown", ShipLine1 = "10 Park Ave", ShipCity = "Brooklyn", ShipRegion = "NY", ShipPostalCode = "11201", ShipCountry = "US", CreatedAt = now.AddHours(-1), UpdatedAt = now, PaidAt = status == OrderStatus.Paid ? now : null,
            };
            var g = new OrderGroup { Id = Guid.NewGuid(), OrderId = id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), SellerName = "Mazel Bakery", BrandName = "B", DeliveryKind = "carrier", ShippingSpeed = "standard", ItemsCents = total, TotalCents = total, SellerOrderId = Guid.NewGuid(), SellerOrderNumber = "SO-" + number };
            o.Groups.Add(g);
            o.Lines.Add(new OrderLine { Id = Guid.NewGuid(), OrderId = id, GroupId = g.Id, MarketplaceLineId = number + "-1", ListingId = Guid.NewGuid(), VariantId = Guid.NewGuid(), SellerId = g.SellerId, BrandId = g.BrandId, Title = "Classic Braided Challah", VariantName = "Small", Quantity = 2, UnitPriceCents = 650, LineTotalCents = 1300 });
            db.Orders.Add(o);
            await db.SaveChangesAsync();
            return 0;
        });

    private static Task<List<VoicePayCallback>> Rows(VoiceScenario s) => s.K.Db(db => db.VoicePayCallbacks.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync());
    private static FakeOutboundCaller Dialer(VoiceScenario s) => s.F.Services.GetRequiredService<FakeOutboundCaller>();

    private static async Task<int> Dispatch(VoiceScenario s, int afterSeconds = 60)
    {
        s.F.Clock.Advance(TimeSpan.FromSeconds(afterSeconds));
        using var scope = s.F.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PayCallbackService>().DispatchDueAsync(CancellationToken.None);
    }

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;

    private static async Task<(HttpResponseMessage R, string Body)> Hook(VoiceScenario s, string path, string token, string sid, Dictionary<string, string>? extra = null, string? signature = null)
    {
        var form = new Dictionary<string, string> { ["Direction"] = "outbound-api", ["To"] = Phone };
        if (extra != null) foreach (var kv in extra) form[kv.Key] = kv.Value;
        var r = await VoiceFactory.Hook(s.C, path + "?t=" + token, sid, From, signature, form);
        return (r, await r.Content.ReadAsStringAsync());
    }

    private static string Said(string xml) => string.Concat(XDocument.Parse(xml).Descendants("Say").Select(x => x.Value + " "));

    /// <summary>Asks for the callback, lets time pass and returns the call's sid and token as SignalWire would have them.</summary>
    private static async Task<(string Sid, string Token)> Dialed(VoiceScenario s)
    {
        var tool = await s.Tool("schedule_pay_callback", new { order_number = Order });
        Assert.True(tool["status"]?.GetValue<string>() == "scheduled", tool.ToJsonString());
        Assert.Equal(1, await Dispatch(s));
        var row = (await Rows(s)).Single();
        return (row.CallSid!, TokenOf(Dialer(s).Requests.Single().AnswerUrl));
    }

    // ---------- the tool ----------

    [Fact]
    public async Task Asking_for_a_callback_schedules_one_call_to_the_number_verified_in_this_call_and_changes_nothing_else()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var def = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Single(t => t!["name"]!.GetValue<string>() == "schedule_pay_callback")!;
            Assert.DoesNotContain("number", def["parameters"]!["properties"]!.AsObject().Select(p => p.Key).Where(k => k != "order_number"));   // the model cannot name a number
            var r = await s.Tool("schedule_pay_callback", new { order_number = Order });
            Assert.Equal("scheduled", r["status"]!.GetValue<string>());
            var row = Assert.Single(await Rows(s));
            Assert.Equal((Order, Phone, PayCallbackStatus.Scheduled), (row.OrderNumber, row.Phone, row.Status));
            Assert.Equal(TimeSpan.FromMinutes(30), row.ExpiresAt - row.CreatedAt);
            Assert.Equal(TimeSpan.FromSeconds(45), row.DueAt - row.CreatedAt);
            Assert.NotNull(row.AccountId);
            Assert.Equal(OrderStatus.PendingPayment, (await s.K.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Number == Order))).Status);   // nothing about the order changed
            Assert.Empty(Dialer(s).Requests);                                                                          // and nobody was rung yet: the caller's own call ends first
            Assert.Equal(new[] { "asked_by_caller" }, (await s.K.Db(db => db.VoicePayCallbackEvents.AsNoTracking().ToListAsync())).Select(e => e.Detail));
        }
    }

    [Fact]
    public async Task A_callback_is_refused_for_an_unverified_caller_another_buyers_order_a_paid_order_a_second_time_and_too_many_a_day()
    {
        var (s, jane) = await Ready(new() { ["Voice:PayCallback:MaxPerNumberPerDay"] = "2" }, dial: false);
        using (s)
        {
            await s.K.VerifiedSession("other@example.com", name: "Other Person");
            var other = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "other@example.com"))).Id;
            await Seed(s, other, "MP-20261120-OTHER001");
            await Seed(s, jane, "MP-20261120-PAID0001", OrderStatus.Paid);
            await Seed(s, jane, "MP-20261120-TWO00002"); await Seed(s, jane, "MP-20261120-TRE00003");
            await s.Dial();
            Assert.Equal("need_verification", (await s.Tool("schedule_pay_callback", new { order_number = Order }))["error"]!.GetValue<string>());
            await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
            Assert.Equal("not_found", (await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-OTHER001" }))["error"]!.GetValue<string>());   // not theirs: as if it did not exist
            Assert.Equal("not_found", (await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-NOPE0000" }))["error"]!.GetValue<string>());
            Assert.Equal("not_payable", (await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-PAID0001" }))["error"]!.GetValue<string>());
            Assert.Equal("which_order", (await s.Tool("schedule_pay_callback", new { order_number = "MP 2026; DROP" }))["error"]!.GetValue<string>());
            Assert.Equal("scheduled", (await s.Tool("schedule_pay_callback", new { order_number = Order }))["status"]!.GetValue<string>());
            Assert.Equal("already_scheduled", (await s.Tool("schedule_pay_callback", new { order_number = Order }))["error"]!.GetValue<string>());   // one active callback per order
            Assert.Equal("scheduled", (await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-TWO00002" }))["status"]!.GetValue<string>());
            Assert.Equal("too_many", (await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-TRE00003" }))["error"]!.GetValue<string>());   // two a day for this number
            Assert.Equal(2, (await Rows(s)).Count);
        }
    }

    [Fact]
    public async Task With_no_order_number_the_newest_unpaid_order_of_the_caller_is_meant_and_a_guest_gets_only_an_order_they_tried_to_pay_for_by_phone_from_this_number()
    {
        var (s, jane) = await Ready(dial: false);
        using (s)
        {
            await Seed(s, jane, "MP-20261120-NEWER001");
            var later = s.F.Clock.GetUtcNow().AddMinutes(30);
            await s.K.Db(async db => { await db.Orders.Where(o => o.Number == "MP-20261120-NEWER001").ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAt, later)); return 0; });
            await s.Dial();
            await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
            Assert.Equal("scheduled", (await s.Tool("schedule_pay_callback"))["status"]!.GetValue<string>());
            Assert.Equal("MP-20261120-NEWER001", (await Rows(s)).Single().OrderNumber);
        }
        // a caller without an account: only an order whose payment was tried by phone from the number they verified
        var g = new VoiceScenario(config: Cfg());
        using (g)
        {
            await g.SyncCatalog(); g.PortalShipping();
            await Seed(g, Guid.Empty, "MP-20261120-GUEST001");
            await g.K.Db(async db => { await db.Orders.Where(o => o.Number == "MP-20261120-GUEST001").ExecuteUpdateAsync(u => u.SetProperty(o => o.AccountId, (Guid?)null)); return 0; });
            await g.Dial();
            await g.Tool("identify_caller"); await g.EndStreamAfterGoodbye(); await g.AfterStream();
            var (_, gtoken) = await g.Type(g.LastCode()); await g.Attach(gtoken);
            Assert.Equal("not_found", (await g.Tool("schedule_pay_callback"))["error"]!.GetValue<string>());                     // nobody tried to pay for it from this number
            Assert.Equal("not_found", (await g.Tool("schedule_pay_callback", new { order_number = "MP-20261120-GUEST001" }))["error"]!.GetValue<string>());
            var call = await g.K.Db(db => db.VoiceCalls.AsNoTracking().SingleAsync());
            await g.K.Db(async db => { db.VoicePaymentAttempts.Add(new VoicePaymentAttempt { Id = Guid.NewGuid(), CallId = call.Id, OrderNumber = "MP-20261120-GUEST001", Method = "keypad", AmountCents = 1805, CreatedAt = g.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
            Assert.Equal("scheduled", (await g.Tool("schedule_pay_callback"))["status"]!.GetValue<string>());
            var row = (await Rows(g)).Single();
            Assert.Equal(("MP-20261120-GUEST001", null), (row.OrderNumber, row.AccountId));
        }
    }

    [Fact]
    public async Task Without_the_call_settings_or_a_phone_payment_provider_the_callback_is_unavailable_and_nothing_is_stored()
    {
        foreach (var more in new Dictionary<string, string?>[] { new() { ["Voice:PhonePaymentProvider"] = "none" }, new() { ["Voice:PayCallback:Enabled"] = "false" }, new() { ["SignalWire:FromNumber"] = "" } })
        {
            var (s, _) = await Ready(more);
            using (s)
            {
                Assert.Equal("callback_unavailable", (await s.Tool("schedule_pay_callback", new { order_number = Order }))["error"]!.GetValue<string>());
                Assert.Empty(await Rows(s));
            }
        }
    }

    [Fact]
    public async Task The_database_itself_allows_only_one_active_callback_per_order_but_a_finished_one_does_not_block_a_new_one()
    {
        var (s, _) = await Ready(dial: false);
        using (s)
        {
            VoicePayCallback New(string status) => new() { Id = Guid.NewGuid(), OrderNumber = Order, Phone = Phone, RequestedByCallId = Guid.NewGuid(), CreatedAt = s.F.Clock.GetUtcNow(), DueAt = s.F.Clock.GetUtcNow(), ExpiresAt = s.F.Clock.GetUtcNow().AddMinutes(30), Status = status };
            await s.K.Db(async db => { db.VoicePayCallbacks.Add(New("no_answer")); db.VoicePayCallbacks.Add(New("paid")); db.VoicePayCallbacks.Add(New(PayCallbackStatus.Scheduled)); await db.SaveChangesAsync(); return 0; });
            await Assert.ThrowsAsync<DbUpdateException>(() => s.K.Db(async db => { db.VoicePayCallbacks.Add(New(PayCallbackStatus.Dialing)); await db.SaveChangesAsync(); return 0; }));
        }
    }

    // ---------- placing the call ----------

    [Fact]
    public async Task The_call_is_placed_once_after_the_delay_to_the_verified_number_from_the_shops_number_with_signed_urls()
    {
        var (s, _) = await Ready();
        using (s)
        {
            await s.Tool("schedule_pay_callback", new { order_number = Order });
            Assert.Equal(0, await Dispatch(s, 10));                                                                    // not yet due
            Assert.Equal(1, await Dispatch(s, 60));
            Assert.Equal(0, await Dispatch(s, 5));                                                                     // never twice
            var req = Dialer(s).Requests.Single();
            Assert.Equal((Phone, From, 30), (req.ToE164, req.FromE164, req.RingSeconds));
            Assert.StartsWith(VoiceFactory.BaseUrl + PayCallbackService.AnswerPath + "?t=", req.AnswerUrl);
            Assert.StartsWith(VoiceFactory.BaseUrl + PayCallbackService.StatusPath + "?t=", req.StatusUrl);
            var tokens = s.F.Services.GetRequiredService<PayCallbackTokens>();
            var row = (await Rows(s)).Single();
            Assert.Equal(row.Id, tokens.Validate(TokenOf(req.AnswerUrl), s.F.Clock.GetUtcNow()));
            Assert.Equal((PayCallbackStatus.Dialing, true), (row.Status, row.CallSid is { Length: > 0 }));
            Assert.DoesNotContain(Order, req.AnswerUrl + req.StatusUrl);                                               // nothing about the order is in the URLs
        }
    }

    [Fact]
    public async Task A_provider_that_refuses_marks_the_callback_dial_failed_without_keeping_its_words_and_a_late_one_is_never_placed()
    {
        var (s, _) = await Ready();
        using (s)
        {
            Dialer(s).Answer = _ => new(false, null, 400, "21211");
            await s.Tool("schedule_pay_callback", new { order_number = Order });
            Assert.Equal(0, await Dispatch(s));
            var row = (await Rows(s)).Single();
            Assert.Equal("dial_failed", row.Status);
            Assert.Contains(await s.K.Db(db => db.VoicePayCallbackEvents.AsNoTracking().ToListAsync()), e => e.Detail == "provider_refused_400");
            Assert.NotNull(row.EndedAt);
            // a second order whose time ran out before the dispatcher came: expired, never dialled
            await Seed(s, (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id, "MP-20261120-LATE0001");
            Dialer(s).Answer = _ => new(true, "CA1", 201, null);
            await s.Tool("schedule_pay_callback", new { order_number = "MP-20261120-LATE0001" });
            Assert.Equal(0, await Dispatch(s, 31 * 60));
            Assert.Equal("expired", (await Rows(s)).Single(r => r.OrderNumber == "MP-20261120-LATE0001").Status);
            Assert.Single(Dialer(s).Requests);
        }
    }

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return f(request, body);
        }
    }

    [Fact]
    public async Task The_signalwire_request_is_the_documented_one_and_only_the_status_and_the_code_of_an_error_are_logged()
    {
        var ok = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"sid":"CAabc123","status":"queued","direction":"outbound-api"}""") });
        var opts = Options.Create(new SignalWireOptions { Space = "shop", ProjectId = "proj", ApiToken = "tok", FromNumber = From });
        var result = await new SignalWireOutboundCaller(new HttpClient(ok), opts, NullLogger<SignalWireOutboundCaller>.Instance).PlaceAsync(new(Phone, From, "https://x.test/a?t=1", "https://x.test/s?t=1", 30), CancellationToken.None);
        Assert.Equal((true, "CAabc123"), (result.Ok, result.CallSid));
        var (req, body) = Assert.Single(ok.Seen);
        Assert.Equal("https://shop.signalwire.com/api/laml/2010-04-01/Accounts/proj/Calls", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("proj:tok"))), req.Headers.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", req.Content!.Headers.ContentType!.MediaType);
        var form = System.Web.HttpUtility.ParseQueryString(body);
        Assert.Equal(new Dictionary<string, string> { ["To"] = Phone, ["From"] = From, ["Url"] = "https://x.test/a?t=1", ["Method"] = "POST", ["StatusCallback"] = "https://x.test/s?t=1", ["StatusCallbackMethod"] = "POST", ["Timeout"] = "30", ["MachineDetection"] = "Enable", ["Record"] = "false" },
            form.AllKeys.ToDictionary(k => k!, k => form[k]!));
        Assert.Null(form["StatusCallbackEvent"]);                                                                      // not sent: how several values are written is not on the page, and the default is the wanted one

        var log = new ListLogger();
        var bad = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"code":"21211","message":"The 'To' number +15551234567 is not valid","more_info":"https://x"}""") });
        var failed = await new SignalWireOutboundCaller(new HttpClient(bad), opts, new ListLoggerOf<SignalWireOutboundCaller>(log)).PlaceAsync(new(Phone, From, "u", "s", 30), CancellationToken.None);
        Assert.Equal((false, 400, "21211"), (failed.Ok, failed.HttpStatus, failed.Code));
        var lines = string.Join("\n", log.Lines);
        Assert.Contains("400", lines); Assert.Contains("21211", lines);
        Assert.DoesNotContain("5551234567", lines); Assert.DoesNotContain("not valid", lines);
        var notJson = await new SignalWireOutboundCaller(new HttpClient(new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") })), opts, NullLogger<SignalWireOutboundCaller>.Instance).PlaceAsync(new(Phone, From, "u", "s", 30), CancellationToken.None);
        Assert.False(notJson.Ok);                                                                                      // an answer without a call id is not a placed call
        Assert.False((await new SignalWireOutboundCaller(new HttpClient(ok), Options.Create(new SignalWireOptions()), NullLogger<SignalWireOutboundCaller>.Instance).PlaceAsync(new(Phone, From, "u", "s", 30), CancellationToken.None)).Ok);
    }

    private sealed class ListLoggerOf<T>(ListLogger inner) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    // ---------- the webhooks ----------

    [Fact]
    public async Task Whoever_answers_hears_only_who_is_calling_and_a_question_with_no_order_detail_and_the_callback_becomes_answered_once()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            var (r, xml) = await Hook(s, PayCallbackService.AnswerPath, token, sid);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var gather = XDocument.Parse(xml).Descendants("Gather").Single();
            Assert.Equal(VoiceFactory.BaseUrl + PayCallbackService.ConfirmPath + "?t=" + Uri.EscapeDataString(token), gather.Attribute("action")!.Value);
            Assert.Equal(VoiceScripts.CallbackAsk, Said(xml).Trim());
            foreach (var secret in new[] { Order, "1805", "eighteen", "dollar", "Challah", "Jane", "Brown", "Park", "Mazel" }) Assert.DoesNotContain(secret, Said(xml), StringComparison.OrdinalIgnoreCase);   // what is SAID holds nothing of the order
            var row = (await Rows(s)).Single();
            Assert.Equal((PayCallbackStatus.Answered, sid), (row.Status, row.CallSid));
            Assert.NotNull(row.CallId);
            var again = await Hook(s, PayCallbackService.AnswerPath, token, sid);                                       // a repeat of the fetch starts nothing twice
            Assert.Equal(VoiceScripts.CallbackNeutral, Said(again.Body).Trim());
            Assert.Single(await s.K.Db(db => db.VoiceCalls.AsNoTracking().Where(c => c.CallSid == sid).ToListAsync()));
        }
    }

    [Fact]
    public async Task A_machine_a_wrong_number_a_wrong_direction_a_stranger_call_and_an_expired_callback_hear_only_the_neutral_line()
    {
        foreach (var (extra, expect) in new (Dictionary<string, string>, string)[]
        {
            (new() { ["AnsweredBy"] = "machine" }, "machine"),
            (new() { ["To"] = "+15559990000" }, "wrong_number"),
        })
        {
            var (s, _) = await Ready();
            using (s)
            {
                var (sid, token) = await Dialed(s);
                var (_, xml) = await Hook(s, PayCallbackService.AnswerPath, token, sid, extra);
                Assert.Equal(VoiceScripts.CallbackNeutral, Said(xml).Trim());
                Assert.Empty(XDocument.Parse(xml).Descendants("Gather"));
                Assert.DoesNotContain(Order, xml);
                Assert.Equal(expect, (await Rows(s)).Single().Status);
            }
        }
        var (s2, _) = await Ready();
        using (s2)
        {
            var (sid, token) = await Dialed(s2);
            Assert.Equal(VoiceScripts.CallbackNeutral, Said((await Hook(s2, PayCallbackService.AnswerPath, token, sid, new() { ["Direction"] = "inbound" })).Body).Trim());   // only a call launched through the API
            Assert.Equal(VoiceScripts.CallbackNeutral, Said((await Hook(s2, PayCallbackService.AnswerPath, token, "CAsomeoneelse")).Body).Trim()); // not the call that was placed
            Assert.Equal(PayCallbackStatus.Dialing, (await Rows(s2)).Single().Status);                                  // neither of them used the one-time start up
            s2.F.Clock.Advance(TimeSpan.FromMinutes(31));
            Assert.Equal(VoiceScripts.CallbackNeutral, Said((await Hook(s2, PayCallbackService.AnswerPath, token, sid)).Body).Trim());   // answered after the 30 minutes: nothing starts
            Assert.Equal("expired", (await Rows(s2)).Single().Status);
            s2.F.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s2, PayCallbackService.AnswerPath, token, sid)).R.StatusCode);   // and past the grace the token itself is dead
        }
    }

    [Fact]
    public async Task A_request_without_signalwires_signature_or_with_a_forged_or_borrowed_token_is_refused_and_changes_nothing()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.AnswerPath, token, sid, signature: "bad")).R.StatusCode);
            var forged = token[..^4] + (token.EndsWith("0000") ? "1111" : "0000");
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.AnswerPath, forged, sid)).R.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.AnswerPath, "garbage", sid)).R.StatusCode);
            var stream = s.F.Services.GetRequiredService<StreamTokens>().Issue(Guid.NewGuid());                         // a stream token is not a callback token
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.AnswerPath, stream, sid)).R.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.ConfirmPath, forged, sid)).R.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s, PayCallbackService.StatusPath, forged, sid, new() { ["CallStatus"] = "completed" })).R.StatusCode);
            Assert.Equal(PayCallbackStatus.Dialing, (await Rows(s)).Single().Status);
            Assert.Empty(await s.K.Db(db => db.VoiceCalls.AsNoTracking().Where(c => c.CallSid == sid).ToListAsync()));
        }
    }

    [Fact]
    public async Task Only_a_clear_yes_goes_on_to_the_payment_step_through_the_same_provider_and_the_keypad_pay_ends_the_call_and_marks_the_order_paid()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            await Hook(s, PayCallbackService.AnswerPath, token, sid);
            var unclear = await Hook(s, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "um what" });   // one more try
            Assert.Equal(VoiceScripts.CallbackRetry, Said(unclear.Body).Trim());
            var (_, yes) = await Hook(s, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" });
            Assert.Contains("eighteen", Said(yes));                                                                      // the amount is spoken only now that a person said yes
            Assert.Equal("Say", XDocument.Parse(yes).Root!.Elements().First().Name.LocalName);                           // the amount is spoken, then the provider's own step
            Assert.True(XDocument.Parse(yes).Root!.Elements().Count() >= 2);
            var attempt = Assert.Single(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
            Assert.Equal((Order, 1805L, "keypad", "pending"), (attempt.OrderNumber, attempt.AmountCents, attempt.Method, attempt.Status));
            var row = (await Rows(s)).Single();
            Assert.Equal((PayCallbackStatus.Paying, attempt.Id), (row.Status, row.AttemptId));
            Assert.DoesNotMatch(@"\d{12,}", yes);                                                                        // no card data anywhere
            // the caller presses 1 in the provider's own step: the order is paid, the platform thanks and hangs up (no assistant to go back to)
            var keys = await VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, sid, Phone, null, new() { ["Digits"] = "1" });
            var done = XDocument.Parse(await keys.Content.ReadAsStringAsync());
            Assert.Equal(VoiceScripts.PayThanks, string.Concat(done.Descendants("Say").Select(x => x.Value)));
            Assert.Single(done.Descendants("Hangup"));
            Assert.Empty(done.Descendants("Connect")); Assert.Empty(done.Descendants("Redirect"));                       // no stream, no assistant: the payment was the whole call
            Assert.Equal(OrderStatus.Paid, (await s.K.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Number == Order))).Status);
            Assert.Equal("paid", (await Rows(s)).Single().Status);
            Assert.Equal("paid", (await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().SingleAsync())).Status);
        }
    }

    [Fact]
    public async Task A_no_an_unclear_answer_twice_and_an_order_that_got_paid_meanwhile_end_the_call_without_a_payment_step()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            await Hook(s, PayCallbackService.AnswerPath, token, sid);
            Assert.Equal(VoiceScripts.CallbackNotNow, Said((await Hook(s, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "no" })).Body).Trim());
            Assert.Equal("declined", (await Rows(s)).Single().Status);
            Assert.Empty(await s.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
        }
        var (s2, _) = await Ready();
        using (s2)
        {
            var (sid, token) = await Dialed(s2);
            await Hook(s2, PayCallbackService.AnswerPath, token, sid);
            await Hook(s2, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "mumble" });
            Assert.Equal(VoiceScripts.CallbackNeutral, Said((await Hook(s2, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "mumble" })).Body).Trim());
            Assert.Equal("unconfirmed", (await Rows(s2)).Single().Status);
        }
        var (s3, _) = await Ready();
        using (s3)
        {
            var (sid, token) = await Dialed(s3);
            await Hook(s3, PayCallbackService.AnswerPath, token, sid);
            await s3.K.Db(async db => { await db.Orders.Where(o => o.Number == Order).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OrderStatus.Paid)); return 0; });
            Assert.Equal(VoiceScripts.CallbackNotPayable, Said((await Hook(s3, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" })).Body).Trim());
            Assert.Equal("not_payable", (await Rows(s3)).Single().Status);
            Assert.Empty(await s3.K.Db(db => db.VoicePaymentAttempts.AsNoTracking().ToListAsync()));
        }
    }

    [Fact]
    public async Task The_end_of_the_call_settles_the_callback_by_what_happened_and_a_paid_order_always_wins()
    {
        foreach (var (callStatus, answer, expect) in new[] { ("no-answer", false, "no_answer"), ("busy", false, "busy"), ("failed", false, "failed"), ("completed", true, "unconfirmed") })
        {
            var (s, _) = await Ready();
            using (s)
            {
                var (sid, token) = await Dialed(s);
                if (answer) await Hook(s, PayCallbackService.AnswerPath, token, sid);
                var r = await Hook(s, PayCallbackService.StatusPath, token, sid, new() { ["CallStatus"] = callStatus });
                Assert.Equal(HttpStatusCode.OK, r.R.StatusCode);
                var row = (await Rows(s)).Single();
                Assert.Equal(expect, row.Status);
                Assert.NotNull(row.EndedAt);
                Assert.Equal(HttpStatusCode.OK, (await Hook(s, PayCallbackService.StatusPath, token, sid, new() { ["CallStatus"] = "completed" })).R.StatusCode);   // a repeat changes nothing
                Assert.Equal(expect, (await Rows(s)).Single().Status);
            }
        }
        var (s2, _) = await Ready();
        using (s2)
        {
            var (sid, token) = await Dialed(s2);
            await s2.K.Db(async db => { await db.Orders.Where(o => o.Number == Order).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OrderStatus.Paid)); return 0; });
            await Hook(s2, PayCallbackService.StatusPath, token, sid, new() { ["CallStatus"] = "completed" });
            Assert.Equal("paid", (await Rows(s2)).Single().Status);
        }
    }

    // ---------- the edge of the 30 minutes ----------

    [Fact]
    public async Task A_call_that_would_be_answered_too_late_to_finish_is_not_placed_and_the_callback_expires()
    {
        var (s, _) = await Ready();
        using (s)
        {
            await s.Tool("schedule_pay_callback");
            Assert.Equal(0, await Dispatch(s, 26 * 60));                                                               // 4 minutes of the 30 are left: less than the 5 it needs
            Assert.Empty(Dialer(s).Requests);
            var row = (await Rows(s)).Single();
            Assert.Equal("expired", row.Status);
            Assert.Contains(await s.K.Db(db => db.VoicePayCallbackEvents.AsNoTracking().ToListAsync()), e => e.Detail == "too_late_to_dial");
        }
        var (s2, _) = await Ready();
        using (s2)
        {
            await s2.Tool("schedule_pay_callback");
            Assert.Equal(1, await Dispatch(s2, 24 * 60));                                                              // 6 minutes left: placed
        }
    }

    [Fact]
    public async Task A_payment_that_finishes_after_the_thirty_minutes_but_inside_the_grace_is_recorded_as_paid_and_the_sweep_leaves_it_alone()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            s.F.Clock.Advance(TimeSpan.FromMinutes(24));                                                               // answered at minute 25
            await Hook(s, PayCallbackService.AnswerPath, token, sid);
            await Hook(s, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" });
            s.F.Clock.Advance(TimeSpan.FromMinutes(8));                                                                // the keypad step ends at minute 33, past the 30
            using (var scope = s.F.Services.CreateScope()) Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PayCallbackService>().ExpireOldAsync(CancellationToken.None));
            Assert.Equal(PayCallbackStatus.Paying, (await Rows(s)).Single().Status);
            var keys = await VoiceFactory.Hook(s.C, VoiceEndpoints.KeypadPath, sid, Phone, null, new() { ["Digits"] = "1" });
            Assert.Equal(VoiceScripts.PayThanks, string.Concat(XDocument.Parse(await keys.Content.ReadAsStringAsync()).Descendants("Say").Select(x => x.Value)));
            Assert.Equal("paid", (await Rows(s)).Single().Status);
            Assert.Equal(OrderStatus.Paid, (await s.K.Db(db => db.Orders.AsNoTracking().SingleAsync(o => o.Number == Order))).Status);
        }
    }

    [Fact]
    public async Task The_end_of_call_word_is_accepted_inside_the_grace_after_the_window_and_refused_past_it_and_the_sweep_then_settles_by_the_order()
    {
        var (s, _) = await Ready();
        using (s)
        {
            var (sid, token) = await Dialed(s);
            await Hook(s, PayCallbackService.AnswerPath, token, sid);
            await Hook(s, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" });
            await s.K.Db(async db => { await db.Orders.Where(o => o.Number == Order).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OrderStatus.Paid)); return 0; });   // the connector's report came in
            s.F.Clock.Advance(TimeSpan.FromMinutes(40));                                                               // past the 30, inside the 50
            Assert.Equal(HttpStatusCode.OK, (await Hook(s, PayCallbackService.StatusPath, token, sid, new() { ["CallStatus"] = "completed" })).R.StatusCode);
            Assert.Equal("paid", (await Rows(s)).Single().Status);                                                     // the order was paid: that is its final word, not "expired"
        }
        var (s2, _) = await Ready();
        using (s2)
        {
            var (sid, token) = await Dialed(s2);
            await Hook(s2, PayCallbackService.AnswerPath, token, sid);
            await Hook(s2, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" });
            s2.F.Clock.Advance(TimeSpan.FromMinutes(52));                                                              // past the grace
            Assert.Equal(HttpStatusCode.Forbidden, (await Hook(s2, PayCallbackService.StatusPath, token, sid, new() { ["CallStatus"] = "completed" })).R.StatusCode);
            using (var scope = s2.F.Services.CreateScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PayCallbackService>().ExpireOldAsync(CancellationToken.None));
            Assert.Equal("expired", (await Rows(s2)).Single().Status);                                                 // the order is not paid: expired
            await s2.K.Db(async db => { await db.Orders.Where(o => o.Number == Order).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OrderStatus.Paid)); return 0; });
        }
        var (s3, _) = await Ready();
        using (s3)
        {
            var (sid, token) = await Dialed(s3);
            await Hook(s3, PayCallbackService.AnswerPath, token, sid);
            await Hook(s3, PayCallbackService.ConfirmPath, token, sid, new() { ["SpeechResult"] = "yes" });
            await s3.K.Db(async db => { await db.Orders.Where(o => o.Number == Order).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OrderStatus.Paid)); return 0; });
            s3.F.Clock.Advance(TimeSpan.FromMinutes(52));
            using var scope = s3.F.Services.CreateScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PayCallbackService>().ExpireOldAsync(CancellationToken.None));
            Assert.Equal("paid", (await Rows(s3)).Single().Status);                                                    // the sweep too: a paid order is never recorded as expired
        }
    }

    // ---------- staff, data and retention ----------

    [Fact]
    public async Task Staff_with_voice_view_see_the_waiting_callbacks_with_the_number_masked_and_every_look_is_audited()
    {
        using var s = new StaffKit(Cfg());
        var id = Guid.NewGuid();
        await s.Db(async db =>
        {
            db.VoicePayCallbacks.Add(new VoicePayCallback { Id = id, OrderNumber = Order, Phone = Phone, RequestedByCallId = Guid.NewGuid(), CreatedAt = s.F.Clock.GetUtcNow(), DueAt = s.F.Clock.GetUtcNow(), ExpiresAt = s.F.Clock.GetUtcNow().AddMinutes(30) });
            db.VoicePayCallbackEvents.Add(new VoicePayCallbackEvent { Id = Guid.NewGuid(), CallbackId = id, At = s.F.Clock.GetUtcNow(), Status = "scheduled", Detail = "asked_by_caller" });
            await db.SaveChangesAsync();
            return 0;
        });
        var (admin, _, _) = await s.Admin();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post("/api/v1/staff/voice/pay-callbacks", new { }, admin)).StatusCode);   // a reason is needed
        var r = await s.Post("/api/v1/staff/voice/pay-callbacks", new { reason = "checking the waiting callbacks" }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        var row = JsonNode.Parse(text)!["callbacks"]![0]!;
        Assert.Equal((Order, "scheduled"), (row["orderNumber"]!.GetValue<string>(), row["status"]!.GetValue<string>()));
        Assert.DoesNotContain("5551234567", text); Assert.EndsWith("4567", row["phone"]!.GetValue<string>());
        Assert.Equal("asked_by_caller", row["events"]![0]!["detail"]!.GetValue<string>());
        var audit = (await s.Audit()).Single(a => a.Action == VoicePayCallbackStaff.ListAction);
        Assert.Equal("checking the waiting callbacks", audit.Reason);
    }

    [Fact]
    public async Task An_erased_account_takes_its_callbacks_the_export_has_them_with_the_number_masked_and_retention_deletes_only_finished_ones()
    {
        var (s, jane) = await Ready(dial: false);
        using (s)
        {
            VoicePayCallback Row(string order, string status, DateTimeOffset at, Guid? account) => new() { Id = Guid.NewGuid(), OrderNumber = order, AccountId = account, Phone = Phone, RequestedByCallId = Guid.NewGuid(), CreatedAt = at, DueAt = at, ExpiresAt = at.AddMinutes(30), Status = status };
            var now = s.F.Clock.GetUtcNow();
            var old = Row("MP-OLD00001", "no_answer", now.AddDays(-100), null);
            var oldActive = Row("MP-OLD00002", PayCallbackStatus.Dialing, now.AddDays(-100), null);
            var mine = Row("MP-MINE0001", "paid", now, jane);
            await s.K.Db(async db =>
            {
                db.VoicePayCallbacks.AddRange(old, oldActive, mine);
                db.VoicePayCallbackEvents.AddRange(new VoicePayCallbackEvent { Id = Guid.NewGuid(), CallbackId = old.Id, At = now, Status = "x", Detail = "d" }, new VoicePayCallbackEvent { Id = Guid.NewGuid(), CallbackId = mine.Id, At = now, Status = "x", Detail = "d" });
                await db.SaveChangesAsync();
                return 0;
            });
            using (var scope = s.F.Services.CreateScope())
            {
                var exported = await scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountDataExporter>().Single(e => e.Section == "pay-callbacks").ExportAsync(jane, CancellationToken.None);
                var json = System.Text.Json.JsonSerializer.Serialize(exported);
                Assert.Contains("MP-MINE0001", json); Assert.DoesNotContain("5551234567", json);
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PayCallbackService>().PurgeAsync(CancellationToken.None));   // retention: 90 days, finished only
            }
            Assert.Equal(new[] { "MP-MINE0001", "MP-OLD00002" }, (await Rows(s)).Select(r => r.OrderNumber).Order());
            using (var scope = s.F.Services.CreateScope())
                foreach (var e in scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountEraser>().OfType<VoicePayCallbackEraser>()) await e.EraseAsync(jane, CancellationToken.None);
            Assert.Equal(new[] { "MP-OLD00002" }, (await Rows(s)).Select(r => r.OrderNumber));
            Assert.Empty(await s.K.Db(db => db.VoicePayCallbackEvents.AsNoTracking().ToListAsync()));
        }
    }

    [Fact]
    public void The_settings_are_checked_and_the_tokens_are_bound_to_their_purpose_and_time()
    {
        Assert.Empty(new VoicePayCallbackOptions().Problems());
        Assert.NotEmpty(new VoicePayCallbackOptions { ExpiryMinutes = 1 }.Problems());
        Assert.NotEmpty(new VoicePayCallbackOptions { RingSeconds = 5 }.Problems());
        Assert.NotEmpty(new VoicePayCallbackOptions { MaxPerNumberPerDay = 0 }.Problems());
        Assert.NotEmpty(new VoicePayCallbackOptions { GraceMinutes = 1 }.Problems());
        Assert.NotEmpty(new VoicePayCallbackOptions { MinMinutesLeftToDial = 0 }.Problems());
        var t = new PayCallbackTokens(Options.Create(new VoiceOptions { StreamTokenKey = new string('k', 40) }));
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var token = t.Issue(id, now.AddMinutes(30));
        Assert.Equal(id, t.Validate(token, now));
        Assert.Equal(id, t.Validate(token, now.AddMinutes(30)));
        Assert.Null(t.Validate(token, now.AddMinutes(30).AddSeconds(1)));
        Assert.Null(t.Validate(null, now)); Assert.Null(t.Validate("", now)); Assert.Null(t.Validate(token + "x", now)); Assert.Null(t.Validate(new string('a', 300), now));
        Assert.Null(new PayCallbackTokens(Options.Create(new VoiceOptions { StreamTokenKey = new string('j', 40) })).Validate(token, now));   // another key
    }
}
