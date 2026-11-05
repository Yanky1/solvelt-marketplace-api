using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>
/// M5's eraser for cancel requests and returns (module "order-cancellations-and-returns" of the PII registry, review Q1; decision M5-28): the buyer's and the seller's free text, the
/// buyer's idempotency key and the return label links are deleted when the account is erased. Status, reason code, quantities, ids and dates stay: they are the order's accounting and
/// audit trail and hold no personal data. Writes through the injected context, so it runs inside M1's erasure transaction. Idempotent.
/// </summary>
public sealed class AfterSalesAccountEraser(MarketplaceDb db) : IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        await db.OrderCancelRequests.Where(r => r.AccountId == accountId && (r.Note != null || r.DecisionNote != null))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null).SetProperty(r => r.DecisionNote, (string?)null), ct);
        await db.OrderReturns.Where(r => r.AccountId == accountId && (r.Comment != null || r.DecisionNote != null || r.ClientKey != null || r.LabelUrl != null || r.LabelTrackingUrl != null || r.LabelTrackingNumber != null || r.LabelCarrier != null))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Comment, (string?)null).SetProperty(r => r.DecisionNote, (string?)null).SetProperty(r => r.ClientKey, (string?)null)
                .SetProperty(r => r.LabelUrl, (string?)null).SetProperty(r => r.LabelUrlExpiresAt, (DateTimeOffset?)null).SetProperty(r => r.LabelTrackingUrl, (string?)null)
                .SetProperty(r => r.LabelTrackingNumber, (string?)null).SetProperty(r => r.LabelCarrier, (string?)null), ct);
    }
}
