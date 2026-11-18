using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solvelt.PayConnector;

/// <summary>
/// The connector service: a separate, minimal web application (its own project, its own deployment and secret store, no database but its own SQLite ledger, no reference from the marketplace API).
/// The ONLY place in this repository that sees a card number. Sandbox only in this release: it refuses to start in Production.
/// </summary>
public static class ConnectorApp
{
    public const string Path = "/connector";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? customise = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ConnectorHandler.MaxBodyBytes);
        var section = builder.Configuration.GetSection("Connector");
        builder.Services.Configure<ConnectorOptions>(section);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new ChargeLedger(sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.LedgerPath));
        builder.Services.AddHttpClient<ISolaGateway, SolaGateway>();
        builder.Services.AddHttpClient<IMarketplaceApi, MarketplaceApi>();
        builder.Services.AddSingleton<ConnectorHandler>();
        builder.Services.AddHostedService<ReportRetryService>();
        customise?.Invoke(builder);

        var o = section.Get<ConnectorOptions>() ?? new ConnectorOptions();
        var problems = ConnectorOptions.Problems(o, builder.Environment.IsProduction());
        if (problems.Count > 0) throw new InvalidOperationException("The pay connector is not configured safely:\n - " + string.Join("\n - ", problems));

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost(Path, Connector);
        return app;
    }

    private static async Task<IResult> Connector(HttpContext ctx, ConnectorHandler handler, IOptions<ConnectorOptions> options)
    {
        var o = options.Value;
        var isHttps = ctx.Request.IsHttps || (o.TrustForwardedProto && string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase));
        var buffer = ArrayPool<byte>.Shared.Rent(ConnectorHandler.MaxBodyBytes + 1);
        try
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await ctx.Request.Body.ReadAsync(buffer.AsMemory(read), ctx.RequestAborted);
                if (n == 0) break;
                read += n;
            }
            ConnectorReply reply;
            try { reply = await handler.HandleAsync(isHttps, ctx.Request.Headers.Authorization.ToString(), ctx.Request.Headers["X-SignalWire-Signature"].ToString(), buffer.AsMemory(0, read), ctx.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Solvelt.PayConnector").LogError("A connector request failed unexpectedly ({Type}).", ex.GetType().Name);   // the type only: an exception text could echo input
                return Results.StatusCode(500);
            }
            if (reply.ChargeId is null && reply.ErrorCode is null) return Results.StatusCode(reply.Status);
            return Results.Json(new { charge_id = reply.ChargeId, error_code = reply.ErrorCode, error_message = reply.ErrorMessage }, statusCode: reply.Status);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); ArrayPool<byte>.Shared.Return(buffer); }
    }
}
