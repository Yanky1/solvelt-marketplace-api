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
