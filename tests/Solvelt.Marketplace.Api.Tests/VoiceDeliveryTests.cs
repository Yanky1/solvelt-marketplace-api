using System.Xml.Linq;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Address, delivery, the quote and the server's own read-back and confirmation (slice 3, decision V-04), through whole scripted calls.</summary>
public class VoiceDeliveryTests
{
    private const string Number = "+15551234567", Email = "jane@example.com";

    private static object NewAddress(string? city = "Brooklyn", string state = "NY") => new { street = "1 Main St", apartment = "4B", city, state, postal_code = "11201" };

    /// <summary>A guest call with two small challah in the cart, verified by SMS code, named.</summary>
    private static async Task<VoiceScenario> GuestWithCart(Dictionary<string, string?>? config = null, int qty = 2)
    {
        var s = new VoiceScenario(config: config);
        await s.SyncCatalog();
        s.PortalShipping();
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = qty });
        await Verify(s);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        await s.Tool("set_guest_email", new { email = "dana.levi@example.com" });
        await s.Tool("confirm_guest_email");
        return s;
    }

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller");
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
    }

    private static async Task SetAddress(VoiceScenario s)
    {
        Assert.Equal("read_back_needed", (await s.Tool("set_delivery_address", NewAddress()))["status"]!.GetValue<string>());
        Assert.Equal("confirmed", (await s.Tool("confirm_address"))["status"]!.GetValue<string>());
    }

    /// <summary>The model asks for the read-back; the stream ends; SignalWire's next instruction is the server's Gather.</summary>
    private static async Task<XElement> ReadBack(VoiceScenario s)
    {
        var r = await s.Tool("read_back_order");
        Assert.Equal("reading_back", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();
        return await s.AfterStream();
    }

    /// <summary>The server's summary the model was given when the stream resumed (waits for one that holds <paramref name="contains"/>).</summary>
    private static async Task<string> Summary(VoiceScenario s, string contains)
        => (await s.Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["role"]?.GetValue<string>() == "system" && n["item"]!["content"]![0]!["text"]!.GetValue<string>().Contains(contains)))["item"]!["content"]![0]!["text"]!.GetValue<string>();

    private static async Task<long> TotalNow(VoiceScenario s)
    {
        using var scope = s.F.Services.CreateScope();
        var st = s.State();
        var (_, view, _) = await scope.ServiceProvider.GetRequiredService<QuoteService>().QuoteAsync(CheckoutAccess(st), new QuoteRequest(new QuoteAddress("US", "NY", "11201"), st.Selections), CancellationToken.None);
        return view!.TotalCents;
    }

    private static CartOwner CheckoutAccess(VoiceCallState st) => st.Level == CallerLevel.Verified && st.AccountId is { } id ? new CartOwner(id, null) : new CartOwner(null, st.CartToken);

    [Fact]
    public async Task A_guest_goes_from_cart_to_a_confirmed_order_and_the_server_reads_it_back_not_the_model()
    {
        using var s = await GuestWithCart();
        // the new address is read back by the tool for the caller to confirm; options wait for that
        var set = await s.Tool("set_delivery_address", NewAddress());
        var said = set["read_back"]!.GetValue<string>();
        Assert.Contains("Dana Levi", said); Assert.Contains("one Main St", said); Assert.Contains("four B", said); Assert.Contains("Brooklyn", said); Assert.Contains("New York", said); Assert.Contains("one one two zero one", said);
        Assert.True(VoiceSpeech.IsSpeakable(said));
        Assert.Equal("address_not_confirmed", (await s.Tool("get_delivery_options"))["error"]!.GetValue<string>());
        Assert.Equal("address_not_confirmed", (await s.Tool("read_back_order"))["error"]!.GetValue<string>());
        await s.Tool("confirm_address");

        var options = await s.Tool("get_delivery_options");
        var list = options["groups"]![0]!["options"]!.AsArray();
        Assert.Equal(1, list[0]!["position"]!.GetValue<int>());
        Assert.Contains("standard shipping, five dollars", list[0]!["option"]!.GetValue<string>());
        Assert.Equal("selected", (await s.Tool("choose_delivery", new { option_position = 1 }))["status"]!.GetValue<string>());

        var gather = await ReadBack(s);
        var g = gather.Element("Gather")!;
        Assert.Equal(("dtmf speech", "1"), (g.Attribute("input")!.Value, g.Attribute("numDigits")!.Value));
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.KeypadPath, g.Attribute("action")!.Value);
        var spoken = g.Element("Say")!.Value;
        var total = await TotalNow(s);
        Assert.Contains("two Classic Braided Challah, Small", spoken); Assert.Contains("Delivery to Dana Levi", spoken); Assert.Contains("standard shipping, five dollars", spoken);
        Assert.Contains("Your total is " + VoiceSpeech.Money(total), spoken);
        Assert.EndsWith(VoiceScripts.ConfirmQuestion, spoken);
        Assert.True(VoiceSpeech.IsSpeakable(spoken), spoken);
        Assert.Equal(ConfirmationStatus.ReadBack, s.State().Confirmation);
        Assert.Equal(total, s.State().QuoteTotalCents);

        var (resume, token) = await s.Type("1");
        Assert.Equal(VoiceScripts.ConfirmThanks, resume.Element("Say")!.Value);
        await s.Attach(token);
        Assert.Equal(ConfirmationStatus.Confirmed, s.State().Confirmation);
        Assert.Contains("just confirmed", await Summary(s, "just confirmed"));
        // the model never heard the read-back, the address or the total: only the server's summary of where things stand
        Assert.DoesNotContain(s.Fake.Received, m => m.Contains("Your total is") || m.Contains("11201") || m.Contains("4B"));
    }

    [Fact]
    public async Task A_tiered_cart_is_read_back_with_the_discount_and_the_free_gift_and_the_phone_total_equals_the_web_quote()
    {
        using var s = await GuestWithCart(qty: 10);
        using (var scope = s.F.Services.CreateScope())                       // "10+ at $1.00 each, with a free card wallet", Mazel / Challah Corner
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            db.CatalogOffers.Add(new CatalogOffer
            {
                Id = Guid.NewGuid(), SellerId = Catalog.FixtureCatalogSource.Id("seller:mazel"), BrandId = Catalog.FixtureCatalogSource.Id("brand:challah-corner"), Name = "Bundle", Currency = "USD", Scope = "all",
                ProductIdsJson = "[]", CategoryIdsJson = "[]", TiersJson = System.Text.Json.JsonSerializer.Serialize(new[] { new Offers.OfferTier(10, 100) }),
                GiftsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new Offers.OfferGiftDoc(10, Guid.NewGuid(), 1, "Card wallet", true) }),
                Version = 1, KnownVersion = 1, Visible = true, FirstSeenAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }
        await SetAddress(s);
        await s.Tool("get_delivery_options");
        await s.Tool("choose_delivery", new { option_position = 1 });
        var spoken = (await ReadBack(s)).Element("Gather")!.Element("Say")!.Value;
        Assert.Contains("quantity discount saves you", spoken);
        Assert.Contains("You also get a free Card wallet", spoken);
        Assert.Contains("Items " + VoiceSpeech.Money(1000), spoken);          // ten units at the tier price, the same figure the web quote shows
        var total = await TotalNow(s);
        Assert.Contains("Your total is " + VoiceSpeech.Money(total), spoken);
        Assert.Equal(total, s.State().QuoteTotalCents);
    }

    [Theory]
    [InlineData("", "yes", true)]
    [InlineData("1", null, true)]
    [InlineData("", "Yeah.", true)]
    [InlineData("", "yes, that's correct", true)]
    [InlineData("", "yes but wait", false)]       // a "no" of any kind wins
    [InlineData("", "no", false)]
    [InlineData("2", null, false)]
    public async Task Yes_by_voice_or_one_confirms_and_no_by_voice_or_two_asks_for_a_change(string digits, string? speech, bool confirms)
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await ReadBack(s);
        var (resume, token) = await s.Type(digits, speech);
        Assert.NotEmpty(token);
        Assert.True(confirms == (resume.Element("Say")?.Value == VoiceScripts.ConfirmThanks), resume.ToString());
        Assert.Equal(confirms ? ConfirmationStatus.Confirmed : ConfirmationStatus.ReadBack, s.State().Confirmation);
        if (!confirms) Assert.Equal(VoiceScripts.ConfirmChange, resume.Element("Say")!.Value);
        await s.Attach(token);
        if (!confirms) Assert.Contains("wants to change", await Summary(s, "wants to change"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "mumble")]
    [InlineData("3", null)]
    [InlineData("", "I think maybe it could be a yes or a no or something else entirely")]
    public async Task An_unclear_answer_is_asked_again_once_and_never_counts_as_a_yes(string digits, string? speech)
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await ReadBack(s);
        var (first, _) = await s.Type(digits, speech);
        Assert.Equal(VoiceScripts.ConfirmRetry, first.Element("Gather")!.Element("Say")!.Value);        // asked again
        Assert.Equal(ConfirmationStatus.ReadBack, s.State().Confirmation);
        var (second, token) = await s.Type(digits, speech);
        Assert.NotNull(second.Element("Connect"));                                                       // the call goes on, the agent asks
        await s.Attach(token);
        Assert.NotEqual(ConfirmationStatus.Confirmed, s.State().Confirmation);
        Assert.Contains("no clear yes or no", await Summary(s, "no clear yes or no"));
    }

    [Theory]
    [InlineData("cart")]
    [InlineData("address")]
    [InlineData("delivery")]
    public async Task Any_change_after_the_confirmation_takes_it_away_and_a_new_read_back_is_needed(string change)
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await s.Tool("get_delivery_options");
        await ReadBack(s);
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        Assert.Equal(ConfirmationStatus.Confirmed, s.State().Confirmation);
        switch (change)
        {
            case "cart": await s.Tool("search_products", new { query = "classic braided challah" }); await s.Tool("add_to_cart", new { position = 1, option_position = 2, quantity = 1 }); break;
            case "address": await s.Tool("set_delivery_address", NewAddress(city: "Queens")); break;
            default: await s.Tool("get_delivery_options"); await s.Tool("choose_delivery", new { option_position = 2 }); break;
        }
        var st = s.State();
        Assert.Equal(ConfirmationStatus.None, st.Confirmation);
        Assert.Null(st.QuoteKey);
        Assert.False(st.ConfirmReadBack());
        // a stray "1" typed on the keypad with no read-back pending confirms nothing
        var (late, _) = await s.Type("1");
        Assert.NotNull(late.Element("Hangup"));
        Assert.NotEqual(ConfirmationStatus.Confirmed, st.Confirmation);
    }

    [Fact]
    public async Task A_one_typed_during_the_sms_code_step_is_a_wrong_code_never_a_confirmation()
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await s.Tool("identify_caller", new { other_number = "555-000-1234" });
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (xml, _) = await s.Type("1");
        Assert.NotNull(xml.Element("Gather"));
        Assert.NotEqual(ConfirmationStatus.Confirmed, s.State().Confirmation);
    }

    [Fact]
    public async Task The_phone_order_limit_stops_the_read_back_and_starts_no_keypad_step()
    {
        using var s = await GuestWithCart(new() { ["Voice:MaxOrderAmount"] = "10" });     // 10 dollars: two challah and shipping are over
        await SetAddress(s);
        var r = await s.Tool("read_back_order");
        Assert.Equal("order_limit", r["error"]!.GetValue<string>());
        Assert.Null(s.State().KeypadStep);
        Assert.Null(s.State().QuoteKey);
    }

    [Fact]
    public async Task The_read_back_needs_a_cart_an_identity_a_name_and_an_address()
    {
        using var s = new VoiceScenario();
        await s.SyncCatalog(); s.PortalShipping(); await s.Dial();
        Assert.Equal("need_identity", (await s.Tool("set_delivery_address", NewAddress()))["error"]!.GetValue<string>());
        Assert.Equal("cart_empty", (await s.Tool("read_back_order"))["error"]!.GetValue<string>());
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        Assert.Equal("need_address", (await s.Tool("read_back_order"))["error"]!.GetValue<string>());
        await Verify(s);                                                                                    // a guest, no name yet
        Assert.Equal("need_name", (await s.Tool("set_delivery_address", NewAddress()))["error"]!.GetValue<string>());
        Assert.Equal("need_address", (await s.Tool("confirm_address"))["error"]!.GetValue<string>());
        Assert.Equal("no_options", (await s.Tool("choose_delivery", new { option_position = 1 }))["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "NY", "the city")]
    [InlineData("Brooklyn", "ZZ", "something in the address")]
    [InlineData("Brooklyn", "", "something in the address")]
    public async Task An_address_that_does_not_validate_is_refused_with_what_to_ask_again(string? city, string state, string problem)
    {
        using var s = await GuestWithCart();
        var r = await s.Tool("set_delivery_address", NewAddress(city, state));
        Assert.Equal("invalid_address", r["error"]!.GetValue<string>());
        Assert.Contains(problem, r["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Null(s.State().DeliveryAddress);
    }

    [Fact]
    public async Task A_new_address_resets_the_choices_and_a_correction_needs_a_fresh_confirmation()
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await s.Tool("get_delivery_options");
        await s.Tool("choose_delivery", new { option_position = 2 });
        Assert.NotEmpty(s.State().Selections);
        await s.Tool("set_delivery_address", NewAddress(city: "Queens"));
        var st = s.State();
        Assert.Equal((false, 0, 0), (st.AddressConfirmed, st.Selections.Count, st.Choices.Count));
        Assert.Equal("address_not_confirmed", (await s.Tool("get_delivery_options"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Saved_addresses_are_for_a_verified_account_only_and_come_from_that_account()
    {
        using var s = new VoiceScenario();
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
        await s.K.VerifiedSession("other@example.com", name: "Other Person");
        var other = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "other@example.com"));
        using (var scope = s.F.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            await accounts.CreateAddressAsync(jane.Id, new AddressBody("Jane Brown", "10 Park Ave", null, "Brooklyn", "NY", "11201", "US", null, null, null, true), CancellationToken.None);
            await accounts.CreateAddressAsync(other.Id, new AddressBody("Other Person", "99 Secret Rd", null, "Albany", "NY", "12207", "US", null, null, null, true), CancellationToken.None);
        }
        await s.Dial();
        Assert.Equal("need_verification", (await s.Tool("list_saved_addresses"))["error"]!.GetValue<string>());
        Assert.Equal("need_verification", (await s.Tool("use_saved_address", new { position = 1 }))["error"]!.GetValue<string>());
        await Verify(s);
        var list = await s.Tool("list_saved_addresses");
        Assert.Equal(1, list["found"]!.GetValue<int>());
        Assert.Equal("ten Park Ave, Brooklyn", list["addresses"]![0]!["address_spoken"]!.GetValue<string>());
        Assert.DoesNotContain("Secret", list.ToJsonString());
        Assert.Equal("no_such_position", (await s.Tool("use_saved_address", new { position = 2 }))["error"]!.GetValue<string>());
        var use = await s.Tool("use_saved_address", new { position = 1 });
        Assert.Equal("set", use["status"]!.GetValue<string>());
        var st = s.State();
        Assert.Equal(("10 Park Ave", "Jane Brown", true), (st.DeliveryAddress!.Line1, st.DeliveryAddress.Name, st.AddressConfirmed));
    }

    [Fact]
    public async Task An_account_caller_orders_to_a_saved_address_and_reads_back_with_the_accounts_name()
    {
        using var s = new VoiceScenario();
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        var jane = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
        using (var scope = s.F.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAddressAsync(jane.Id, new AddressBody("Jane Brown", "10 Park Ave", null, "Brooklyn", "NY", "11201", "US", null, null, null, true), CancellationToken.None);
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 1 });
        await Verify(s);
        await s.Tool("list_saved_addresses");
        await s.Tool("use_saved_address", new { position = 1 });
        var gather = await ReadBack(s);
        var spoken = gather.Element("Gather")!.Element("Say")!.Value;
        Assert.Contains("Delivery to Jane Brown, ten Park Ave, Brooklyn, New York", spoken);
        var (_, token) = await s.Type("1");
        await s.Attach(token);
        Assert.Equal(ConfirmationStatus.Confirmed, s.State().Confirmation);
    }

    [Fact]
    public async Task A_second_read_back_replaces_the_first_and_only_the_current_one_can_be_confirmed()
    {
        using var s = await GuestWithCart();
        await SetAddress(s);
        await ReadBack(s);
        var (_, token) = await s.Type("2");                      // change
        await s.Attach(token);
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 2, quantity = 1 });          // a bigger loaf
        var gather = await ReadBack(s);
        Assert.Contains("one Classic Braided Challah, Large", gather.Element("Gather")!.Element("Say")!.Value);
        var st = s.State();
        Assert.Equal(ConfirmationStatus.ReadBack, st.Confirmation);
        Assert.Equal(await TotalNow(s), st.QuoteTotalCents);
    }

    // ---------- speech and parsing ----------

    [Theory]
    [InlineData("1 Main St", "one Main St")]
    [InlineData("12 Oak Avenue", "twelve Oak Avenue")]
    [InlineData("123 Elm Street", "one twenty-three Elm Street")]
    [InlineData("105 Elm Street", "one oh five Elm Street")]
    [InlineData("1245 Park Avenue", "twelve forty-five Park Avenue")]
    [InlineData("1200 Park Avenue", "twelve hundred Park Avenue")]
    [InlineData("1205 Park Avenue", "twelve oh five Park Avenue")]
    [InlineData("Apt 4B", "Apt four B")]
    public void Streets_are_said_the_way_a_person_says_them(string line, string said) => Assert.Equal(said, VoiceSpeech.Street(line));

    [Theory]
    [InlineData("11201", "one one two zero one")]
    [InlineData("M5V 2T6", "M five V two T six")]
    public void Postal_codes_are_read_digit_by_digit(string code, string said) => Assert.Equal(said, VoiceSpeech.Digits(code));

    [Fact]
    public void Delivery_windows_are_dates_a_person_says()
    {
        var today = new DateOnly(2026, 11, 17);
        Assert.Equal("Friday the twentieth to Tuesday the twenty-fourth", VoiceSpeech.DateRange("2026-11-20", "2026-11-24", today));
        Assert.Equal("Friday the twentieth", VoiceSpeech.DateRange("2026-11-20", "2026-11-20", today));
        Assert.Equal("", VoiceSpeech.DateRange("soon", "later", today));
        Assert.Equal("New York", VoiceSpeech.State("ny")); Assert.Equal("ZZ", VoiceSpeech.State("ZZ"));
    }

    [Theory]
    [InlineData("1", null, ConfirmChoice.Yes)]
    [InlineData("2", null, ConfirmChoice.No)]
    [InlineData("", "yes", ConfirmChoice.Yes)]
    [InlineData("", "Yes!", ConfirmChoice.Yes)]
    [InlineData("", "yes please confirm it", ConfirmChoice.Yes)]
    [InlineData("", "no", ConfirmChoice.No)]
    [InlineData("", "no, wait", ConfirmChoice.No)]
    [InlineData("", "yes no", ConfirmChoice.No)]
    [InlineData("", "I don't think so", ConfirmChoice.No)]
    [InlineData("", "it's not right", ConfirmChoice.No)]
    [InlineData("", "go back", ConfirmChoice.No)]
    [InlineData("", "", ConfirmChoice.Unclear)]
    [InlineData("", null, ConfirmChoice.Unclear)]
    [InlineData("", "banana", ConfirmChoice.Unclear)]
    [InlineData("5", "yes", ConfirmChoice.Unclear)]
    [InlineData("11", null, ConfirmChoice.Unclear)]
    [InlineData("", "yes yes yes yes yes yes yes", ConfirmChoice.Unclear)]
    public void Answers_to_the_read_back(string digits, string? speech, ConfirmChoice expected) => Assert.Equal(expected, ReadBackAnswer.Parse(digits, speech));
}
