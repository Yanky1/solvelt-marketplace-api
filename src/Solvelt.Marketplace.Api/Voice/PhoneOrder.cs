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

    /// <summary>
    /// A provider says an attempt was paid. Accepted only if the amount equals the order total and the invoice is the order number; then the order is marked paid through the SAME path a Stripe
    /// payment takes (the signed success event), so portal ingest, mail and refunds behave as for any order. Nothing real is connected: this runs only with the in-process fake money (no Stripe key,
    /// development or test environment); anywhere else it refuses, and real-money phone orders stay off until the owner's Stripe-or-Sola decision.
    /// </summary>
    public async Task<bool> RecordPaidAsync(PaymentAttempt attempt, PhonePaymentOutcome outcome, CancellationToken ct)
    {
        if (outcome.Status != PhonePaymentStatus.Paid || outcome.PaidCents != attempt.AmountCents || !string.Equals(outcome.InvoiceRef, attempt.OrderNumber, StringComparison.Ordinal))
        {
            log.LogError("A phone payment for order {Number} was refused: the provider's amount or invoice does not match the order.", attempt.OrderNumber);
            return false;
        }
        var order = await db.Orders.AsNoTracking().Where(x => x.Number == attempt.OrderNumber).Select(x => new { x.TotalCents, x.PaymentIntentId, x.Status, x.Number, x.Currency }).FirstOrDefaultAsync(ct);
        if (order is null || order.TotalCents != attempt.AmountCents || order.PaymentIntentId != attempt.IntentId) return false;
        if (order.Status == OrderStatus.Paid) return true;   // a repeated callback of the same attempt
        if (!StartupValidator.IsDevLike(env) || !string.IsNullOrWhiteSpace(stripe.Value.SecretKey)) { log.LogError("A phone payment cannot be recorded: no phone payment provider is connected to real money."); return false; }
        fakeStripe.TryCapture(attempt.IntentId);
        var evt = StripeEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            id = "evt_phone_" + attempt.AttemptId, type = "payment_intent.succeeded", created = time.GetUtcNow().ToUnixTimeSeconds(),
            data = new { @object = new { id = attempt.IntentId, amount = attempt.AmountCents, amount_received = attempt.AmountCents, currency = order.Currency.ToLowerInvariant(), metadata = new Dictionary<string, string> { ["marketplace_order_id"] = attempt.OrderNumber } } },
        })));
        await stripeEvents.ProcessAsync(evt, ct);
        return await db.Orders.AsNoTracking().AnyAsync(x => x.Number == attempt.OrderNumber && x.Status == OrderStatus.Paid, ct);   // only PAID counts: an event the pipeline turned into a failure (amount, currency) is not a placed order
    }
}
