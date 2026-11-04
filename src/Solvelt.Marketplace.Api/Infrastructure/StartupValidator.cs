namespace Solvelt.Marketplace.Api.Infrastructure;

/// <summary>
/// Fixed, obviously-fake values used only in Development/Testing so the app runs without any env.
/// They are not secrets and are rejected outright in every other environment.
/// </summary>
public static class DevDefaults
{
    public const string PortalInboundSecret = "dev-only-portal-inbound-secret-not-for-production";
    public const string PortalWebhookSecret = "dev-only-portal-webhook-secret-not-for-production";
    public const string RelayDomain = "relay.solvelt.test";
    public const string SqliteConnection = "Data Source=marketplace-dev.db";
}

public static class StartupValidator
{
    public static bool IsDevLike(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Testing");

    /// <summary>Returns every configuration problem; empty means the configuration is acceptable.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();
        if (IsDevLike(env)) return problems;

        var inbound = config["Portal:InboundSecret"];
        var webhook = config["Portal:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(inbound)) problems.Add("Portal:InboundSecret (env Portal__InboundSecret) is required.");
        if (string.IsNullOrWhiteSpace(webhook)) problems.Add("Portal:WebhookSecret (env Portal__WebhookSecret) is required.");
        if (!string.IsNullOrWhiteSpace(inbound) && inbound == webhook)
            problems.Add("Portal:InboundSecret and Portal:WebhookSecret must differ; one key per purpose.");
        foreach (var (name, value) in new[] { ("Portal:InboundSecret", inbound), ("Portal:WebhookSecret", webhook) })
            if (value is { Length: > 0 and < 32 }) problems.Add($"{name} must be at least 32 characters.");
        if (inbound == DevDefaults.PortalInboundSecret || webhook == DevDefaults.PortalWebhookSecret)
            problems.Add("Development default secrets must not be used outside Development/Testing.");
        if (string.IsNullOrWhiteSpace(config["Portal:BaseUrl"])) problems.Add("Portal:BaseUrl (env Portal__BaseUrl) is required.");
        if (string.IsNullOrWhiteSpace(config["Marketplace:RelayDomain"])) problems.Add("Marketplace:RelayDomain (env Marketplace__RelayDomain) is required.");
        if (string.IsNullOrWhiteSpace(config["Marketplace:PublicBaseUrl"])) problems.Add("Marketplace:PublicBaseUrl (env Marketplace__PublicBaseUrl) is required.");
        if (!string.Equals(config["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
            problems.Add("Database:Provider must be Postgres outside Development/Testing.");
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Default")))
            problems.Add("ConnectionStrings:Default (env ConnectionStrings__Default) is required.");
        return problems;
    }
}
