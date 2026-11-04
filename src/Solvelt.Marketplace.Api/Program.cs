using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Portal.Webhooks;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from the application directory, not the current directory, so `dotnet <path>/Solvelt.Marketplace.Api.dll`
// behaves the same from anywhere. (ContentRootPath is left alone: WebApplicationFactory sets it for tests.)
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// URLs, query strings and paths carry tokens and email addresses. These filters are set in code, after configuration, so neither a
// missing appsettings.json nor a config override can loosen them (decision M1-13, review F3).
builder.Logging.AddSafeLogFilters();

var problems = StartupValidator.Validate(builder.Configuration, builder.Environment);
if (problems.Count > 0)
    throw new InvalidOperationException("Invalid configuration:\n - " + string.Join("\n - ", problems));

var devLike = StartupValidator.IsDevLike(builder.Environment);
builder.Services.Configure<PortalOptions>(o => OptionsSetup.Portal(o, builder.Configuration, devLike));
builder.Services.Configure<MarketplaceOptions>(o => OptionsSetup.Marketplace(o, builder.Configuration, devLike));
builder.Services.Configure<WebOptions>(o => OptionsSetup.Web(o, builder.Configuration, devLike));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPortalClient();
builder.Services.AddSingleton<ClientIpResolver>();
builder.Services.AddSingleton<ProjectionRegistry>();
builder.Services.AddSingleton<TerminalStateRules>();
builder.Services.AddScoped<ProjectionStore>();
builder.Services.AddScoped<PortalEventProcessor>();
builder.Services.AddScoped<PortalEventMaintenance>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<PortalEventWorker>();
builder.Services.AddSingleton(sp => new AttemptLimiter(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddMarketplaceRateLimiting();
builder.Services.AddDbContext<MarketplaceDb>(o =>
{
    var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
    var cs = builder.Configuration.GetConnectionString("Default");
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(cs);
    else o.UseSqlite(cs ?? DevDefaults.SqliteConnection);
});

var app = builder.Build();

app.UseMiddleware<SafeRequestLoggingMiddleware>();
app.UseMiddleware<ProblemExceptionMiddleware>();
app.UseMiddleware<StatusProblemMiddleware>();
app.UseRateLimiter();

if (devLike)
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Database.EnsureCreated();
}

app.MapGet("/health", (ClientIpResolver forwarding) => Results.Json(new { status = "ok", forwarding = new { invalid = forwarding.InvalidCount } }));
app.MapGet("/api/v1/ping", () => Results.Json(new { pong = true })).RequireRateLimiting(RateLimitPolicies.Public);

app.MapPortalWebhooks();

app.Run();

public partial class Program;
