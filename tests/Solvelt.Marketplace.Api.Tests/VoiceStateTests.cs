using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The per-call server state and the speech-ready text (decision V-02): what survives a task switch, what a change invalidates, how money and dates are said.</summary>
public class VoiceStateTests
{
    private static VoiceCallState Ready()
    {
        var s = new VoiceCallState(Guid.NewGuid());
        s.Identify(CallerLevel.Verified, Guid.NewGuid(), verifiedNow: true);
        s.SetCart("cart-1", 2); s.SetAddress("home"); s.SetDelivery("standard");
        s.SetQuote("q-1", 4500);
        return s;
    }

    [Fact]
    public void A_confirmation_needs_a_read_back_of_a_current_quote_and_nothing_else_confirms()
    {
        var s = Ready();
        Assert.False(s.Confirm());                                   // "yes" before any read-back confirms nothing
        s.ReadBackDone();
        Assert.Equal(ConfirmationStatus.ReadBack, s.Confirmation);
        Assert.True(s.Confirm());
        Assert.Equal(ConfirmationStatus.Confirmed, s.Confirmation);
        Assert.False(new VoiceCallState(Guid.NewGuid()).Confirm());   // no quote at all
    }

    [Theory]
    [InlineData("cart")]
    [InlineData("address")]
    [InlineData("delivery")]
    [InlineData("identity")]
    [InlineData("order")]
    public void Any_change_that_affects_the_order_invalidates_the_quote_and_the_confirmation(string change)
    {
        var s = Ready(); s.ReadBackDone(); Assert.True(s.Confirm());
        switch (change)
        {
            case "cart": s.SetCart("cart-1", 3); break;
            case "address": s.SetAddress("work"); break;
            case "delivery": s.SetDelivery("pickup"); break;
            case "identity": s.Identify(CallerLevel.Guest, null, verifiedNow: false); break;
            default: s.OrderChanged(); break;
        }
        Assert.Null(s.QuoteKey); Assert.Null(s.QuoteTotalCents);
        Assert.Equal(ConfirmationStatus.None, s.Confirmation);
        Assert.False(s.Confirm());                                   // the old yes does not carry over: a fresh read-back is required
    }

    [Fact]
    public void Leaving_a_task_keeps_its_state_and_a_pending_account_change_does_not_touch_the_order()
    {
        var s = Ready(); s.ReadBackDone();
        s.SetTask("order status");
        s.SetPendingAccountChange("new name");
        Assert.Equal((2, "home", "standard", "q-1"), (s.CartItemCount, s.AddressLabel, s.DeliveryChoice, s.QuoteKey));
        Assert.Equal(ConfirmationStatus.ReadBack, s.Confirmation);
        s.SetTask("order");
        Assert.True(s.Confirm());
    }

    [Fact]
    public void A_new_quote_resets_the_confirmation_and_verification_in_this_call_never_goes_away()
    {
        var s = Ready(); s.ReadBackDone(); s.SetQuote("q-2", 4700);
        Assert.Equal(ConfirmationStatus.None, s.Confirmation);
        s.Identify(CallerLevel.Verified, s.AccountId, verifiedNow: false);
        Assert.True(s.VerifiedInThisCall);
    }

    [Fact]
    public void The_summary_says_where_things_stand_and_holds_nothing_secret()
    {
        var s = Ready(); s.SetTask("delivery"); s.SetPendingAccountChange("change the name"); s.ReadBackDone();
        var t = s.Summary();
        Assert.Contains("verified by SMS code in this call", t); Assert.Contains("2 items", t); Assert.Contains("delivery", t);
        Assert.Contains("awaits the caller's yes", t); Assert.Contains("change the name", t);
        Assert.Equal(t, s.Summary());                                // the same text for the same state
        Assert.DoesNotContain("4500", t);                            // no figure: only a tool may give a total
        Assert.DoesNotContain("cart-1", t); Assert.DoesNotContain("q-1", t);
        Assert.Contains("empty", new VoiceCallState(Guid.NewGuid()).Summary());
        Assert.Contains("not identified", new VoiceCallState(Guid.NewGuid()).Summary());
    }

    [Fact]
    public void State_of_a_finished_call_is_dropped_and_old_ones_are_swept()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var store = new VoiceStateStore(clock);
        var a = store.GetOrCreate(Guid.NewGuid()); a.Connected(clock.GetUtcNow());
        Assert.Same(a, store.GetOrCreate(a.CallId));
        clock.Advance(VoiceStateStore.Grace + TimeSpan.FromMinutes(1));
        store.GetOrCreate(Guid.NewGuid());
        Assert.Null(store.Find(a.CallId));
        var b = store.GetOrCreate(Guid.NewGuid()); store.Remove(b.CallId);
        Assert.Null(store.Find(b.CallId));
    }

    [Theory]
    [InlineData(1299, "twelve ninety-nine")]
    [InlineData(1200, "twelve dollars")]
    [InlineData(100, "one dollar")]
    [InlineData(101, "one oh one")]
    [InlineData(5, "five cents")]
    [InlineData(1, "one cent")]
    [InlineData(50000, "five hundred dollars")]
    [InlineData(12345, "one hundred twenty-three forty-five")]
    [InlineData(0, "zero cents")]
    public void Money_is_said_the_way_a_person_says_it(long cents, string said) => Assert.Equal(said, VoiceSpeech.Money(cents));

    [Theory]
    [InlineData("2026-11-17", "Tuesday the seventeenth")]
    [InlineData("2026-11-18", "Wednesday the eighteenth")]
    [InlineData("2026-11-20", "Friday the twentieth")]
    [InlineData("2026-11-21", "Saturday the twenty-first")]
    [InlineData("2026-11-30", "Monday the thirtieth")]
    [InlineData("2026-12-04", "Friday the fourth of December")]
    [InlineData("2026-11-03", "Tuesday the third")]
    [InlineData("2026-11-12", "Thursday the twelfth")]
    public void Dates_are_said_as_a_weekday_and_an_ordinal_never_as_iso(string iso, string said)
        => Assert.Equal(said, VoiceSpeech.Date(DateOnly.Parse(iso), new DateOnly(2026, 11, 17)));

    [Fact]
    public void Order_numbers_are_read_out_in_short_groups() => Assert.Equal("M P, one zero four, nine two", VoiceSpeech.Spelled("MP-10492"));

    [Fact]
    public void Lists_are_cut_to_three_then_offer_more()
    {
        Assert.Equal("", VoiceSpeech.List([]));
        Assert.Equal("a", VoiceSpeech.List(["a"]));
        Assert.Equal("a and b", VoiceSpeech.List(["a", "b"]));
        Assert.Equal("a, b and c", VoiceSpeech.List(["a", "b", "c"]));
        Assert.Equal("a, b and c, and four more if you like", VoiceSpeech.List(["a", "b", "c", "d", "e", "f", "g"]));
    }

    [Theory]
    [InlineData("The blue mug is twelve ninety-nine and ships Tuesday the fourth.", true)]
    [InlineData("See https://solvelt.com/p/1", false)]
    [InlineData("**Total** is here", false)]
    [InlineData("Ships on 2026-12-04", false)]
    [InlineData("It costs $12.99", false)]
    [InlineData("Costs 12.99", false)]
    [InlineData("- item one", true)]
    [InlineData("a | b", false)]
    public void Text_with_markdown_links_iso_dates_or_raw_prices_is_not_speakable(string text, bool speakable) => Assert.Equal(speakable, VoiceSpeech.IsSpeakable(text));
}
