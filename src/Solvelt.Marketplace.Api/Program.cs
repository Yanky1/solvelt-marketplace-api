using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<AttemptLimiter>();
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

app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/api/v1/ping", () => Results.Json(new { pong = true })).RequireRateLimiting(RateLimitPolicies.Public);

app.Run();

public partial class Program;
