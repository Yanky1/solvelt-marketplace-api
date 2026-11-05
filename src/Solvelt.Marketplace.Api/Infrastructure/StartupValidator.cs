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
    public const string WebAuthnRpId = "localhost";
    public const string WebAuthnOrigin = "http://localhost:3000";
}

public static class StartupValidator
{
    /// <summary>The only environment names that may run with <c>Demo:Enabled</c> on (compared case-insensitively after trimming).</summary>
    public static readonly HashSet<string> DemoAllowedEnvironments = new(["Development", "Testing", "Staging"], StringComparer.OrdinalIgnoreCase);

    public static bool IsDevLike(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Testing");

    public const int LeaseMarginSeconds = 30;

    private static int IntOr(string? value, int fallback) => int.TryParse(value, out var n) ? n : fallback;

    /// <summary>Things that are allowed but should not last: logged at start-up, never fatal.</summary>
    public static IReadOnlyList<string> Warnings(IConfiguration config, IHostEnvironment env)
    {
        var warnings = new List<string>();
        if (!IsDevLike(env) && string.IsNullOrWhiteSpace(config["Portal:ImageKey"]))
            warnings.Add("Portal:ImageKey (env Portal__ImageKey) is not set: image downloads are signed with Portal:InboundSecret. Set a separate key before the portal sets Orders:ImageAcceptInboundKey=false (then also set Portal:RequireImageKey=true).");
        return warnings;
    }

    /// <summary>
    /// The passkey settings (decision M1-80). In production the RP id and the origins are required and the origins must be https; in every environment, what is set must be
    /// consistent: an RP id that is a plain lower-case domain (not an address, no scheme or port) and origins that are bare scheme://host[:port] whose host is the RP id or below it.
    /// </summary>
    /// <summary>
    /// A small built-in list of public suffixes that are not a single label (the single-label ones are refused by the dot rule): the common country second-level domains and the shared hosting
    /// suffixes where unrelated sites live under one name. Not the whole Public Suffix List; a registrable domain under any of them is fine.
    /// </summary>
    public static readonly HashSet<string> PublicSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "ac.uk", "gov.uk", "sch.uk", "com.au", "net.au", "org.au", "edu.au", "gov.au", "co.nz", "org.nz", "net.nz", "co.jp", "or.jp", "ne.jp", "ac.jp",
        "com.br", "net.br", "org.br", "com.mx", "com.ar", "com.cn", "net.cn", "org.cn", "com.hk", "com.sg", "com.tw", "com.tr", "com.ua", "co.za", "co.in", "net.in", "org.in", "co.il", "co.kr", "or.kr", "co.id",
        "github.io", "gitlab.io", "herokuapp.com", "vercel.app", "netlify.app", "pages.dev", "workers.dev", "azurewebsites.net", "cloudapp.net", "cloudfront.net", "amazonaws.com", "appspot.com", "web.app",
        "firebaseapp.com", "onrender.com", "fly.dev", "railway.app", "ngrok.io", "ngrok-free.app", "blogspot.com", "wordpress.com", "wixsite.com", "myshopify.com", "squarespace.com", "repl.co", "glitch.me",
        "now.sh", "surge.sh", "render.com", "up.railway.app", "trycloudflare.com", "duckdns.org", "no-ip.org", "dyndns.org",
    };

    public static IReadOnlyList<string> WebAuthnProblems(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();
        var rpId = config["Auth:WebAuthnRpId"];
        var origins = config.GetSection("Auth:WebAuthnOrigins").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        var production = !IsDevLike(env);
        if (production && string.IsNullOrWhiteSpace(rpId)) problems.Add("Auth:WebAuthnRpId (env Auth__WebAuthnRpId) is required in production: the domain the web site is served from, for example solvelt.com.");
        if (production && origins.Count == 0) problems.Add("Auth:WebAuthnOrigins (env Auth__WebAuthnOrigins__0, __1, ...) is required in production: the https origins of the web site.");
        if (!string.IsNullOrWhiteSpace(rpId))
        {
            var id = rpId.Trim();
            if (id != id.ToLowerInvariant() || id.Contains("://") || id.Contains(':') || id.Contains('/') || System.Net.IPAddress.TryParse(id, out _) || id.StartsWith('.') || id.EndsWith('.') || id.Any(char.IsWhiteSpace))
                problems.Add("Auth:WebAuthnRpId must be a lower-case domain name without scheme, port or path, and not an IP address.");
            else if (production && !id.Contains('.')) problems.Add("Auth:WebAuthnRpId must be a registrable domain in production (not localhost).");
            else if (production && PublicSuffixes.Contains(id)) problems.Add($"Auth:WebAuthnRpId '{id}' is a public suffix: every site under it could use passkeys of this site. Use the registrable domain the site is served from, for example solvelt.com.");
        }
        foreach (var o in origins)
        {
            if (!Uri.TryCreate(o, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/") || o.EndsWith('/'))
            { problems.Add($"Auth:WebAuthnOrigins entry '{o}' must be a bare origin such as https://www.example.com (scheme, host and optional port, no path or trailing slash)."); continue; }
            if (production && uri.Scheme != Uri.UriSchemeHttps) problems.Add($"Auth:WebAuthnOrigins entry '{o}' must be https in production.");
            if (!string.IsNullOrWhiteSpace(rpId) && uri.Host != rpId.Trim() && !uri.Host.EndsWith("." + rpId.Trim(), StringComparison.Ordinal))
                problems.Add($"Auth:WebAuthnOrigins entry '{o}' is not on the RP id '{rpId.Trim()}' (its host must be the RP id or below it).");
        }
        return problems;
    }

    /// <summary>Everything start-up checks: the shared validation plus the passkey settings (M1-80). Program.cs calls this.</summary>
    public static IReadOnlyList<string> AllProblems(IConfiguration config, IHostEnvironment env) => Validate(config, env).Concat(WebAuthnProblems(config, env)).ToList();

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
        if (string.Equals(config["Stripe:FakeCaptureOnWebhook"], "true", StringComparison.OrdinalIgnoreCase) && !IsDevLike(env))
            problems.Add("Stripe:FakeCaptureOnWebhook (env Stripe__FakeCaptureOnWebhook) is for Development/Testing with the in-process fake payment provider only.");
        // Demo data is test-mode data (M9). An ALLOWLIST, not a denylist (orchestrator, M9-06): it may be switched on only when the environment name is exactly Development, Testing or Staging
        // (trimmed, case-insensitive). Anything else refuses, whatever it is called ("Prod", " Production", "Live", an unset name which is Production): a denylist would let a misspelt name
        // of the real site start with demo data on. There is no override. Checked first, before the dev-like early return below.
        // The value is read by the SAME function the application uses (DemoOptions.Parse: bool.TryParse, which also trims control characters such as a trailing NUL, so "true\0" is TRUE for both;
        // review LOW-3). Blank is "not set" (an empty Demo__Enabled= in a container template). A non-blank value that is not a boolean refuses the start in every environment: a typo must be seen.
        var rawDemo = config["Demo:Enabled"];
        var demoOn = DemoOptions.Parse(rawDemo);
        if (!string.IsNullOrWhiteSpace(rawDemo) && !bool.TryParse(rawDemo, out _)) problems.Add("Demo:Enabled (env Demo__Enabled) is not a valid true or false.");
        if (demoOn && !DemoAllowedEnvironments.Contains(env.EnvironmentName?.Trim() ?? ""))
            problems.Add("Demo:Enabled (env Demo__Enabled) must not be true here: demo data is test-mode data and is allowed only when ASPNETCORE_ENVIRONMENT is Development, Testing or Staging (a test site runs as Staging).");
        // In every environment: a handler (a portal call, a mail) must always finish well inside the lease that protects its message,
        // otherwise a second dispatcher takes the message over while the first is still sending it (review of af8d339, item 7).
        var lease = IntOr(config["Outbox:LeaseSeconds"], 120);
        var portalDeadline = Math.Clamp(IntOr(config["Portal:TotalDeadlineSeconds"], 30), 1, 60);
        var emailTimeout = IntOr(config["Email:SendTimeoutSeconds"], 20);
        if (lease < portalDeadline + LeaseMarginSeconds) problems.Add($"Outbox:LeaseSeconds ({lease}) must be at least Portal:TotalDeadlineSeconds ({portalDeadline}) + {LeaseMarginSeconds}.");
        if (lease < emailTimeout + LeaseMarginSeconds) problems.Add($"Outbox:LeaseSeconds ({lease}) must be at least Email:SendTimeoutSeconds ({emailTimeout}) + {LeaseMarginSeconds}.");
        if (emailTimeout < 1) problems.Add("Email:SendTimeoutSeconds must be at least 1.");
        problems.AddRange(Solvelt.Marketplace.Api.Staff.StaffConfig.Problems(config));   // M7: bootstrap token and IP allow-list (every environment)
        problems.AddRange(Solvelt.Marketplace.Api.Services.ServicesModule.ConfigProblems(config));   // M4: availability window and limits (every environment)
        if (IsDevLike(env)) return problems;

        var keys = new (string Name, string Env, string? Value)[]
        {
            ("Portal:InboundSecret", "Portal__InboundSecret", config["Portal:InboundSecret"]),
            ("Portal:WebhookSecret", "Portal__WebhookSecret", config["Portal:WebhookSecret"]),
            ("Portal:ImageKey", "Portal__ImageKey", config["Portal:ImageKey"]),
            ("Web:ForwardingKey", "Web__ForwardingKey", config["Web:ForwardingKey"]),
            ("Admin:ApiKey", "Admin__ApiKey", config["Admin:ApiKey"]),
            ("Staff:BootstrapToken", "Staff__BootstrapToken", config["Staff:BootstrapToken"]),
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
            if (string.IsNullOrWhiteSpace(value))
            {
                if (name == "Portal:ImageKey") { if (string.Equals(config["Portal:RequireImageKey"], "true", StringComparison.OrdinalIgnoreCase)) problems.Add($"{name} (env {envName}) is required because Portal:RequireImageKey is true (the portal no longer accepts the inbound key for image downloads)."); continue; }
                if (name is not ("Admin:ApiKey" or "Stripe:WebhookSecret" or "Staff:BootstrapToken")) problems.Add($"{name} (env {envName}) is required.");
                continue;
            }
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
        else if (!string.IsNullOrWhiteSpace(catalogSource) && !string.Equals(catalogSource, "None", StringComparison.OrdinalIgnoreCase) && !string.Equals(catalogSource, "Http", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Catalog:Source '{catalogSource}' is not valid (Http, the default; or None).");
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
        // the origin of the one-click List-Unsubscribe URL in every notification mail: a wrong default would put dead links into mail clients' unsubscribe buttons (M5 review, info)
        if (!Uri.TryCreate(config["Notifications:PublicApiBaseUrl"], UriKind.Absolute, out var apiBase) || apiBase.Scheme != Uri.UriSchemeHttps)
            problems.Add("Notifications:PublicApiBaseUrl (env Notifications__PublicApiBaseUrl) is required and must be an absolute https URL.");
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
