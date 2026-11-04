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
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            await Write(ctx, 413, "payload_too_large", "Request body too large.");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled exception on {Method}", ctx.Request.Method);
            if (!ctx.Response.HasStarted) await Write(ctx, 500, "internal_error", "Something went wrong.");
        }
    }

    internal static Task Write(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.Clear();
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
