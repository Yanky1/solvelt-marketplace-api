using System.Diagnostics;
using System.Text.Json;

namespace Solvelt.Marketplace.Api.Infrastructure;

/// <summary>
/// Request logging that never touches bodies, query strings, headers, tokens, emails or addresses:
/// only method, route template, status and duration are logged.
/// </summary>
public sealed class SafeRequestLoggingMiddleware(RequestDelegate next, ILogger<SafeRequestLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        try { await next(ctx); }
        finally
        {
            var route = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
            log.LogInformation("{Method} {Route} -> {Status} in {Ms}ms", ctx.Request.Method, route, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>Unhandled exceptions become <c>{code, message}</c> 500s with no detail leaked.</summary>
public sealed class ProblemExceptionMiddleware(RequestDelegate next, ILogger<ProblemExceptionMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (BadHttpRequestException ex)
        {
            // Malformed requests (bad route/query values, unreadable bodies, oversized bodies) are the caller's mistake, never a 500.
            if (!ctx.Response.HasStarted)
                await (ex.StatusCode == 413 ? Write(ctx, 413, "payload_too_large", "Request body too large.") : Write(ctx, 400, "bad_request", "The request could not be understood."));
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Exception messages (database, HTTP, parsing) can carry PII, so only the type, the stack and a correlation id
            // are logged; the same id is returned to the caller so a report can be matched to the log line.
            var correlationId = Guid.NewGuid().ToString("N");
            log.LogError("Unhandled {ExceptionType} on {Method} [{CorrelationId}]\n{Stack}", ex.GetType().FullName, ctx.Request.Method, correlationId, ex.StackTrace);
            if (!ctx.Response.HasStarted) await Write(ctx, 500, "internal_error", "Something went wrong.", correlationId);
        }
    }

    internal static Task Write(HttpContext ctx, int status, string code, string message, string? correlationId = null)
    {
        ctx.Response.Clear();
        if (correlationId is not null) ctx.Response.Headers["X-Correlation-Id"] = correlationId;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new ProblemBody(code, message), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

/// <summary>Turns bare 404/405 (no body written) into the standard problem shape.</summary>
public sealed class StatusProblemMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await next(ctx);
        if (!ctx.Response.HasStarted && ctx.Response.ContentLength is null && string.IsNullOrEmpty(ctx.Response.ContentType))
        {
            switch (ctx.Response.StatusCode)
            {
                case 404: await ProblemExceptionMiddleware.Write(ctx, 404, "not_found", "Not found."); break;
                case 405: await ProblemExceptionMiddleware.Write(ctx, 405, "method_not_allowed", "Method not allowed."); break;
            }
        }
    }
}

public static class SafeLogging
{
    /// <summary>
    /// Category prefixes that would write a full URL, path or query: hosting diagnostics (request start/finish and the RequestPath logging
    /// scope attached to every line of a request), routing (DfaMatcher logs the path at Debug), the rest of ASP.NET Core, EF Core, and
    /// outgoing HttpClient requests.
    /// </summary>
    internal static readonly (string Prefix, LogLevel Level)[] Enforced =
    [
        ("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None),
        ("Microsoft.AspNetCore", LogLevel.Warning),
        ("Microsoft.EntityFrameworkCore", LogLevel.Warning),
        ("System.Net.Http.HttpClient", LogLevel.Warning),
    ];

    /// <summary>
    /// .NET picks the filter rule with the MOST SPECIFIC category prefix, whatever its order, so a configuration entry such as
    /// <c>Microsoft.AspNetCore.Routing.Matching.DfaMatcher: Trace</c> would beat a coarser rule set in code. This post-configuration
    /// therefore removes every rule under the sensitive prefixes (config, environment, command line, AddFilter) and adds the enforced
    /// ones last, so nothing can loosen them (decision M1-29).
    /// </summary>
    public static ILoggingBuilder AddSafeLogFilters(this ILoggingBuilder logging)
    {
        logging.Services.PostConfigure<LoggerFilterOptions>(Enforce);
        return logging;
    }

    internal static void Enforce(LoggerFilterOptions options)
    {
        foreach (var rule in options.Rules.Where(r => r.CategoryName is { } c && Enforced.Any(e => c.StartsWith(e.Prefix, StringComparison.Ordinal))).ToList())
            options.Rules.Remove(rule);
        foreach (var (prefix, level) in Enforced)
            options.Rules.Add(new LoggerFilterRule(null, prefix, level, null));
    }
}
