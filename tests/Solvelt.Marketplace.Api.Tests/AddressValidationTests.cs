using System.Text.Json;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The save-time address check (decision V-63), ported from the telephone system's own tests without the carrier half: missing parts, a bad ZIP, a ZIP that disagrees, an unknown ZIP, an unavailable check, Google's spelling.</summary>
public class AddressValidationTests
{
    private static CheckoutAddress Addr(string line1 = "12 Mazel Lane", string? city = "Albany", string? state = "NY", string? zip = "12207", string country = "US")
        => new("Dana Levi", line1, null, city, state, zip, country, null);

    private static Task<AddressValidation> Run(CheckoutAddress a, FakePostcodeLookup? lookup = null, int priorRetries = 0)
    {
        var l = lookup ?? new FakePostcodeLookup();
        return AddressValidator.ValidateAsync(a, priorRetries, l.IsConfigured, (z, ct) => l.FindAsync(z, ct), CancellationToken.None);
    }

    [Fact]
    public async Task Missing_parts_are_reported_before_any_network_call()
    {
        var l = new FakePostcodeLookup();
        var v = await Run(Addr(line1: "Mazel Lane", city: "", state: " "), l);
        Assert.Equal(AddressStatus.Incomplete, v.Status);
        Assert.Equal(["house_number", "city", "state"], v.Missing.Select(m => m.Field));
        Assert.Equal("Still needed: the house number, the city and the state.", v.Message);
        Assert.Empty(l.Calls);                                                                                        // not a single lookup
        Assert.False(v.CanSaveWithoutConfirmation);
        Assert.Equal(["street", "zip"], (await Run(Addr(line1: "12", zip: null), l)).Missing.Select(m => m.Field));    // a line with only a number has no street
        Assert.Empty(l.Calls);
    }

    [Theory]
    [InlineData("1220")]
    [InlineData("abcde")]
    [InlineData("")]
    [InlineData("12 07")]
    public async Task A_malformed_zip_is_rejected_locally_as_incomplete(string zip)
    {
        var l = new FakePostcodeLookup();
        var v = await Run(Addr(zip: zip), l);
        Assert.Equal(AddressStatus.Incomplete, v.Status);
        Assert.Contains(v.Missing, m => m.Field == "zip");
        Assert.Contains("ZIP code", v.Message);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task A_zip_plus_four_counts_by_its_first_five_digits()
    {
        var l = new FakePostcodeLookup();
        var v = await Run(Addr(zip: "12207-1234"), l);
        Assert.Equal(AddressStatus.Valid, v.Status);
        Assert.Equal(["12207"], l.Calls);
        Assert.Equal("12207", AddressShape.ZipFive("12207-1234")); Assert.Null(AddressShape.ZipFive("1220-5")); Assert.Null(AddressShape.ZipFive("12345 6")); Assert.Equal("12207", AddressShape.ZipFive(" 12207 ")); Assert.Equal("12207", AddressShape.ZipFive("122071234"));
    }

    [Fact]
    public async Task A_zip_from_a_different_state_is_caught_and_googles_city_and_state_are_read_back()
    {
        var v = await Run(Addr(city: "asdfewr", state: "NY", zip: "33034"));
        Assert.Equal(AddressStatus.Corrected, v.Status);
        Assert.Contains("Homestead", v.Message);
        Assert.Contains("Read the corrected address back", v.Message);
        Assert.Equal([new AddressCorrection("city", "asdfewr", "Homestead"), new AddressCorrection("state", "NY", "FL")], v.Corrections);
        Assert.Equal(("Homestead", "FL", "12 Mazel Lane", "33034"), (v.Normalized!.City, v.Normalized.Region, v.Normalized.Line1, v.Normalized.PostalCode));
        Assert.False(v.CanSaveWithoutConfirmation);                                                                   // the caller still has to say yes to the corrected address
    }

    [Fact]
    public async Task An_unknown_zip_is_caught_as_inconsistent()
    {
        var v = await Run(Addr(zip: "00001"));
        Assert.Equal(AddressStatus.Inconsistent, v.Status);
        Assert.Contains("does not appear to exist", v.Message);
        Assert.Contains("zero zero zero zero one", v.Message);
        Assert.Null(v.Normalized);
    }

    [Fact]
    public async Task A_failing_or_unavailable_zip_check_falls_through_instead_of_rejecting()
    {
        var l = new FakePostcodeLookup { Unavailable = true };
        var v = await Run(Addr(city: "Anywhere", zip: "33034"), l);
        Assert.Equal(AddressStatus.Unverified, v.Status);
        Assert.True(v.CanSaveWithoutConfirmation);
        Assert.Equal("Anywhere", v.Normalized!.City);                                                                 // nothing is changed
        Assert.Equal(["33034"], l.Calls);
    }

    [Fact]
    public async Task With_no_lookup_at_all_the_address_is_still_saveable_and_a_foreign_address_is_not_checked()
    {
        var off = new FakePostcodeLookup { IsConfigured = false };
        var v = await Run(Addr(city: "Anywhere", zip: "33034"), off);
        Assert.Equal(AddressStatus.Unverified, v.Status); Assert.True(v.CanSaveWithoutConfirmation); Assert.Empty(off.Calls);
        var l = new FakePostcodeLookup();
        Assert.Equal(AddressStatus.Unverified, (await Run(Addr(country: "CA", zip: "12345"), l)).Status); Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task A_state_name_and_its_code_are_not_the_same_to_the_check_so_it_is_a_read_back_of_googles_form()
    {
        var l = new FakePostcodeLookup { Answer = _ => new ZipInfo("Albany", "New York", null) };
        var v = await Run(Addr(state: "NY"), l);
        Assert.Equal(AddressStatus.Corrected, v.Status);
        Assert.Equal("New York", v.Normalized!.Region);                                                               // documented: google owns city and state, so the read-back is of its form
        Assert.Equal([new AddressCorrection("state", "NY", "New York")], v.Corrections);
    }

    [Theory]
    [InlineData("New York City", "New York")]
    [InlineData("St. Louis", "Saint Louis")]
    [InlineData("Ft. Worth", "Fort Worth")]
    [InlineData("Buffalo", "Albany")]
    public async Task Googles_spelling_is_adopted_and_read_back(string said, string google)
    {
        var l = new FakePostcodeLookup { Answer = _ => new ZipInfo(google, "NY", null) };
        var v = await Run(Addr(city: said), l);
        Assert.Equal(AddressStatus.Corrected, v.Status);
        Assert.Equal(google, v.Normalized!.City);
        Assert.Equal([new AddressCorrection("city", said, google)], v.Corrections);
        Assert.Contains(google, v.Message);
    }

    [Theory]
    [InlineData("albany", "ALBANY")]
    [InlineData("Albany", "albany")]
    [InlineData("  Albany ", "Albany")]
    public async Task Casing_and_spaces_alone_are_not_a_change(string said, string google)
    {
        var l = new FakePostcodeLookup { Answer = _ => new ZipInfo(google, "NY", null) };
        var v = await Run(Addr(city: said), l);
        Assert.Equal(AddressStatus.Valid, v.Status);
        Assert.Empty(v.Corrections);
        Assert.True(v.CanSaveWithoutConfirmation);
    }

    [Fact]
    public async Task After_two_corrections_the_address_as_given_goes_on_but_never_an_incomplete_one()
    {
        Assert.Equal(2, AddressValidator.MaxAttempts);
        var wrong = Addr(city: "Brooklyn", zip: "33034");
        var first = await Run(wrong, priorRetries: 0); var second = await Run(wrong, priorRetries: 1);
        Assert.False(first.ProceededAfterRetries); Assert.False(second.ProceededAfterRetries);
        Assert.Equal(AddressStatus.Corrected, second.Status);
        var third = await Run(wrong, priorRetries: 2);
        Assert.True(third.ProceededAfterRetries);                                                                     // the caller will not settle: what they gave goes on to the read-back
        Assert.Equal("Brooklyn", third.Normalized!.City); Assert.Equal("", third.Message);
        var unknown = await Run(Addr(zip: "00001"), priorRetries: 2);
        Assert.True(unknown.ProceededAfterRetries); Assert.Equal(AddressStatus.Inconsistent, unknown.Status); Assert.Equal("12 Mazel Lane", unknown.Normalized!.Line1);
        var incomplete = await Run(Addr(line1: "Mazel Lane"), priorRetries: 9);
        Assert.False(incomplete.ProceededAfterRetries); Assert.Equal(AddressStatus.Incomplete, incomplete.Status);   // never for an incomplete address
    }

    [Fact]
    public void The_shape_rule_is_one_function_a_line_is_split_into_house_number_and_street()
    {
        Assert.Equal(("12", "Mazel Lane"), AddressShape.SplitLine("12 Mazel Lane"));
        Assert.Equal(("12B", "Oak Ave"), AddressShape.SplitLine("12B Oak Ave"));
        Assert.Equal(("", "Mazel Lane"), AddressShape.SplitLine("Mazel Lane"));
        Assert.Equal(("12", ""), AddressShape.SplitLine("12"));
        Assert.Equal(("", ""), AddressShape.SplitLine(null));
        Assert.Empty(AddressShape.Check("12", "Mazel Lane", "Albany", "NY", "12207"));
        Assert.Equal(["house_number", "street", "city", "state", "zip"], AddressShape.Check(null, "", " ", null, "").Select(m => m.Field));
        Assert.Equal("must be five digits", Assert.Single(AddressShape.Check("12", "Mazel Lane", "Albany", "NY", "1220")).Problem);
    }

    [Fact]
    public async Task No_message_names_one_of_our_systems_or_a_vendor()
    {
        string[] forbidden = ["api", "database", "lookup", "endpoint", "google", "geocod", "places", "shippo", "tool", "system", "server", "vendor"];
        var l = new FakePostcodeLookup();
        var messages = new List<string>();
        foreach (var a in new[] { Addr(line1: "Mazel Lane"), Addr(city: ""), Addr(state: ""), Addr(zip: ""), Addr(zip: "123"), Addr(line1: "12"), Addr(city: "x", zip: "33034"), Addr(zip: "00001"), Addr(city: "Brooklyn") })
            messages.Add((await Run(a, l)).Message);
        foreach (var m in AddressShape.Check(null, null, null, null, "12").Concat(AddressShape.Check("1", "a", "b", "c", null))) messages.Add(AddressShape.Say([m]));
        Assert.Contains(messages, m => m.Length > 0);
        foreach (var m in messages) foreach (var word in forbidden) Assert.DoesNotContain(word, m, StringComparison.OrdinalIgnoreCase);
    }
}
