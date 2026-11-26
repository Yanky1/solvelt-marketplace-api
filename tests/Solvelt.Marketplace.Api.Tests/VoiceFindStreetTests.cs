using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The street search, the advisory check and the save-time address check on the telephone line (decision V-63), through whole scripted calls with fake lookups.</summary>
public class VoiceFindStreetTests
{
    private static async Task<VoiceScenario> Guest(FakePostcodeLookup? zips, FakeStreetSuggest? streets, Action<IServiceCollection>? more = null)
    {
        var s = new VoiceScenario(services: sv =>
        {
            if (zips is not null) { sv.RemoveAll<IPostcodeLookup>(); sv.AddSingleton<IPostcodeLookup>(zips); }
            if (streets is not null) { sv.RemoveAll<IStreetSuggest>(); sv.AddSingleton<IStreetSuggest>(streets); }
            more?.Invoke(sv);
        });
        await s.SyncCatalog(); s.PortalShipping();
        await s.Dial();
        await s.Tool("search_products", new { query = "classic braided challah" });
        await s.Tool("add_to_cart", new { position = 1, option_position = 1, quantity = 2 });
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
        await s.Tool("set_guest_name", new { name = "Dana Levi" });
        return s;
    }

    private static string Code(JsonNode n) => n["error"]?.GetValue<string>() ?? n["status"]?.GetValue<string>() ?? n.ToJsonString();
    private static IReadOnlyList<string> Names(VoiceScenario s) => s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()).ToList();
    private static object Take(string line = "12 Mazel Lane", string city = "Albany", string zip = "12207", string state = "NY") => new { street = line, city, state, postal_code = zip };

    [Fact]
    public async Task Without_the_key_find_street_is_not_offered_and_is_refused_but_check_address_and_the_usual_flow_work()
    {
        using var s = await Guest(null, null);                                                                         // the defaults: no key set
        Assert.DoesNotContain("find_street", Names(s)); Assert.Contains("check_address", Names(s)); Assert.Contains("set_delivery_address", Names(s));
        Assert.Equal("unknown_tool", Code(await s.Tool("find_street", new { zip = "12207", partial_street = "maz" })));
        Assert.Equal("read_back_needed", Code(await s.Tool("set_delivery_address", Take(city: "Brooklyn"))));         // no ZIP check: taken as given
        Assert.Equal("Brooklyn", s.State().DeliveryAddress!.City);
        using var keyed = new TestFactory(VoiceFactory.Config(new() { ["Voice:Google:MapsApiKey"] = "test-key" }));
        Assert.Contains("find_street", keyed.Services.GetRequiredService<VoiceToolRunner>().Definitions().Select(t => t!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void A_missing_key_makes_one_warning_once_and_only_when_the_voice_line_is_on()
    {
        AddressLookupStartup.Reset();
        var log = new ListLogger();
        AddressLookupStartup.WarnOnce(new FakePostcodeLookup { IsConfigured = false }, voiceEnabled: false, log);
        Assert.Empty(log.Lines);
        AddressLookupStartup.WarnOnce(new FakePostcodeLookup { IsConfigured = false }, voiceEnabled: true, log);
        AddressLookupStartup.WarnOnce(new FakePostcodeLookup { IsConfigured = false }, voiceEnabled: true, log);
        var line = Assert.Single(log.Lines);
        Assert.Contains("Voice:Google:MapsApiKey", line); Assert.Contains("usual way", line);
        AddressLookupStartup.Reset();
        var on = new ListLogger();
        AddressLookupStartup.WarnOnce(new FakePostcodeLookup(), voiceEnabled: true, on);
        Assert.Empty(on.Lines);
        AddressLookupStartup.Reset();
    }

    [Fact]
    public async Task Find_street_gives_the_streets_and_the_city_and_state_of_the_zip_so_only_the_house_number_is_left()
    {
        var zips = new FakePostcodeLookup(); var streets = new FakeStreetSuggest();
        using var s = await Guest(zips, streets);
        Assert.Contains("find_street", Names(s));
        var r = await s.Tool("find_street", new { zip = "12207", partial_street = "maz" });
        Assert.Equal(("12207", "Albany", "NY"), (r["zip"]!.GetValue<string>(), r["city"]!.GetValue<string>(), r["state"]!.GetValue<string>()));
        Assert.Equal(["Mazel Lane", "Maple Avenue"], r["matches"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("do not ask for them", r["instruction"]!.GetValue<string>());
        var call = Assert.Single(streets.Calls);
        Assert.Equal("maz", call.Partial);
        Assert.Equal(new GeoViewport(42.64, -73.77, 42.67, -73.74), call.Viewport);                                    // the search is limited to the ZIP's box
        await s.Tool("find_street", new { zip = "12207-4455", partial_street = "mazel lane" });                        // a ZIP+4 counts as its five digits
        Assert.Equal(["12207"], zips.Calls);                                                                           // the ZIP was asked about ONCE in this call
        using var again = await Guest(zips, streets);                                                                  // another call: asked again (city and state are not kept across calls)
        await again.Tool("find_street", new { zip = "12207", partial_street = "maz" });
        Assert.Equal(["12207", "12207"], zips.Calls);
        var d = s.F.Services.GetRequiredService<VoiceToolRunner>().Definitions().Single(t => t!["name"]!.GetValue<string>() == "find_street")!;
        Assert.Equal(["partial_street", "zip"], d["parameters"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
        Assert.Contains("first few letters", d["description"]!.GetValue<string>());
        Assert.Contains("Never name a tool", d["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_matches_says_so_with_the_zips_place_and_a_zip_that_does_not_exist_or_is_malformed_is_refused()
    {
        var streets = new FakeStreetSuggest { Answer = _ => [] };
        using var s = await Guest(new FakePostcodeLookup(), streets);
        var none = await s.Tool("find_street", new { zip = "33034", partial_street = "zzz" });
        Assert.Empty(none["matches"]!.AsArray());
        Assert.Contains("No streets near that ZIP code start with 'zzz'", none["instruction"]!.GetValue<string>());
        Assert.Contains("Homestead, Florida", none["instruction"]!.GetValue<string>());                                // the ZIP's place, in words
        Assert.Equal("zip_not_found", Code(await s.Tool("find_street", new { zip = "00001", partial_street = "maz" })));
        Assert.Equal("bad_zip", Code(await s.Tool("find_street", new { zip = "1220", partial_street = "maz" })));
        Assert.Equal("need_street", Code(await s.Tool("find_street", new { zip = "12207", partial_street = "  " })));
        Assert.Single(streets.Calls);                                                                                  // none of the refused ones searched
    }

    [Fact]
    public async Task Any_failure_of_the_zip_check_or_the_street_search_or_too_many_searches_say_unavailable_and_the_usual_flow_goes_on()
    {
        var zips = new FakePostcodeLookup(); var streets = new FakeStreetSuggest();
        using var s = await Guest(zips, streets);
        streets.Unavailable = true;
        var failed = await s.Tool("find_street", new { zip = "12207", partial_street = "maz" });
        Assert.Equal("street_lookup_unavailable", Code(failed)); Assert.Contains("set_delivery_address", failed["instruction"]!.GetValue<string>());
        streets.Unavailable = false; zips.Unavailable = true;
        using var s2 = await Guest(zips, streets);
        Assert.Equal("street_lookup_unavailable", Code(await s2.Tool("find_street", new { zip = "33034", partial_street = "maz" })));
        Assert.Equal("read_back_needed", Code(await s2.Tool("set_delivery_address", Take(city: "Anywhere", zip: "33034", state: "FL"))));   // and an address is still taken as given
        zips.Unavailable = false;
        using var s3 = await Guest(zips, streets);
        for (var i = 0; i < FindStreetTool.MaxPerCall; i++) await s3.Tool("find_street", new { zip = "12207", partial_street = "m" + i });
        Assert.Equal("street_lookup_unavailable", Code(await s3.Tool("find_street", new { zip = "12207", partial_street = "again" })));
        using var anon = new VoiceScenario(services: sv => { sv.RemoveAll<IPostcodeLookup>(); sv.AddSingleton<IPostcodeLookup>(zips); sv.RemoveAll<IStreetSuggest>(); sv.AddSingleton<IStreetSuggest>(streets); });
        await anon.Dial();
        Assert.Equal("need_identity", Code(await anon.Tool("find_street", new { zip = "12207", partial_street = "maz" })));
    }

    [Fact]
    public async Task Check_address_only_advises_and_uses_the_same_rule_as_the_save()
    {
        using var s = await Guest(null, null);
        var ok = await s.Tool("check_address", new { house_number = "12", street = "Mazel Lane", city = "Albany", state = "NY", zip = "12207" });
        Assert.True(ok["complete"]!.GetValue<bool>());
        var bad = await s.Tool("check_address", new { street = "Mazel Lane", city = "Albany", state = "NY", zip = "1220" });
        Assert.False(bad["complete"]!.GetValue<bool>());
        Assert.Equal([("house_number", "missing"), ("zip", "must be five digits")], bad["missing"]!.AsArray().Select(m => (m!["field"]!.GetValue<string>(), m["problem"]!.GetValue<string>())));
        Assert.Null(s.State().DeliveryAddress);                                                                        // it changes nothing
        Assert.Equal("incomplete_address", Code(await s.Tool("set_delivery_address", new { street = "Mazel Lane", city = "Albany", state = "NY", postal_code = "12207" })));   // the save says the same
    }

    [Fact]
    public async Task A_zip_that_disagrees_is_corrected_read_back_and_never_saved_without_the_callers_yes()
    {
        var zips = new FakePostcodeLookup();
        using var s = await Guest(zips, null);
        var r = await s.Tool("set_delivery_address", Take(city: "Brooklyn"));                                          // 12207 is Albany, New York
        Assert.Equal("read_back_needed", Code(r));
        Assert.Contains("Albany", r["read_back"]!.GetValue<string>());
        Assert.Contains("That ZIP code is Albany, New York", r["message"]!.GetValue<string>());
        Assert.Equal(("Albany", "NY"), (s.State().DeliveryAddress!.City, s.State().DeliveryAddress!.Region));
        Assert.False(s.State().AddressConfirmed);
        Assert.Equal("address_not_confirmed", Code(await s.Tool("get_delivery_options")));
        Assert.Equal("confirmed", Code(await s.Tool("confirm_address")));
        var plain = await s.Tool("set_delivery_address", Take());                                                      // an address that agrees has no message
        Assert.Null(plain["message"]);
    }

    [Fact]
    public async Task An_unknown_zip_is_not_saved_and_after_two_tries_the_address_as_given_goes_on_to_the_read_back_but_an_incomplete_one_never_does()
    {
        using var s = await Guest(new FakePostcodeLookup(), null);
        var first = await s.Tool("set_delivery_address", Take(zip: "00001"));
        Assert.Equal("inconsistent_address", Code(first)); Assert.Contains("does not appear to exist", first["message"]!.GetValue<string>());
        Assert.Null(s.State().DeliveryAddress);
        Assert.Equal("inconsistent_address", Code(await s.Tool("set_delivery_address", Take(zip: "00001"))));
        Assert.Equal(2, s.State().AddressRetries);
        var third = await s.Tool("set_delivery_address", Take(zip: "00001", city: "Brooklyn"));
        Assert.Equal("read_back_needed", Code(third));                                                                 // the caller will not settle: what they gave goes on
        Assert.Equal(("Brooklyn", "00001"), (s.State().DeliveryAddress!.City, s.State().DeliveryAddress!.PostalCode));
        Assert.True(s.State().AddressProceededAfterRetries);
        Assert.False(s.State().AddressConfirmed);                                                                      // still only the usual read-back and the caller's yes
        Assert.Equal("incomplete_address", Code(await s.Tool("set_delivery_address", new { street = "Mazel Lane", city = "Albany", state = "NY", postal_code = "12207" })));
        Assert.Equal("incomplete_address", Code(await s.Tool("set_delivery_address", new { street = "Mazel Lane", city = "Albany", state = "NY", postal_code = "12207" })));
    }

    [Fact]
    public async Task An_unavailable_zip_check_never_rejects_an_address()
    {
        var zips = new FakePostcodeLookup { Unavailable = true };
        using var s = await Guest(zips, null);
        Assert.Equal("read_back_needed", Code(await s.Tool("set_delivery_address", Take(city: "Brooklyn"))));
        Assert.Equal("Brooklyn", s.State().DeliveryAddress!.City);
    }

    [Fact]
    public async Task A_saved_address_is_checked_the_same_way_googles_city_is_what_the_server_reads_back_and_the_callers_yes_saves()
    {
        const string email = "jane@example.com";
        var zips = new FakePostcodeLookup();
        using var s = new VoiceScenario(config: new() { ["Voice:PhonePaymentProvider"] = "fake" }, services: sv => { sv.RemoveAll<IPostcodeLookup>(); sv.AddSingleton<IPostcodeLookup>(zips); });
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(email);
        await s.SetPhone(email, "(555) 123-4567", verified: true);
        var jane = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == email))).Id;
        await s.Dial();
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
        Assert.Equal("incomplete_address", Code(await s.Tool("manage_saved_address", new { action = "add", street = "Mazel Lane", city = "Albany", state = "NY", postal_code = "12207" })));
        Assert.Equal("inconsistent_address", Code(await s.Tool("manage_saved_address", new { action = "add", street = "12 Mazel Lane", city = "Albany", state = "NY", postal_code = "00001" })));
        var r = await s.Tool("manage_saved_address", new { action = "add", street = "12 Mazel Lane", city = "Brooklyn", state = "NY", postal_code = "12207" });
        Assert.Equal("reading_back", Code(r));
        await s.EndStreamAfterGoodbye();
        var said = (await s.AfterStream()).Element("Gather")!.Element("Say")!.Value;
        Assert.Contains("Albany", said); Assert.DoesNotContain("Brooklyn", said);                                       // the server reads Google's city, not what was said
        Assert.Empty(await s.K.Db(db => db.BuyerAddresses.AsNoTracking().Where(a => a.AccountId == jane).ToListAsync()));   // nothing saved yet
        var (resume, resumeToken) = await s.Type("1"); await s.Attach(resumeToken);
        Assert.Equal("Albany", Assert.Single(await s.K.Db(db => db.BuyerAddresses.AsNoTracking().Where(a => a.AccountId == jane).ToListAsync())).City);   // only the caller's yes saved it
        Assert.NotNull(resume);
    }

    [Fact]
    public async Task No_key_no_address_and_no_street_reaches_a_log_in_a_whole_flow_including_failures()
    {
        var zips = new FakePostcodeLookup(); var streets = new FakeStreetSuggest();
        using var s = await Guest(zips, streets);
        await s.Tool("find_street", new { zip = "12207", partial_street = "mazel" });
        await s.Tool("set_delivery_address", Take(city: "Brooklyn"));
        await s.Tool("confirm_address");
        streets.Unavailable = true; zips.Unavailable = true;
        await s.Tool("find_street", new { zip = "33034", partial_street = "oak" });
        var all = string.Join("\n", s.F.Logs.Lines);
        foreach (var piece in new[] { "Mazel", "Albany", "12207", "33034", "Homestead", "oak", "Brooklyn" }) Assert.DoesNotContain(piece, all, StringComparison.OrdinalIgnoreCase);
    }
}
