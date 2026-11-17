using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Order placement and payment by phone through the fake provider (slice 4, decision V-06). Nothing real is connected: the fake takes no card.</summary>
public class VoicePaymentTests
{
    private static object Address => new { street = "1 Main St", apartment = "4B", city = "Brooklyn", state = "NY", postal_code = "11201" };

    private static Dictionary<string, string?> Fake(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Voice:PhonePaymentProvider"] = "fake" };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller");
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
    }

    /// <summary>A guest with two small challah, named, with a confirmed email, address and read-back: ready to pay.</summary>
    private static async Task<VoiceScenario> Confirmed(Dictionary<string, string?>? config = null, int qty = 2)
    {
        var s = new VoiceScenario(config: Fake(config));
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = qty });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" });
        await s.Tool("confirm_guest_email");
        await s.Tool("set_delivery_address", Address);
        await s.Tool("confirm_address");
        await ConfirmReadBack(s);
        return s;
    }

    private static async Task ConfirmReadBack(VoiceScenario s)
    {
        Assert.Equal("reading_back", (await s.Tool("read_back_order"))["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        Assert.Equal(ConfirmationStatus.Confirmed, s.State().Confirmation);
    }

    private static FakePhonePaymentProvider Provider(VoiceScenario s) => s.F.Services.GetRequiredService<FakePhonePaymentProvider>();

    /// <summary>pay_for_order, the stream ends, and SignalWire runs the provider's instruction.</summary>
    private static async Task<XElement> StartPayment(VoiceScenario s)
    {
        var r = await s.Tool("pay_for_order");
        Assert.Equal("payment_starting", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    private static Task<List<MarketplaceOrder>> Orders(VoiceScenario s) => s.K.Db(db => db.Orders.AsNoTracking().ToListAsync());

    [Fact]
    public async Task A_guest_confirms_pays_on_the_keypad_and_a_paid_order_exists_that_was_priced_and_read_back_by_the_server()
    {
        using var s = await Confirmed();
        var total = s.State().QuoteTotalCents!.Value;
        var instruction = await StartPayment(s);
        var gather = instruction.Element("Gather")!;
        Assert.Equal(VoiceScripts.FakePayPrompt, gather.Element("Say")!.Value);
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, gather.Attribute("action")!.Value);
        // the order exists, pending, at exactly the amount that was read back, and the provider was asked for exactly that
        var pending = Assert.Single(await Orders(s));
        Assert.Equal((OrderStatus.PendingPayment, total), (pending.Status, pending.TotalCents));
        var attempt = Assert.Single(Provider(s).Attempts);
        Assert.Equal((pending.Number, total, "USD", PhonePaymentMethod.Keypad), (attempt.OrderNumber, attempt.AmountCents, attempt.Currency, attempt.Method));

        var (resume, token) = await s.Type("1");
        Assert.Equal(VoiceScripts.PayThanks, resume.Element("Say")!.Value);
        await s.Attach(token);

        var paid = Assert.Single(await Orders(s));
        Assert.Equal((OrderStatus.Paid, total, total), (paid.Status, paid.TotalCents, paid.PaidCents));
        Assert.Equal(("dana.levi@example.com", "Dana Levi", "NY"), (paid.GuestEmail, paid.ShipName, paid.ShipRegion));
        Assert.Equal(paid.Number, s.State().OrderNumber);
        var call = await s.Row();
        Assert.Equal(VoiceOutcome.OrderPlaced, call.Outcome);
        // the caller got a text confirmation on the verified phone, with the order number and no address
        var sms = s.Sms.Sent.Last();
        Assert.Equal("+15551234567", sms.To); Assert.Contains(paid.Number, sms.Body); Assert.DoesNotContain("Main St", sms.Body); Assert.DoesNotContain("11201", sms.Body);
        // the model is told it was placed, with the number to read out; it never saw a card, a digit event, the address or the email
        var summary = await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains("placed and paid"));
        Assert.NotNull(summary);
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("11201") || m.Contains("dana.levi") || m.Contains("\"digit"));
        var check = await s.Tool("check_payment");
        Assert.True(check["order_placed"]!.GetValue<bool>());
        Assert.Equal("already_placed", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_payment_is_believed_only_when_the_amount_and_the_invoice_match_the_servers_order()
    {
        foreach (var hook in new[] { "amount", "invoice" })
        {
            using var s = await Confirmed();
            var total = s.State().QuoteTotalCents!.Value;
            if (hook == "amount") Provider(s).PaidCentsOverride = total - 1; else Provider(s).InvoiceOverride = "MP-SOMEONE-ELSES";
            await StartPayment(s);
            var (resume, token) = await s.Type("1");
            Assert.Equal(VoiceScripts.PayFailed, resume.Element("Say")!.Value);
            await s.Attach(token);
            Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);            // no capture, no paid order
            Assert.Null(s.State().OrderNumber);
            Assert.NotEqual(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
            Assert.Contains("did not go through", (await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains("did not go through")))["item"]!["content"]![0]!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_declined_payment_leaves_the_order_unpaid_and_a_retry_is_the_same_order_and_the_same_payment_intent()
    {
        using var s = await Confirmed();
        await StartPayment(s);
        var (declined, token) = await s.Type("2");
        Assert.Equal(VoiceScripts.PayFailed, declined.Element("Say")!.Value);
        await s.Attach(token);
        var first = Assert.Single(await Orders(s));
        Assert.Equal(OrderStatus.PendingPayment, first.Status);

        await StartPayment(s);                                                        // the caller tries again
        var second = Assert.Single(await Orders(s));                                  // not a second order
        Assert.Equal((first.Number, first.PaymentIntentId), (second.Number, second.PaymentIntentId));
        var (ok, token2) = await s.Type("1");
        Assert.Equal(VoiceScripts.PayThanks, ok.Element("Say")!.Value);
        await s.Attach(token2);
        Assert.Equal(OrderStatus.Paid, Assert.Single(await Orders(s)).Status);
    }

    [Fact]
    public async Task A_one_typed_after_the_payment_was_settled_pays_nothing_twice()
    {
        using var s = await Confirmed();
        await StartPayment(s);
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        var again = await s.Type("1");                                                // no payment step is pending any more
        Assert.NotNull(again.Xml.Element("Hangup"));
        var paid = Assert.Single(await Orders(s));
        Assert.Equal(paid.TotalCents, paid.PaidCents);
        Assert.Single(Provider(s).Attempts);
    }

    [Fact]
    public async Task Payment_attempts_in_one_call_are_limited()
    {
        using var s = await Confirmed(new() { ["Voice:MaxPaymentAttempts"] = "2" });
        for (var i = 0; i < 2; i++)
        {
            await StartPayment(s);
            var (_, token) = await s.Type("2");
            await s.Attach(token);
        }
        Assert.Equal("too_many_attempts", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
        Assert.Equal(OrderStatus.PendingPayment, Assert.Single(await Orders(s)).Status);
    }

    [Fact]
    public async Task One_number_can_place_only_so_many_phone_orders_a_day()
    {
        using var s = await Confirmed(new() { ["Voice:MaxOrdersPerDay"] = "1" });
        await StartPayment(s);
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        Assert.Equal(VoiceOutcome.OrderPlaced, (await s.Row()).Outcome);
        await s.Phone!.Send(new { @event = "stop" });
        await s.Phone.WaitClosed();
        // the same number calls again and gets as far as paying
        s.Sid = "CA-second-call";
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" });
        await s.Tool("confirm_guest_email");
        await s.Tool("set_delivery_address", Address);
        await s.Tool("confirm_address");
        await ConfirmReadBack(s);
        Assert.Equal("daily_limit", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
        Assert.Single(await s.K.Db(db => db.Orders.Where(o => o.Status == OrderStatus.Paid).ToListAsync()));
    }

    [Fact]
    public async Task The_caller_number_is_counted_by_a_keyed_hash_never_stored()
    {
        using var s = await Confirmed();
        var call = await s.Row();
        Assert.Equal(64, call.CallerKey!.Length);
        Assert.DoesNotContain("5551234567", call.CallerKey);
        Assert.Equal("***4567", call.CallerMasked);
        Assert.NotEqual(call.CallerKey, s.F.Services.GetRequiredService<VoiceCallStore>().CallerKeyOf("+15559999999"));
    }

    [Fact]
    public async Task A_price_that_moved_after_the_yes_is_never_charged_the_caller_must_hear_the_new_total()
    {
        using var s = await Confirmed();
        var before = s.State().QuoteTotalCents;
        await s.K.Db(async db => await db.CatalogVariants.Where(v => v.Id == Solvelt.Marketplace.Api.Catalog.FixtureCatalogSource.Id("variant:challah-classic:0")).ExecuteUpdateAsync(x => x.SetProperty(v => v.PriceCents, 700L)));
        var r = await s.Tool("pay_for_order");
        Assert.Equal("price_changed", r["error"]!.GetValue<string>());
        Assert.Equal(ConfirmationStatus.None, s.State().Confirmation);                  // a new read-back is needed
        Assert.Empty(await Orders(s));
        Assert.Empty(Provider(s).Attempts);
        Assert.NotNull(before);
    }

    [Fact]
    public async Task Payment_needs_a_confirmed_read_back_a_provider_and_a_method_the_provider_has()
    {
        using var none = new VoiceScenario(config: null);                               // no provider configured: the default
        await none.SyncCatalog(); none.PortalShipping(); await none.Dial();
        Assert.Equal("payments_unavailable", (await none.Tool("pay_for_order"))["error"]!.GetValue<string>());

        using var s = new VoiceScenario(config: Fake());
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        Assert.Equal("method_unavailable", (await s.Tool("pay_for_order", new { method = "text_link" }))["error"]!.GetValue<string>());
        Assert.Equal("method_unavailable", (await s.Tool("pay_for_order", new { method = "bitcoin" }))["error"]!.GetValue<string>());
        Assert.Equal("not_confirmed", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
        Assert.Empty(await Orders(s));
    }

    [Fact]
    public async Task A_provider_that_is_down_starts_no_keypad_step_and_the_order_stays_pending()
    {
        using var s = await Confirmed();
        Provider(s).Fail = true;
        Assert.Equal("payments_unavailable", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
        Assert.Null(s.State().KeypadStep);
    }

    [Fact]
    public async Task An_account_caller_needs_no_email_and_the_order_is_the_accounts()
    {
        using var s = new VoiceScenario(config: Fake());
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession("jane@example.com");
        await s.SetPhone("jane@example.com", "(555) 123-4567", verified: true);
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        await Verify(s);
        await s.Tool("set_delivery_address", Address);
        await s.Tool("confirm_address");
        Assert.Equal("not_needed", (await s.Tool("set_guest_email", new { email = "x@example.com" }))["error"]!.GetValue<string>());
        await ConfirmReadBack(s);
        await StartPayment(s);
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "jane@example.com"));
        var order = Assert.Single(await Orders(s));
        Assert.Equal((OrderStatus.Paid, jane.Id, null), (order.Status, order.AccountId, order.GuestEmail));
    }

    [Fact]
    public async Task A_guest_must_give_and_confirm_an_email_before_the_read_back()
    {
        using var s = new VoiceScenario(config: Fake());
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_delivery_address", Address);
        await s.Tool("confirm_address");
        Assert.Equal("need_email", (await s.Tool("read_back_order"))["error"]!.GetValue<string>());
        Assert.Equal("no_email", (await s.Tool("confirm_guest_email"))["error"]!.GetValue<string>());
        foreach (var bad in new[] { "", "nobody", "a@b", "x y@example.com", "dana@@example.com" })
            Assert.Equal("bad_email", (await s.Tool("set_guest_email", new { email = bad }))["error"]!.GetValue<string>());
        var r = await s.Tool("set_guest_email", new { email = "Dana.Levi@Example.COM" });
        Assert.Equal("D a n a dot L e v i at E x a m p l e dot com".ToLowerInvariant(), r["read_back"]!.GetValue<string>().ToLowerInvariant());
        Assert.Null(s.State().GuestEmail);                                                // not saved until the caller agrees
        Assert.Equal("confirmed", (await s.Tool("confirm_guest_email"))["status"]!.GetValue<string>());
        Assert.Equal("dana.levi@example.com", s.State().GuestEmail);
        Assert.Equal("reading_back", (await s.Tool("read_back_order"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changing_the_email_after_the_yes_takes_the_confirmation_away()
    {
        using var s = await Confirmed();
        await s.Tool("set_guest_email", new { email = "other@example.com" });
        await s.Tool("confirm_guest_email");
        Assert.Equal(ConfirmationStatus.None, s.State().Confirmation);
        Assert.Equal("not_confirmed", (await s.Tool("pay_for_order"))["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("dana.levi@example.com", "d a n a dot l e v i at e x a m p l e dot com")]
    [InlineData("a_b-c+1@mail.co.uk", "a underscore b dash c plus one at m a i l dot c o dot u k")]
    public void Emails_are_spelled_for_reading_back(string email, string said) => Assert.Equal(said, VoiceSpeech.Email(email).ToLowerInvariant());
}
