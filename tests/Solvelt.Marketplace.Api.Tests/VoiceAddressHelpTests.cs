using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Address help (decision V-61), vendor-neutral: suggestions are read back and agreed by a spoken yes, an address is never kept as confirmed unless the caller confirms it, and with no lookup nothing changes.</summary>
public class VoiceAddressHelpTests
{
    private static readonly AddressParts Main = new("12 Mazel Lane", "Brooklyn", "NY", "11201"), Oak = new("14 Oak Avenue", "Brooklyn", "NY", "11201");

    private static FakeAddressLookup Lookup(params AddressParts[] found)
    {
        var f = new FakeAddressLookup();
        f.Suggestions = _ => found.Select((p, i) => new AddressSuggestion("sugg-" + i, p)).ToList();
        f.Parts = id => id.StartsWith("sugg-") && int.TryParse(id[5..], out var i) && i < found.Length ? found[i] : null;
        return f;
    }

    private static async Task<VoiceScenario> Guest(IAddressLookup? lookup, bool allowSave = true)
    {
        var s = new VoiceScenario(config: new() { ["Voice:Google:AllowSaveConfirmedAddress"] = allowSave ? "true" : "false" }, services: sv => { if (lookup is not null) { sv.RemoveAll<IAddressLookup>(); sv.AddSingleton(lookup); } });
        await s.SyncCatalog(); s.PortalShipping();
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        return s;
    }

    private static string Err(JsonNode n) => n["error"]?.GetValue<string>() ?? n["status"]?.GetValue<string>() ?? n.ToJsonString();
    private static IReadOnlyList<string> Names(VoiceScenario s) => s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task With_no_lookup_the_two_tools_are_not_offered_and_a_call_to_them_is_refused_and_the_usual_address_flow_is_unchanged()
    {
        using var s = await Guest(null);
        Assert.DoesNotContain("suggest_address", Names(s)); Assert.DoesNotContain("use_suggested_address", Names(s));
        Assert.Contains("set_delivery_address", Names(s));
        Assert.Equal("unknown_tool", Err(await s.Tool("suggest_address", new { text = "12 Mazel" })));
        Assert.Equal("unknown_tool", Err(await s.Tool("use_suggested_address", new { position = 1 })));
        Assert.Equal("read_back_needed", Err(await s.Tool("set_delivery_address", new { street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" })));
        Assert.Equal("confirmed", Err(await s.Tool("confirm_address")));
        var off = new FakeAddressLookup { IsConfigured = false };
        using var s2 = await Guest(off);                                                                              // a lookup that says it is not configured behaves the same
        Assert.DoesNotContain("suggest_address", Names(s2));
        Assert.Empty(off.Queries);
    }

    [Fact]
    public void A_missing_lookup_makes_one_warning_once_and_only_when_the_voice_line_is_on()
    {
        AddressHelpStartup.Reset();
        var log = new ListLogger();
        AddressHelpStartup.WarnOnce(new NotConfiguredAddressLookup(), voiceEnabled: false, log);
        Assert.Empty(log.Lines);
        AddressHelpStartup.WarnOnce(new NotConfiguredAddressLookup(), voiceEnabled: true, log);
        AddressHelpStartup.WarnOnce(new NotConfiguredAddressLookup(), voiceEnabled: true, log);
        var line = Assert.Single(log.Lines);
        Assert.Contains("not available", line); Assert.Contains("Voice:Google:MapsApiKey", line);
        AddressHelpStartup.Reset();
        var configured = new ListLogger();
        AddressHelpStartup.WarnOnce(new FakeAddressLookup(), voiceEnabled: true, configured);
        Assert.Empty(configured.Lines);
        AddressHelpStartup.Reset();
    }

    [Fact]
    public async Task Suggestions_are_read_one_at_a_time_as_spoken_text_with_no_ids_and_only_the_query_goes_to_the_lookup()
    {
        var fake = Lookup(Main, Oak, new("16 Pine Street", "Brooklyn", "NY", "11201"), new("18 Elm Street", "Brooklyn", "NY", "11201"));
        using var s = await Guest(fake);
        Assert.Contains("suggest_address", Names(s)); Assert.Contains("use_suggested_address", Names(s));
        var r = await s.Tool("suggest_address", new { text = "twelve maz", postal_code = "11201-4455" });
        Assert.Equal("found", r["status"]!.GetValue<string>());
        var list = r["suggestions"]!.AsArray();
        Assert.Equal(3, list.Count);                                                                                  // at most three, whatever the lookup returned
        Assert.Equal(1, list[0]!["position"]!.GetValue<int>());
        Assert.Contains("Mazel Lane", list[0]!["spoken"]!.GetValue<string>());
        Assert.DoesNotMatch(@"\d", list[0]!["spoken"]!.GetValue<string>());                                          // spoken: numbers as words
        Assert.DoesNotContain("sugg-", r.ToJsonString());                                                             // the lookup's ids never reach the model
        Assert.Contains("one", r["instruction"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var q = Assert.Single(fake.Queries);
        Assert.Equal(("twelve maz", "11201"), (q.Text, q.PostalCode));                                                // nothing but the words and the five-digit ZIP
        Assert.DoesNotContain("Dana", q.Text);
        Assert.Null(s.State().DeliveryAddress);                                                                       // a suggestion takes, saves and confirms nothing
        Assert.False(s.State().AddressConfirmed);
    }

    [Fact]
    public async Task A_yes_to_a_suggestion_starts_the_usual_read_back_and_the_address_is_never_confirmed_until_the_caller_confirms_it()
    {
        var fake = Lookup(Main, Oak);
        using var s = await Guest(fake);
        await s.Tool("suggest_address", new { text = "12 Mazel" });
        var used = await s.Tool("use_suggested_address", new { position = 1, apartment = "4B" });
        Assert.Equal("read_back_needed", used["status"]!.GetValue<string>());
        Assert.Contains("Mazel", used["read_back"]!.GetValue<string>());
        Assert.Contains("four B", used["read_back"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);        // the apartment the caller added
        Assert.Equal(["sugg-0"], fake.Resolved);
        var st = s.State();
        Assert.Equal(("12 Mazel Lane", "4B", "Brooklyn", "NY", "11201"), (st.DeliveryAddress!.Line1, st.DeliveryAddress.Line2, st.DeliveryAddress.City, st.DeliveryAddress.Region, st.DeliveryAddress.PostalCode));
        Assert.False(st.AddressConfirmed);                                                                            // held, not confirmed
        Assert.Equal("address_not_confirmed", Err(await s.Tool("get_delivery_options")));                             // nothing depends on it yet
        Assert.Equal("confirmed", Err(await s.Tool("confirm_address")));                                              // only the caller's agreement to the read-back
        Assert.True(s.State().AddressConfirmed);
        Assert.Equal("no_such_position", Err(await s.Tool("use_suggested_address", new { position = 1 })));           // a suggestion is used once
    }

    [Fact]
    public async Task With_the_save_decision_not_taken_the_lookup_only_helps_the_server_copies_nothing_and_the_caller_enters_the_confirmed_address_the_usual_way()
    {
        var fake = Lookup(Main);
        fake.Attribution = "According to Google Maps";
        using var s = await Guest(fake, allowSave: false);
        var found = await s.Tool("suggest_address", new { text = "12 Mazel" });
        Assert.Equal("According to Google Maps", found["attribution"]!.GetValue<string>());                              // the credit, spoken before the first suggestion
        Assert.Contains("According to Google Maps", found["instruction"]!.GetValue<string>());
        var used = await s.Tool("use_suggested_address", new { position = 1 });
        Assert.Equal("confirm_with_caller", used["status"]!.GetValue<string>());
        Assert.Equal(("12 Mazel Lane", "Brooklyn", "NY", "11201"), (used["street"]!.GetValue<string>(), used["city"]!.GetValue<string>(), used["state"]!.GetValue<string>(), used["postal_code"]!.GetValue<string>()));
        Assert.Contains("set_delivery_address", used["instruction"]!.GetValue<string>());
        Assert.Null(s.State().DeliveryAddress);                                                                           // the server stored nothing from the lookup
        Assert.False(s.State().AddressConfirmed);
        Assert.Equal("read_back_needed", Err(await s.Tool("set_delivery_address", new { street = "12 Mazel Lane", apartment = "4B", city = "Brooklyn", state = "NY", postal_code = "11201" })));   // the caller's own confirmed entry, the usual path
        Assert.Equal("address_not_confirmed", Err(await s.Tool("get_delivery_options")));
        Assert.Equal("no_such_position", Err(await s.Tool("use_suggested_address", new { position = 1 })));                // still used once
        var plain = Lookup(Main);                                                                                          // no credit configured: none said
        using var s2 = await Guest(plain);
        Assert.Null((await s2.Tool("suggest_address", new { text = "12 Mazel" }))["attribution"]);
    }

    [Fact]
    public async Task A_google_style_suggestion_is_read_as_the_street_then_the_rest_with_numbers_as_words()
    {
        var fake = new FakeAddressLookup { Suggestions = _ => [new AddressSuggestion("g1", new AddressParts("1245 Main Street", "", "", ""), "Brooklyn, NY 11201, USA")] };
        using var s = await Guest(fake);
        var spoken = (await s.Tool("suggest_address", new { text = "1245 main" }))["suggestions"]![0]!["spoken"]!.GetValue<string>();
        Assert.Equal("twelve forty-five Main Street, Brooklyn, NY one one two zero one, USA", spoken);
        Assert.DoesNotMatch(@"\d", spoken);
    }

    [Fact]
    public async Task A_no_leaves_nothing_behind_and_the_usual_flow_takes_over_and_a_bad_position_or_no_search_is_refused()
    {
        var fake = Lookup(Main);
        using var s = await Guest(fake);
        Assert.Equal("no_such_position", Err(await s.Tool("use_suggested_address", new { position = 1 })));   // nothing was suggested yet
        await s.Tool("suggest_address", new { text = "12 Mazel" });
        // the caller said no to it: the model does not call use_suggested_address; it takes the address the usual way
        Assert.Null(s.State().DeliveryAddress);
        Assert.Equal("no_such_position", Err(await s.Tool("use_suggested_address", new { position = 2 })));
        Assert.Equal("no_such_position", Err(await s.Tool("use_suggested_address", new { position = 0 })));
        Assert.Equal("read_back_needed", Err(await s.Tool("set_delivery_address", new { street = "1 Main St", city = "Brooklyn", state = "NY", postal_code = "11201" })));
        Assert.Equal("1 Main St", s.State().DeliveryAddress!.Line1);
        Assert.False(s.State().AddressConfirmed);
        Assert.Empty(fake.Resolved);                                                                                  // nothing was resolved: no yes, no lookup
    }

    [Fact]
    public async Task A_suggestion_the_checks_refuse_is_not_kept_and_none_found_a_failure_and_too_many_tries_all_fall_back_to_the_usual_flow()
    {
        var fake = Lookup(new AddressParts("12 Mazel Lane", "Brooklyn", "ZZ", "11201"));
        using var s = await Guest(fake);
        await s.Tool("suggest_address", new { text = "12 Mazel" });
        Assert.Equal("invalid_address", Err(await s.Tool("use_suggested_address", new { position = 1 })));            // the same validation as a spelled address
        Assert.Null(s.State().DeliveryAddress);
        fake.Suggestions = _ => [];
        var none = await s.Tool("suggest_address", new { text = "999 Nowhere" });
        Assert.Equal("none_found", none["status"]!.GetValue<string>()); Assert.Contains("set_delivery_address", none["instruction"]!.GetValue<string>());
        Assert.Equal("too_short", Err(await s.Tool("suggest_address", new { text = "a" })));
        fake.Fail = new HttpRequestException("secret vendor body about 12 Mazel Lane");
        var failed = await s.Tool("suggest_address", new { text = "12 Mazel" });
        Assert.Equal("address_help_failed", Err(failed));
        Assert.DoesNotContain("secret vendor", failed.ToJsonString()); Assert.DoesNotContain("Mazel Lane", failed.ToJsonString());
        Assert.Contains("set_delivery_address", failed["instruction"]!.GetValue<string>());
        fake.Fail = null; fake.Suggestions = _ => [];
        for (var i = 0; i < 5; i++) await s.Tool("suggest_address", new { text = "12 Mazel " + i });
        Assert.Equal("too_many", Err(await s.Tool("suggest_address", new { text = "12 Mazel again" })));
    }

    [Fact]
    public async Task A_caller_who_is_not_identified_gets_nothing_and_vendor_text_is_cleaned_before_it_is_spoken_or_stored()
    {
        var dirty = new AddressParts("12 \"Mazel\"\n<Lane>", "Brook\tlyn", "ny", "11201 junk", "us");
        var fake = Lookup(dirty);
        using var s = new VoiceScenario(services: sv => { sv.RemoveAll<IAddressLookup>(); sv.AddSingleton<IAddressLookup>(fake); });
        await s.Dial();
        Assert.Equal("need_identity", Err(await s.Tool("suggest_address", new { text = "12 Mazel" })));
        Assert.Empty(fake.Queries);
        var parts = AddressHelp.Clean(dirty);
        Assert.Equal(("12 Mazel Lane", "Brook lyn", "NY", "11201", "US"), (parts.Line1, parts.City, parts.Region, parts.PostalCode, parts.Country));
        Assert.Equal("11201", AddressHelp.CleanPostal("11201-4455")); Assert.Null(AddressHelp.CleanPostal("112")); Assert.Equal("", AddressHelp.Clean((string?)null));
        Assert.Equal(AddressHelp.MaxQueryChars, AddressHelp.Clean(new string('x', 500), AddressHelp.MaxQueryChars).Length);
    }

    [Fact]
    public async Task No_address_text_reaches_a_log_in_a_whole_suggestion_flow_even_when_the_lookup_fails()
    {
        var fake = Lookup(Main);
        using var s = await Guest(fake);
        await s.Tool("suggest_address", new { text = "twelve mazel lane" });
        await s.Tool("use_suggested_address", new { position = 1, apartment = "4B" });
        await s.Tool("confirm_address");
        fake.Fail = new HttpRequestException("12 Mazel Lane Brooklyn");
        await s.Tool("suggest_address", new { text = "12 Mazel Lane" });
        var all = string.Join("\n", s.F.Logs.Lines);
        foreach (var piece in new[] { "Mazel", "Brooklyn", "11201", "twelve mazel" }) Assert.DoesNotContain(piece, all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("address lookup failed", all);                                                                // only that it failed, and the exception type
    }
}
