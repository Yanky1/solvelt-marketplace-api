using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Notifications;
using Solvelt.Marketplace.Api.Outbox;

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

    /// <summary>One export per account in this window (review MED-2): the file is the largest thing the API builds for one request, and an attacker with a stolen session should not be able to loop it.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private static async Task<IResult> Export(HttpContext ctx, IEnumerable<IAccountDataExporter> exporters, MarketplaceDb db, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        var who = ctx.Buyer();
        var now = time.GetUtcNow();
        if (now - who.AuthenticatedAt > AccountService.RecentSignIn) return Problems.Json(403, "reauthentication_required", "Confirm your password again first.");

        // The earlier export is read from its own notification (kind data_exported): no table of its own, and it is the buyer's record that the file was downloaded.
        var since = now.Subtract(MinInterval);
        var last = await db.Notifications.AsNoTracking().Where(n => n.AccountId == who.AccountId && n.Kind == NotificationKinds.DataExported && n.CreatedAt > since)
            .OrderByDescending(n => n.CreatedAt).Select(n => (DateTimeOffset?)n.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is { } at) return Tooquick(ctx, at + MinInterval - now);

        var sections = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var e in exporters) sections[e.Section] = await e.ExportAsync(who.AccountId, ct);

        // One notification (in the centre and by email, always on: it is about the account's security) and one log line with the counts, never the content. The unique key is the ten-minute
        // window, so two requests that both passed the check above cannot both be answered: the second one's insert is refused.
        var bucket = now.ToUnixTimeSeconds() / (long)MinInterval.TotalSeconds;
        var outbox = new OutboxWriter(db, time);
        Notifier.AddForAccount(db, outbox, time, who.AccountId, NotificationKinds.DataExported, $"data-export:{who.AccountId:N}:{bucket}", null, null, new Dictionary<string, object?>());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Tooquick(ctx, MinInterval); }
        logs.CreateLogger("Solvelt.Marketplace.Api.Account.DataExport").LogInformation("Account data export: account {AccountId}, sections {Counts}", who.AccountId, string.Join(", ", sections.Select(kv => $"{kv.Key}={Count(kv.Value)}")));

        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"solvelt-my-data.json\"";
        return Results.Json(new { generatedAt = now, sections });
    }

    private static IResult Tooquick(HttpContext ctx, TimeSpan wait)
    {
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problems.Json(429, "export_rate_limited", "You can download your data once every ten minutes. Please try again later.");
    }

    /// <summary>How many entries a section holds, for the log: the items of a list, or the items of the lists in an object. Never the content.</summary>
    internal static int Count(object? section)
    {
        if (section is null) return 0;
        var e = JsonSerializer.SerializeToElement(section);
        return e.ValueKind switch
        {
            JsonValueKind.Array => e.GetArrayLength(),
            JsonValueKind.Object => e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).Sum(p => p.Value.GetArrayLength()),
            _ => 1,
        };
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
            .Select(x => new { x.FullName, x.Line1, x.Line2, x.City, x.Region, x.PostalCode, x.Country, x.Phone, demo = x.IsDemo }).ToListAsync(ct);
        return new { profile = a, addresses };
    }
}
