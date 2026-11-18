using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Voice;

public enum PlaceStatus { Placed, PriceChanged, LimitReached, NotReady, Unavailable }
public enum SettleStatus { Paid, AlreadyPaid, Declined, Failed, Mismatch, NotApplied, Unknown, Pending }

/// <summary>
/// Turns a confirmed read-back into an order and, once a provider reports a payment, into a PAID order (decision V-06). The order goes through the EXISTING checkout (<see cref="CheckoutService"/>:
/// the same quote, quote hash, idempotency and one-payable-order-per-cart rules), never around it. A payment is believed only after the server compares what the provider says with its own order:
/// the amount paid must equal the order total and the invoice must be the order number.
/// </summary>
public sealed class PhoneOrderService(CheckoutService checkout, MarketplaceDb db, VoiceCallStore calls, IOptions<VoiceOptions> options, IOptions<MarketplaceOptions> marketplace,
    IOptions<StripeOptions> stripe, FakePaymentProvider fakeStripe, StripeEventProcessor stripeEvents, Microsoft.Extensions.Hosting.IHostEnvironment env, TimeProvider time, ILogger<PhoneOrderService> log)
{
    public async Task<(PlaceStatus Status, string? OrderNumber, long TotalCents, string? Intent)> PlaceAsync(VoiceCallState s, VoiceCall call, CancellationToken ct)
    {
        if (s.Confirmation != ConfirmationStatus.Confirmed || s.QuoteKey is null || s.DeliveryAddress is null) return (PlaceStatus.NotReady, null, 0, null);
        var o = options.Value;
        if (call.CallerKey is { } key && await calls.OrdersTodayAsync(key, ct) >= Math.Max(1, o.MaxOrdersPerDay)) return (PlaceStatus.LimitReached, null, 0, null);
        CheckoutActor actor;
        if (s.Level == CallerLevel.Verified && s.VerifiedInThisCall && s.AccountId is { } id)
        {
            var acct = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == id && a.DeletedAt == null).Select(a => new { a.DisplayName, a.RelayEmail }).FirstOrDefaultAsync(ct);
            if (acct is null) return (PlaceStatus.NotReady, null, 0, null);
            actor = new CheckoutActor(id, null, null, acct.DisplayName, acct.RelayEmail);
        }
        else
        {
            if (s.Level != CallerLevel.Guest || !s.VerifiedInThisCall || s.CartToken is null || s.GuestEmail is null || s.GuestName is null) return (PlaceStatus.NotReady, null, 0, null);
            actor = new CheckoutActor(null, s.CartToken, s.GuestEmail, s.GuestName, RelayIdentity.NewRelayEmail(marketplace.Value.RelayDomain ?? throw new InvalidOperationException("Marketplace:RelayDomain is not configured.")));
        }
        var (status, result, _) = await checkout.PlaceAsync(actor, new CheckoutRequest(s.DeliveryAddress, s.Selections, null, s.QuoteKey, s.GuestEmail), ct);
        switch (status)
        {
            case CheckoutStatus.Ok:
                if (result!.Quote.TotalCents != s.QuoteTotalCents) return (PlaceStatus.PriceChanged, null, 0, null);   // the quote the caller heard is not the order: never charge another amount
                var intent = await db.Orders.AsNoTracking().Where(x => x.Number == result.OrderNumber).Select(x => x.PaymentIntentId).FirstOrDefaultAsync(ct);
                return (PlaceStatus.Placed, result.OrderNumber, result.Quote.TotalCents, intent);
            case CheckoutStatus.PriceChanged: return (PlaceStatus.PriceChanged, null, 0, null);
            default: return (PlaceStatus.Unavailable, null, 0, null);
        }
    }

    /// <summary>Opens the persisted attempt for an order: exactly the amount the server priced. It outlives the call (a text-link payment can finish after it).</summary>
    public async Task<VoicePaymentAttempt> OpenAttemptAsync(Guid callId, string orderNumber, long amountCents, string method, CancellationToken ct)
    {
        var a = new VoicePaymentAttempt { Id = Guid.NewGuid(), CallId = callId, OrderNumber = orderNumber, Method = method, AmountCents = amountCents, Currency = "USD", CreatedAt = time.GetUtcNow() };
        db.VoicePaymentAttempts.Add(a);
        await db.SaveChangesAsync(ct);
        return a;
    }

    /// <summary>
    /// A provider reports an outcome for an attempt. A payment is believed only when the amount EQUALS the attempt's amount (our order total) and the invoice IS our order number; a different amount
    /// (a customer who edited a payment link) or invoice is recorded as <c>mismatch</c> and logged for a person, never applied. A paid attempt marks the order paid through the SAME path a Stripe
    /// payment takes, allowed ONLY with the in-process fake money (no Stripe key, development or test environment): anywhere else it refuses (<see cref="SettleStatus.NotApplied"/>), so no real money
    /// can be taken for an order that this API would not then fulfil. Repeats (the provider's transaction reference is unique) change nothing.
    /// </summary>
    public async Task<SettleStatus> SettleAsync(Guid attemptId, PhonePaymentOutcome outcome, CancellationToken ct)
    {
        var attempt = await db.VoicePaymentAttempts.FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is null) return SettleStatus.Unknown;
        if (attempt.Status == "paid")
        {
            if (outcome.Status == PhonePaymentStatus.Paid && !string.IsNullOrEmpty(outcome.TransactionId) && !string.Equals(outcome.TransactionId, attempt.ProviderRef, StringComparison.Ordinal))
                await RecordDuplicateAsync(attempt, outcome, ct);   // the same link was paid twice: the customer was charged twice
            return SettleStatus.AlreadyPaid;
        }
        if (attempt.Status != "pending") return attempt.Status == "mismatch" ? SettleStatus.Mismatch : SettleStatus.Failed;
        if (outcome.Status == PhonePaymentStatus.Pending) return SettleStatus.Pending;   // the provider's report has not arrived yet: the attempt stays open, nothing is closed or applied
        var now = time.GetUtcNow();
        async Task<SettleStatus> Close(string status, SettleStatus result)
        {
            attempt.Status = status; attempt.SettledAt = now; attempt.ProviderRef ??= outcome.TransactionId;
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); return SettleStatus.AlreadyPaid; }   // the same provider reference was already settled
            return result;
        }
        if (outcome.Status == PhonePaymentStatus.Declined) return await Close("declined", SettleStatus.Declined);
        if (outcome.Status != PhonePaymentStatus.Paid) return await Close("failed", SettleStatus.Failed);
        if (outcome.PaidCents != attempt.AmountCents || !string.Equals(outcome.InvoiceRef, attempt.OrderNumber, StringComparison.Ordinal))
        {
            log.LogError("A phone payment for order {Number} (attempt {Attempt}) does not match the order: the provider took {Paid} cents where {Expected} were asked. It is NOT applied and needs a person.", attempt.OrderNumber, attempt.Id, outcome.PaidCents, attempt.AmountCents);
            return await Close("mismatch", SettleStatus.Mismatch);
        }
        var order = await db.Orders.AsNoTracking().Where(x => x.Number == attempt.OrderNumber).Select(x => new { x.TotalCents, x.PaymentIntentId, x.Status, x.Currency }).FirstOrDefaultAsync(ct);
        if (order is null || order.TotalCents != attempt.AmountCents || order.PaymentIntentId is null) return await Close("failed", SettleStatus.Failed);
        if (order.Status != OrderStatus.Paid)
        {
            if (!StartupValidator.IsDevLike(env) || !string.IsNullOrWhiteSpace(stripe.Value.SecretKey)) { log.LogError("A phone payment for order {Number} cannot be recorded: no phone payment provider is connected to real money.", attempt.OrderNumber); return await Close("failed", SettleStatus.NotApplied); }
            fakeStripe.TryCapture(order.PaymentIntentId);
            var evt = StripeEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id = "evt_phone_" + attempt.Id.ToString("N"), type = "payment_intent.succeeded", created = now.ToUnixTimeSeconds(),
                data = new { @object = new { id = order.PaymentIntentId, amount = attempt.AmountCents, amount_received = attempt.AmountCents, currency = order.Currency.ToLowerInvariant(), metadata = new Dictionary<string, string> { ["marketplace_order_id"] = attempt.OrderNumber } } },
            })));
            await stripeEvents.ProcessAsync(evt, ct);
            if (!await db.Orders.AsNoTracking().AnyAsync(x => x.Number == attempt.OrderNumber && x.Status == OrderStatus.Paid, ct)) return await Close("failed", SettleStatus.Failed);   // only PAID counts
        }
        return await Close("paid", SettleStatus.Paid);
    }

    /// <summary>
    /// A second approved sale for an invoice that is already paid (the customer paid the same link twice). Only the first settles the order; the second is kept as its own row
    /// (status <c>duplicate</c>, keyed by the provider's transaction id so a repeated notification adds nothing) and logged as an error, so a person refunds or voids it in the provider's dashboard.
    /// Nothing is refunded automatically: that needs the provider's transaction API.
    /// </summary>
    private async Task RecordDuplicateAsync(VoicePaymentAttempt paid, PhonePaymentOutcome outcome, CancellationToken ct)
    {
        if (await db.VoicePaymentAttempts.AnyAsync(a => a.ProviderRef == outcome.TransactionId, ct)) return;
        var now = time.GetUtcNow();
        db.VoicePaymentAttempts.Add(new VoicePaymentAttempt
        {
            Id = Guid.NewGuid(), CallId = paid.CallId, OrderNumber = paid.OrderNumber, Method = paid.Method, AmountCents = outcome.PaidCents, Currency = paid.Currency,
            Status = "duplicate", ProviderRef = outcome.TransactionId, CreatedAt = now, SettledAt = now,
        });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); return; }   // recorded by a parallel notification
        log.LogError("Order {Number} was paid twice: attempt {Attempt} settled it, and a second approved sale ({Paid} cents, transaction {Ref}) was taken for the same invoice. It needs a refund or void by a person.", paid.OrderNumber, paid.Id, outcome.PaidCents, outcome.TransactionId);
    }

    /// <summary>Brings the call's state up to date with the persisted attempt (a text-link payment can be settled by a webhook while the call goes on, or after it).</summary>
    public async Task<bool> RefreshAsync(VoiceCallState state, CancellationToken ct)
    {
        if (state.Payment is not { } p || !Guid.TryParseExact(p.AttemptId, "N", out var id)) return state.OrderNumber is not null;
        var row = await db.VoicePaymentAttempts.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).FirstOrDefaultAsync(ct);
        if (row is not null) p.Status = row;
        if (row == "paid" && state.OrderNumber is null) { state.SetOrderPlaced(p.OrderNumber); await calls.MarkOrderPlacedAsync(state.CallId, ct); }
        return state.OrderNumber is not null;
    }
}
