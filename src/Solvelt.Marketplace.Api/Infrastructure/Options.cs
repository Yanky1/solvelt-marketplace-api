namespace Solvelt.Marketplace.Api.Infrastructure;

public sealed class PortalOptions
{
    public string? BaseUrl { get; set; }
    /// <summary>The portal's PUBLIC API origin (its <c>Shipping:PublicBaseUrl</c>): the only origin a return-label link may have. Defaults to <see cref="BaseUrl"/>.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Signs our calls to the portal (portal: Orders:InboundSecret).</summary>
    public string? InboundSecret { get; set; }
    /// <summary>Verifies portal webhooks (portal: Orders:OutboundSecret).</summary>
    public string? WebhookSecret { get; set; }
    /// <summary>
    /// Signs the image downloads (<c>GET /catalog/images/{id}</c>) with a key of their own, distinct from every other key. Until the portal stops accepting the
    /// inbound key for images (portal <c>Orders:ImageAcceptInboundKey=false</c>) it is optional and image calls fall back to <see cref="InboundSecret"/> with a
    /// start-up warning; set <see cref="RequireImageKey"/> when the portal has switched.
    /// </summary>
    public string? ImageKey { get; set; }
    /// <summary>Production refuses to start without <see cref="ImageKey"/> (set it when the portal no longer accepts the inbound key for images).</summary>
    public bool RequireImageKey { get; set; }
    /// <summary>Per-attempt deadline in seconds (contract: at most 30).</summary>
    public int AttemptTimeoutSeconds { get; set; } = 30;
    /// <summary>Days the raw payload of an inbound portal event is kept (parked events: until applied).</summary>
    public int InboundPayloadRetentionDays { get; set; } = 30;
    /// <summary>
    /// Event types whose payload carries live driver positions or short-lived proof URLs (CONTRACT.md §5: positions at most 24 h, proof URLs never
    /// kept past expiry). Their payload is dropped after <see cref="InboundShortRetentionHours"/> even when parked (a parked one then expires).
    /// </summary>
    /// <summary>A parked event whose aggregate never appears expires after this many days: it becomes <c>skipped</c> and its payload is dropped.</summary>
    public int InboundParkedMaxAgeDays { get; set; } = 30;
    /// <summary>A parked event older than this many days is reported by the maintenance job (a warning with counts, never payloads).</summary>
    public int InboundParkedWarnAfterDays { get; set; } = 7;
    public string[] InboundShortRetentionTypes { get; set; } = ["order.tracking_updated", "shipment.delivered"];
    public int InboundShortRetentionHours { get; set; } = 24;
    /// <summary>Days the inbound event id row is kept; must outlive the portal's retry horizon (the id table is the replay guard).</summary>
    public int InboundRowRetentionDays { get; set; } = 90;
    /// <summary>Deadline for the whole call across all attempts and backoffs (decision O-08). Longer persistence is the outbox's job.</summary>
    public int TotalDeadlineSeconds { get; set; } = 30;
    /// <summary>
    /// Contract request C10 (merged by the portal in 3ee6752 and confirmed by its own verification, decision M3-33): disputes keyed per (dispute id, seller order). ON by default: a dispute that spans
    /// several seller orders is sent as one event per seller order. Off (an emergency switch, or a portal older than C10): those events are held in the outbox until it is turned on again, when the
    /// order worker releases them. Single-seller disputes are not affected.
    /// </summary>
    public bool PerSellerOrderDisputes { get; set; } = true;
    /// <summary>
    /// Contract request C8 (merged by the portal in 3ed6c8b, decision M3-37): per seller order shipping speed (standard, expedited or overnight), method (at most 80 characters) and the tax on shipping
    /// as their own fields of <c>shipping[]</c>. ON by default. Off (an emergency switch, or a portal older than C8): one order-level speed (the fastest chosen) and tax on shipping folded into line
    /// tax; see <c>IngestMapping</c>.
    /// </summary>
    public bool PerGroupShipping { get; set; } = true;
    /// <summary>Total attempts per call including the first.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>Backoff base in milliseconds: base x 2^(attempt-1), jittered, capped at 30 s.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;
}

public sealed class MarketplaceOptions
{
    public string? RelayDomain { get; set; }
    public string? PublicBaseUrl { get; set; }
}

public sealed class DatabaseOptions
{
    /// <summary>"Sqlite" (dev/tests) or "Postgres" (production).</summary>
    public string Provider { get; set; } = "Sqlite";
}

public sealed class WebOptions
{
    /// <summary>
    /// Key the web server (BFF) signs the forwarded end-user IP with. Its own key: never a portal secret.
    /// </summary>
    public string? ForwardingKey { get; set; }
}

/// <summary>Option defaults, separated from Program.cs so the dev-only fallbacks can be unit tested.</summary>
public static class OptionsSetup
{
    public static void Portal(PortalOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Portal").Bind(o);
        if (!devLike) return;
        o.InboundSecret ??= DevDefaults.PortalInboundSecret;
        o.WebhookSecret ??= DevDefaults.PortalWebhookSecret;
    }

    public static void Marketplace(MarketplaceOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Marketplace").Bind(o);
        if (devLike) { o.RelayDomain ??= DevDefaults.RelayDomain; o.PublicBaseUrl ??= DevDefaults.PublicBaseUrl; }
    }

    public static void Auth(AuthOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Auth").Bind(o);
        if (devLike) { o.EmailLinkKey ??= DevDefaults.EmailLinkKey; o.GuestLinkKey ??= DevDefaults.GuestLinkKey; }
    }

    public static void Checkout(CheckoutOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Checkout").Bind(o);
        if (devLike) o.QuoteKey ??= DevDefaults.QuoteKey;
    }

    public static void Notifications(NotificationOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Notifications").Bind(o);
        if (devLike) o.UnsubscribeKey ??= DevDefaults.UnsubscribeKey;
    }

    public static void Admin(AdminOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Admin").Bind(o);
        if (devLike) o.ApiKey ??= DevDefaults.AdminApiKey;
    }

    public static void Stripe(Payments.StripeOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Stripe").Bind(o);
        if (devLike) o.WebhookSecret ??= DevDefaults.StripeWebhookSecret;
    }

    public static void Web(WebOptions o, IConfiguration config, bool devLike)
    {
        config.GetSection("Web").Bind(o);
        if (devLike) o.ForwardingKey ??= DevDefaults.WebForwardingKey;
    }
}

public sealed class CatalogOptions
{
    /// <summary>"Fixture" (realistic seed; Development/Testing default), "Http" (the portal's signed catalog feed; default elsewhere) or "None" (empty catalog).</summary>
    public string? Source { get; set; }
    public int SyncIntervalSeconds { get; set; } = 60;
    public int PageSize { get; set; } = 100;
    public int MaxPagesPerRun { get; set; } = 50;
    public int MaxPendingFetchesPerRun { get; set; } = 100;
    /// <summary>A seller (display name) is deleted this many days after it last had a visible listing.</summary>
    public int SellerRetentionDays { get; set; } = 90;
}

public sealed class OutboxOptions
{
    /// <summary>Failures before a message is dead-lettered (same as the portal's own outbox).</summary>
    public int MaxAttempts { get; set; } = 8;
    /// <summary>Backoff after failure n is <c>BackoffSeconds x 2^(n-1)</c>, capped at <see cref="MaxBackoffSeconds"/>.</summary>
    public int BackoffSeconds { get; set; } = 30;
    public int MaxBackoffSeconds { get; set; } = 3600;
    /// <summary>How long a dispatcher owns a claimed message; must exceed the longest handler (a portal call is bounded by its own total deadline).</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 50;
    public int PollSeconds { get; set; } = 5;
    /// <summary>Delivered messages lose their payload after this many days (it may hold an address or a secret link); dead ones after <see cref="DeadPayloadRetentionDays"/>.</summary>
    public int DeliveredPayloadRetentionDays { get; set; } = 1;
    public int DeadPayloadRetentionDays { get; set; } = 30;
    /// <summary>Rows are deleted after this many days (the dedupe key stops protecting then).</summary>
    public int RowRetentionDays { get; set; } = 90;
}

public sealed class AdminOptions
{
    /// <summary>Key for the operations endpoints (<c>/api/admin/*</c>). Its own key. Unset means the endpoints answer 404 (disabled).</summary>
    public string? ApiKey { get; set; }
    /// <summary>The host (and port) the operations endpoints answer on, e.g. <c>admin.internal:8443</c>. Requests for any other host get 404 (O-12). Required outside Development/Testing when a key is set.</summary>
    public string? Host { get; set; }
}

public sealed class EmailOptions
{
    /// <summary><c>Smtp</c> is the only provider. Development/Testing use an in-memory sink unless this is set.</summary>
    public string? Provider { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUserName { get; set; }
    public string? SmtpPassword { get; set; }
    public string? From { get; set; }
    public bool SmtpUseTls { get; set; } = true;
    /// <summary>A send is cut off after this long, always well inside the outbox lease.</summary>
    public int SendTimeoutSeconds { get; set; } = 20;
}

public sealed class NotificationOptions
{
    /// <summary>Key of the unsubscribe links in notification mail (HMAC). Its own key: never a portal, webhook, web, admin or auth link key (decision M5-08).</summary>
    public string? UnsubscribeKey { get; set; }
    /// <summary>In-app notifications are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 180;
    /// <summary>The origin the one-click unsubscribe URL in a mail's <c>List-Unsubscribe</c> header points at (the public origin that reaches this API). Defaults to Marketplace:PublicBaseUrl.</summary>
    public string? PublicApiBaseUrl { get; set; }
}

public sealed class AuthOptions
{
    /// <summary>Key of the emailed verify/reset links (HMAC). Its own key: never a portal, webhook, web or admin key.</summary>
    public string? EmailLinkKey { get; set; }
    /// <summary>Key of the guest order-access links (HMAC). Its own key: a guest link and an account link can never stand in for each other.</summary>
    public string? GuestLinkKey { get; set; }
    /// <summary>How long a guest order-access link works from the moment it is made.</summary>
    public int GuestLinkDays { get; set; } = 30;
    /// <summary>PBKDF2 iterations of the password hasher (ASP.NET Core Identity V3, HMAC-SHA512). Tests lower it; a stored hash keeps its own count and is upgraded at the next sign-in.</summary>
    public int PasswordIterations { get; set; } = 210_000;
    public int PasswordMinLength { get; set; } = 10;
    public int SessionIdleDays { get; set; } = 7;
    public int SessionAbsoluteDays { get; set; } = 30;
    public int VerificationTokenHours { get; set; } = 24;
    public int ResetTokenMinutes { get; set; } = 60;
    /// <summary>Failed sign-ins per address with no delay at all. After failure n (n above this) the next attempt must wait 2^(n - this - 1) seconds, capped at <see cref="SignInMaxDelaySeconds"/>.</summary>
    public int SignInFreeFailures { get; set; } = 5;
    public int SignInMaxDelaySeconds { get; set; } = 900;
    /// <summary>Failed sign-in (and verify-email) attempts one client IP may make per hour, across all addresses. Shared by every instance (stored in the database).</summary>
    public int SignInIpFailuresPerHour { get; set; } = 20;
    /// <summary>Largest request body the auth endpoints read. A password is at most 128 characters.</summary>
    public int MaxBodyBytes { get; set; } = 16 * 1024;
}

/// <summary>Checkout settings. <see cref="QuoteKey"/> keys the HMAC that binds a quote to a checkout (its own key, required in production, never the dev default, distinct from every other key).</summary>
public sealed class CheckoutOptions
{
    public string? QuoteKey { get; set; }
}

/// <summary>
/// Test-mode demo data for buyers (M9). Off by default; the API refuses to start with it on outside Development and Testing (StartupValidator), with no override.
/// While it is off every demo endpoint answers 404.
/// </summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }
}
