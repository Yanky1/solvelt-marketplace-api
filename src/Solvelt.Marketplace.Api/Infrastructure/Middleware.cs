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
    /// Categories whose loggers are silent, whatever any filter says: hosting diagnostics (request start/finish with the full URL, and the
    /// <c>RequestPath</c> logging scope attached to every line of a request), routing (DfaMatcher logs the request path at Debug), and EF Core's own
    /// error logs (<c>Update</c>: "An exception occurred in SaveChanges"; <c>Database.Command</c>: "Failed executing DbCommand"), which carry the database
    /// exception whose message can quote row values (a PostgreSQL unique violation names the key). Our code logs the exception type and an id instead
    /// (M1-17, M2-12). Matched as case-insensitive prefixes, like .NET matches categories.
    /// </summary>
    internal static readonly string[] SilentPrefixes = ["Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Routing", "Microsoft.EntityFrameworkCore.Update", "Microsoft.EntityFrameworkCore.Database.Command"];

    /// <summary>Categories that may only log warnings and above: the rest of ASP.NET Core, EF Core, and outgoing HttpClient requests (they log URLs below Warning).</summary>
    internal static readonly string[] WarningOnlyPrefixes = ["Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Net.Http.HttpClient"];

    internal static bool IsSilent(string category) => SilentPrefixes.Any(p => category.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    internal static bool IsWarningOnly(string category) => WarningOnlyPrefixes.Any(p => category.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Filters are the wrong layer to protect against: .NET picks the rule with the most specific category, a provider-specific rule beats a
    /// provider-less one, wildcards and different casing slip past removal. So the guarantee is enforced where loggers are CREATED: the logger factory
    /// is wrapped (<see cref="SafeLoggerFactory"/>), and a logger for a sensitive category is a no-op (or warning-only) whatever the configuration, the
    /// environment or the command line say. The filter post-configuration below is only a second layer (it also tidies the rules).
    /// </summary>
    public static ILoggingBuilder AddSafeLogFilters(this ILoggingBuilder logging)
    {
        logging.Services.PostConfigure<LoggerFilterOptions>(Enforce);
        var original = logging.Services.LastOrDefault(d => d.ServiceType == typeof(ILoggerFactory));
        if (original is not null)
        {
            logging.Services.Remove(original);
            logging.Services.AddSingleton<ILoggerFactory>(sp => new SafeLoggerFactory(Create(sp, original)));
        }
        return logging;
    }

    private static ILoggerFactory Create(IServiceProvider sp, ServiceDescriptor d)
        => d.ImplementationInstance as ILoggerFactory
           ?? (d.ImplementationFactory?.Invoke(sp) as ILoggerFactory)
           ?? (ILoggerFactory)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);

    /// <summary>Second layer: drop every rule that could name a sensitive category (case-insensitive prefix, or any wildcard rule) and add the enforced levels.</summary>
    internal static void Enforce(LoggerFilterOptions options)
    {
        var sensitive = SilentPrefixes.Concat(WarningOnlyPrefixes).ToArray();
        foreach (var rule in options.Rules.Where(r => r.CategoryName is { } c && (c.Contains('*') || sensitive.Any(p => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)))).ToList())
            options.Rules.Remove(rule);
        foreach (var p in SilentPrefixes) options.Rules.Add(new LoggerFilterRule(null, p, LogLevel.None, null));
        foreach (var p in WarningOnlyPrefixes) options.Rules.Add(new LoggerFilterRule(null, p, LogLevel.Warning, null));
    }
}

/// <summary>Hands out no-op loggers for silent categories and warning-only loggers for the broad ones; everything else comes from the real factory.</summary>
internal sealed class SafeLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name =>
        SafeLogging.IsSilent(name) ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        : SafeLogging.IsWarningOnly(name) ? new WarningOnlyLogger(inner.CreateLogger(name))
        : inner.CreateLogger(name));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() => inner.Dispose();
}

internal sealed class WarningOnlyLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;   // no request scopes from these categories either
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning) inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
