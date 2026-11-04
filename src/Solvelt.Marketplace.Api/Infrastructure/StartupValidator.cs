using Solvelt.Marketplace.Api.Auth;

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
    public const string GuestLinkKey = "dev-only-guest-link-key-not-for-production";
    public const string QuoteKey = "dev-only-checkout-quote-key-not-for-production";
    public const string AdminApiKey = "dev-only-admin-api-key-not-for-production";
    public const string UnsubscribeKey = "dev-only-unsubscribe-key-not-for-production";
    public const string StripeWebhookSecret = "whsec_dev_only_not_for_production_0000000";
    public const string PublicBaseUrl = "http://localhost:3000";
    public const string RelayDomain = "relay.solvelt.test";
    public const string SqliteConnection = "Data Source=marketplace-dev.db";
}

public static class StartupValidator
{
    public static bool IsDevLike(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Testing");

    public const int LeaseMarginSeconds = 30;

    private static int IntOr(string? value, int fallback) => int.TryParse(value, out var n) ? n : fallback;

    /// <summary>Returns every configuration problem; empty means the configuration is acceptable.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();
        // Test mode only, in EVERY environment (also Development and Testing): a live Stripe key stops the application.
        foreach (var (name, value, prefixes) in new[] { ("Stripe:SecretKey", config["Stripe:SecretKey"], new[] { "sk_test_", "rk_test_" }), ("Stripe:PublishableKey", config["Stripe:PublishableKey"], new[] { "pk_test_" }) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Contains("_live_", StringComparison.OrdinalIgnoreCase)) problems.Add($"{name} is a LIVE key. Only Stripe test keys are allowed (test mode only).");
            else if (!prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal))) problems.Add($"{name} must be a Stripe test key ({string.Join(" or ", prefixes)}...).");
        }
        // In every environment: a handler (a portal call, a mail) must always finish well inside the lease that protects its message,
        // otherwise a second dispatcher takes the message over while the first is still sending it (review of af8d339, item 7).
        var lease = IntOr(config["Outbox:LeaseSeconds"], 120);
        var portalDeadline = Math.Clamp(IntOr(config["Portal:TotalDeadlineSeconds"], 30), 1, 60);
        var emailTimeout = IntOr(config["Email:SendTimeoutSeconds"], 20);
        if (lease < portalDeadline + LeaseMarginSeconds) problems.Add($"Outbox:LeaseSeconds ({lease}) must be at least Portal:TotalDeadlineSeconds ({portalDeadline}) + {LeaseMarginSeconds}.");
        if (lease < emailTimeout + LeaseMarginSeconds) problems.Add($"Outbox:LeaseSeconds ({lease}) must be at least Email:SendTimeoutSeconds ({emailTimeout}) + {LeaseMarginSeconds}.");
        if (emailTimeout < 1) problems.Add("Email:SendTimeoutSeconds must be at least 1.");
        if (IsDevLike(env)) return problems;

        var keys = new (string Name, string Env, string? Value)[]
        {
            ("Portal:InboundSecret", "Portal__InboundSecret", config["Portal:InboundSecret"]),
            ("Portal:WebhookSecret", "Portal__WebhookSecret", config["Portal:WebhookSecret"]),
            ("Web:ForwardingKey", "Web__ForwardingKey", config["Web:ForwardingKey"]),
            ("Admin:ApiKey", "Admin__ApiKey", config["Admin:ApiKey"]),
            ("Auth:EmailLinkKey", "Auth__EmailLinkKey", config["Auth:EmailLinkKey"]),
            ("Auth:GuestLinkKey", "Auth__GuestLinkKey", config["Auth:GuestLinkKey"]),
            ("Checkout:QuoteKey", "Checkout__QuoteKey", config["Checkout:QuoteKey"]),
            ("Notifications:UnsubscribeKey", "Notifications__UnsubscribeKey", config["Notifications:UnsubscribeKey"]),
            ("Stripe:WebhookSecret", "Stripe__WebhookSecret", config["Stripe:WebhookSecret"]),
        };
        var devDefaults = new[] { DevDefaults.PortalInboundSecret, DevDefaults.PortalWebhookSecret, DevDefaults.WebForwardingKey, DevDefaults.AdminApiKey, DevDefaults.EmailLinkKey, DevDefaults.GuestLinkKey, DevDefaults.QuoteKey, DevDefaults.UnsubscribeKey, DevDefaults.StripeWebhookSecret };
        foreach (var (name, envName, value) in keys)
        {
            // The admin key is optional: unset disables /api/admin/*. The Stripe webhook secret is required only once a Stripe key is set (checked below).
            // When set, both are held to the same rules as every other key.
            if (string.IsNullOrWhiteSpace(value)) { if (name is not ("Admin:ApiKey" or "Stripe:WebhookSecret")) problems.Add($"{name} (env {envName}) is required."); continue; }
            if (value.Length < 32) problems.Add($"{name} must be at least 32 characters.");
            // Any dev default in any slot is rejected, so swapped defaults cannot pass.
            if (devDefaults.Contains(value)) problems.Add($"{name}: Development default secrets must not be used outside Development/Testing.");
        }
        // The Stripe keys are optional here (checked above) but must also differ from every other key and from each other: a Stripe key must never double as one of ours.
        var present = keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).ToList();
        foreach (var (name, envName) in new[] { ("Stripe:SecretKey", "Stripe__SecretKey"), ("Stripe:PublishableKey", "Stripe__PublishableKey") })
            if (config[name] is { Length: > 0 } sv && !string.IsNullOrWhiteSpace(sv)) present.Add((name, envName, sv));
        for (var i = 0; i < present.Count; i++)
            for (var j = i + 1; j < present.Count; j++)
                if (present[i].Value == present[j].Value)
                    problems.Add($"{present[i].Name} and {present[j].Name} must differ; one key per purpose.");
        if (!string.IsNullOrWhiteSpace(config["Stripe:SecretKey"]) && string.IsNullOrWhiteSpace(config["Stripe:WebhookSecret"]))
            problems.Add("Stripe:WebhookSecret (env Stripe__WebhookSecret) is required when Stripe:SecretKey is set.");
        if (string.IsNullOrWhiteSpace(config["Portal:BaseUrl"])) problems.Add("Portal:BaseUrl (env Portal__BaseUrl) is required.");
        else if (BaseUrlProblem(config["Portal:BaseUrl"]!, requireHttps: true) is { } baseUrlProblem) problems.Add(baseUrlProblem);
        var catalogSource = config["Catalog:Source"];
        if (string.Equals(catalogSource, "Fixture", StringComparison.OrdinalIgnoreCase)) problems.Add("Catalog:Source=Fixture (seed data) must not be used outside Development/Testing.");
        else if (string.Equals(catalogSource, "Http", StringComparison.OrdinalIgnoreCase)) problems.Add("Catalog:Source=Http is not available yet (the portal catalog feed, contract change C1, is not built): use None.");
        else if (!string.IsNullOrWhiteSpace(catalogSource) && !string.Equals(catalogSource, "None", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Catalog:Source '{catalogSource}' is not valid (None; Http once the portal feed exists).");
        // Mail: without a provider no buyer could verify an address or reset a password, so start-up fails instead of letting mail die in the outbox (M1-50).
        var provider = config["Email:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) problems.Add("Email:Provider (env Email__Provider) is required: Smtp.");
        else if (!string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase)) problems.Add($"Email:Provider '{provider}' is not valid (Smtp).");
        else
        {
            if (string.IsNullOrWhiteSpace(config["Email:SmtpHost"])) problems.Add("Email:SmtpHost (env Email__SmtpHost) is required for the Smtp provider.");
            if (string.IsNullOrWhiteSpace(config["Email:From"])) problems.Add("Email:From (env Email__From) is required for the Smtp provider.");
            else if (AuthValidation.EmailProblem(config["Email:From"]) is not null) problems.Add("Email:From must be an email address.");
        }
        // The operations endpoints are not for the public host (O-12): with a key they also need their own host.
        if (!string.IsNullOrWhiteSpace(config["Admin:ApiKey"]))
        {
            var adminHost = config["Admin:Host"];
            if (string.IsNullOrWhiteSpace(adminHost)) problems.Add("Admin:Host (env Admin__Host) is required when Admin:ApiKey is set: /api/admin must not be reachable through the public host.");
            else if (Uri.TryCreate(config["Marketplace:PublicBaseUrl"], UriKind.Absolute, out var pub) && string.Equals(adminHost.Trim(), pub.Authority, StringComparison.OrdinalIgnoreCase))
                problems.Add("Admin:Host must not be the public host (Marketplace:PublicBaseUrl).");
        }
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
