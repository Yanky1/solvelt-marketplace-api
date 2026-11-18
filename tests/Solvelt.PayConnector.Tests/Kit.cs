using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Solvelt.PayConnector.Tests;

/// <summary>Everything the connector tests share: a valid configuration, a signed SignalWire request, fakes for the marketplace API and for Sola, and a log collector that sees every line.</summary>
internal static class Kit
{
    public const string SigningKey = "signalwire-signing-key-for-tests-0123456789";
    public const string SharedKey = "connector-shared-key-for-tests-0123456789abcdef";
    public const string BasicUser = "payuser", BasicPassword = "basic-password-for-tests-0123456789abcdef";
    public const string PublicUrl = "https://pay.example.com/connector";
    public const string Pan = "4111111111111111", Cvv = "123";
    public const string Attempt = "0123456789abcdef0123456789abcdef";
    public const string Invoice = "MP-20261118-ABCD1234";

    public static ConnectorOptions Options(Action<ConnectorOptions>? tweak = null)
    {
        var o = new ConnectorOptions
        {
            ApiBaseUrl = "https://api.example.com", SharedKey = SharedKey, SignalWireSigningKey = SigningKey, PublicUrl = PublicUrl, BasicUser = BasicUser, BasicPassword = BasicPassword,
            SolaKey = "sola-sandbox-key-for-tests", LedgerPath = ":memory:", MaxChargeCents = 50_000,
        };
        tweak?.Invoke(o);
        return o;
    }

    public static string BasicHeader(string user = BasicUser, string password = BasicPassword) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    /// <summary>The body SignalWire posts to the connector (the documented fields).</summary>
    public static byte[] Body(string? tx = null, string amount = "35.00", string pan = Pan, string cvv = Cvv, string attempt = Attempt, string currency = "usd", string? extra = null)
        => Encoding.UTF8.GetBytes($$"""{"transaction_id":"{{tx ?? Guid.NewGuid().ToString()}}","method":"credit-card","cardnumber":"{{pan}}","cvv":"{{cvv}}","postal_code":"11201","description":"Solvelt order","chargeAmount":"{{amount}}","token_type":"reusable","expiry_month":"01","expiry_year":"38","currency_code":"{{currency}}","parameters":[{"attempt":"{{attempt}}"}]{{extra}}}""");

    public static string Sign(byte[] body, string key = SigningKey, string url = PublicUrl) => SignalWireRequest.Sign(key, url, body);
}

internal sealed class FakeApi : IMarketplaceApi
{
    public Expectation? Expect { get; set; } = new(3500, Kit.Invoice, "USD");
    public bool ReportWorks { get; set; } = true;
    public Exception? ThrowOnReport { get; set; }
    public Exception? ThrowOnExpect { get; set; }
    public List<ChargeReport> Reports { get; } = [];
    public int ExpectCalls { get; private set; }
    public Task<Expectation?> ExpectAsync(string attemptId, CancellationToken ct) { ExpectCalls++; if (ThrowOnExpect is not null) throw ThrowOnExpect; return Task.FromResult(Expect); }
    public Task<bool> ReportAsync(ChargeReport report, CancellationToken ct) { if (ThrowOnReport is not null) throw ThrowOnReport; if (!ReportWorks) return Task.FromResult(false); Reports.Add(report); return Task.FromResult(true); }
}

internal sealed class FakeSola : ISolaGateway
{
    public SolaResult Result { get; set; } = new(SolaOutcome.Approved, "601518451", 3500, null);
    public Exception? Throw { get; set; }
    public List<(SolaSale Sale, string Pan, string Cvv)> Calls { get; } = [];
    public Task<SolaResult> SaleAsync(SolaSale sale, ConnectorRequest card, CancellationToken ct)
    {
        Calls.Add((sale, Encoding.ASCII.GetString(card.Pan), Encoding.ASCII.GetString(card.Cvv)));
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

internal sealed class LogCollector : ILoggerProvider
{
    public List<string> Lines { get; } = [];
    public ILogger CreateLogger(string categoryName) => new L(this);
    public void Dispose() { }
    private sealed class L(LogCollector c) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (c.Lines) c.Lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : " | " + exception));
        }
    }
}

internal sealed class Rig : IDisposable
{
    public FakeApi Api { get; } = new();
    public FakeSola Sola { get; } = new();
    public ChargeLedger Ledger { get; } = new(":memory:");
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    public LogCollector Logs { get; } = new();
    public ConnectorOptions Opts { get; }
    public ConnectorHandler Handler { get; }

    public Rig(Action<ConnectorOptions>? tweak = null)
    {
        Opts = Kit.Options(tweak);
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Handler = new ConnectorHandler(Microsoft.Extensions.Options.Options.Create(Opts), Api, Sola, Ledger, Time, factory.CreateLogger<ConnectorHandler>());
    }

    public Task<ConnectorReply> Post(byte[]? body = null, bool https = true, string? auth = null, string? signature = null, bool sign = true)
    {
        body ??= Kit.Body();
        return Handler.HandleAsync(https, auth ?? Kit.BasicHeader(), signature ?? (sign ? Kit.Sign(body) : null), body, CancellationToken.None);
    }

    public void Dispose() => Ledger.Dispose();
}
