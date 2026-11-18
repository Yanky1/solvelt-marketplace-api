using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Account setup and editing by phone, and switching between tasks inside one call (slice 6, decision V-09), through whole scripted calls.
/// Every change is read back by the SERVER and applied only by the caller's yes on the keypad; only a caller verified by SMS code in THIS call can edit anything.
/// </summary>
public class VoiceAccountTests
{
    private const string Email = "jane@example.com", Number = "+15551234567", Other = "+15557654321";
    private static object Address => new { street = "1 Main St", apartment = "4B", city = "Brooklyn", state = "NY", postal_code = "11201" };

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    private static async Task<(VoiceScenario S, Guid Jane)> AccountCall()
    {
        var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
        await s.Dial(); await Verify(s);
        return (s, jane);
    }

    /// <summary>The model proposes a change; the stream ends and SignalWire plays the server's read-back: returns what was read.</summary>
    private static async Task<string> ReadBack(VoiceScenario s, string tool, object args)
    {
        var r = await s.Tool(tool, args);
        Assert.True(r["status"]?.GetValue<string>() == "reading_back", r.ToJsonString());
        await s.EndStreamAfterGoodbye();
        var gather = (await s.AfterStream()).Element("Gather")!;
        var said = gather.Element("Say")!.Value;
        Assert.True(VoiceSpeech.IsSpeakable(said), said);
        Assert.EndsWith("To leave it as it is, say no or press 2.", said);
        return said;
    }

    private static async Task<string> Answer(VoiceScenario s, string digits)
    {
        var (resume, token) = await s.Type(digits);
        await s.Attach(token);
        return resume.Element("Say")!.Value;
    }

    private static Task<BuyerAccount> Account(VoiceScenario s, Guid id) => s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
    private static Task<List<BuyerAddress>> Addresses(VoiceScenario s, Guid id) => s.K.Db(db => db.BuyerAddresses.AsNoTracking().Where(a => a.AccountId == id).OrderBy(a => a.CreatedAt).ToListAsync());

    // ---------- account setup ----------

    [Fact]
    public async Task A_guest_becomes_an_account_by_phone_and_the_cart_carries_over()
    {
        using var s = new VoiceScenario();
        await s.SyncCatalog(); await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await Verify(s);                                                                                   // a guest: the number is proven, nobody owns it
        Assert.Equal(CallerLevel.Guest, s.State().Level);

        var said = await ReadBack(s, "create_account", new { name = "Dana Levi" });
        Assert.Contains("Dana Levi", said); Assert.Contains("ending", said); Assert.Contains("no password and no email", said);
        Assert.Empty(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));                  // asking creates nothing
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("no password and no email"));               // the model never wrote the read-back

        Assert.Equal(VoiceScripts.AccountCreated, await Answer(s, "1"));
        var account = Assert.Single(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        Assert.Equal(("Dana Levi", null, null, Number, Number), (account.DisplayName, account.Email, account.PasswordHash, account.Phone, account.PhoneVerifiedE164));
        var st = s.State();
        Assert.Equal((CallerLevel.Verified, account.Id, true), (st.Level, st.AccountId, st.VerifiedInThisCall));
        Assert.Equal(2, st.CartItemCount);                                                                 // the cart moved into the account
        var cart = await s.Tool("view_cart");
        Assert.Contains("classic", cart.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dana", await Summary(s));
        // the next call from this number is recognised as the account
        var match = await s.F.Services.GetRequiredService<PhoneIdentity>().FindAsync(Number, default);
        Assert.Equal(account.Id, match!.AccountId);
    }

    private static async Task<string> Summary(VoiceScenario s) => (await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains("account"))).ToJsonString();

    [Fact]
    public async Task Saying_no_to_the_account_read_back_creates_nothing()
    {
        using var s = new VoiceScenario();
        await s.Dial(); await Verify(s);
        await ReadBack(s, "create_account", new { name = "Dana Levi" });
        Assert.Equal(VoiceScripts.AccountKept, await Answer(s, "2"));
        Assert.Empty(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        Assert.Equal(CallerLevel.Guest, s.State().Level);
    }

    [Fact]
    public async Task An_unverified_caller_cannot_create_or_edit_anything()
    {
        using var s = new VoiceScenario();
        await s.Dial();                                                                                    // anonymous: nothing proven yet
        foreach (var (tool, args) in new (string, object)[] { ("create_account", new { name = "Dana Levi" }), ("change_my_name", new { name = "Dana Levi" }), ("manage_saved_address", new { action = "add", street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }), ("change_my_phone", new { new_number = Other }), ("text_me_account_link", new { topic = "password" }) })
            Assert.Equal("need_verification", (await s.Tool(tool, args))["error"]!.GetValue<string>());
        await Verify(s);                                                                                   // a verified GUEST has no account to edit
        foreach (var (tool, args) in new (string, object)[] { ("change_my_name", new { name = "Dana Levi" }), ("manage_saved_address", new { action = "add", street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" }), ("change_my_phone", new { new_number = Other }) })
            Assert.Equal("need_verification", (await s.Tool(tool, args))["error"]!.GetValue<string>());
        Assert.Empty(await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync()));
        Assert.Null(s.State().KeypadStep);
    }

    [Fact]
    public async Task A_recognised_caller_who_has_not_typed_a_code_in_this_call_cannot_edit()
    {
        var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" });
        using (s)
        {
            await s.K.VerifiedSession(Email);
            await s.SetPhone(Email, "(555) 123-4567", verified: true);
            await s.Dial();                                                                                // caller ID matches the account, but caller ID alone never authenticates
            Assert.Equal("need_verification", (await s.Tool("change_my_name", new { name = "Mallory X" }))["error"]!.GetValue<string>());
            Assert.Equal("Jane", (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).DisplayName.Split(' ')[0]);
        }
    }

    [Fact]
    public async Task A_number_that_already_belongs_to_an_account_is_never_given_a_second_one()
    {
        // an account whose email is still unproven holds the number (it is not recognised, so the caller is a guest): the number must not be duplicated
        var s = new VoiceScenario();
        using (s)
        {
            await s.K.SignUp("pending@example.com");
            await s.K.Db(async db => await db.BuyerAccounts.Where(a => a.Email == "pending@example.com").ExecuteUpdateAsync(x => x.SetProperty(a => a.PhoneVerifiedE164, Number).SetProperty(a => a.Phone, Number)));
            await s.Dial(); await Verify(s);
            Assert.Equal(CallerLevel.Guest, s.State().Level);
            Assert.Equal("number_in_use", (await s.Tool("create_account", new { name = "Dana Levi" }))["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_number_claimed_between_the_read_back_and_the_yes_is_refused_cleanly_with_no_takeover_and_no_error()
    {
        using var s = new VoiceScenario();
        await s.Dial(); await Verify(s);
        await ReadBack(s, "create_account", new { name = "Dana Levi" });                                   // nothing holds the number yet: the read-back is offered
        await s.K.SignUp("rival@example.com");                                                              // ...then another account proves the same number before the yes
        await s.K.Db(async db => await db.BuyerAccounts.Where(a => a.Email == "rival@example.com").ExecuteUpdateAsync(x => x.SetProperty(a => a.PhoneVerifiedE164, Number).SetProperty(a => a.Phone, Number)));
        Assert.Equal(VoiceScripts.AccountRefused, await Answer(s, "1"));                                   // refused, spoken, no 500
        var accounts = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().ToListAsync());
        var rival = Assert.Single(accounts);                                                                // no second account
        Assert.Equal((Number, "rival@example.com"), (rival.PhoneVerifiedE164, rival.Email));               // and the rival keeps the number
        Assert.Equal(CallerLevel.Guest, s.State().Level);
        using var scope = s.F.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().CreateAsync("Dana Levi", Number, default));   // the service itself says no, it does not throw
    }

    [Fact]
    public async Task A_phone_only_account_is_not_a_door_for_an_email_address_it_has_none()
    {
        // an email-less account is recognised by its number; an account with an UNVERIFIED email still is not (V-05)
        using var s = new VoiceScenario();
        await s.K.SignUp("pending@example.com");
        await s.K.Db(async db => await db.BuyerAccounts.Where(a => a.Email == "pending@example.com").ExecuteUpdateAsync(x => x.SetProperty(a => a.PhoneVerifiedE164, Number)));
        Assert.Null(await s.F.Services.GetRequiredService<PhoneIdentity>().FindAsync(Number, default));
    }

    // ---------- account editing ----------

    [Fact]
    public async Task A_name_change_is_read_back_and_saved_only_by_a_yes()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var said = await ReadBack(s, "change_my_name", new { name = "Jane Q Brown" });
            Assert.Contains("Jane Q Brown", said);
            Assert.DoesNotContain("Q Brown", (await Account(s, jane)).DisplayName);                       // asking changed nothing
            Assert.Equal(VoiceScripts.AccountSaved, await Answer(s, "1"));
            Assert.Equal("Jane Q Brown", (await Account(s, jane)).DisplayName);
            Assert.Equal("Jane", s.State().FirstName);

            await ReadBack(s, "change_my_name", new { name = "Mallory Evil" });
            Assert.Equal(VoiceScripts.AccountKept, await Answer(s, "2"));
            Assert.Equal("Jane Q Brown", (await Account(s, jane)).DisplayName);                            // a no changes nothing
            await ReadBack(s, "change_my_name", new { name = "Mallory Evil" });
            var again = await s.Type("7");                                                                 // anything that is not a clear yes is asked once more...
            Assert.Equal(VoiceScripts.AccountRetry, again.Xml.Element("Gather")!.Element("Say")!.Value);
            var (resume, token) = await s.Type("7");                                                       // ...and then counts as a no
            Assert.Equal(VoiceScripts.AccountKept, resume.Element("Say")!.Value);
            await s.Attach(token);
            Assert.Equal("Jane Q Brown", (await Account(s, jane)).DisplayName);
        }
    }

    [Fact]
    public async Task Saved_addresses_are_added_edited_made_default_and_removed_with_a_read_back_each_time()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var add = await ReadBack(s, "manage_saved_address", new { action = "add", street = "1 Main St", apartment = "4B", city = "Brooklyn", state = "NY", postal_code = "11201" });
            Assert.Contains("add this address", add); Assert.Contains("Brooklyn", add);
            Assert.Empty(await Addresses(s, jane));
            Assert.Equal(VoiceScripts.AccountSaved, await Answer(s, "1"));
            await ReadBack(s, "manage_saved_address", new { action = "add", street = "20 Elm St", city = "Albany", state = "NY", postal_code = "12207" });
            await Answer(s, "1");
            var saved = await Addresses(s, jane);
            Assert.Equal(new[] { "1 Main St", "20 Elm St" }, saved.Select(a => a.Line1).ToArray());
            Assert.True(saved[0].IsDefault); Assert.False(saved[1].IsDefault);

            // positions come from the list the server read out: without it nothing can be edited
            Assert.Equal("no_such_position", (await s.Tool("manage_saved_address", new { action = "remove", position = 1 }))["error"]!.GetValue<string>());
            await s.Tool("list_saved_addresses");
            var edit = await ReadBack(s, "manage_saved_address", new { action = "edit", position = 2, city = "Schenectady", postal_code = "12305" });
            Assert.Contains("Schenectady", edit);
            Assert.Equal("Albany", (await Addresses(s, jane))[1].City);
            await Answer(s, "1");
            Assert.Equal(("Schenectady", "12305", "20 Elm St"), ((await Addresses(s, jane))[1].City, (await Addresses(s, jane))[1].PostalCode, (await Addresses(s, jane))[1].Line1));

            await s.Tool("list_saved_addresses");
            await ReadBack(s, "manage_saved_address", new { action = "set_default", position = 2 });
            await Answer(s, "1");
            Assert.Equal("20 Elm St", (await Addresses(s, jane)).Single(a => a.IsDefault).Line1);

            await s.Tool("list_saved_addresses");
            await ReadBack(s, "manage_saved_address", new { action = "remove", position = 2 });
            Assert.Equal(2, (await Addresses(s, jane)).Count);                                             // asking removed nothing
            await Answer(s, "1");
            Assert.Equal(new[] { "20 Elm St" }, (await Addresses(s, jane)).Select(a => a.Line1).ToArray());   // the list puts the default first: position 2 was the old address
        }
    }

    [Fact]
    public async Task A_bad_address_is_sent_back_to_the_caller_and_nothing_is_read_back_or_saved()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var r = await s.Tool("manage_saved_address", new { action = "add", street = "1 Main St", city = "Brooklyn", state = "ZZ", postal_code = "11201" });
            Assert.Equal("invalid_address", r["error"]!.GetValue<string>());
            Assert.Null(s.State().KeypadStep);
            Assert.Empty(await Addresses(s, jane));
        }
    }

    [Fact]
    public async Task Changing_the_phone_number_needs_a_code_typed_for_the_NEW_number_then_a_yes()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            var r = await s.Tool("change_my_phone", new { new_number = Other });
            Assert.Equal("code_sent", r["status"]!.GetValue<string>());
            Assert.Equal(Other, s.Sms.Sent.Last().To);                                                     // the code goes to the NEW number
            await s.EndStreamAfterGoodbye(); await s.AfterStream();
            Assert.Equal(Number, (await Account(s, jane)).PhoneVerifiedE164);                              // nothing changed yet
            // a wrong code proves nothing
            var wrong = await s.Type("000000");
            Assert.NotNull(wrong.Xml.Element("Gather"));
            Assert.Equal(Number, (await Account(s, jane)).PhoneVerifiedE164);
            // the right code leads to the read-back, NOT to a saved change
            var (gatherXml, _) = await s.Type(s.LastCode(Other));
            var said = gatherXml.Element("Gather")!.Element("Say")!.Value;
            Assert.Contains("change the phone number on your account", said); Assert.Contains("five five five", said);
            Assert.Equal(Number, (await Account(s, jane)).PhoneVerifiedE164);
            Assert.Equal(VoiceScripts.AccountSaved, await Answer(s, "1"));
            var a = await Account(s, jane);
            Assert.Equal((Other, Other), (a.Phone, a.PhoneVerifiedE164));
            Assert.Equal(Other, s.State().VerifiedPhone);
            var identity = s.F.Services.GetRequiredService<PhoneIdentity>();
            Assert.Null(await identity.FindAsync(Number, default));                                         // the old number no longer opens the account
            Assert.Equal(jane, (await identity.FindAsync(Other, default))!.AccountId);
        }
    }

    [Fact]
    public async Task A_new_number_that_is_never_proven_is_never_saved()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.Tool("change_my_phone", new { new_number = Other });
            await s.EndStreamAfterGoodbye(); await s.AfterStream();
            for (var i = 0; i < PhoneCodeService.MaxAttempts - 1; i++) Assert.NotNull((await s.Type("000000")).Xml.Element("Gather"));
            var (last, token) = await s.Type("000000");                                                    // the fifth wrong code locks it
            Assert.Equal(VoiceScripts.CodeFailed, last.Element("Say")!.Value);
            await s.Attach(token);
            Assert.Equal(Number, (await Account(s, jane)).PhoneVerifiedE164);                              // still the old, proven number
            Assert.Null(s.State().AccountChange);
        }
    }

    [Fact]
    public async Task The_service_itself_refuses_a_phone_change_whose_number_was_not_proven()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            using var scope = s.F.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<PhoneAccountService>();
            var outcome = await service.ApplyAsync(s.State(), new AccountChangeRequest("phone", "x", Phone: Other, PhoneProved: false), default);
            Assert.Equal(AccountApply.Refused, outcome);
            Assert.Equal(Number, (await Account(s, jane)).PhoneVerifiedE164);
        }
    }

    [Fact]
    public async Task Password_payment_methods_and_deletion_are_not_done_by_phone_only_a_link_to_the_web_site_is_texted()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            foreach (var topic in new[] { "password", "payment_methods", "delete_account" })
            {
                Assert.Equal("sent", (await s.Tool("text_me_account_link", new { topic }))["status"]!.GetValue<string>());
                var sms = s.Sms.Sent.Last();
                Assert.Equal(Number, sms.To); Assert.Contains("/account", sms.Body);
            }
            Assert.Equal("bad_topic", (await s.Tool("text_me_account_link", new { topic = "everything" }))["error"]!.GetValue<string>());
            Assert.Equal("bad_topic", (await s.Tool("text_me_account_link", new { topic = "email" }))["error"]!.GetValue<string>());   // the email IS done by phone now (V-13b)
            var a = await Account(s, jane);
            Assert.Equal((Email, null), (a.Email, a.DeletedAt is null ? null : "deleted"));              // nothing was changed or deleted by phone
        }
    }

    // ---------- switching tasks inside one call ----------

    private static async Task<(VoiceScenario S, Guid Jane)> ShoppingCall(int qty = 2)
    {
        var (s, jane) = await AccountCall();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = qty });
        await s.Tool("set_delivery_address", Address);
        await s.Tool("confirm_address");
        return (s, jane);
    }

    private static async Task ConfirmReadBack(VoiceScenario s)
    {
        Assert.Equal("reading_back", (await s.Tool("read_back_order"))["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye(); await s.AfterStream();
        await Answer(s, "1");
        Assert.Equal(ConfirmationStatus.Confirmed, s.State().Confirmation);
    }

    [Fact]
    public async Task Switching_from_checkout_to_account_editing_and_back_keeps_the_cart_and_address_but_a_changed_name_needs_a_new_read_back()
    {
        var (s, jane) = await ShoppingCall();
        using (s)
        {
            await ConfirmReadBack(s);
            var before = s.State();
            Assert.Equal(2, before.CartItemCount);
            // detour: change the account's name
            await ReadBack(s, "change_my_name", new { name = "Jane Q Brown" });
            await Answer(s, "1");
            var st = s.State();
            Assert.Equal((2, "in Brooklyn"), (st.CartItemCount, st.AddressLabel));                         // the cart and the address are intact
            Assert.NotEqual(ConfirmationStatus.Confirmed, st.Confirmation);                                // who is ordering changed: the confirmation is gone
            Assert.True((await s.Tool("pay_for_order", new { method = "keypad" }))["error"] is not null);   // not payable on the old read-back
            await ConfirmReadBack(s);                                                                      // a fresh read-back brings it back
            Assert.Contains("Current task", s.State().Summary());
        }
    }

    [Fact]
    public async Task Switching_to_order_status_mid_checkout_and_back_loses_nothing()
    {
        var (s, jane) = await ShoppingCall();
        using (s)
        {
            await s.K.Db(async db =>
            {
                var now = s.F.Clock.GetUtcNow();
                var id = Guid.NewGuid();
                var o = new MarketplaceOrder { Id = id, Number = "MP-20261110-AAAA1111", AccountId = jane, Status = OrderStatus.Placed, Currency = "USD", ItemsCents = 1805, TotalCents = 1805, PaymentIntentId = "pi_x", BuyerName = "Jane", RelayEmail = "relay-x@relay.test", ShipName = "Jane Brown", ShipLine1 = "10 Park Ave", ShipCity = "Brooklyn", ShipRegion = "NY", ShipPostalCode = "11201", ShipCountry = "US", CreatedAt = now.AddDays(-3), UpdatedAt = now, PaidAt = now.AddDays(-3) };
                var g = new OrderGroup { Id = Guid.NewGuid(), OrderId = id, SellerId = Guid.NewGuid(), BrandId = Guid.NewGuid(), SellerName = "Mazel Bakery", BrandName = "B", DeliveryKind = "carrier", ShippingSpeed = "standard", ItemsCents = 1805, TotalCents = 1805, SellerOrderId = Guid.NewGuid(), SellerOrderNumber = "SO-1" };
                o.Groups.Add(g);
                db.SellerOrderStates.Add(new SellerOrderState { GroupId = g.Id, OrderId = id, Status = "new" });
                db.Orders.Add(o);
                await db.SaveChangesAsync();
                return 0;
            });
            await ConfirmReadBack(s);
            var version = s.State().Version;
            var orders = await s.Tool("list_my_orders");
            Assert.Equal(1, orders["found"]!.GetValue<int>());
            Assert.Contains("Mazel", (await s.Tool("order_status", new { position = 1 })).ToJsonString());
            var st = s.State();
            Assert.Equal((2, "in Brooklyn", ConfirmationStatus.Confirmed, version), (st.CartItemCount, st.AddressLabel, st.Confirmation, st.Version));   // looking things up changes nothing
            Assert.Equal("payment_starting", (await s.Tool("pay_for_order", new { method = "keypad" }))["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task An_address_edit_after_the_read_back_forces_a_new_read_back()
    {
        var (s, _) = await ShoppingCall();
        using (s)
        {
            await ConfirmReadBack(s);
            await s.Tool("set_delivery_address", new { street = "9 Oak Ave", city = "Brooklyn", state = "NY", postal_code = "11201" });
            await s.Tool("confirm_address");
            Assert.NotEqual(ConfirmationStatus.Confirmed, s.State().Confirmation);
            Assert.True((await s.Tool("pay_for_order", new { method = "keypad" }))["error"] is not null);   // not payable on the old read-back
            await ConfirmReadBack(s);
            Assert.Equal("payment_starting", (await s.Tool("pay_for_order", new { method = "keypad" }))["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_long_call_with_corrections_and_many_switches_places_exactly_the_order_that_was_confirmed()
    {
        var (s, jane) = await ShoppingCall(qty: 2);
        using (s)
        {
            Assert.Equal(2, s.State().CartItemCount);
            await s.Tool("view_cart");
            await s.Tool("change_cart", new { line_position = 1, quantity = 3 });                          // "no, 3 not 2"
            await ReadBack(s, "change_my_name", new { name = "Jane Q Brown" }); await Answer(s, "1");      // switch: account
            await s.Tool("list_saved_addresses");
            await s.Tool("change_cart", new { line_position = 1, quantity = 1 });                          // "go back": fewer
            await s.Tool("change_cart", new { line_position = 1, quantity = 2 });                          // "actually 2"
            await ReadBack(s, "manage_saved_address", new { action = "add", street = "20 Elm St", city = "Albany", state = "NY", postal_code = "12207" }); await Answer(s, "2");   // started, then "cancel that"
            Assert.Empty(await Addresses(s, jane));
            await s.Tool("set_delivery_address", Address); await s.Tool("confirm_address");                // switch back to delivery
            var total = (await s.Tool("get_delivery_options")).ToJsonString();
            Assert.NotNull(total);
            await ConfirmReadBack(s);
            var confirmedTotal = s.State().QuoteTotalCents!.Value;

            var pay = await s.Tool("pay_for_order", new { method = "keypad" });
            Assert.Equal("payment_starting", pay["status"]!.GetValue<string>());
            await s.EndStreamAfterGoodbye(); await s.AfterStream();
            var (done, token) = await s.Type("1");
            Assert.Equal(VoiceScripts.PayThanks, done.Element("Say")!.Value);
            await s.Attach(token);

            var order = Assert.Single(await s.K.Db(db => db.Orders.AsNoTracking().Include(o => o.Lines).ToListAsync()));
            Assert.Equal((OrderStatus.Paid, confirmedTotal, jane, "NY"), (order.Status, order.PaidCents, order.AccountId, order.ShipRegion));
            Assert.Equal(2, order.Lines.Sum(l => l.Quantity));                                             // exactly the 2 that stood at the confirmed read-back
            Assert.Equal("1 Main St", order.ShipLine1);
        }
    }
}
