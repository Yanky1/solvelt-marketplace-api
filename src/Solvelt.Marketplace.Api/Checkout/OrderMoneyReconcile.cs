using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Checkout;

public enum MoneyMismatchKind
{
    /// <summary>The portal lists a refund we hold no row for (its event was lost, or is still in flight).</summary>
    PortalRefundUnknownHere,
    /// <summary>The portal's refund and our row for it differ in amount (a refund we cut because Stripe had refunded part of it differs on purpose).</summary>
    RefundAmountDiffers,
    /// <summary>The portal counts a refund that our row says was rejected or failed (the buyer did not get that money back).</summary>
    PortalRefundNotLiveHere,
    /// <summary>We hold a live refund that came from the portal and the portal does not list it.</summary>
    OurRefundNotAtPortal,
    /// <summary>The portal's refunded total for the seller order is not the sum of our live refunds for it.</summary>
    RefundedTotalDiffers,
}

public sealed record MoneyMismatch(MoneyMismatchKind Kind, string SellerOrderNumber, string? RefundId, long Portal, long Ours);

/// <summary>
/// The money side of the portal order read (C14/C16, decision M3-41). After M5's reconcile has brought the FULFILMENT state up to the portal's view, this compares the portal's refunds and refunded
/// total of each seller order with our own refund rows and ALERTS on any difference. It only ever reads: it moves no money, changes no counter and no status, whatever the portal says; a person
/// settles the difference (a refund event that was lost is replayed by the portal's admin retry, which our webhook then applies like any other). The alert is an error log with the order and seller
/// order numbers and the amounts (no buyer data). An alert may also be a refund still in flight: the read is of one moment.
/// </summary>
public sealed class OrderMoneyReconcileObserver(MarketplaceDb db, ILogger<OrderMoneyReconcileObserver> log) : IOrderReconcileObserver
{
    public async Task ObserveAsync(string marketplaceOrderId, PortalOrderState state, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Groups).FirstOrDefaultAsync(o => o.Number == marketplaceOrderId, ct);
        if (order is null) return;
        var rows = await db.OrderRefunds.AsNoTracking().Where(r => r.OrderId == order.Id && r.GroupId != null).ToListAsync(ct);
        foreach (var so in state.SellerOrders ?? [])
        {
            var group = order.Groups.FirstOrDefault(g => g.SellerOrderId == so.SellerOrderId);
            if (group is null) continue;
            foreach (var m in Compare(group.SellerOrderNumber ?? so.SellerOrderNumber ?? "?", so, rows.Where(r => r.GroupId == group.Id).ToList()))
                log.LogError("Order money reconcile: {Kind} on order {Order}, seller order {SellerOrder}, refund {RefundId}: portal {Portal} cents, ours {Ours} cents. Nothing was changed; a person should look (it may be a refund still in flight)",
                    m.Kind, marketplaceOrderId, m.SellerOrderNumber, m.RefundId ?? "-", m.Portal, m.Ours);
        }
    }

    /// <summary>The differences between the portal's view of ONE seller order and our refund rows for its group (pure: no database, no logging).</summary>
    public static IReadOnlyList<MoneyMismatch> Compare(string sellerOrderNumber, PortalSellerOrderState portal, IReadOnlyList<OrderRefund> ours)
    {
        var found = new List<MoneyMismatch>();
        var byKey = ours.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pr in portal.Refunds ?? [])
        {
            if (string.IsNullOrEmpty(pr.RefundId)) continue;
            listed.Add("portal:" + pr.RefundId);
            if (!byKey.TryGetValue("portal:" + pr.RefundId, out var row)) { found.Add(new(MoneyMismatchKind.PortalRefundUnknownHere, sellerOrderNumber, pr.RefundId, pr.AmountCents, 0)); continue; }
            if (row.Status is RefundStatus.Rejected or RefundStatus.Failed) { found.Add(new(MoneyMismatchKind.PortalRefundNotLiveHere, sellerOrderNumber, pr.RefundId, pr.AmountCents, 0)); continue; }
            if (row.AmountCents != pr.AmountCents) found.Add(new(MoneyMismatchKind.RefundAmountDiffers, sellerOrderNumber, pr.RefundId, pr.AmountCents, row.AmountCents));
        }
        foreach (var row in ours.Where(r => r.Key.StartsWith("portal:", StringComparison.Ordinal) && r.Status is RefundStatus.Reserved or RefundStatus.Succeeded && !listed.Contains(r.Key)))
            found.Add(new(MoneyMismatchKind.OurRefundNotAtPortal, sellerOrderNumber, row.Key["portal:".Length..], 0, row.AmountCents));
        var live = ours.Where(r => r.Status is RefundStatus.Reserved or RefundStatus.Succeeded).Sum(r => r.AmountCents);
        if (portal.RefundedCents != live) found.Add(new(MoneyMismatchKind.RefundedTotalDiffers, sellerOrderNumber, null, portal.RefundedCents, live));
        return found;
    }
}
