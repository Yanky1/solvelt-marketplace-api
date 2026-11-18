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
        app.MapPost(ChargeTokenPath, ChargeToken);
        return app;
    }

    public const string ChargeTokenPath = "/charge-token";

    /// <summary>The marketplace API asks for a sale with a saved card (V-16). Signed with the shared key like the other API calls ({unix ts}.{body}, five minutes, both directions); https only; the body is tiny.</summary>
    private static async Task<IResult> ChargeToken(HttpContext ctx, ConnectorHandler handler, IOptions<ConnectorOptions> options, TimeProvider time)
    {
        var o = options.Value;
        var isHttps = ctx.Request.IsHttps || (o.TrustForwardedProto && string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase));
        if (o.RequireHttps && !isHttps) return Results.StatusCode(403);
        if (ctx.Request.ContentLength is null or 0 or > 1024) return Results.StatusCode(400);
        using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (!ConnectorSigning.Verify(o.SharedKey!, ctx.Request.Headers[ConnectorSigning.TimestampHeader].ToString(), body, ctx.Request.Headers[ConnectorSigning.SignatureHeader].ToString(), time.GetUtcNow(), TimeSpan.FromMinutes(5))) return Results.StatusCode(401);
        string? attempt, token;
        try { using var d = System.Text.Json.JsonDocument.Parse(body); attempt = d.RootElement.GetProperty("attemptId").GetString(); token = d.RootElement.GetProperty("token").GetString(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return Results.StatusCode(400); }
        if (string.IsNullOrWhiteSpace(attempt) || string.IsNullOrWhiteSpace(token)) return Results.StatusCode(400);
        ConnectorReply reply;
        try { reply = await handler.ChargeTokenAsync(attempt, token, ctx.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Solvelt.PayConnector").LogError("A token sale request failed unexpectedly ({Type}).", ex.GetType().Name);
            return Results.StatusCode(500);
        }
        var json = System.Text.Json.JsonSerializer.Serialize(new { chargeId = reply.ChargeId, errorCode = reply.ErrorCode });
        var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
        ctx.Response.Headers[ConnectorSigning.TimestampHeader] = ts;
        ctx.Response.Headers[ConnectorSigning.SignatureHeader] = ConnectorSigning.Sign(o.SharedKey!, ts, json);
        return Results.Content(json, "application/json");
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
            if (reply.IsToken) return Results.Json(new { token_id = reply.ChargeId, error_code = reply.ErrorCode, error_message = reply.ErrorMessage }, statusCode: reply.Status);
            if (reply.ChargeId is null && reply.ErrorCode is not null && reply.ErrorCode is "already_saved") return Results.Json(new { token_id = (string?)null, error_code = reply.ErrorCode, error_message = reply.ErrorMessage }, statusCode: reply.Status);
            return Results.Json(new { charge_id = reply.ChargeId, error_code = reply.ErrorCode, error_message = reply.ErrorMessage }, statusCode: reply.Status);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); ArrayPool<byte>.Shared.Return(buffer); }
    }
}
