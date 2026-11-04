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
        if (devLike) o.RelayDomain ??= DevDefaults.RelayDomain;
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
}
