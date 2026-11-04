using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Payments;

public enum ReserveOutcome { Reserved, Duplicate, ExceedsPaid, NotPaid }

/// <summary>One line of a portal refund: our <c>marketplaceLineId</c> and how many units.</summary>
public sealed record PortalRefundLine(string MarketplaceLineId, int Quantity);

/// <summary>
/// A refund the portal asked for. <see cref="TotalCents"/> is the FULL amount to give the buyer (it already includes line tax and <see cref="ShippingCents"/>). <see cref="FullCancel"/>:
/// everything not yet returned on the seller order; <see cref="Clamp"/>: cut to what is left instead of refusing. <see cref="TaxCents"/> is the portal's own figure once it sends it.
/// </summary>
public sealed record PortalRefund(Guid OrderId, Guid GroupId, long TotalCents, long ShippingCents, IReadOnlyList<PortalRefundLine> Lines, long? TaxCents, bool FullCancel, bool Clamp, string Key, string Reason, string? PortalRefundId, long? ShippingTaxCents = null);

/// <summary>The tax we sent per line at ingest (the line's own tax plus its share of tax charged on shipping while that is folded in, C8): the base of every back-out, so a reversal is exact.</summary>
public static class OrderTaxSent
{
    public static long[] PerLine(OrderGroup g, IReadOnlyList<OrderLine> orderedLines, bool perGroupShipping)
    {
        var shipTax = perGroupShipping ? new long[orderedLines.Count] : Checkout.OrderIngestHandler.AllocateShippingTax(g.ShippingTaxCents, orderedLines);
        return orderedLines.Select((l, i) => l.TaxCents + shipTax[i]).ToArray();
    }
}

/// <summary>
/// The only way money leaves: a refund is first RESERVED against the cap with conditional updates
/// (<c>RefundedCents + amount &lt;= PaidCents</c> on the order, <c>&lt;= TotalCents</c> on the seller order), together with its unique idempotency key and the outbox message that will
/// call Stripe. So refunds can never exceed what was paid, in total or per seller order, however many events, retries or parallel requests arrive.
/// </summary>
public sealed class RefundService(MarketplaceDb db, OutboxWriter outbox, TimeProvider time)
{
    public const string StripeRefundType = "stripe.refund";

    /// <summary>
    /// Reserves and queues. Inside the caller's transaction when there is one (the caller saves and commits); otherwise in a transaction of its own, saved here.
    /// </summary>
    public async Task<ReserveOutcome> ReserveAsync(Guid orderId, Guid? groupId, long amountCents, long shippingCents, string key, string reason, bool reportToPortal, string? portalRefundId, CancellationToken ct)
    {
        if (amountCents <= 0) throw new ArgumentOutOfRangeException(nameof(amountCents));
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.OrderRefunds.AnyAsync(r => r.Key == key, ct)) return ReserveOutcome.Duplicate;
        var paid = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.PaidAt).FirstOrDefaultAsync(ct);
        if (paid is null) return ReserveOutcome.NotPaid;
        var order = await db.Orders.Where(o => o.Id == orderId && o.PaidAt != null && o.RefundedCents + amountCents <= o.PaidCents)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + amountCents), ct);
        if (order == 0) return ReserveOutcome.ExceedsPaid;
        if (groupId is { } gid)
        {
            var g = await db.OrderGroups.Where(x => x.Id == gid && x.OrderId == orderId && x.RefundedCents + amountCents <= x.TotalCents)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents + amountCents), ct);
            if (g == 0)
            {
                await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents - amountCents), ct);
                return ReserveOutcome.ExceedsPaid;
            }
        }
        var now = time.GetUtcNow().ToUniversalTime();
        db.OrderRefunds.Add(new OrderRefund
        {
            Id = Guid.NewGuid(), Key = key, OrderId = orderId, GroupId = groupId, AmountCents = amountCents, ShippingCents = shippingCents, Reason = reason, Status = RefundStatus.Reserved,
            ReportToPortal = reportToPortal, PortalRefundId = portalRefundId, CreatedAt = now, UpdatedAt = now,
        });
        outbox.Enqueue(StripeRefundType, new RefundPayload(key), $"refund:{key}");
        if (own)
        {
            await db.SaveChangesAsync(ct);   // the unique key decides a parallel duplicate: it throws and the transaction (reservation included) rolls back
            await tx!.CommitAsync(ct);
        }
        return ReserveOutcome.Reserved;
    }

    /// <summary>
    /// Reserves a refund the PORTAL asked for on one seller order (<c>refund.issued</c>, an order cancellation). Decision M3-16 (portal answer to C9): the portal's amounts ALREADY INCLUDE the
    /// line tax, so the buyer (Stripe) gets back exactly <see cref="PortalRefund.TotalCents"/>, nothing added. The portal's ledger excludes tax, so <c>refund.processed</c> reports the amount
    /// with the tax backed out: per line the tax we sent at ingest, floor-prorated by the quantity refunded, the last refund of a line taking the remainder (the portal does the same
    /// to the line's amount, so the two reconcile to the cent). Everything runs under the seller order's row lock, so parallel refunds, replays and cancellations never both fit.
    /// </summary>
    public async Task<ReserveOutcome> ReservePortalAsync(PortalRefund r, bool perGroupShipping, CancellationToken ct)
    {
        var own = db.Database.CurrentTransaction is null;
        await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.OrderRefunds.AnyAsync(x => x.Key == r.Key, ct)) return ReserveOutcome.Duplicate;
        // the lock: a write to the seller order's row (every refund of it starts here; a concurrent one waits, then sees our counters)
        if (await db.OrderGroups.Where(x => x.Id == r.GroupId && x.OrderId == r.OrderId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents), ct) == 0) return ReserveOutcome.NotPaid;
        var paid = await db.Orders.AsNoTracking().Where(o => o.Id == r.OrderId).Select(o => o.PaidAt).FirstOrDefaultAsync(ct);
        if (paid is null) return ReserveOutcome.NotPaid;
        var g = await db.OrderGroups.AsNoTracking().FirstAsync(x => x.Id == r.GroupId, ct);
        var lines = await db.OrderLines.AsNoTracking().Where(l => l.GroupId == g.Id).ToListAsync(ct);
        lines = lines.OrderBy(l => l.MarketplaceLineId, StringComparer.Ordinal).ToList();
        var sentTax = OrderTaxSent.PerLine(g, lines, perGroupShipping);

        var left = g.TotalCents - g.RefundedCents;
        var shippingLeft = g.ShippingCents - g.RefundedShippingCents;
        long total = r.TotalCents, shipping = r.ShippingCents;
        if (r.FullCancel) { total = Math.Min(total, left); shipping = Math.Min(shippingLeft, total); }          // a full cancel returns everything not yet returned, shipping included
        else if (r.Clamp && total > left) { total = left; shipping = Math.Min(shipping, total); }
        if (total <= 0 || shipping < 0 || shipping > total || total > left || shipping > shippingLeft) return ReserveOutcome.ExceedsPaid;

        // which lines (and how many units) this refund covers, and the tax inside it
        var parts = new List<(OrderLine Line, int Qty, long Tax)>();
        if (r.FullCancel)
            for (var i = 0; i < lines.Count; i++)
            {
                var q = lines[i].Quantity - lines[i].RefundedQuantity;
                if (q > 0) parts.Add((lines[i], q, sentTax[i] - lines[i].RefundedTaxCents));
            }
        else
            foreach (var grp in r.Lines.GroupBy(l => l.MarketplaceLineId, StringComparer.Ordinal))
            {
                var i = lines.FindIndex(l => l.MarketplaceLineId == grp.Key);
                var q = grp.Sum(x => x.Quantity);
                if (i < 0 || q <= 0 || lines[i].RefundedQuantity + q > lines[i].Quantity) return ReserveOutcome.ExceedsPaid;   // a line that is not on this seller order, or more units than it has
                var last = lines[i].RefundedQuantity + q == lines[i].Quantity;
                parts.Add((lines[i], q, last ? sentTax[i] - lines[i].RefundedTaxCents : sentTax[i] * q / lines[i].Quantity));   // floor, the last refund of a line takes the remainder
            }
        var remainingTax = g.TaxCents - g.RefundedTaxCents;
        // C8 mode (shipping tax is the portal's own field, not folded into line tax; brief N8): the amount INCLUDES the shipping tax. The portal's explicit shippingTaxCents is used as sent; only when it is
        // missing (an older portal) is it inferred, in proportion to the shipping refunded, the last shipping refund (or a full cancel) taking the remainder. Never more than is left of it.
        long shippingTaxBack = 0;
        var shippingTaxLeft = perGroupShipping ? Math.Max(0, g.ShippingTaxCents - g.RefundedShippingTaxCents) : 0;
        if (perGroupShipping)
        {
            if (r.ShippingTaxCents is { } sentShippingTax) shippingTaxBack = sentShippingTax;
            else if (r.FullCancel) shippingTaxBack = shippingTaxLeft;
            else if (shipping > 0 && g.ShippingCents > 0)
                shippingTaxBack = g.RefundedShippingCents + shipping >= g.ShippingCents ? shippingTaxLeft : g.ShippingTaxCents * shipping / g.ShippingCents;
            shippingTaxBack = Math.Clamp(shippingTaxBack, 0, shippingTaxLeft);
        }
        long tax;
        if (r.TaxCents is { } explicitTax)
        {
            tax = Math.Clamp(explicitTax, 0, Math.Min(remainingTax, total - shipping));                              // the portal sends it (it includes the shipping tax): then we only read it
            shippingTaxBack = Math.Min(shippingTaxBack, tax);                                                         // the shipping tax is part of the tax, never more
        }
        else
        {
            long linesTax;
            if (parts.Count > 0) linesTax = parts.Sum(p => p.Tax);
            else
            {
                // no lines (a goodwill refund of an amount): the same floor proration on the seller order's remaining LINE tax and gross (in C8 mode the shipping tax is in neither)
                var itemsGross = total - shipping - shippingTaxBack;
                var remainingGross = g.ItemsCents + (g.TaxCents - (perGroupShipping ? g.ShippingTaxCents : 0)) - (g.RefundedCents - g.RefundedShippingCents - (perGroupShipping ? g.RefundedShippingTaxCents : 0));
                var remainingLineTax = remainingTax - shippingTaxLeft;
                linesTax = itemsGross <= 0 || remainingGross <= 0 || remainingLineTax <= 0 ? 0 : itemsGross >= remainingGross ? remainingLineTax : remainingLineTax * itemsGross / remainingGross;
            }
            linesTax = Math.Clamp(linesTax, 0, Math.Max(0, Math.Min(remainingTax - shippingTaxLeft, total - shipping - shippingTaxBack)));
            shippingTaxBack = Math.Min(shippingTaxBack, Math.Max(0, total - shipping - linesTax));                   // review C2: the shipping tax must fit inside what is left of the amount, or it is not refunded (and not counted)
            tax = linesTax + shippingTaxBack;
        }

        if (await db.Orders.Where(o => o.Id == r.OrderId && o.PaidAt != null && o.RefundedCents + total <= o.PaidCents).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + total), ct) == 0)
            return ReserveOutcome.ExceedsPaid;
        var moved = await db.OrderGroups.Where(x => x.Id == g.Id && x.RefundedCents + total <= x.TotalCents && x.RefundedShippingCents + shipping <= x.ShippingCents)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents + total).SetProperty(x => x.RefundedShippingCents, x => x.RefundedShippingCents + shipping)
                .SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents + tax).SetProperty(x => x.RefundedShippingTaxCents, x => x.RefundedShippingTaxCents + shippingTaxBack), ct);
        if (moved == 0)
        {
            await db.Orders.Where(o => o.Id == r.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents - total), ct);
            return ReserveOutcome.ExceedsPaid;
        }
        foreach (var (line, qty, lineTax) in parts)
        {
            var id = line.Id;
            await db.OrderLines.Where(l => l.Id == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.RefundedQuantity, l => l.RefundedQuantity + qty).SetProperty(l => l.RefundedTaxCents, l => l.RefundedTaxCents + lineTax), ct);
        }
        var now = time.GetUtcNow().ToUniversalTime();
        db.OrderRefunds.Add(new OrderRefund
        {
            Id = Guid.NewGuid(), Key = r.Key, OrderId = r.OrderId, GroupId = g.Id, AmountCents = total, ShippingCents = shipping, TaxCents = tax, ShippingTaxCents = shippingTaxBack, Reason = r.Reason, Status = RefundStatus.Reserved,
            ReportToPortal = true, PortalRefundId = r.PortalRefundId, CreatedAt = now, UpdatedAt = now,
            PartsJson = JsonSerializer.Serialize(parts.Select(p => new RefundPart(p.Line.Id, p.Qty, p.Tax)).ToList()),
        });
        outbox.Enqueue(StripeRefundType, new RefundPayload(r.Key), $"refund:{r.Key}");
        if (own) { await db.SaveChangesAsync(ct); await tx!.CommitAsync(ct); }
        return ReserveOutcome.Reserved;
    }


    /// <summary>Gives back everything a refund took from the counters (order, seller order, shipping, tax, shipping tax, the units and their tax), exactly as it took them.</summary>
    public static async Task ReleaseCountersAsync(MarketplaceDb db, OrderRefund refund, CancellationToken ct)
    {
        await db.Orders.Where(o => o.Id == refund.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents - refund.AmountCents), ct);
        if (refund.GroupId is not { } g) return;
        await db.OrderGroups.Where(x => x.Id == g).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents - refund.AmountCents)
            .SetProperty(x => x.RefundedShippingCents, x => x.RefundedShippingCents - refund.ShippingCents).SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents - refund.TaxCents)
            .SetProperty(x => x.RefundedShippingTaxCents, x => x.RefundedShippingTaxCents - refund.ShippingTaxCents), ct);
        foreach (var part in refund.PartsJson is null ? [] : JsonSerializer.Deserialize<List<RefundPart>>(refund.PartsJson) ?? [])
        {
            var (id, qty, lt) = (part.LineId, part.Quantity, part.TaxCents);
            await db.OrderLines.Where(l => l.Id == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.RefundedQuantity, l => l.RefundedQuantity - qty).SetProperty(l => l.RefundedTaxCents, l => l.RefundedTaxCents - lt), ct);
        }
    }

    public sealed record RefundPart(Guid LineId, int Quantity, long TaxCents);

    public sealed record RefundPayload(string Key, int Attempt = 0);
}

/// <summary>
/// Calls Stripe for a reserved refund (idempotent on the refund key), then tells the portal and the buyer, all in one transaction with the status change.
/// When Stripe refuses, the handler does NOT guess from the error code (decision M3-28, review R1/R2): it reads what Stripe has really refunded, records any refund it did not know (made in
/// Stripe's dashboard) as external money, and only then decides: a refund that still fits is simply refused and its counters given back; one that no longer fits is cut to what is left (and re-sent
/// under a new idempotency attempt), or, when nothing is left, given back entirely. So the counters always follow Stripe-confirmed money, never a reservation Stripe never executed.
/// </summary>
public sealed class StripeRefundHandler(MarketplaceDb db, IPaymentProvider payments, OutboxWriter outbox, TimeProvider time, ILogger<StripeRefundHandler>? log = null) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } = [RefundService.StripeRefundType];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        RefundService.RefundPayload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<RefundService.RefundPayload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.Key)) return OutboxResult.Dead("invalid_payload");
        var refund = await db.OrderRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.Key == p.Key, ct);
        if (refund is null) return OutboxResult.Dead("refund_missing");
        if (refund.Status != RefundStatus.Reserved) return OutboxResult.Delivered();   // already done (a retry after the result was recorded)
        if (p.Attempt < refund.Attempt) return OutboxResult.Delivered();                // a message of an older send (the amount was cut and re-sent since): it must not send the new amount under the old key (review Q2)
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstAsync(o => o.Id == refund.OrderId, ct);
        if (order.PaymentIntentId is null) return OutboxResult.Dead("no_payment_intent");

        RefundResult result;
        try
        {
            result = await payments.RefundAsync(order.PaymentIntentId, refund.AmountCents, "refund:" + refund.Key + (p.Attempt > 0 ? ":" + p.Attempt : ""),
                new Dictionary<string, string> { ["marketplace_order_id"] = order.Number, ["refund_key"] = refund.Key }, ct);
        }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex) { return await RefusedAsync(refund, order, p, ex.Code, ct); }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await FinalizeSucceededAsync(refund, order, result.Id, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return OutboxResult.Delivered();
    }

    /// <summary>A refund of ours that Stripe has executed: marks it done and queues what follows (the portal's <c>refund.processed</c>, the buyer's mail). The caller saves and commits.</summary>
    private async Task<bool> FinalizeSucceededAsync(OrderRefund refund, MarketplaceOrder order, string stripeRefundId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var changed = await db.OrderRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Succeeded).SetProperty(r => r.StripeRefundId, stripeRefundId).SetProperty(r => r.UpdatedAt, now), ct);
        if (changed == 0) return false;
        var group = refund.GroupId is { } gid ? order.Groups.FirstOrDefault(g => g.Id == gid) : null;
        if (refund.ReportToPortal && group?.SellerOrderNumber is { } sellerOrderNumber)
            outbox.EnqueuePaymentEvent($"refund-processed:{refund.Key}", PaymentEventTypes.RefundProcessed,
                new RefundProcessedData(stripeRefundId, order.Number, sellerOrderNumber, null, refund.AmountCents - refund.ShippingCents - refund.TaxCents, refund.ShippingCents, order.Currency, refund.Reason));
        // The buyer is told only now that Stripe has really returned the money (never on reservation: a refund Stripe refuses must not have been announced as done)
        if (refund.Reason == "order_failed") await MailFailedOrderAsync(order, refund.AmountCents, ct);
        return true;
    }

    private async Task<OutboxResult> RefusedAsync(OrderRefund refund, MarketplaceOrder order, RefundService.RefundPayload p, string code, CancellationToken ct)
    {
        // An idempotency error means the SAME key reached Stripe with another amount, or while the first request was still running: never a reason to give anything back. Look again later with a fresh read (review Q2).
        // Only "the first request is still running" is a reason to wait; any other idempotency error is a bug alarm: dead-lettered where a person sees it, never retried and never a release (review L4).
        if (code == "idempotency_key_in_use") return OutboxResult.Retry("stripe_" + code);
        if (code.StartsWith("idempotency", StringComparison.Ordinal)) { log?.LogError("Refund {Key} of order {Number}: Stripe answered {Code}; the same key reached it with another request. A person must look", refund.Key, order.Number, code); return OutboxResult.Dead("stripe_" + code); }

        long? read = null;   // null: Stripe does not know the intent (or the charge), so no money can have left through it
        IReadOnlyList<ProviderRefund> listed = [];
        try
        {
            read = await payments.GetAmountRefundedAsync(order.PaymentIntentId!, ct);
            listed = await payments.ListRefundsAsync(order.PaymentIntentId!, ct);
        }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex) when (ex.Code == "resource_missing") { }
        catch (PaymentRejectedException ex) { return OutboxResult.Dead("stripe_" + ex.Code); }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().ToUniversalTime();
        // the order's row lock (a no-op write), like every other writer of its refund counters
        await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents), ct);
        var all = await db.OrderRefunds.AsNoTracking().Where(r => r.OrderId == order.Id).ToListAsync(ct);
        var current = all.FirstOrDefault(r => r.Id == refund.Id);
        if (current is null || current.Status != RefundStatus.Reserved) { await tx.RollbackAsync(CancellationToken.None); return OutboxResult.Delivered(); }   // settled meanwhile
        // Under the lock the row is read again: a worker that was slow past its outbox lease finds the refund already cut and re-sent under a newer attempt, and must not touch it (review M1: a double release).
        if (current.Attempt != p.Attempt) { await tx.RollbackAsync(CancellationToken.None); return OutboxResult.Delivered(); }
        refund = current;

        // A reserved refund of OURS that Stripe has already executed (our call timed out and waits for its retry) is MONEY THAT LEFT: it is matched by our refund_key metadata and finished, never counted as a
        // dashboard refund and never released (review Q1). A refund Stripe lists as failed or canceled is not money.
        // Only a refund Stripe lists as SUCCEEDED is finished (review M2): pending or requires_action is not money yet (look again later), failed or canceled is not money at all.
        var executed = listed.Where(l => l.RefundKey is not null && l.Status == "succeeded").GroupBy(l => l.RefundKey!).ToDictionary(g => g.Key, g => g.First());
        if (!executed.ContainsKey(refund.Key) && listed.Any(l => l.RefundKey == refund.Key && l.Status is "pending" or "requires_action"))
        { await tx.RollbackAsync(CancellationToken.None); return OutboxResult.Retry("stripe_refund_pending"); }
        var finished = false;
        foreach (var row in all.Where(r => r.Status == RefundStatus.Reserved && r.Reason != "external" && executed.ContainsKey(r.Key)).ToList())
            if (await FinalizeSucceededAsync(row, order, executed[row.Key].Id, ct))
            {
                if (row.Id == refund.Id) finished = true;
                all[all.FindIndex(r => r.Id == row.Id)] = Copy(row, RefundStatus.Succeeded);
            }
        if (finished) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return OutboxResult.Delivered(); }   // it went through after all

        var ours = all.Where(r => r.Reason != "external" && r.Status == RefundStatus.Succeeded).Sum(r => r.AmountCents);
        var known = all.Where(r => r.Reason == "external" && r.Status == RefundStatus.Succeeded).Sum(r => r.AmountCents);
        var confirmed = read ?? ours + known;
        var unknown = confirmed - ours - known;
        if (unknown > 0)
        {
            // money that left through Stripe's dashboard and we had not heard of: it is real, so it takes its place in the counters now (keyed by this refund and send, never by a cumulative figure that can come back)
            db.OrderRefunds.Add(new OrderRefund { Id = Guid.NewGuid(), Key = $"external:{refund.Key}:{p.Attempt}", OrderId = order.Id, AmountCents = unknown, Reason = "external", Status = RefundStatus.Succeeded, CreatedAt = now, UpdatedAt = now });
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + unknown), ct);
            log?.LogError("Order {Number}: Stripe has refunded {Confirmed} cents, {Unknown} of them outside this system (dashboard); recorded as external money", order.Number, confirmed, unknown);
        }
        var others = all.Where(r => r.Id != refund.Id && r.Reason != "external" && r.Status == RefundStatus.Reserved).Sum(r => r.AmountCents);
        var usable = Math.Max(0, order.PaidCents - confirmed - others);          // what can still go back to the card, after the refunds already queued ahead of this one
        if (refund.AmountCents > usable && usable > 0)
        {
            // The money is partly gone: send what is left, under a new idempotency attempt (the old key belongs to the old amount). The refund becomes a plain AMOUNT (decision M3-34): everything the
            // original reservation took from the counters (units, tax, shipping) is given back, and the cut amount is booked on its own with its tax prorated, so a later release gives back exactly what is booked.
            var newTax = refund.AmountCents == 0 ? 0 : refund.TaxCents * usable / refund.AmountCents;
            // The cut is conditional on the row still being this attempt and still reserved: one row changed or nothing else is done (a parallel worker already cut it).
            var cut = await db.OrderRefunds.Where(r => r.Id == refund.Id && r.Attempt == p.Attempt && r.Status == RefundStatus.Reserved)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.AmountCents, usable).SetProperty(r => r.ShippingCents, 0L).SetProperty(r => r.TaxCents, newTax)
                    .SetProperty(r => r.ShippingTaxCents, 0L).SetProperty(r => r.PartsJson, (string?)null).SetProperty(r => r.Attempt, p.Attempt + 1).SetProperty(r => r.UpdatedAt, now), ct);
            if (cut != 1) { await tx.RollbackAsync(CancellationToken.None); return OutboxResult.Delivered(); }
            await RefundService.ReleaseCountersAsync(db, refund, ct);
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.RefundedCents, o => o.RefundedCents + usable), ct);
            if (refund.GroupId is { } g) await db.OrderGroups.Where(x => x.Id == g).ExecuteUpdateAsync(s => s.SetProperty(x => x.RefundedCents, x => x.RefundedCents + usable).SetProperty(x => x.RefundedTaxCents, x => x.RefundedTaxCents + newTax), ct);
            outbox.Enqueue(RefundService.StripeRefundType, new RefundService.RefundPayload(refund.Key, p.Attempt + 1), $"refund:{refund.Key}:{p.Attempt + 1}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log?.LogError("Refund {Key} of order {Number} was cut from {Was} to {Now} cents because Stripe has already refunded {Confirmed}; it is being sent again", refund.Key, order.Number, refund.AmountCents, usable, confirmed);
            return OutboxResult.Delivered();
        }

        // Nothing (more) can go back, or the refund fits and was refused for another reason: nothing moved for this refund, so everything it reserved is given back.
        // (A refund that no longer fits leaves the cap consumed by the CONFIRMED money instead: the external row above.)
        var changed = await db.OrderRefunds.Where(r => r.Id == refund.Id && r.Status == RefundStatus.Reserved)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RefundStatus.Rejected).SetProperty(r => r.UpdatedAt, now), ct);
        if (changed == 1)
        {
            await RefundService.ReleaseCountersAsync(db, refund, ct);
            if (refund.Reason == "order_failed" && usable == 0) await MailFailedOrderAsync(order, 0, ct);   // the money was already returned another way: say so
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (usable == 0 && refund.AmountCents > 0) log?.LogError("Refund {Key} of order {Number} ({Amount} cents) was refused and nothing is left to return: Stripe has refunded {Confirmed} of {Paid}; a person should check", refund.Key, order.Number, refund.AmountCents, confirmed, order.PaidCents);
        return OutboxResult.Dead("stripe_" + code);
    }

    private static OrderRefund Copy(OrderRefund r, string status) => new()
    {
        Id = r.Id, Key = r.Key, OrderId = r.OrderId, GroupId = r.GroupId, AmountCents = r.AmountCents, ShippingCents = r.ShippingCents, TaxCents = r.TaxCents, ShippingTaxCents = r.ShippingTaxCents, PartsJson = r.PartsJson,
        Reason = r.Reason, Attempt = r.Attempt, Status = status, ReportToPortal = r.ReportToPortal, PortalRefundId = r.PortalRefundId, CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
    };

    private async Task MailFailedOrderAsync(MarketplaceOrder order, long refunded, CancellationToken ct)
    {
        var email = await Checkout.OrderMail.AddressAsync(db, order, ct);
        if (email is null) return;
        var windowGone = order.FailureCode?.StartsWith("delivery_", StringComparison.Ordinal) == true;
        var text = refunded <= 0 ? "everything has already been returned to your card"
            : refunded >= order.PaidCents ? $"{Checkout.OrderIngestHandler.Money(refunded, order.Currency)} is being refunded to your card in full"
            : $"the remaining {Checkout.OrderIngestHandler.Money(refunded, order.Currency)} is being refunded to your card (the rest was already returned)";
        outbox.EnqueueEmail(new EmailMessage(email, "We couldn't place your order " + order.Number,
            (windowGone ? "The delivery time you chose is no longer available, so the seller couldn't accept your order. " : "One of the items could no longer be ordered, so the sellers couldn't accept your order. ")
            + $"You have not been charged: {text} (it can take a few days to show). Please place the order again and choose again.\n"), $"mail:failed:{order.Number}");
    }
}

/// <summary>
/// Cancels a PaymentIntent of a closed order (superseded or expired), with retries: an intent that stays payable is the one thing we must never leave behind. A Stripe refusal means it can no
/// longer be cancelled (it already succeeded, or is already cancelled): nothing to do here, the webhook refunds a payment that landed.
/// </summary>
public sealed class StripeCancelHandler(IPaymentProvider payments) : IOutboxHandler
{
    public const string Type = "stripe.cancel";
    public sealed record Payload(string IntentId);
    public static string DedupeKey(string intentId) => "cancel:" + intentId;
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.IntentId)) return OutboxResult.Dead("invalid_payload");
        return await CancelAsync(payments, p.IntentId, ct);
    }

    /// <summary>Cancels an intent and says whether that is DONE (see the comments inside).</summary>
    public static async Task<OutboxResult> CancelAsync(IPaymentProvider payments, string intentId, CancellationToken ct)
    {
        try { await payments.CancelIntentAsync(intentId, "cancel:" + intentId, ct); }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex)
        {
            // Delivered ONLY when the intent is verifiably closed (already cancelled, or already paid: the webhook refunds a payment that landed) or does not exist at all. Anything else
            // (a rotated key, a 400 for an intent still in flight) is NOT "done": the purge of the order waits for a Delivered cancel, so a person must see it (decision M3-25).
            if (ex.Code == "resource_missing") return OutboxResult.Delivered();
            if (ex.Code == "payment_intent_unexpected_state")
            {
                try
                {
                    var status = await payments.GetIntentStatusAsync(intentId, ct);
                    if (status is "succeeded" or "canceled") return OutboxResult.Delivered();
                    return OutboxResult.Retry("intent_" + status);   // processing / requires_capture: the money may still land; look again
                }
                catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
                catch (PaymentRejectedException ex2) when (ex2.Code == "resource_missing") { return OutboxResult.Delivered(); }
                catch (PaymentRejectedException ex2) { return OutboxResult.Dead("stripe_" + ex2.Code); }
            }
            return OutboxResult.Dead("stripe_" + ex.Code);
        }
        return OutboxResult.Delivered();
    }
}

/// <summary>
/// Refunds a payment by INTENT id, for money that landed on an intent no live order stands behind (the order was purged, or a stray intent of an order that stores another). Idempotent on the intent
/// (the refund key is derived from it), so a retry or a repeated webhook refunds once; Stripe refuses more than was captured.
/// </summary>
public sealed class StripeIntentRefundHandler(IPaymentProvider payments) : IOutboxHandler
{
    public const string Type = "stripe.refund-intent";
    public sealed record Payload(string IntentId, long AmountCents, string OrderNumber);
    public static string DedupeKey(string intentId) => "refund-intent:" + intentId;
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.IntentId) || p.AmountCents <= 0) return OutboxResult.Dead("invalid_payload");
        try { await payments.RefundAsync(p.IntentId, p.AmountCents, "refund:" + DedupeKey(p.IntentId), new Dictionary<string, string> { ["marketplace_order_id"] = p.OrderNumber, ["reason"] = "no_live_order" }, ct); }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex) when (ex.Code == "charge_already_refunded") { return OutboxResult.Delivered(); }
        catch (PaymentRejectedException ex) { return OutboxResult.Dead("stripe_" + ex.Code); }
        return OutboxResult.Delivered();
    }
}

/// <summary>
/// An order was closed while it had NO intent stored: the intent may still exist at Stripe (a crash between create and store, a timeout after Stripe created it, the intent_mismatch branch). It
/// cannot be paid (its secret was never returned) but must not stay live. Asking Stripe again with the SAME key (<c>pi:&lt;number&gt;</c>, valid 24 hours: orders are closed within 23) returns that
/// intent, or creates one that is cancelled at once; either way nothing is left open (decision M3-25).
/// </summary>
public sealed class StripeOrphanCancelHandler(MarketplaceDb db, IPaymentProvider payments) : IOutboxHandler
{
    public const string Type = "stripe.cancel-orphan";
    public sealed record Payload(string OrderNumber);
    public static string DedupeKey(string orderNumber) => "cancel-orphan:" + orderNumber;
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || string.IsNullOrEmpty(p.OrderNumber)) return OutboxResult.Dead("invalid_payload");
        var order = await db.Orders.AsNoTracking().Where(o => o.Number == p.OrderNumber).Select(o => new { o.TotalCents, o.Currency, o.PaymentIntentId, o.Status }).FirstOrDefaultAsync(ct);
        if (order is null) return OutboxResult.Delivered();
        if (order.PaymentIntentId is not null) return OutboxResult.Delivered();   // an intent is stored: the normal cancel owns it
        if (order.Status is OrderStatus.PendingPayment or OrderStatus.PaymentProcessing) return OutboxResult.Delivered();   // not closed after all
        try
        {
            var intent = await payments.CreateIntentAsync(order.TotalCents, order.Currency, "pi:" + p.OrderNumber, new Dictionary<string, string> { ["marketplace_order_id"] = p.OrderNumber }, ct);
            return await StripeCancelHandler.CancelAsync(payments, intent.Id, ct);
        }
        catch (PaymentsUnavailableException) { return OutboxResult.Retry("stripe_unavailable"); }
        catch (PaymentRejectedException ex) { return OutboxResult.Dead("stripe_" + ex.Code); }
    }
}
