using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var problems = StartupValidator.Validate(builder.Configuration, builder.Environment);
if (problems.Count > 0)
    throw new InvalidOperationException("Invalid configuration:\n - " + string.Join("\n - ", problems));

var devLike = StartupValidator.IsDevLike(builder.Environment);
builder.Services.Configure<PortalOptions>(o =>
{
    builder.Configuration.GetSection("Portal").Bind(o);
    if (devLike)
    {
        o.InboundSecret ??= DevDefaults.PortalInboundSecret;
        o.WebhookSecret ??= DevDefaults.PortalWebhookSecret;
    }
});
builder.Services.Configure<MarketplaceOptions>(o =>
{
    builder.Configuration.GetSection("Marketplace").Bind(o);
    if (devLike) o.RelayDomain ??= DevDefaults.RelayDomain;
});
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));

builder.Services.AddSingleton(TimeProvider.System);
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
