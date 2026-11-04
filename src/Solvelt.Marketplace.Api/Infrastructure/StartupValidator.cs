namespace Solvelt.Marketplace.Api.Infrastructure;

/// <summary>
/// Fixed, obviously-fake values used only in Development/Testing so the app runs without any env.
/// They are not secrets and are rejected outright in every other environment.
/// </summary>
public static class DevDefaults
{
    public const string PortalInboundSecret = "dev-only-portal-inbound-secret-not-for-production";
    public const string PortalWebhookSecret = "dev-only-portal-webhook-secret-not-for-production";
    public const string WebForwardingKey = "dev-only-web-forwarding-key-not-for-production";
    public const string EmailLinkKey = "dev-only-email-link-key-not-for-production";
    public const string AdminApiKey = "dev-only-admin-api-key-not-for-production";
    public const string PublicBaseUrl = "http://localhost:3000";
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

        var keys = new (string Name, string Env, string? Value)[]
        {
            ("Portal:InboundSecret", "Portal__InboundSecret", config["Portal:InboundSecret"]),
            ("Portal:WebhookSecret", "Portal__WebhookSecret", config["Portal:WebhookSecret"]),
            ("Web:ForwardingKey", "Web__ForwardingKey", config["Web:ForwardingKey"]),
            ("Admin:ApiKey", "Admin__ApiKey", config["Admin:ApiKey"]),
            ("Auth:EmailLinkKey", "Auth__EmailLinkKey", config["Auth:EmailLinkKey"]),
        };
        var devDefaults = new[] { DevDefaults.PortalInboundSecret, DevDefaults.PortalWebhookSecret, DevDefaults.WebForwardingKey, DevDefaults.AdminApiKey, DevDefaults.EmailLinkKey };
        foreach (var (name, envName, value) in keys)
        {
            // The admin key is optional: unset disables /api/admin/*. When set it is held to the same rules as every other key.
            if (string.IsNullOrWhiteSpace(value)) { if (name != "Admin:ApiKey") problems.Add($"{name} (env {envName}) is required."); continue; }
            if (value.Length < 32) problems.Add($"{name} must be at least 32 characters.");
            // Any dev default in any slot is rejected, so swapped defaults cannot pass.
            if (devDefaults.Contains(value)) problems.Add($"{name}: Development default secrets must not be used outside Development/Testing.");
        }
        var present = keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).ToList();
        for (var i = 0; i < present.Count; i++)
            for (var j = i + 1; j < present.Count; j++)
                if (present[i].Value == present[j].Value)
                    problems.Add($"{present[i].Name} and {present[j].Name} must differ; one key per purpose.");
        if (string.IsNullOrWhiteSpace(config["Portal:BaseUrl"])) problems.Add("Portal:BaseUrl (env Portal__BaseUrl) is required.");
        else if (BaseUrlProblem(config["Portal:BaseUrl"]!, requireHttps: true) is { } baseUrlProblem) problems.Add(baseUrlProblem);
        var catalogSource = config["Catalog:Source"];
        if (string.Equals(catalogSource, "Fixture", StringComparison.OrdinalIgnoreCase)) problems.Add("Catalog:Source=Fixture (seed data) must not be used outside Development/Testing.");
        else if (!string.IsNullOrWhiteSpace(catalogSource) && !string.Equals(catalogSource, "None", StringComparison.OrdinalIgnoreCase) && !string.Equals(catalogSource, "Http", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Catalog:Source '{catalogSource}' is not valid (None or Http).");
        if (string.IsNullOrWhiteSpace(config["Marketplace:RelayDomain"])) problems.Add("Marketplace:RelayDomain (env Marketplace__RelayDomain) is required.");
        if (string.IsNullOrWhiteSpace(config["Marketplace:PublicBaseUrl"])) problems.Add("Marketplace:PublicBaseUrl (env Marketplace__PublicBaseUrl) is required.");
        if (!string.Equals(config["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
            problems.Add("Database:Provider must be Postgres outside Development/Testing.");
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Default")))
            problems.Add("ConnectionStrings:Default (env ConnectionStrings__Default) is required.");
        return problems;
    }

    /// <summary>
    /// The portal base URL is an origin: absolute, https outside development, no credentials, no path (the signed path is the
    /// absolute path we send), no query, no fragment. Returns the problem, or null when acceptable.
    /// </summary>
    public static string? BaseUrlProblem(string value, bool requireHttps)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "Portal:BaseUrl must be an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps && (requireHttps || uri.Scheme != Uri.UriSchemeHttp)) return requireHttps ? "Portal:BaseUrl must be https." : "Portal:BaseUrl must be http(s).";
        if (uri.UserInfo.Length > 0) return "Portal:BaseUrl must not contain credentials.";
        if (uri.AbsolutePath != "/") return "Portal:BaseUrl must not contain a path.";
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return "Portal:BaseUrl must not contain a query or fragment.";
        return null;
    }
}
