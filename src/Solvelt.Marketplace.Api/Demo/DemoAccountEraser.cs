using Solvelt.Marketplace.Api.Account;

namespace Solvelt.Marketplace.Api.Demo;

/// <summary>
/// Account deletion removes demo rows too (M9). It is registered BEFORE the other modules' erasers, so a demo order, booking or thread disappears completely instead of being
/// kept in redacted form like a real one. It owns no entity of its own (the rows are the other modules' tables, marked), so it is listed in <see cref="PiiRegistry"/> with none.
/// </summary>
public sealed class DemoAccountEraser(DemoService demo) : IAccountEraser
{
    public Task EraseAsync(Guid accountId, CancellationToken ct) => demo.RemoveRowsAsync(accountId, ct);
}
