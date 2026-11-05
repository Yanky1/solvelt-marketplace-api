using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Staff;

/// <summary>What a staff member may do (decision M7-02). The matrix is code and is also written out in docs/STAFF.md; a test keeps the two equal.</summary>
public static class StaffPermissions
{
    public const string BuyersView = "buyers.view", BuyersReveal = "buyers.reveal", BuyersLock = "buyers.lock", BuyersSignOut = "buyers.sign_out",
        OrdersView = "orders.view", PaymentsView = "payments.view", RefundsRetry = "refunds.retry",
        IngestsView = "ingests.view", IngestsRetry = "ingests.retry", OutboxView = "outbox.view", OutboxRetry = "outbox.retry",
        DeadLettersView = "deadletters.view", DeadLettersRetry = "deadletters.retry", StripeFailuresView = "stripe.view",
        StaffManage = "staff.manage", AuditView = "audit.view";

    private const string S = StaffRoleNames.Support, F = StaffRoleNames.Finance, A = StaffRoleNames.Admin;

    public static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
    {
        [BuyersView] = [S, F, A],
        [BuyersReveal] = [S, A],
        [BuyersLock] = [S, A],
        [BuyersSignOut] = [S, A],
        [OrdersView] = [S, F, A],
        [PaymentsView] = [F, A],
        [RefundsRetry] = [F, A],
        [IngestsView] = [S, F, A],
        [IngestsRetry] = [S, A],
        [OutboxView] = [S, F, A],
        [OutboxRetry] = [A],
        [DeadLettersView] = [S, F, A],
        [DeadLettersRetry] = [A],
        [StripeFailuresView] = [F, A],
        [StaffManage] = [A],
        [AuditView] = [A],
    };

    public static bool Allowed(string role, string permission) => Matrix.TryGetValue(permission, out var roles) && roles.Contains(role);
    public static IReadOnlyList<string> For(string role) => Matrix.Where(m => m.Value.Contains(role)).Select(m => m.Key).Order().ToList();
    public static bool IsRole(string? role) => role is not null && StaffRoleNames.All.Contains(role);
}

/// <summary>The signed-in staff member, set by <see cref="RequireStaffFilter"/>.</summary>
public sealed record StaffPrincipal(Guid StaffId, Guid SessionId, string Email, string DisplayName, string Role, DateTimeOffset AuthenticatedAt)
{
    public bool Can(string permission) => StaffPermissions.Allowed(Role, permission);
    public bool Recent(DateTimeOffset now, int minutes) => now - AuthenticatedAt <= TimeSpan.FromMinutes(Math.Max(1, minutes));
}
