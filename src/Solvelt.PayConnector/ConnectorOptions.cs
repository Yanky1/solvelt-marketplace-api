namespace Solvelt.PayConnector;

/// <summary>
/// The <c>Connector</c> section. Secrets (the SignalWire signing key, the connector URL's Basic password, the key shared with the marketplace API, Sola's API key) come from this service's OWN secret
/// store (environment), never from the repository and never from the marketplace API's configuration.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>The marketplace API's public https origin (no path): where the connector asks what to charge and reports the result.</summary>
    public string? ApiBaseUrl { get; set; }
    /// <summary>Signs the connector's calls to the marketplace API (and the API's answers): HMAC-SHA256. A key of its own; at least 32 characters; never the SignalWire signing key.</summary>
    public string? SharedKey { get; set; }
    /// <summary>The project's SignalWire Signing Key: verifies <c>X-SignalWire-Signature</c> on every connector request. Secret.</summary>
    public string? SignalWireSigningKey { get; set; }
    /// <summary>The connector URL exactly as SignalWire calls it, WITHOUT credentials (https, path <c>/connector</c> unless changed): the URL part of the signed text.</summary>
    public string? PublicUrl { get; set; }
    /// <summary>The credentials SignalWire sends as a Basic header (they are written into the paymentConnectorUrl). A second layer: signature AND Basic.</summary>
    public string? BasicUser { get; set; }
    public string? BasicPassword { get; set; }
    /// <summary>Sola's API key (xKey). Secret. The sandbox key while real money is off.</summary>
    public string? SolaKey { get; set; }
    public string SolaUrl { get; set; } = "https://x1.cardknox.com/gatewayjson";
    public string SolaSoftwareName { get; set; } = "Solvelt PayConnector";
    public string SolaSoftwareVersion { get; set; } = "1.0.0";
    /// <summary>Seconds the gateway may take (also sent as <c>xTimeoutSeconds</c>).</summary>
    public int SolaTimeoutSeconds { get; set; } = 25;
    /// <summary>The SQLite file of the charge ledger: transaction ids, attempt ids, amounts and results. NEVER card data.</summary>
    public string LedgerPath { get; set; } = "connector-ledger.db";
    /// <summary>The most one charge may be, in cents (a sanity cap below the marketplace's own phone-order limit).</summary>
    public long MaxChargeCents { get; set; } = 50_000;
    /// <summary>Connector requests allowed per payment attempt (a caller may retype a wrong card number a couple of times); a PAID attempt takes no further request at all.</summary>
    public int MaxRequestsPerAttempt { get; set; } = 3;
    /// <summary>Refuse plain http (true everywhere but a development box behind nothing).</summary>
    public bool RequireHttps { get; set; } = true;
    /// <summary>True when a platform in front terminates TLS and says so with <c>X-Forwarded-Proto: https</c> (for example Azure App Service). Off by default: then only a request that arrived over https itself is accepted.</summary>
    public bool TrustForwardedProto { get; set; }

    /// <summary>Start-up problems. This release is SANDBOX ONLY: it refuses to start in Production (real money stays off until the owner decides, V-13).</summary>
    public static IReadOnlyList<string> Problems(ConnectorOptions o, bool production)
    {
        var p = new List<string>();
        if (production) p.Add("The pay connector is sandbox-only in this release: it refuses to start in Production (real-money phone payments are not enabled; that is a separate decision of the owner).");
        if (!Uri.TryCreate(o.ApiBaseUrl, UriKind.Absolute, out var api) || (api.Scheme != "https" && !(o.RequireHttps == false && api.Scheme == "http")) || api.AbsolutePath != "/")
            p.Add("Connector:ApiBaseUrl must be the https origin of the marketplace API (no path).");
        if (string.IsNullOrWhiteSpace(o.SharedKey) || o.SharedKey.Length < 32) p.Add("Connector:SharedKey (env Connector__SharedKey) must be at least 32 characters.");
        if (string.IsNullOrWhiteSpace(o.SignalWireSigningKey) || o.SignalWireSigningKey.Length < 16) p.Add("Connector:SignalWireSigningKey (env Connector__SignalWireSigningKey) is required.");
        else if (o.SignalWireSigningKey == o.SharedKey) p.Add("Connector:SharedKey must differ from the SignalWire signing key (one key per purpose).");
        if (!Uri.TryCreate(o.PublicUrl, UriKind.Absolute, out var pub) || (pub.Scheme != "https" && !(o.RequireHttps == false && pub.Scheme == "http")) || !string.IsNullOrEmpty(pub.UserInfo) || !string.IsNullOrEmpty(pub.Fragment))
            p.Add("Connector:PublicUrl must be the https URL SignalWire calls, without credentials or a fragment.");
        if (string.IsNullOrWhiteSpace(o.BasicUser) || string.IsNullOrWhiteSpace(o.BasicPassword) || o.BasicPassword.Length < 24) p.Add("Connector:BasicUser and Connector:BasicPassword (at least 24 characters) are required: the URL carries credentials as a second layer.");
        else if (o.BasicPassword == o.SharedKey || o.BasicPassword == o.SignalWireSigningKey) p.Add("Connector:BasicPassword must differ from every other secret.");
        if (string.IsNullOrWhiteSpace(o.SolaKey)) p.Add("Connector:SolaKey (env Connector__SolaKey) is required.");
        if (!Uri.TryCreate(o.SolaUrl, UriKind.Absolute, out var sola) || sola.Scheme != "https") p.Add("Connector:SolaUrl must be an https URL.");
        if (o.MaxChargeCents is < 1 or > 1_000_000) p.Add("Connector:MaxChargeCents must be between 1 and 1000000.");
        if (o.MaxRequestsPerAttempt is < 1 or > 5) p.Add("Connector:MaxRequestsPerAttempt must be between 1 and 5.");
        if (o.SolaTimeoutSeconds is < 5 or > 60) p.Add("Connector:SolaTimeoutSeconds must be between 5 and 60.");
        return p;
    }
}
