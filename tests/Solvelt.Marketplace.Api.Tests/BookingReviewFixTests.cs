using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Services;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The independent review of f9aa676..e766c74 (booking money): H1/H2 deferred refunds, M3 sale reports, M4/M5 refunds that ignore disputes, L6-L9 (decision M4-22).</summary>
public sealed class BookingReviewFixTests : BookingFlowBase
{
    private DateTimeOffset Now => _k.F.Clock.GetUtcNow();
    private long T0 => Now.ToUnixTimeSeconds();

    private Task<HttpResponseMessage> StripeObject(string type, object obj, long? created = null)
        => PostStripe(JsonSerializer.Serialize(new { id = $"evt_r{++_ev}", type, created = created ?? T0, data = new { @object = obj } }));

    private async Task<long> ReserveOrDefer(Guid paymentId, long cents, string key, string reason = "test")
    {
        using var scope = _k.F.Services.CreateScope();
        var rest = await scope.ServiceProvider.GetRequiredService<BookingRefundService>().ReserveOrDeferAsync(paymentId, cents, key, reason, false, NullLogger.Instance, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();   // a no-op when the service already saved
        return rest;
    }

    private async Task<(Booking Booking, FakePaymentProvider.Intent Intent)> PaidNotConfirmed()
    {
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        Assert.True((await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, intent.AmountCents))).IsSuccessStatusCode);
        return (b, intent);
    }

    private Task<List<BookingRefund>> Refunds() => _k.Db(db => db.BookingRefunds.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync());

    // ---------- H1: a deferred refund is paid once ----------

    [Fact]
    public async Task H1_a_replayed_refund_that_was_reserved_is_not_deferred_and_paid_again_when_the_dispute_is_won()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        var pay = await Pay(b.Id);
        Assert.Equal(0, await ReserveOrDefer(pay.Id, 5000, "K1"));                                           // 5000 under K1
        await StripeObject("charge.dispute.created", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "needs_response" }, T0 + 1);
        await ReserveOrDefer(pay.Id, 5000, "K1");                                                            // the same refund replayed while the dispute is open
        await StripeObject("charge.dispute.closed", new { id = "dp_1", payment_intent = intent.Id, amount = 24000, status = "won" }, T0 + 5);
        await Drain();
        Assert.Equal(5000, Stripe.Refunds.Sum(r => r.AmountCents));                                          // the buyer gets 5000 for a 5000 refund
        Assert.Equal(5000, (await Pay(b.Id)).RefundedCents);
        Assert.DoesNotContain(await Refunds(), r => r.Key.EndsWith(":deferred"));
    }

    [Fact]
    public async Task H1_a_refund_that_was_wholly_deferred_and_then_released_is_not_reserved_again_by_a_replay()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        var pay = await Pay(b.Id);
        await StripeObject("charge.dispute.created", new { id = "dp_2", payment_intent = intent.Id, amount = 24000, status = "needs_response" }, T0 + 1);
        Assert.Equal(5000, await ReserveOrDefer(pay.Id, 5000, "K2"));                                        // everything is deferred: no row under K2
        await StripeObject("charge.dispute.closed", new { id = "dp_2", payment_intent = intent.Id, amount = 24000, status = "won" }, T0 + 5);
        await Drain();
        await ReserveOrDefer(pay.Id, 5000, "K2");                                                            // replay after the release
        await Drain();
        Assert.Equal(5000, Stripe.Refunds.Sum(r => r.AmountCents));
        Assert.Equal(5000, (await Pay(b.Id)).RefundedCents);
    }

    [Fact]
    public async Task H1_parallel_identical_requests_reserve_and_defer_once()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        var pay = await Pay(b.Id);
        await StripeObject("charge.dispute.created", new { id = "dp_3", payment_intent = intent.Id, amount = 20000, status = "needs_response" }, T0 + 1);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReserveOrDefer(pay.Id, 10000, "K3")));
        var rows = await Refunds();
        Assert.Equal(4000, rows.Where(r => r.Key == "K3").Sum(r => r.AmountCents));
        Assert.Equal(6000, rows.Where(r => r.Key == "K3:deferred").Sum(r => r.AmountCents));
        Assert.Equal(4000, (await Pay(b.Id)).RefundedCents);
    }

    // ---------- H2: nothing stays deferred ----------

    [Fact]
    public async Task H2_a_deferred_refund_whose_dispute_is_already_over_is_released_by_the_upkeep()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        var pay = await Pay(b.Id);
        await StripeObject("charge.dispute.created", new { id = "dp_4", payment_intent = intent.Id, amount = 24000, status = "needs_response" }, T0 + 1);
        await ReserveOrDefer(pay.Id, 5000, "K4");
        Assert.Contains(await Refunds(), r => r.Status == RefundStatus.Deferred);
        // the release was lost (the dispute-won event committed between the refund's read and its insert): the hold is gone, the row is still deferred
        await _k.Db(db => db.BookingPayments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisputedCents, 0L)));
        using (var scope = _k.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
        await Drain();
        Assert.DoesNotContain(await Refunds(), r => r.Status == RefundStatus.Deferred);
        Assert.Equal(5000, Stripe.Refunds.Sum(r => r.AmountCents));
    }

    // ---------- M3: a sale is reported only for a payment that is still captured ----------

    [Fact]
    public async Task M3_a_payment_voided_while_the_portal_confirms_is_not_reported_as_a_sale_and_the_portal_booking_is_cancelled()
    {
        await Ready();
        var (b, _) = await PaidNotConfirmed();
        _portal.OnConfirmed = () => _k.Db(db => db.BookingPayments.Where(p => p.BookingId == b.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Void))).GetAwaiter().GetResult();
        await Drain();
        Assert.False((await Pay(b.Id)).ReportedToPortal);
        Assert.Empty(_portal.PaymentEvents);
        Assert.Contains(_portal.Holds.Single().RequestIds, r => r.StartsWith("cancel-late-"));
    }

    [Fact]
    public async Task M3_a_quote_booking_payment_voided_before_it_is_reported_is_not_reported_as_a_sale()
    {
        await Ready();
        var b = await NewHold();
        Assert.True((await Checkout(b.Id)).IsSuccessStatusCode);
        await _k.Db(db => db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentDueAt, (DateTimeOffset?)Now.AddMinutes(30))));   // a booking made from an accepted quote
        var intent = IntentOf(b.MarketplaceBookingId);
        Stripe.Capture(intent.Id);
        _k.F.Services.GetRequiredService<BookingHooks>().BeforeSaleReport = () => _k.Db(db => db.BookingPayments.Where(p => p.BookingId == b.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, BookingPaymentStatus.Void)));
        Assert.True((await PostStripe(StripeEvent("payment_intent.succeeded", intent.Id, b.MarketplaceBookingId, intent.AmountCents))).IsSuccessStatusCode);
        await Drain();
        Assert.False((await Pay(b.Id)).ReportedToPortal);
        Assert.Empty(_portal.PaymentEvents);
        Assert.Contains(_portal.Holds.Single().RequestIds, r => r.StartsWith("cancel-late-"));
    }

    // ---------- M4 / M5: refunds respect an open dispute ----------

    [Fact]
    public async Task M4_a_confirm_refusal_with_a_dispute_open_refunds_what_can_go_back_and_defers_the_rest()
    {
        await Ready();
        var (b, intent) = await PaidNotConfirmed();
        await StripeObject("charge.dispute.created", new { id = "dp_5", payment_intent = intent.Id, amount = 10000, status = "needs_response" }, T0 + 1);
        _portal.ConfirmFailNext = (System.Net.HttpStatusCode.Conflict, "hold_expired");
        await Drain();
        Assert.Equal(14000, Assert.Single(Stripe.Refunds).AmountCents);                                      // not nothing
        Assert.Equal(10000, Assert.Single(await Refunds(), r => r.Status == RefundStatus.Deferred).AmountCents);
        Assert.DoesNotContain("in full", (await _k.Mail("jane@example.com")).Last().TextBody);              // the buyer is not told "in full" when part is held
    }

    [Fact]
    public async Task M5_a_failed_refund_that_is_re_issued_while_a_dispute_is_open_is_reserved_and_deferred_not_lost()
    {
        await Ready();
        var b = await PaidBooking();
        var intent = IntentOf(b.MarketplaceBookingId);
        await PortalEvent("booking.cancelled", Now.AddMinutes(1), b, new { cancelledBy = "seller", refundDueCents = 24000 });
        await Drain();
        var refund = Assert.Single(Stripe.Refunds);
        await StripeObject("charge.dispute.created", new { id = "dp_6", payment_intent = intent.Id, amount = 10000, status = "needs_response" }, T0 + 1);
        Assert.True((await StripeObject("charge.refund.updated", new { id = refund.Id, status = "failed" })).IsSuccessStatusCode);
        var rows = await Refunds();
        Assert.Equal(14000, Assert.Single(rows, r => r.Key.EndsWith(":retry1")).AmountCents);
        Assert.Equal(10000, Assert.Single(rows, r => r.Key.EndsWith(":retry1:deferred")).AmountCents);
    }

    // ---------- L7: the eraser, quote fields ----------

    [Fact]
    public async Task L7_deleting_the_account_redacts_a_finished_quotes_phone_and_preferred_times_too()
    {
        await Ready();
        var account = await _k.Db(db => db.BuyerAccounts.AsNoTracking().Where(a => a.Email == "jane@example.com").Select(a => a.Id).SingleAsync());
        var qid = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.Quotes.Add(new BuyerQuote
            {
                Id = qid, MarketplaceQuoteId = "mq-erase-1", AccountId = account, IdempotencyKey = "erase-key-0001", RequestHash = "h", ListingId = Service, Status = QuoteStatus.Declined, Location = "customer",
                Details = "Please bring a ladder", PreferredTimesJson = "[\"2030-01-01T10:00:00+00:00\"]", Note = "note", Phone = "+1 555 123 4567", Currency = "USD",
                AddrName = "Jane Brown", AddrLine1 = "1 Main St", AddrCity = "Austin", AddrCountry = "US", CreatedAt = Now, UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        using (var scope = _k.F.Services.CreateScope()) await new BookingsAccountEraser(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), _k.F.Clock).EraseAsync(account, default);
        var q = await _k.Db(db => db.Quotes.AsNoTracking().SingleAsync(x => x.Id == qid));
        Assert.Null(q.Phone); Assert.Null(q.PreferredTimesJson); Assert.Null(q.Note); Assert.Null(q.AddrLine1); Assert.Null(q.AddrName);
        Assert.Equal("", q.Details);
        Assert.NotNull(q.RedactedAt);
    }

    [Fact]
    public async Task O23_an_open_quote_keeps_its_data_when_the_account_is_deleted_and_is_wiped_by_the_sweep_once_it_ends()
    {
        await Ready();
        var account = await _k.Db(db => db.BuyerAccounts.AsNoTracking().Where(a => a.Email == "jane@example.com").Select(a => a.Id).SingleAsync());
        var qid = Guid.NewGuid();
        await _k.Db(async db =>
        {
            db.Quotes.Add(new BuyerQuote { Id = qid, MarketplaceQuoteId = "mq-open-1", AccountId = account, IdempotencyKey = "open-key-0001", RequestHash = "h", ListingId = Service, Status = QuoteStatus.Quoted, Location = "online",
                Details = "Please bring a ladder", Phone = "+1 555 123 4567", Currency = "USD", CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Send(HttpMethod.Post, "/api/v1/account/delete", _jane, new { password = Solvelt.Marketplace.Api.Tests.Infrastructure.AuthKit.Password })).StatusCode);
        await Drain();
        Assert.Equal("+1 555 123 4567", (await _k.Db(db => db.Quotes.AsNoTracking().SingleAsync(x => x.Id == qid))).Phone);       // still open: identified
        await _k.Db(db => db.Quotes.Where(x => x.Id == qid).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QuoteStatus.Expired)));
        using (var scope = _k.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BookingMaintenance>().RunAsync();
        var q = await _k.Db(db => db.Quotes.AsNoTracking().SingleAsync(x => x.Id == qid));
        Assert.Null(q.Phone); Assert.Equal("", q.Details); Assert.NotNull(q.RedactedAt);
    }

    // ---------- L8: the .ics file name ----------

    [Fact]
    public async Task L8_the_calendar_file_name_contains_only_letters_digits_and_dashes()
    {
        await Ready();
        var b = await PaidBooking();
        await _k.Db(db => db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Number, "BK-2026\";x=1/../0001")));
        var r = await Send(HttpMethod.Get, $"/api/v1/bookings/{b.Id}/calendar.ics", _jane);
        var name = r.Content.Headers.ContentDisposition!.FileName!.Trim('"');
        Assert.Matches("^booking-[A-Za-z0-9-]+\\.ics$", name);
    }

    // ---------- the options cache bound ----------

    [Fact]
    public async Task The_service_options_cache_never_holds_more_than_its_bound()
    {
        await Ready();
        var cache = _k.F.Services.GetRequiredService<ServiceOptionsCache>();
        for (var i = 0; i < ServiceOptionsCache.MaxEntries + 120; i++) await cache.GetAsync(Guid.NewGuid(), CancellationToken.None);   // the portal answers 404: cached as "no options"
        Assert.True(cache.Count <= ServiceOptionsCache.MaxEntries, cache.Count.ToString());
        Assert.True(cache.Count > ServiceOptionsCache.MaxEntries - 1000);                                    // and it is trimmed, not emptied
    }
}
