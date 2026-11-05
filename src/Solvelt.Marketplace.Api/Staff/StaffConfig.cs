using System.Net;

namespace Solvelt.Marketplace.Api.Staff;

/// <summary>Staff area settings (<c>Staff:*</c>, decisions M7-01..M7-05).</summary>
public sealed class StaffOptions
{
    /// <summary>
    /// One-time bootstrap secret (>= 32 characters). While the database has NO staff account the API creates the first admin from <see cref="BootstrapEmail"/> and accepts this token ONCE to
    /// register that admin's first passkey. There is never a default password or a default account. Remove the setting after the first sign-in.
    /// </summary>
    public string? BootstrapToken { get; set; }
    public string? BootstrapEmail { get; set; }
    public string BootstrapName { get; set; } = "First admin";
    /// <summary>Optional. IP addresses or CIDR ranges that may reach <c>/api/v1/staff</c>; empty means no restriction. Everyone else gets 404.</summary>
    public string[] AllowedIps { get; set; } = [];
    public int SessionIdleMinutes { get; set; } = 30;
    public int SessionAbsoluteHours { get; set; } = 8;
    /// <summary>How recent a passkey check must be for the sensitive actions (revealing PII, locking, refunds, account management).</summary>
    public int StepUpMinutes { get; set; } = 5;
    public int EnrolmentHours { get; set; } = 24;
    public int MaxPasskeys { get; set; } = 5;
    /// <summary>Audit entries are deleted after this many days.</summary>
    public int AuditRetentionDays { get; set; } = 730;
    /// <summary>Break-glass reveals one staff member may make per hour (counted from the audit log).</summary>
    public int RevealsPerHour { get; set; } = 20;
}

public static class StaffConfig
{
    /// <summary>Problems with the staff settings (every environment).</summary>
    public static IEnumerable<string> Problems(IConfiguration config)
    {
        var token = config["Staff:BootstrapToken"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            if (token.Length < 32) yield return "Staff:BootstrapToken must be at least 32 characters.";
            else if (token.Distinct().Count() < 12) yield return "Staff:BootstrapToken is too repetitive: use at least 12 different characters (for example `openssl rand -base64 48`).";
            var email = config["Staff:BootstrapEmail"];
            if (string.IsNullOrWhiteSpace(email) || Auth.AuthValidation.EmailProblem(email) is not null) yield return "Staff:BootstrapEmail (env Staff__BootstrapEmail) must be a valid address when Staff:BootstrapToken is set.";
        }
        // An allow-list that is SET but blank must never mean "everyone" (security review MED-1): a blank entry is refused at start instead of being dropped.
        foreach (var entry in config.GetSection("Staff:AllowedIps").GetChildren().Select(c => c.Value))
        {
            if (string.IsNullOrWhiteSpace(entry)) yield return "Staff:AllowedIps has a blank entry. Remove the setting to allow every address, or give addresses or CIDR ranges.";
            else if (!IpAllowList.TryParseEntry(entry, out _)) yield return $"Staff:AllowedIps has an entry that is neither an IP address nor a CIDR range: '{entry}'.";
        }
    }
}

/// <summary>A list of addresses and CIDR ranges. Empty allows everyone.</summary>
public sealed class IpAllowList(IReadOnlyList<IPNetwork> networks)
{
    public bool IsEmpty => networks.Count == 0;
    public static IpAllowList FromConfig(IConfiguration config) => From(config.GetSection("Staff:AllowedIps").GetChildren().Select(c => c.Value ?? ""));

    public static bool TryParseEntry(string entry, out IPNetwork network)
    {
        network = default;
        entry = entry.Trim();
        if (entry.Contains('/')) return IPNetwork.TryParse(entry, out network);
        if (!IPAddress.TryParse(entry, out var ip)) return false;
        network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    public static IpAllowList From(IEnumerable<string> entries)
        => new(entries.Select(e => !string.IsNullOrWhiteSpace(e) && TryParseEntry(e, out var n) ? n : throw new InvalidOperationException("Invalid or blank Staff:AllowedIps entry.")).ToList());

    public bool Allows(IPAddress? address)
    {
        if (IsEmpty) return true;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(n => n.Contains(address));
    }
}

/// <summary>
/// The IP allow-list as middleware that runs BEFORE routing and body limits (security review LOW 4): an address outside <c>Staff:AllowedIps</c> gets the same 404 whatever it sends
/// (a wrong method, an oversized body), so the staff area does not exist for it.
/// </summary>
public sealed class StaffNetworkMiddleware(RequestDelegate next, IConfiguration config, Infrastructure.ClientIpResolver ips)
{
    private readonly IpAllowList _list = IpAllowList.FromConfig(config);

    public Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/staff") || _list.Allows(ips.ResolveAddress(ctx))) return next(ctx);
        return Infrastructure.ProblemExceptionMiddleware.Write(ctx, 404, "not_found", "Not found.");
    }
}
