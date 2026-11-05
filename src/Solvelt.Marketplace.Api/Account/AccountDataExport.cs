using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Account;

/// <summary>
/// One module's part of "download my data" (decision M5-32): called with the account's own id, returns what the module holds about THAT buyer as plain data (no secrets, nothing about
/// other people). Register as a scoped service; the sections are merged by <see cref="AccountDataExportEndpoint"/>.
/// </summary>
public interface IAccountDataExporter
{
    string Section { get; }
    Task<object?> ExportAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// <c>POST /api/v1/account/data-export</c>: the signed-in buyer downloads what we hold about them as one JSON file. It needs a recent sign-in (the same rule as deleting the account, because
/// the file holds addresses, messages and orders), is never cached and is an attachment. Password hashes, session and email tokens and other people's data are never in it.
/// </summary>
public static class AccountDataExportEndpoint
{
    public static IEndpointRouteBuilder MapAccountDataExport(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/account/data-export", Export).RequireRateLimiting(RateLimitPolicies.Auth).AddEndpointFilter<RequireBuyerFilter>();
        return app;
    }

    private static async Task<IResult> Export(HttpContext ctx, IEnumerable<IAccountDataExporter> exporters, TimeProvider time, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        var who = ctx.Buyer();
        if (time.GetUtcNow() - who.AuthenticatedAt > AccountService.RecentSignIn) return Problems.Json(403, "reauthentication_required", "Confirm your password again first.");
        var sections = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var e in exporters) sections[e.Section] = await e.ExportAsync(who.AccountId, ct);
        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"solvelt-my-data.json\"";
        return Results.Json(new { generatedAt = time.GetUtcNow(), sections });
    }
}

/// <summary>The account itself and its address book.</summary>
public sealed class AccountProfileExporter(MarketplaceDb db) : IAccountDataExporter
{
    public string Section => "account";

    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
    {
        var a = await db.BuyerAccounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => new { x.Email, x.DisplayName, x.Phone, x.RelayEmail, x.CreatedAt, x.EmailVerifiedAt }).FirstOrDefaultAsync(ct);
        var addresses = await db.BuyerAddresses.AsNoTracking().Where(x => x.AccountId == accountId).OrderBy(x => x.Id)
            .Select(x => new { x.FullName, x.Line1, x.Line2, x.City, x.Region, x.PostalCode, x.Country, x.Phone }).ToListAsync(ct);
        return new { profile = a, addresses };
    }
}
