using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Staff;

public enum OpsOutcome { Ok, NotFound, NotRetryable, AlreadyQueued }

/// <summary>
/// Orders, payments and queues for staff (decisions M7-10..M7-14). Nothing here talks to Stripe or the portal: a retry only puts a DEAD outbox message back in the queue (or replays a parked
/// inbound event), so the existing idempotent handlers do the work under the same keys they always use.
/// </summary>
public sealed class StaffOpsService(MarketplaceDb db, TimeProvider time, IOptions<StaffOptions> options, Portal.Webhooks.PortalEventProcessor replay)
{
    private DateTimeOffset Now => time.GetUtcNow().ToUniversalTime();

    // ---------- orders and payments ----------

    public async Task<Guid?> FindOrderAsync(Guid? id, string? number, CancellationToken ct)
    {
        if (id is { } g) return await db.Orders.AsNoTracking().Where(o => o.Id == g).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(number) && number.Length <= 40) { var n = number.Trim(); return await db.Orders.AsNoTracking().Where(o => o.Number == n).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct); }
        return null;
    }

    /// <summary>The order as staff see it: no address, no phone, no gift note; the buyer by public name; the payment block only for a role that may see payments.</summary>
    public async Task<object?> OrderViewAsync(Guid id, bool payments, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Groups).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var refunds = await db.OrderRefunds.AsNoTracking().Where(r => r.OrderId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var dead = await DeadRefundMessagesAsync(refunds, ct);
        var lines = await db.OrderLines.CountAsync(l => l.OrderId == id, ct);
        object? payment = null;
        if (payments)
        {
            var sellerNumbers = o.Groups.Select(g => g.SellerOrderNumber).Where(n => n != null).Select(n => ":" + n).ToList();
            var disputeMessages = sellerNumbers.Count == 0 ? [] : await db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey != null && m.DedupeKey.StartsWith("dispute:")).Select(m => new { m.DedupeKey, m.Status }).ToListAsync(ct);
            var eventIds = disputeMessages.Where(m => sellerNumbers.Any(n => m.DedupeKey!.EndsWith(n))).Select(m => (Id: m.DedupeKey!.Split(':')[1], m.Status)).DistinctBy(x => x.Id).ToList();
            var ids = eventIds.Select(e => e.Id).ToList();
            var events = await db.StripeEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => new { e.Id, e.Type, e.ObjectId, e.ReceivedAt }).ToListAsync(ct);
            var disputeIds = events.Select(e => e.ObjectId).Where(x => x != null).Distinct().ToList();
            var states = await db.DisputeStates.AsNoTracking().Where(d => disputeIds.Contains(d.DisputeId)).ToListAsync(ct);
            payment = new
            {
                paymentIntentId = o.PaymentIntentId,
                disputes = events.GroupBy(e => e.ObjectId).Select(gr => new { disputeId = gr.Key, lastEventType = gr.OrderByDescending(e => e.ReceivedAt).First().Type, closed = states.FirstOrDefault(s => s.DisputeId == gr.Key)?.Closed ?? false }),
                refunds = refunds.Select(r => new { id = r.Id, stripeRefundId = r.StripeRefundId, portalRefundId = r.PortalRefundId, key = r.Key }),
            };
        }
        return new
        {
            id = o.Id, number = o.Number, status = o.Status, createdAt = o.CreatedAt, paidAt = o.PaidAt, placedAt = o.PlacedAt, failureCode = o.FailureCode, currency = o.Currency, demo = o.IsDemo,
            itemsCents = o.ItemsCents, shippingCents = o.ShippingCents, taxCents = o.TaxCents, totalCents = o.TotalCents, paidCents = o.PaidCents, refundedCents = o.RefundedCents,
            buyer = new { accountId = o.AccountId, name = Masking.Name(o.BuyerName), guestEmail = Masking.Email(o.GuestEmail), relayEmail = o.RelayEmail, shipCountry = o.ShipCountry },
            lines,
            groups = o.Groups.OrderBy(g => g.SellerOrderNumber).Select(g => new { id = g.Id, sellerOrderNumber = g.SellerOrderNumber, totalCents = g.TotalCents, refundedCents = g.RefundedCents }),
            refunds = refunds.Select(r => new { id = r.Id, amountCents = r.AmountCents, status = r.Status, reason = r.Reason, attempt = r.Attempt, createdAt = r.CreatedAt, updatedAt = r.UpdatedAt, retryable = dead.Contains(r.Id) }),
            payment,
        };
    }

    private static string[] RefundKeys(OrderRefund r) => r.Attempt > 0 ? [$"refund:{r.Key}:{r.Attempt}", $"refund:{r.Key}"] : [$"refund:{r.Key}"];

    /// <summary>The ids of the RESERVED refunds whose outbox message has died (Stripe unreachable for the whole backoff, say): those, and only those, can be retried.</summary>
    private async Task<HashSet<Guid>> DeadRefundMessagesAsync(List<OrderRefund> refunds, CancellationToken ct)
    {
        var reserved = refunds.Where(r => r.Status == RefundStatus.Reserved).ToList();
        if (reserved.Count == 0) return [];
        var keys = reserved.SelectMany(RefundKeys).ToList();
        var deadKeys = (await db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey != null && keys.Contains(m.DedupeKey) && m.Status == OutboxStatus.Dead && m.Payload != null).Select(m => m.DedupeKey!).ToListAsync(ct)).ToHashSet();
        return reserved.Where(r => RefundKeys(r)[0] is var current && deadKeys.Contains(current)).Select(r => r.Id).ToHashSet();
    }

    /// <summary>
    /// Retries a failed refund (decision M7-10): the refund must still be RESERVED (money counted, Stripe never confirmed) and its outbox message dead. The message goes back in the queue and
    /// <see cref="StripeRefundHandler"/> runs it under the same Stripe idempotency key as before, so Stripe sees one refund however often this is clicked. A refund Stripe refused is final (its
    /// counters were given back): a new refund is a money decision made through the portal, not a retry.
    /// </summary>
    public async Task<(OpsOutcome Outcome, string? State)> RetryRefundAsync(StaffPrincipal who, Guid orderId, Guid refundId, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var refund = await db.OrderRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.Id == refundId && r.OrderId == orderId, ct);
        if (refund is null) return (OpsOutcome.NotFound, null);
        if (refund.Status != RefundStatus.Reserved) return (OpsOutcome.NotRetryable, refund.Status);
        var current = RefundKeys(refund)[0];
        var revived = await OutboxAdmin.ReviveDeadAsync(db, m => m.DedupeKey == current, Now, ct);
        if (revived != 1)
        {
            var state = await db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey == current).Select(m => m.Status).FirstOrDefaultAsync(ct);
            return state == OutboxStatus.Pending ? (OpsOutcome.AlreadyQueued, state) : (OpsOutcome.NotRetryable, state ?? "no_message");
        }
        StaffAudit.Add(db, time, who, "refund.retry", "order", orderId.ToString("D"), reason, new { refundId, attempt = refund.Attempt });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (OpsOutcome.Ok, null);
    }

    public async Task AuditAsync(StaffPrincipal who, string action, string? targetType, string? targetId, string reason, CancellationToken ct)
    {
        StaffAudit.Add(db, time, who, action, targetType, targetId, reason);
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> BuyerOrdersAsync(Guid accountId, CancellationToken ct)
        => await db.Orders.AsNoTracking().Where(o => o.AccountId == accountId).OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(50)
            .Select(o => new { id = o.Id, number = o.Number, status = o.Status, createdAt = o.CreatedAt, totalCents = o.TotalCents, refundedCents = o.RefundedCents, demo = o.IsDemo }).ToListAsync(ct);

    // ---------- stuck ingests ----------

    /// <summary>Paid orders the portal has not accepted yet (still <c>paid</c>, not <c>placed</c>) for longer than <c>Staff:StuckIngestMinutes</c>, with what their ingest message is doing.</summary>
    public async Task<object> StuckIngestsAsync(CancellationToken ct)
    {
        var cutoff = Now.AddMinutes(-Math.Max(1, options.Value.StuckIngestMinutes));
        var orders = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Paid && o.PaidAt != null && o.PaidAt < cutoff).OrderBy(o => o.PaidAt).Take(100)
            .Select(o => new { o.Id, o.Number, o.PaidAt, o.TotalCents, o.IsDemo, o.FailureCode }).ToListAsync(ct);
        var keys = orders.Select(o => "ingest:" + o.Number).ToList();
        var msgs = (await db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey != null && keys.Contains(m.DedupeKey)).Select(m => new { m.DedupeKey, m.Status, m.Attempts, m.LastError, m.NextAttemptAt, HasPayload = m.Payload != null }).ToListAsync(ct)).ToDictionary(m => m.DedupeKey!);
        return new
        {
            stuckAfterMinutes = options.Value.StuckIngestMinutes,
            orders = orders.Select(o =>
            {
                msgs.TryGetValue("ingest:" + o.Number, out var m);
                return new { id = o.Id, number = o.Number, paidAt = o.PaidAt, totalCents = o.TotalCents, demo = o.IsDemo, message = m is null ? null : new { status = m.Status, attempts = m.Attempts, lastError = m.LastError, nextAttemptAt = m.NextAttemptAt }, retryable = m is { Status: OutboxStatus.Dead, HasPayload: true } };
            }),
        };
    }

    public async Task<(OpsOutcome Outcome, string? State)> RetryIngestAsync(StaffPrincipal who, Guid orderId, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.Number, o.Status }).FirstOrDefaultAsync(ct);
        if (order is null) return (OpsOutcome.NotFound, null);
        if (order.Status != OrderStatus.Paid) return (OpsOutcome.NotRetryable, order.Status);   // already placed, or failed and refunded: the handler would do nothing
        var key = "ingest:" + order.Number;
        if (await OutboxAdmin.ReviveDeadAsync(db, m => m.DedupeKey == key, Now, ct) != 1)
        {
            var state = await db.OutboxMessages.AsNoTracking().Where(m => m.DedupeKey == key).Select(m => m.Status).FirstOrDefaultAsync(ct);
            return state == OutboxStatus.Pending ? (OpsOutcome.AlreadyQueued, state) : (OpsOutcome.NotRetryable, state ?? "no_message");
        }
        StaffAudit.Add(db, time, who, "ingest.retry", "order", orderId.ToString("D"), reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (OpsOutcome.Ok, null);
    }

    // ---------- outbox ----------

    public async Task<IResult> OutboxAsync(string? status, string? type, int? limit, string? before, CancellationToken ct)
    {
        string? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            filter = new[] { OutboxStatus.Pending, OutboxStatus.Delivered, OutboxStatus.Dead }.FirstOrDefault(x => string.Equals(x, status, StringComparison.OrdinalIgnoreCase));
            if (filter is null) return Problems.BadRequest("invalid_status", "status must be pending, delivered or dead.");
        }
        DateTimeOffset at = default; Guid afterId = default;
        var paged = !string.IsNullOrEmpty(before);
        if (paged && !StaffCursor.TryDecode(before!, out at, out afterId)) return Problems.BadRequest("invalid_cursor", "The cursor is not valid.");
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var counts = (await db.OutboxMessages.GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var q = db.OutboxMessages.AsNoTracking().Where(m => filter == null || m.Status == filter);
        if (!string.IsNullOrEmpty(type)) q = q.Where(m => m.Type == type);
        if (paged) q = q.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(afterId) < 0));
        // never the payload (it can hold an email address, a link or an address)
        var rows = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1)
            .Select(m => new { m.Id, m.Type, m.Status, m.Attempts, m.NextAttemptAt, m.LastError, m.CreatedAt, m.DeliveredAt, m.DeadAt, HasPayload = m.Payload != null }).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return Results.Json(new
        {
            counts = new { pending = counts.GetValueOrDefault(OutboxStatus.Pending), delivered = counts.GetValueOrDefault(OutboxStatus.Delivered), dead = counts.GetValueOrDefault(OutboxStatus.Dead) },
            messages = rows.Select(m => new { id = m.Id, type = m.Type, status = m.Status, attempts = m.Attempts, nextAttemptAt = m.NextAttemptAt, lastError = m.LastError, createdAt = m.CreatedAt, deliveredAt = m.DeliveredAt, deadAt = m.DeadAt, retryable = m.Status == OutboxStatus.Dead && m.HasPayload }),
            next = more ? StaffCursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null,
        });
    }

    public async Task<OpsOutcome> RetryOutboxAsync(StaffPrincipal who, Guid id, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var type = await db.OutboxMessages.AsNoTracking().Where(m => m.Id == id).Select(m => m.Type).FirstOrDefaultAsync(ct);
        if (type is null) return OpsOutcome.NotFound;
        if (await OutboxAdmin.ReviveDeadAsync(db, m => m.Id == id, Now, ct) != 1) return OpsOutcome.NotRetryable;
        StaffAudit.Add(db, time, who, "outbox.retry", "outbox", id.ToString("D"), reason, new { type });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return OpsOutcome.Ok;
    }

    // ---------- inbound dead letters (parked events) ----------

    /// <summary>Portal events that could not be applied because the thing they are about is not known yet (parked), oldest first. Never the body.</summary>
    public async Task<object> DeadLettersAsync(int? limit, CancellationToken ct)
    {
        var now = Now;
        var counts = (await db.PortalInboundEvents.GroupBy(e => e.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var rows = await db.PortalInboundEvents.AsNoTracking().Where(e => e.Status == InboundStatus.Parked).OrderBy(e => e.ReceivedAt).Take(Math.Clamp(limit ?? 100, 1, 200))
            .Select(e => new { e.Id, e.Type, e.AggregateType, e.AggregateKey, e.ReceivedAt, e.LastReplayAt, e.Note, HasPayload = e.PayloadJson != null }).ToListAsync(ct);
        return new
        {
            bookingConfirms = await DeadBookingConfirmsAsync(ct),
            counts = counts.ToDictionary(k => k.Key, k => k.Value),
            parked = rows.Select(e => new { id = e.Id, type = e.Type, aggregateType = e.AggregateType, aggregateKey = e.AggregateKey, receivedAt = e.ReceivedAt, ageHours = (int)(now - e.ReceivedAt).TotalHours, lastReplayAt = e.LastReplayAt, note = e.Note, retryable = e.HasPayload }),
        };
    }

    /// <summary>
    /// What a person does about a dead booking confirm (docs/API.md "A dead booking confirm", decision M8-11). The payment stays Captured and the booking a Hold, and nobody is told, so
    /// these are listed among the dead letters with the booking, the payment and the amount. The first two steps happen outside this area (the portal is asked with its signed internal API).
    /// </summary>
    public static readonly string[] BookingConfirmSteps =
    [
        "Ask the portal what it knows about this booking (GET /api/internal/v1/bookings/<booking number>, signed).",
        "If the portal has it as Confirmed or Requested, or the cause is fixed (a wrong Portal__BaseUrl, a finished deploy): retry the outbox message below (an admin; the confirm is idempotent at the portal and the sale is then reported).",
        "If the portal has no such booking or will not confirm it: refund the PaymentIntent in the Stripe dashboard (the charge.refunded event is recorded here) and tell the buyer.",
    ];

    /// <summary>Dead <c>portal.booking-confirm</c> messages with their booking and payment. Never the payload; the ids come from it only to look the rows up, and a purged payload still lists the message.</summary>
    private async Task<List<object>> DeadBookingConfirmsAsync(CancellationToken ct)
    {
        var dead = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == Services.BookingConfirmHandler.Type && m.Status == OutboxStatus.Dead).OrderByDescending(m => m.DeadAt).Take(100)
            .Select(m => new { m.Id, m.LastError, m.Attempts, m.DeadAt, m.Payload }).ToListAsync(ct);
        var parsed = dead.Select(m =>
        {
            Services.BookingConfirmHandler.Payload? p = null;
            try { if (m.Payload is not null) p = System.Text.Json.JsonSerializer.Deserialize<Services.BookingConfirmHandler.Payload>(m.Payload, Portal.PortalJson.Options); } catch (System.Text.Json.JsonException) { }
            return (Message: m, Payload: p);
        }).ToList();
        var bookingIds = parsed.Where(x => x.Payload is not null).Select(x => x.Payload!.BookingId).ToList();
        var paymentIds = parsed.Where(x => x.Payload is not null).Select(x => x.Payload!.PaymentId).ToList();
        var bookings = (await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).Select(b => new { b.Id, b.MarketplaceBookingId, b.Status, b.IsDemo }).ToListAsync(ct)).ToDictionary(b => b.Id);
        var payments = (await db.BookingPayments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).Select(p => new { p.Id, p.Status, p.Currency, p.AmountCents, p.CapturedCents, p.RefundedCents, p.ReportedToPortal }).ToListAsync(ct)).ToDictionary(p => p.Id);
        return parsed.Select(x =>
        {
            var b = x.Payload is { } p1 && bookings.TryGetValue(p1.BookingId, out var bb) ? bb : null;
            var pay = x.Payload is { } p2 && payments.TryGetValue(p2.PaymentId, out var pp) ? pp : null;
            return (object)new
            {
                messageId = x.Message.Id, lastError = x.Message.LastError, attempts = x.Message.Attempts, deadAt = x.Message.DeadAt,
                bookingId = x.Payload?.BookingId, bookingNumber = b?.MarketplaceBookingId, bookingStatus = b?.Status, demo = b?.IsDemo ?? false,
                paymentId = x.Payload?.PaymentId, paymentStatus = pay?.Status, currency = pay?.Currency, amountCents = pay?.AmountCents, capturedCents = pay?.CapturedCents, refundedCents = pay?.RefundedCents,
                // the buyer has paid and nothing was reported: a person has to act
                needsPerson = pay is { Status: BookingPaymentStatus.Captured, ReportedToPortal: false },
                retryable = x.Message.Payload is not null,
                steps = BookingConfirmSteps,
            };
        }).ToList();
    }

    public async Task<(OpsOutcome Outcome, int Replayed)> RetryDeadLetterAsync(StaffPrincipal who, string eventId, string reason, CancellationToken ct)
    {
        var e = await db.PortalInboundEvents.AsNoTracking().Where(x => x.Id == eventId && x.Status == InboundStatus.Parked && x.PayloadJson != null).Select(x => new { x.AggregateType, x.AggregateKey, x.Type }).FirstOrDefaultAsync(ct);
        if (e?.AggregateType is null || e.AggregateKey is null) return (OpsOutcome.NotRetryable, 0);
        // The intent is recorded FIRST and saved on its own: the replay commits in transactions of its own (one per parked event), so it cannot share one with the audit row, and a replay that
        // happened without a record must not be possible (security review MED-2). If this save fails, nothing is replayed.
        StaffAudit.Add(db, time, who, "deadletter.retry", "inbound_event", eventId.Length > 80 ? eventId[..80] : eventId, reason, new { type = e.Type });
        await db.SaveChangesAsync(ct);
        // the existing replay applies every parked event of the aggregate in order, each once (its row version is the guard), so a second click or a parallel replay changes nothing
        var applied = await replay.ReplayParkedAsync(e.AggregateType, e.AggregateKey, ct);
        return (OpsOutcome.Ok, applied);
    }

    // ---------- Stripe webhook failures ----------

    /// <summary>Stripe events we received and could not match to an order of ours, and failure events (payment failed, refund failed). Only ids and types are stored (no payload), so there is nothing to replay here: resend from the Stripe dashboard.</summary>
    public async Task<IResult> StripeFailuresAsync(string? before, int? limit, CancellationToken ct)
    {
        DateTimeOffset at = default; string afterId = "";
        var paged = !string.IsNullOrEmpty(before);
        if (paged)
        {
            if (!StaffCursor.TryDecodeText(before!, out at, out afterId)) return Problems.BadRequest("invalid_cursor", "The cursor is not valid.");
        }
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var q = db.StripeEvents.AsNoTracking().Where(e => e.Outcome == "unmatched" || e.Type.Contains("failed"));
        var total = await q.CountAsync(ct);
        if (paged) q = q.Where(e => e.ReceivedAt < at || (e.ReceivedAt == at && string.Compare(e.Id, afterId) < 0));
        var rows = await q.OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        return Results.Json(new
        {
            total,
            events = rows.Select(e => new { id = e.Id, type = e.Type, outcome = e.Outcome, objectId = e.ObjectId, receivedAt = e.ReceivedAt }),
            next = more ? StaffCursor.EncodeText(rows[^1].ReceivedAt, rows[^1].Id) : null,
        });
    }
}
