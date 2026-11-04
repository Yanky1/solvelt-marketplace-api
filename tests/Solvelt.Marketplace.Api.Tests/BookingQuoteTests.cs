using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Services;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M4 slice 6: quotes (request, the provider's answer, accept one offered time, pay within the window, decline, retention).</summary>
public sealed class BookingQuoteTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();
    private int _q;

    private object QuoteBody(string location = "customer", string details = "Replace the bathroom tap and two window seals.") => new
    {
        listingId = Service, location, details, phone = "+1 555 000 1111", preferredTimes = new[] { Slot() },
        address = location == "customer" ? new { name = "Jane Brown", line1 = "1 Main St", city = "Brooklyn", region = "NY", postalCode = "11201", country = "US", lat = 40.69, lng = -73.99 } : null,
    };

    private async Task<HttpResponseMessage> AskQuote(object? body = null, string? key = null, string? token = null)
        => await Send(HttpMethod.Post, "/api/v1/quotes", token ?? _jane, body ?? QuoteBody(), key ?? "quote-key-" + (++_q).ToString("D4") + "xx");

    private async Task<BuyerQuote> NewQuote(string? token = null)
    {
        var key = "quote-key-" + (++_q).ToString("D4") + "xx";
        var r = await AskQuote(key: key, token: token);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await _k.Db(db => db.Quotes.AsNoTracking().FirstAsync(x => x.IdempotencyKey == key));
    }

    private static object QRef(BuyerQuote q) => new { quoteId = q.PortalQuoteId, marketplaceQuoteId = q.MarketplaceQuoteId };

    private async Task<(HttpStatusCode Status, string Json)> QuoteEvent(string type, DateTimeOffset at, BuyerQuote q, object? extra = null, string? id = null)
    {
        var data = new Dictionary<string, object?> { ["quote"] = QRef(q) };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) data[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return await Post(_k.Http, Request(_k.F, Body(id ?? $"evt-{type}-{++_ev}", type, at, data)));
    }

    private Task<BuyerQuote> QRow(Guid id) => _k.Db(db => db.Quotes.AsNoTracking().FirstAsync(x => x.Id == id));

    /// <summary>The provider quotes 400.00 for 3 hours with two offered times; the portal event reaches us.</summary>
    private async Task<(BuyerQuote Quote, DateTimeOffset[] Slots)> Quoted(long price = 40000)
    {
        var q = await NewQuote();
        var slots = new[] { Slot(0), Slot(5) };
        _portal.Quote(q.MarketplaceQuoteId, price, 180, slots, Now.AddDays(3));
        var r = await QuoteEvent("quote.sent", Now.AddMinutes(1), q, new { priceCents = price, durationMinutes = 180, slots, providerId = Guid.NewGuid(), expiresAt = Now.AddDays(3), note = "Bring your own tap please", currency = "USD" });
        Assert.Equal("applied", StatusOf(r.Json));
        return (await QRow(q.Id), slots);
    }

    [Fact]
    public async Task A_request_is_one_quote_even_in_parallel_carries_the_relay_identity_and_needs_a_valid_body()
    {
        await Ready();
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AskQuote(key: "same-quote-key-1")));
        Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(1, all.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Single(_portal.Quotes);
        var sent = _portal.Quotes.Single().BodyText;
        Assert.Contains("relay-", sent); Assert.DoesNotContain("jane@example.com", sent); Assert.Contains("1 Main St", sent);
        Assert.Equal(HttpStatusCode.Conflict, (await AskQuote(QuoteBody(details: "A completely different job description."), "same-quote-key-1")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AskQuote(QuoteBody(details: "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AskQuote(new { listingId = Service, location = "customer", details = "Needs an address for sure" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AskQuote(new { listingId = Service, location = "online", details = "A long enough description", preferredTimes = new[] { Now.AddDays(-1) } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AskQuote(new { listingId = CatalogKit.ListingId("challah-classic"), location = "online", details = "A long enough description" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/v1/quotes", null, QuoteBody(), "anon-quote-key-1")).StatusCode);
        Assert.Single(_portal.Quotes);
    }

    [Fact]
    public async Task At_most_ten_open_requests_per_buyer()
    {
        await Ready();
        for (var i = 0; i < 10; i++) Assert.True((await AskQuote()).IsSuccessStatusCode);
        var r = await AskQuote();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("too_many_quotes", await Code(r));
    }

    [Fact]
    public async Task The_providers_quote_shows_the_portals_numbers_mails_the_buyer_once_and_a_newer_quote_replaces_an_older_one_in_any_order()
    {
        await Ready();
        var q = await NewQuote();
        var before = (await _k.Mail("jane@example.com")).Count;
        var slots2 = new[] { Slot(1) };
        // the newer quote arrives first, the older one later: the newer wins
        Assert.Equal("applied", StatusOf((await QuoteEvent("quote.sent", Now.AddMinutes(5), q, new { priceCents = 45000, durationMinutes = 120, slots = slots2, expiresAt = Now.AddDays(2) })).Json));
        Assert.Equal("skipped", StatusOf((await QuoteEvent("quote.sent", Now.AddMinutes(1), q, new { priceCents = 99999, durationMinutes = 60, slots = new[] { Slot(2) }, expiresAt = Now.AddDays(2) })).Json));
        await Drain();
        var row = await QRow(q.Id);
        Assert.Equal(QuoteStatus.Quoted, row.Status); Assert.Equal(45000, row.PriceCents); Assert.Equal(120, row.DurationMinutes);
        var view = await Json(await Send(HttpMethod.Get, $"/api/v1/quotes/{q.Id}", _jane));
        Assert.Equal(45000, view.GetProperty("priceCents").GetInt64()); Assert.Equal(1, view.GetProperty("slots").GetArrayLength());
        var mails = await _k.Mail("jane@example.com");
        Assert.Equal(before + 1, mails.Count);
        Assert.Contains($"/account/quotes/{q.Id}", mails.Last().TextBody);
        // incomplete or absurd quotes change nothing
        Assert.Equal("skipped", StatusOf((await QuoteEvent("quote.sent", Now.AddMinutes(9), q, new { priceCents = 12.5, durationMinutes = 60, slots = slots2 })).Json));
        Assert.Equal(45000, (await QRow(q.Id)).PriceCents);
        // other buyers never see it
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/quotes/{q.Id}", _joe)).StatusCode);
        Assert.Empty((await Json(await Send(HttpMethod.Get, "/api/v1/quotes", _joe))).GetProperty("quotes").EnumerateArray());
    }

    [Fact]
    public async Task An_event_that_beats_the_requests_answer_is_parked_then_applied_and_a_wrong_portal_id_is_ignored()
    {
        await Ready();
        _portal.LoseQuoteAnswer = true;
        var key = "lost-quote-key-1";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AskQuote(key: key)).StatusCode);
        var pending = await _k.Db(db => db.Quotes.AsNoTracking().SingleAsync(x => x.IdempotencyKey == key));
        Assert.Equal(QuoteStatus.Requesting, pending.Status);
        var portalId = _portal.FindQuote(pending.MarketplaceQuoteId)!.QuoteId;
        var parked = await Post(_k.Http, Request(_k.F, Body("q-early", "quote.sent", Now, new { quote = new { quoteId = portalId, marketplaceQuoteId = pending.MarketplaceQuoteId }, priceCents = 30000, durationMinutes = 90, slots = new[] { Slot() }, expiresAt = Now.AddDays(2) })));
        Assert.Equal("parked", StatusOf(parked.Json));
        _portal.LoseQuoteAnswer = false;
        Assert.True((await AskQuote(key: key)).IsSuccessStatusCode);          // the retry
        var row = await QRow(pending.Id);
        Assert.Equal(QuoteStatus.Quoted, row.Status); Assert.Equal(30000, row.PriceCents); Assert.Equal(portalId, row.PortalQuoteId);
        var wrong = await Post(_k.Http, Request(_k.F, Body("q-wrong", "quote.declined", Now.AddMinutes(9), new { quote = new { quoteId = Guid.NewGuid(), marketplaceQuoteId = pending.MarketplaceQuoteId } })));
        Assert.Equal("skipped", StatusOf(wrong.Json));
        Assert.Equal(QuoteStatus.Quoted, (await QRow(pending.Id)).Status);
    }

    [Fact]
    public async Task Accepting_one_offered_time_makes_one_booking_at_the_portals_price_and_parallel_accepts_are_one_booking()
    {
        await Ready();
        _portal.QuoteTravelFeeCents = 1500;
        var (q, slots) = await Quoted();
        var body = new { start = slots[0], priceCents = 1, totalCents = 1, addOnIds = Array.Empty<string>() };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = Slot(9) })).StatusCode);   // not offered
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _joe, body)).StatusCode);
        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, body)));
        Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        var ids = new HashSet<Guid>();
        foreach (var r in all) ids.Add((await Json(r)).GetProperty("booking").GetProperty("id").GetGuid());
        Assert.Single(ids);
        Assert.Equal(1, await _k.Db(db => db.Bookings.CountAsync(b => b.QuoteId == q.Id && b.Status != BookingStatus.Failed)));
        Assert.Single(_portal.Holds);
        var b = await Row(ids.Single());
        Assert.Equal(BookingStatus.Confirmed, b.Status);
        Assert.Equal(41500, b.TotalCents);                                   // 400.00 quote + 15.00 travel: the portal's number, not the client's 1
        Assert.Equal(Now.AddMinutes(30), b.PaymentDueAt);
        Assert.Equal(QuoteStatus.Accepted, (await QRow(q.Id)).Status);
        Assert.DoesNotContain("priceCents", _portal.Holds.Single().BodyText);
        var callsBefore = _portal.QuoteAcceptCalls;
        var again = await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, body);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(callsBefore, _portal.QuoteAcceptCalls);                 // a later tap does not call the portal again (the racers above may have: the portal is idempotent on our booking id)
    }

    [Fact]
    public async Task An_expired_or_refused_acceptance_leaves_nothing_half_made_and_a_retry_is_a_new_attempt()
    {
        await Ready();
        var (q, slots) = await Quoted();
        _portal.AcceptFailNext = (HttpStatusCode.Conflict, "slot_unavailable");
        var taken = await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = slots[0] });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("slot_unavailable", await Code(taken));
        Assert.Equal(QuoteStatus.Quoted, (await QRow(q.Id)).Status);          // the quote stays open: choose another time
        Assert.Equal(1, await _k.Db(db => db.Bookings.CountAsync(b => b.QuoteId == q.Id && b.Status == BookingStatus.Failed)));
        var ok = await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = slots[1] });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        // an expired quote cannot be accepted
        var (q2, s2) = await Quoted();
        _k.F.Clock.Advance(TimeSpan.FromDays(4));
        var late = await Send(HttpMethod.Post, $"/api/v1/quotes/{q2.Id}/accept", _jane, new { start = s2[0] });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("quote_expired", await Code(late));
    }

    [Fact]
    public async Task A_quote_booking_is_paid_within_the_window_without_a_second_confirm_and_the_sale_is_reported_once()
    {
        await Ready();
        var (q, slots) = await Quoted();
        var accepted = await Json(await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = slots[0] }));
        var id = accepted.GetProperty("booking").GetProperty("id").GetGuid();
        var co = await Checkout(id);
        Assert.True(co.IsSuccessStatusCode, await co.Content.ReadAsStringAsync());
        var b = await Row(id);
        var intent = IntentOf(b.MarketplaceBookingId);
        Assert.Equal(40000, intent.AmountCents);
        Stripe.Capture(intent.Id);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, 40000));
        await Drain();
        Assert.Equal(0, _portal.ConfirmCalls);                               // already confirmed when the quote was accepted
        Assert.Single(_portal.PaymentEvents);
        Assert.Contains("\"kind\":\"booking\"", _portal.PaymentEvents[0]);
        Assert.Null((await Row(id)).PaymentDueAt);
        // the unpaid timer no longer applies
        _k.F.Clock.Advance(TimeSpan.FromHours(2));
        using var scope = _k.F.Services.CreateScope();
        var res = await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
        Assert.Equal(0, res.Cancelled);
        Assert.Equal(BookingStatus.Confirmed, (await Row(id)).Status);
    }

    [Fact]
    public async Task An_unpaid_quote_booking_is_cancelled_after_the_window_and_a_late_payment_is_refunded()
    {
        await Ready();
        var (q, slots) = await Quoted();
        var accepted = await Json(await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = slots[0] }));
        var id = accepted.GetProperty("booking").GetProperty("id").GetGuid();
        Assert.True((await Checkout(id)).IsSuccessStatusCode);
        var intent = IntentOf((await Row(id)).MarketplaceBookingId);
        _k.F.Clock.Advance(TimeSpan.FromMinutes(31));
        using (var scope = _k.F.Services.CreateScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
            Assert.Equal(1, res.Cancelled);
        }
        var b = await Row(id);
        Assert.Equal(BookingStatus.CancelledByBuyer, b.Status);
        Assert.Contains("cancel-unpaid-", _portal.Holds.Single().RequestIds.Single());
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(id)).StatusCode);   // past the window and closed
        // the buyer pays anyway from an open payment page: the money is returned, never kept
        Stripe.Capture(intent.Id);
        await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, 40000));
        await Drain();
        Assert.Equal(40000, Assert.Single(Stripe.Refunds).AmountCents);
        Assert.Empty(_portal.PaymentEvents);
    }

    [Fact]
    public async Task Declining_and_the_providers_own_decline_or_expiry_are_final()
    {
        await Ready();
        var (q, slots) = await Quoted();
        var d = await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/decline", _jane, new { reason = "too expensive" });
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(QuoteStatus.Withdrawn, (await QRow(q.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/decline", _jane, new { })).StatusCode);   // repeated
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/accept", _jane, new { start = slots[0] })).StatusCode);
        Assert.Equal("skipped", StatusOf((await QuoteEvent("quote.sent", Now.AddMinutes(30), q, new { priceCents = 1, durationMinutes = 60, slots, expiresAt = Now.AddDays(1) })).Json));
        var (q2, _) = await Quoted();
        Assert.Equal("applied", StatusOf((await QuoteEvent("quote.declined", Now.AddMinutes(40), q2, new { reason = "can't do it", by = "seller" })).Json));
        Assert.Equal(QuoteStatus.Declined, (await QRow(q2.Id)).Status);
        Assert.Equal("skipped", StatusOf((await QuoteEvent("quote.sent", Now.AddMinutes(50), q2, new { priceCents = 1, durationMinutes = 60, slots, expiresAt = Now.AddDays(1) })).Json));
        var (q3, _) = await Quoted();
        Assert.Equal("applied", StatusOf((await QuoteEvent("quote.expired", Now.AddMinutes(60), q3)).Json));
        Assert.Equal(QuoteStatus.Expired, (await QRow(q3.Id)).Status);
    }

    [Fact]
    public async Task A_quote_requests_personal_data_is_wiped_30_days_after_it_ended()
    {
        await Ready();
        var (q, _) = await Quoted();
        await Send(HttpMethod.Post, $"/api/v1/quotes/{q.Id}/decline", _jane, new { });
        var open = await NewQuote();                                          // still open: keeps its data
        using (var scope = _k.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
        Assert.NotNull((await QRow(q.Id)).AddrLine1);                          // not yet
        _k.F.Clock.Advance(TimeSpan.FromDays(31));
        using (var scope = _k.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
        var r = await QRow(q.Id);
        Assert.Null(r.AddrLine1); Assert.Null(r.Phone); Assert.Equal("", r.Details); Assert.Null(r.PreferredTimesJson); Assert.NotNull(r.RedactedAt);
        Assert.Equal(40000, r.PriceCents);                                     // numbers stay
        Assert.NotNull((await QRow(open.Id)).AddrLine1);
    }
}
