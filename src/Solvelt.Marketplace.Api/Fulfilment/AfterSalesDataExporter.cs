using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Fulfilment;

/// <summary>M5's part of "download my data" (decision M5-32): the buyer's cancel requests and returns (what they wrote, what the seller answered, the status).</summary>
public sealed class AfterSalesDataExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "cancellations-and-returns";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var orderNumbers = await db.Orders.AsNoTracking().Where(o => o.AccountId == accountId).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var cancels = await db.OrderCancelRequests.AsNoTracking().Where(r => r.AccountId == accountId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var returns = await db.OrderReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.AccountId == accountId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        return new
        {
            cancelRequests = cancels.Select(r => new { order = orderNumbers.GetValueOrDefault(r.OrderId), r.Reason, r.Note, r.Status, sellerNote = r.DecisionNote, r.CreatedAt }),
            returns = returns.Select(r => new { order = orderNumbers.GetValueOrDefault(r.OrderId), r.Reason, r.Comment, r.Status, sellerNote = r.DecisionNote, r.CreatedAt, lines = r.Lines.Select(l => new { l.LineId, l.Quantity }) }),
        };
    }
}
