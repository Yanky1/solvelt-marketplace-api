using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Order status, an SMS summary and cancellation by phone (slice 5, decision V-08), through whole scripted calls.</summary>
public class VoiceOrderStatusTests
{
    private const string Email = "jane@example.com", Number = "+15551234567";

    private static async Task<(VoiceScenario S, Guid Jane)> AccountCall()
    {
        var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        return (s, jane);
    }

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    /// <summary>A placed order with one or two seller orders, at the given fulfilment state, as the portal's events would leave it.</summary>
    private static async Task Seed(VoiceScenario s, Guid account, string number, string state = "new", int sellers = 1, int daysAgo = 3, long total = 1805)
        => await s.K.Db(async db =>
        {
            var now = s.F.Clock.GetUtcNow();
            var id = Guid.NewGuid();
            var o = new MarketplaceOrder
            {
                Id = id, Number = number, AccountId = account, Status = OrderStatus.Placed, Currency = "USD", ItemsCents = total, TotalCents = total, PaymentIntentId = "pi_" + number, BuyerName = "Jane", RelayEmail = "relay-x@relay.test",
                ShipName = "Jane Brown", ShipLine1 = "10 Park Ave", ShipCity = "Brooklyn", ShipRegion = "NY", ShipPostalCode = "11201", ShipCountry = "US", CreatedAt = now.AddDays(-daysAgo), UpdatedAt = now, PaidAt = now.AddDays(-daysAgo),
            };
            for (var i = 0; i < sellers; i++)
            {
                var g = new OrderGroup
                {
                    Id = Guid.NewGuid(), OrderId = id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), SellerName = i == 0 ? "Mazel Bakery" : "Shabbat Table", BrandName = "B", DeliveryKind = "carrier", ShippingSpeed = "standard",
                    ItemsCents = total / sellers, TotalCents = total / sellers, SellerOrderId = Guid.NewGuid(), SellerOrderNumber = $"SO-{number}-{i}",
                };
                o.Groups.Add(g);
                o.Lines.Add(new OrderLine { Id = Guid.NewGuid(), OrderId = id, GroupId = g.Id, MarketplaceLineId = $"{number}-{i + 1}", ListingId = Guid.NewGuid(), VariantId = Guid.NewGuid(), SellerId = g.SellerId, BrandId = g.BrandId, Title = i == 0 ? "Classic Braided Challah" : "Kiddush Cup", VariantName = "Small", Quantity = 2 - i, UnitPriceCents = 650, LineTotalCents = 650L * (2 - i) });
                db.SellerOrderStates.Add(new SellerOrderState { GroupId = g.Id, OrderId = id, Status = state });
            }
            db.Orders.Add(o);
            await db.SaveChangesAsync();
            return 0;
        });

    private static Task<List<OrderCancelRequest>> Cancels(VoiceScenario s) => s.K.Db(db => db.OrderCancelRequests.AsNoTracking().ToListAsync());

    [Fact]
    public async Task A_verified_account_hears_its_own_orders_newest_first_and_never_another_buyers()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.K.VerifiedSession("other@example.com", name: "Other Person");
            var other = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "other@example.com"))).Id;
            await Seed(s, jane, "MP-20261110-AAAA1111", daysAgo: 7);
            await Seed(s, jane, "MP-20261115-BBBB2222", state: "shipped", daysAgo: 2);
            await Seed(s, other, "MP-20261116-CCCC3333", daysAgo: 1);
            await s.Dial();
            Assert.Equal("need_verification", (await s.Tool("list_my_orders"))["error"]!.GetValue<string>());
            await Verify(s);
            var list = await s.Tool("list_my_orders");
            Assert.Equal(2, list["found"]!.GetValue<int>());
            var orders = list["orders"]!.AsArray();
            Assert.Equal(1, orders[0]!["position"]!.GetValue<int>());
            Assert.Contains("M P", orders[0]!["order_number_spoken"]!.GetValue<string>());
            Assert.Contains("B B B B", orders[0]!["order_number_spoken"]!.GetValue<string>());
            Assert.Contains("two two two two", orders[0]!["order_number_spoken"]!.GetValue<string>());
            Assert.DoesNotContain("CCCC", list.ToJsonString());
            Assert.True(VoiceSpeech.IsSpeakable(list["orders"]![0]!["placed"]!.GetValue<string>()));
            // by position: the newest first (the shipped one)
            var first = await s.Tool("order_status", new { position = 1 });
            Assert.Contains("on its way", first["status_spoken"]!.GetValue<string>());
            Assert.Contains("From Mazel Bakery", first["status_spoken"]!.GetValue<string>());
            Assert.True(VoiceSpeech.IsSpeakable(first["status_spoken"]!.GetValue<string>()), first["status_spoken"]!.GetValue<string>());
            var second = await s.Tool("order_status", new { position = 2 });
            Assert.Contains("received by the seller", second["status_spoken"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Another_buyers_order_and_a_number_that_does_not_exist_get_the_same_answer()
    {
        var (s, _) = await AccountCall();
        using (s)
        {
            await s.K.VerifiedSession("other@example.com", name: "Other Person");
            var other = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "other@example.com"))).Id;
            await Seed(s, other, "MP-20261116-CCCC3333");
            await s.Dial(); await Verify(s);
            var theirs = await s.Tool("order_status", new { order_number = "MP-20261116-CCCC3333" });
            var none = await s.Tool("order_status", new { order_number = "MP-20261116-ZZZZ9999" });
            Assert.Equal(none.ToJsonString(), theirs.ToJsonString());
            Assert.Equal("not_found", theirs["error"]!.GetValue<string>());
            Assert.Equal("which_order", (await s.Tool("order_status", new { }))["error"]!.GetValue<string>());
            Assert.Equal("which_order", (await s.Tool("order_status", new { order_number = "MP 1; DROP" }))["error"]!.GetValue<string>());
            Assert.Equal("which_order", (await s.Tool("order_status", new { position = 4 }))["error"]!.GetValue<string>());     // nothing was listed
            Assert.Equal("not_found", (await s.Tool("cancel_order", new { order_number = "MP-20261116-CCCC3333", reason = "changed_mind" }))["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Without_the_code_caller_id_alone_gets_no_order_information_and_no_cancellation()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111");
            await s.Dial();                                                                // from Jane's own number, nothing verified
            foreach (var tool in new[] { "list_my_orders", "order_status", "text_order_summary" })
                Assert.Equal("need_verification", (await s.Tool(tool, new { order_number = "MP-20261110-AAAA1111" }))["error"]!.GetValue<string>());
            Assert.Equal("not_allowed", (await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" }))["error"]!.GetValue<string>());
            Assert.Empty(s.Sms.Sent);
            Assert.Empty(await Cancels(s));
        }
    }

    [Fact]
    public async Task A_text_summary_goes_to_the_verified_phone_with_the_number_and_status_but_no_address_or_items()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111", state: "shipped");
            await s.Dial(); await Verify(s);
            Assert.Equal("sent", (await s.Tool("text_order_summary", new { order_number = "MP-20261110-AAAA1111" }))["status"]!.GetValue<string>());
            var sms = s.Sms.Sent.Last();
            Assert.Equal(Number, sms.To);
            Assert.Contains("MP-20261110-AAAA1111", sms.Body); Assert.Contains("on its way", sms.Body); Assert.Contains("18.05 USD", sms.Body);
            Assert.DoesNotContain("Park", sms.Body); Assert.DoesNotContain("11201", sms.Body); Assert.DoesNotContain("Challah", sms.Body);
            for (var i = 0; i < 2; i++) await s.Tool("text_order_summary", new { order_number = "MP-20261110-AAAA1111" });
            Assert.Equal("too_many_texts", (await s.Tool("text_order_summary", new { order_number = "MP-20261110-AAAA1111" }))["error"]!.GetValue<string>());
            s.Sms.Fail = true;
        }
    }

    // ---------- cancellation ----------

    private static async Task<XElement> AskToCancel(VoiceScenario s, object args)
    {
        var r = await s.Tool("cancel_order", args);
        Assert.True(r["status"]?.GetValue<string>() == "reading_back", r.ToJsonString());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    [Fact]
    public async Task A_cancellation_is_read_back_by_the_server_and_only_a_yes_sends_the_request_to_the_seller()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111");
            await s.Dial(); await Verify(s);
            var gather = await AskToCancel(s, new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            var said = gather.Element("Gather")!.Element("Say")!.Value;
            Assert.Contains("cancel order", said); Assert.Contains("Mazel Bakery", said); Assert.Contains("two Classic Braided Challah", said); Assert.Contains("the seller, who decides", said);
            Assert.EndsWith("To keep the order, say no.", said);
            Assert.True(VoiceSpeech.IsSpeakable(said), said);
            Assert.Empty(await Cancels(s));                                                       // nothing was sent by asking
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains("the seller, who decides"));    // the model never wrote or heard the read-back

            var (resume, token) = await s.Type("1");
            Assert.Equal(VoiceScripts.CancelSent, resume.Element("Say")!.Value);
            await s.Attach(token);
            var req = Assert.Single(await Cancels(s));
            Assert.Equal(("changed_mind", CancelRequestStatus.Pending, jane), (req.Reason, req.Status, req.AccountId));
            Assert.Contains("cancellation request", (await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains("cancellation request and it was sent")))["item"]!["content"]![0]!["text"]!.GetValue<string>());
            // asking again is the same open request
            Assert.Equal("already_requested", (await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" }))["error"]!.GetValue<string>());
            Assert.Single(await Cancels(s));
            Assert.Contains("a cancellation request is", (await s.Tool("order_status", new { order_number = "MP-20261110-AAAA1111" }))["status_spoken"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("2", null)]
    [InlineData("", "no")]
    [InlineData("", "yes wait no")]
    public async Task A_no_leaves_the_order_alone(string digits, string? speech)
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111");
            await s.Dial(); await Verify(s);
            await AskToCancel(s, new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            var (resume, _) = await s.Type(digits, speech);
            Assert.Equal(VoiceScripts.CancelKept, resume.Element("Say")!.Value);
            Assert.Empty(await Cancels(s));
        }
    }

    [Fact]
    public async Task An_unclear_answer_is_asked_again_once_and_then_nothing_is_cancelled()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111");
            await s.Dial(); await Verify(s);
            await AskToCancel(s, new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            Assert.Equal(VoiceScripts.CancelRetry, (await s.Type("", "mumble")).Xml.Element("Gather")!.Element("Say")!.Value);
            var (second, _) = await s.Type("", "mumble");
            Assert.Equal(VoiceScripts.CancelKept, second.Element("Say")!.Value);
            Assert.Empty(await Cancels(s));
        }
    }

    [Theory]
    [InlineData("shipped")]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    public async Task An_order_that_can_no_longer_be_cancelled_is_refused_before_any_read_back(string state)
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111", state: state);
            await s.Dial(); await Verify(s);
            var r = await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            Assert.Equal("not_cancellable", r["error"]!.GetValue<string>());
            Assert.Null(s.State().KeypadStep);
            Assert.Empty(await Cancels(s));
        }
    }

    [Fact]
    public async Task The_existing_cancel_rules_still_decide_after_the_yes_an_order_that_shipped_meanwhile_is_refused()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111");
            await s.Dial(); await Verify(s);
            await AskToCancel(s, new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            await s.K.Db(async db => await db.SellerOrderStates.ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, "shipped")));      // the seller shipped while the caller thought about it
            var (resume, _) = await s.Type("1");
            Assert.Equal(VoiceScripts.CancelRefused, resume.Element("Say")!.Value);
            Assert.Empty(await Cancels(s));
        }
    }

    [Fact]
    public async Task A_reason_must_be_one_of_the_known_ones_and_several_sellers_need_one_named()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111", sellers: 2);
            await s.Dial(); await Verify(s);
            Assert.Equal("bad_reason", (await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111", reason = "because" }))["error"]!.GetValue<string>());
            Assert.Equal("bad_reason", (await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111" }))["error"]!.GetValue<string>());
            var which = await s.Tool("cancel_order", new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind" });
            Assert.Equal("which_seller", which["error"]!.GetValue<string>());
            Assert.Equal(2, which["sellers"]!.AsArray().Count);
            var said = (await AskToCancel(s, new { order_number = "MP-20261110-AAAA1111", reason = "changed_mind", seller = "Shabbat Table" })).Element("Gather")!.Element("Say")!.Value;
            Assert.Contains("Shabbat Table", said); Assert.DoesNotContain("Mazel", said);
            await s.Type("1");
            var req = Assert.Single(await Cancels(s));
            Assert.Equal("SO-MP-20261110-AAAA1111-1", await s.K.Db(db => db.OrderGroups.AsNoTracking().Where(g => g.Id == req.GroupId).Select(g => g.SellerOrderNumber!).SingleAsync()));
        }
    }

    [Fact]
    public async Task A_guest_cannot_cancel_by_phone_and_an_anonymous_caller_is_sent_to_verify()
    {
        using var s = new VoiceScenario(config: null);
        await s.SyncCatalog(); await s.Dial();
        Assert.Equal("not_allowed", (await s.Tool("cancel_order", new { order_number = "MP-1", reason = "changed_mind" }))["error"]!.GetValue<string>());
        await Verify(s);                                                                    // a guest: a number nobody proved belongs to an account
        Assert.Equal(CallerLevel.Guest, s.State().Level);
        var r = await s.Tool("cancel_order", new { order_number = "MP-1", reason = "changed_mind" });
        Assert.Equal("not_allowed", r["error"]!.GetValue<string>());
        Assert.Contains("link in their order email", r["instruction"]!.GetValue<string>());
        Assert.Equal("not_found", (await s.Tool("order_status", new { order_number = "MP-1" }))["error"]!.GetValue<string>());
    }

    // ---------- return (refund) request (decision V-55) ----------

    private static Task<List<OrderReturn>> Returns(VoiceScenario s) => s.K.Db(db => db.OrderReturns.AsNoTracking().Include(r => r.Lines).ToListAsync());

    /// <summary>The seeded order has shipped all its units (what the portal's events leave for a delivered order).</summary>
    private static async Task Shipped(VoiceScenario s, string number)
        => await s.K.Db(async db =>
        {
            var orderId = await db.Orders.AsNoTracking().Where(o => o.Number == number).Select(o => o.Id).SingleAsync();
            foreach (var l in await db.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync())
                db.OrderLineFulfilments.Add(new OrderLineFulfilment { LineId = l.Id, ShippedQty = l.Quantity });
            await db.SaveChangesAsync();
            return 0;
        });

    private static async Task<XElement> AskToReturn(VoiceScenario s, object args)
    {
        var r = await s.Tool("request_refund", args);
        Assert.True(r["status"]?.GetValue<string>() == "reading_back", r.ToJsonString());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    [Fact]
    public async Task A_return_request_is_read_back_by_the_server_and_only_a_yes_sends_it_through_the_websites_own_path()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111", state: "delivered");
            await Shipped(s, "MP-20261110-AAAA1111");
            await s.Dial(); await Verify(s);
            var gather = await AskToReturn(s, new { order_number = "MP-20261110-AAAA1111", reason = "damaged" });
            var said = gather.Element("Gather")!.Element("Say")!.Value;
            Assert.Contains("return items from order", said); Assert.Contains("Mazel Bakery", said); Assert.Contains("the seller, who decides", said);
            Assert.EndsWith("To leave it, say no.", said);
            Assert.True(VoiceSpeech.IsSpeakable(said), said);
            Assert.Empty(await Returns(s));                                                          // asking sends nothing
            Assert.DoesNotContain(s.Fake.Received, m => m.Contains("the seller, who decides"));      // the model never wrote or heard the read-back
            var (resume, token) = await s.Type("1");
            Assert.Equal(VoiceScripts.ReturnSent, resume.Element("Say")!.Value);
            await s.Attach(token);
            var ret = Assert.Single(await Returns(s));
            Assert.Equal(("damaged", ReturnStatus.Pending, jane), (ret.Reason, ret.Status, ret.AccountId));
            Assert.NotEmpty(ret.Lines);
            Assert.StartsWith("phone-", ret.ClientKey);                                              // one request per call and order
            // THE INTENT (V-55): the model cannot move money. After a yes only a RETURN REQUEST exists: no refund row, no refund counted on the order, no call to the card processor, no refund job; the portal gets a return request and nothing else
            Assert.Empty(await s.K.Db(db => db.OrderRefunds.AsNoTracking().ToListAsync()));
            Assert.Equal(0, await s.K.Db(db => db.Orders.AsNoTracking().Where(o => o.Number == "MP-20261110-AAAA1111").Select(o => o.RefundedCents).SingleAsync()));
            Assert.Empty(s.F.Services.GetRequiredService<Solvelt.Marketplace.Api.Payments.FakePaymentProvider>().Refunds);
            var jobs = await s.K.Db(db => db.OutboxMessages.AsNoTracking().Select(m => m.Type).ToListAsync());
            Assert.DoesNotContain(jobs, t => t.Contains("refund", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Solvelt.Marketplace.Api.Fulfilment.BuyerOrderActions.ReturnType, jobs);
            Assert.Empty(s.F.Services.GetRequiredService<Solvelt.Marketplace.Api.Payments.FakeSolaRefundGateway>().Calls);
            Assert.Contains("return request", (await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains("return request"))).ToJsonString());
        }
    }

    [Fact]
    public async Task A_no_leaves_the_return_unsent_and_an_order_that_has_not_shipped_is_pointed_to_cancellation()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await Seed(s, jane, "MP-20261110-AAAA1111", state: "delivered");
            await Shipped(s, "MP-20261110-AAAA1111");
            await Seed(s, jane, "MP-20261111-BBBB2222", state: "new");
            await s.Dial(); await Verify(s);
            await AskToReturn(s, new { order_number = "MP-20261110-AAAA1111", reason = "no_longer_needed" });
            var (resume, back) = await s.Type("2");
            Assert.Equal(VoiceScripts.ReturnKept, resume.Element("Say")!.Value);
            await s.Attach(back);
            Assert.Empty(await Returns(s));
            // an order that has not shipped cannot be returned: the model is told to offer cancel_order
            var notYet = await s.Tool("request_refund", new { order_number = "MP-20261111-BBBB2222", reason = "damaged" });
            Assert.Equal("not_returnable", notYet["error"]!.GetValue<string>());
            Assert.Contains("cancel_order", notYet["instruction"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_return_needs_a_verified_account_a_known_reason_and_the_callers_own_order()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.Dial();
            Assert.Equal("not_allowed", (await s.Tool("request_refund", new { order_number = "MP-1", reason = "damaged" }))["error"]!.GetValue<string>());      // not verified yet
            await Verify(s);
            Assert.Equal("bad_reason", (await s.Tool("request_refund", new { order_number = "MP-20261110-AAAA1111", reason = "because" }))["error"]!.GetValue<string>());
            Assert.Equal("bad_reason", (await s.Tool("request_refund", new { order_number = "MP-20261110-AAAA1111" }))["error"]!.GetValue<string>());
            Assert.Equal("not_found", (await s.Tool("request_refund", new { order_number = "MP-20261116-CCCC3333", reason = "damaged" }))["error"]!.GetValue<string>());   // nobody's order: the same answer as for a stranger's
            Assert.Empty(await Returns(s));
        }
    }

    // ---------- a guest who ordered by phone ----------

    private static async Task GuestOrder(VoiceScenario s)
    {
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" }); await s.Tool("confirm_guest_email");
        await s.Tool("set_delivery_address", new { house_number = "1", street = "Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }); await s.Tool("confirm_address");
        await s.Tool("read_back_order"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type("1"); await s.Attach(token);
        await s.Tool("pay_for_order"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token2) = await s.Type("1"); await s.Attach(token2);
        Assert.NotNull(s.State().OrderNumber);
    }

    [Fact]
    public async Task A_guest_who_ordered_by_phone_can_ask_about_it_in_a_later_call_but_another_number_cannot()
    {
        using var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        await s.SyncCatalog(); s.PortalShipping();
        await GuestOrder(s);
        var number = s.State().OrderNumber!;
        await s.Phone!.Send(new { @event = "stop" }); await s.Phone.WaitClosed();

        s.Sid = "CA-later";                                               // the same number calls again
        await s.Dial(); await Verify(s);
        Assert.Equal(CallerLevel.Guest, s.State().Level);
        var list = await s.Tool("list_my_orders");
        Assert.Equal(1, list["found"]!.GetValue<int>());
        var status = await s.Tool("order_status", new { position = 1 });
        Assert.Contains("being processed", status["status_spoken"]!.GetValue<string>());
        Assert.Equal("sent", (await s.Tool("text_order_summary", new { position = 1 }))["status"]!.GetValue<string>());
        Assert.Contains(number, s.Sms.Sent.Last().Body);
        Assert.DoesNotContain("Main St", s.Sms.Sent.Last().Body);
        await s.Phone!.Send(new { @event = "stop" }); await s.Phone.WaitClosed();

        // someone else calls: their own verified number is not this order's number
        s.Sid = "CA-other";
        await s.K.Db(async db => await db.VoiceCalls.ExecuteUpdateAsync(c => c.SetProperty(x => x.VerifiedKey, "someone-else")));   // as if the order had been placed from another verified number
        await s.Dial(); await Verify(s);
        Assert.Equal(0, (await s.Tool("list_my_orders"))["found"]!.GetValue<int>());
        Assert.Equal("not_found", (await s.Tool("order_status", new { order_number = number }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_claimed_guest_order_shows_only_its_number_and_status_through_the_phone_too()
    {
        using var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        await s.SyncCatalog(); s.PortalShipping();
        await GuestOrder(s);
        var number = s.State().OrderNumber!;
        var acct = await s.K.VerifiedSession("claimer@example.com", name: "Claimer");
        var id = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "claimer@example.com"))).Id;
        await s.K.Db(async db => await db.Orders.Where(o => o.Number == number).ExecuteUpdateAsync(x => x.SetProperty(o => o.AccountId, id).SetProperty(o => o.ClaimedAt, s.F.Clock.GetUtcNow())));
        await s.Phone!.Send(new { @event = "stop" }); await s.Phone.WaitClosed();
        s.Sid = "CA-later"; await s.Dial(); await Verify(s);
        var status = await s.Tool("order_status", new { order_number = number });
        var said = status["status_spoken"]!.GetValue<string>();
        Assert.Contains("More detail is in the account it belongs to", said);
        Assert.DoesNotContain("Mazel", said);
        Assert.NotNull(acct);
    }

    [Fact]
    public void Order_statuses_are_said_in_plain_words()
    {
        Assert.Equal("on its way", OrderSpeech.StatusWords("shipped"));
        Assert.Equal("being processed", OrderSpeech.StatusWords("processing"));
        Assert.Equal("being processed", OrderSpeech.StatusWords("something new"));
        Assert.Equal("partly shipped", OrderSpeech.StatusWords("partially_shipped"));
    }
}
