namespace Solvelt.Marketplace.Api.Infrastructure;

public sealed class PortalOptions
{
    public string? BaseUrl { get; set; }
    /// <summary>Signs our calls to the portal (portal: Orders:InboundSecret).</summary>
    public string? InboundSecret { get; set; }
    /// <summary>Verifies portal webhooks (portal: Orders:OutboundSecret).</summary>
    public string? WebhookSecret { get; set; }
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
        if (devLike) o.EmailLinkKey ??= DevDefaults.EmailLinkKey;
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
    /// <summary>"Fixture" (realistic seed; Development/Testing default), "None" (empty catalog; default elsewhere) or "Http" (portal catalog feed, request C1; not built yet).</summary>
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
}

public sealed class AuthOptions
{
    /// <summary>Key of the emailed verify/reset links (HMAC). Its own key: never a portal, webhook, web or admin key.</summary>
    public string? EmailLinkKey { get; set; }
    /// <summary>PBKDF2 iterations of the password hasher (ASP.NET Core Identity V3, HMAC-SHA512). Tests lower it; a stored hash keeps its own count and is upgraded at the next sign-in.</summary>
    public int PasswordIterations { get; set; } = 210_000;
    public int PasswordMinLength { get; set; } = 10;
    public int SessionIdleDays { get; set; } = 7;
    public int SessionAbsoluteDays { get; set; } = 30;
    public int VerificationTokenHours { get; set; } = 24;
    public int ResetTokenMinutes { get; set; } = 60;
    /// <summary>Wrong passwords in a row before the account is locked for <see cref="LockoutMinutes"/>.</summary>
    public int LockoutThreshold { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
