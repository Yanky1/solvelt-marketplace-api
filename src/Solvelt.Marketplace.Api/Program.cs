using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Outbox;
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
builder.Services.Configure<AdminOptions>(o => OptionsSetup.Admin(o, builder.Configuration, devLike));
builder.Services.Configure<AuthOptions>(o => OptionsSetup.Auth(o, builder.Configuration, devLike));
builder.Services.Configure<NotificationOptions>(o => OptionsSetup.Notifications(o, builder.Configuration, devLike));
builder.Services.AddSingleton<Solvelt.Marketplace.Api.Notifications.UnsubscribeTokens>();
builder.Services.AddScoped<IOutboxHandler, Solvelt.Marketplace.Api.Notifications.NotificationMailHandler>();
builder.Services.AddSingleton<LinkTokens>();
builder.Services.AddScoped<IOutboxHandler, AuthMailHandler>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<GateKeys>();
builder.Services.AddSingleton<GuestLinks>();
builder.Services.AddScoped<GuestClaims>();
builder.Services.AddScoped<SignInGate>();
builder.Services.AddScoped<AuthMaintenance>();
builder.Services.AddScoped<AuthService>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPortalClient();
builder.Services.AddSingleton<ClientIpResolver>();
Solvelt.Marketplace.Api.Services.ServicesModule.AddMarketplaceServices(builder.Services, builder.Configuration, builder.Environment);   // M4: services and bookings (Services/ServicesModule.cs)
builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection("Catalog"));
builder.Services.AddSingleton<FixtureCatalogSource>();
// Resolved lazily from the final configuration. Fixture data is for Development/Testing only (the validator rejects it elsewhere); "None" is an empty catalog.
builder.Services.AddSingleton<ICatalogSource>(sp =>
{
    var source = CatalogSourceSelection.Resolve(sp.GetRequiredService<IConfiguration>()["Catalog:Source"], devLike);
    return source.ToLowerInvariant() switch
    {
        "fixture" => sp.GetRequiredService<FixtureCatalogSource>(),
        "none" => new EmptyCatalogSource(),
        "http" => throw new NotSupportedException("Catalog:Source=Http is not available yet (portal contract change C1)."),
        _ => throw new InvalidOperationException($"Unknown Catalog:Source '{source}'."),
    };
});
builder.Services.AddSingleton<ISearchIndex>(_ => (builder.Configuration["Database:Provider"] ?? "Sqlite").Equals("Postgres", StringComparison.OrdinalIgnoreCase)
    ? new PostgresSearchIndex() : new SqliteSearchIndex());
builder.Services.AddSingleton<IProjectionHandler, CatalogEventHandler>();
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<CategoryMap>();
if (!builder.Environment.IsEnvironment("Testing") && !string.Equals(builder.Configuration["Catalog:Source"], "None", StringComparison.OrdinalIgnoreCase) && (devLike || !string.IsNullOrWhiteSpace(builder.Configuration["Catalog:Source"])))
    builder.Services.AddHostedService<CatalogSyncWorker>();
builder.Services.AddSingleton<ProjectionRegistry>();
builder.Services.AddSingleton<TerminalStateRules>();
builder.Services.AddScoped<ProjectionStore>();
builder.Services.AddScoped<PortalEventProcessor>();
builder.Services.AddScoped<PortalEventMaintenance>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<PortalEventWorker>();
builder.Services.Configure<TaxOptions>(builder.Configuration.GetSection("Tax"));
builder.Services.Configure<ShippingOptions>(builder.Configuration.GetSection("Shipping"));
builder.Services.AddSingleton<ITaxCalculator, SimpleTaxCalculator>();
builder.Services.AddSingleton<ShippingSettingsCache>();
builder.Services.AddScoped<QuoteService>();
builder.Services.Configure<StripeOptions>(o => OptionsSetup.Stripe(o, builder.Configuration, devLike));
builder.Services.AddSingleton<FakePaymentProvider>();
builder.Services.AddHttpClient<StripeHttpProvider>();
// Stripe when a (test) key is configured; the in-process fake in Development/Testing without one; otherwise checkout answers 503 and nothing is charged.
builder.Services.AddScoped<IPaymentProvider>(sp =>
    !string.IsNullOrWhiteSpace(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StripeOptions>>().Value.SecretKey) ? sp.GetRequiredService<StripeHttpProvider>()
    : devLike ? sp.GetRequiredService<FakePaymentProvider>() : new UnconfiguredPaymentProvider());
builder.Services.AddSingleton<CheckoutHooks>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<IOutboxHandler, StripeCancelHandler>();
builder.Services.AddScoped<RefundService>();
builder.Services.AddScoped<OrderMaintenance>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<OrderWorker>();
builder.Services.AddScoped<StripeEventProcessor>();
builder.Services.AddScoped<IOutboxHandler, OrderIngestHandler>();
builder.Services.AddSingleton<IProjectionHandler, PortalRefundHandler>();
builder.Services.AddSingleton<Solvelt.Marketplace.Api.Fulfilment.FulfilmentProjector>();
builder.Services.AddSingleton<IProjectionHandler, Solvelt.Marketplace.Api.Fulfilment.FulfilmentHandler>();
builder.Services.AddScoped<IOutboxHandler, StripeRefundHandler>();
builder.Services.AddHttpClient<Solvelt.Marketplace.Api.Fulfilment.ProofFetcher>((sp, c) =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(o.BaseUrl)) c.BaseAddress = new Uri(o.BaseUrl, UriKind.Absolute);
    c.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });   // a redirect would leave the portal origin
builder.Services.AddScoped<Solvelt.Marketplace.Api.Fulfilment.BuyerOrderActions>();
builder.Services.AddScoped<Solvelt.Marketplace.Api.Fulfilment.FulfilmentMaintenance>();
builder.Services.AddScoped<IOutboxHandler, Solvelt.Marketplace.Api.Fulfilment.CancelRequestOutboxHandler>();
builder.Services.AddScoped<IOutboxHandler, Solvelt.Marketplace.Api.Fulfilment.ReturnRequestOutboxHandler>();
builder.Services.AddScoped<IOutboxHandler, Solvelt.Marketplace.Api.Fulfilment.DeliveryConfirmationOutboxHandler>();
builder.Services.AddSingleton<CartHooks>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<CartMaintenance>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<CartWorker>();
builder.Services.AddScoped<OutboxWriter>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddScoped<OutboxMaintenance>();
builder.Services.AddScoped<OutboxHandlerRegistry>();
builder.Services.AddScoped<IOutboxHandler, EmailOutboxHandler>();
builder.Services.AddScoped<IOutboxHandler, PaymentEventOutboxHandler>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<OpsCounters>();
// Smtp wherever it is configured (production requires it, see the validator); otherwise the in-memory sink in Development/Testing.
if (string.Equals(builder.Configuration["Email:Provider"], "Smtp", StringComparison.OrdinalIgnoreCase)) builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else if (devLike) builder.Services.AddSingleton<IEmailSender, DevEmailSink>();
else throw new InvalidOperationException("Email:Provider is not configured.");
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddSingleton(sp => new AttemptLimiter(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddMarketplaceRateLimiting();
builder.Services.AddDbContext<MarketplaceDb>(o =>
{
    var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
    var cs = builder.Configuration.GetConnectionString("Default");
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(cs);
    else o.UseSqlite(cs ?? DevDefaults.SqliteConnection);
    // A query that Includes several collections without AsSplitQuery multiplies rows (variants x images x attributes x FAQs); in tests that is an error, not a log line.
    if (builder.Environment.IsEnvironment("Testing")) o.ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
});

var app = builder.Build();

// Carts and checkouts are personal: never stored by a shared cache or the browser (also on 401 and 429, which is why this runs before the rate limiter).
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/v1/cart") || ctx.Request.Path.StartsWithSegments("/api/v1/checkout"))
        ctx.Response.OnStarting(() => { ctx.Response.Headers.CacheControl = "private, no-store"; return Task.CompletedTask; });
    await next();
});
app.UseMiddleware<SafeRequestLoggingMiddleware>();
app.UseMiddleware<ProblemExceptionMiddleware>();
app.UseMiddleware<StatusProblemMiddleware>();
app.UseMiddleware<AuthBodyLimitMiddleware>();
app.UseRateLimiter();
// Personalised JSON is never cacheable, framework-made 400s (malformed bodies) included: the header is set when the response starts, whatever produced it.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/v1/orders") || path.StartsWithSegments("/api/v1/notifications"))
        ctx.Response.OnStarting(() => { if (!ctx.Response.Headers.ContainsKey("Cache-Control")) ctx.Response.Headers.CacheControl = "private, no-store"; return Task.CompletedTask; });
    await next();
});

if (devLike)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
    db.Database.EnsureCreated();
    SearchSchema.Ensure(db);
}

app.MapGet("/health", (ClientIpResolver forwarding, OpsCounters ops) => Results.Json(new
{
    status = "ok",
    forwarding = new { invalid = forwarding.InvalidCount },
    // Counts since start-up: alert when either rises.
    outbox = new { deadLettered = ops.DeadLettered },
    admin = new { refused = ops.AdminDenied },
}));
app.MapGet("/api/v1/ping", () => Results.Json(new { pong = true })).RequireRateLimiting(RateLimitPolicies.Public);

Solvelt.Marketplace.Api.Services.ServicesModule.MapMarketplaceServices(app);   // M4
app.MapPortalWebhooks();
app.MapCatalog();
app.MapOutboxAdmin();
app.MapAuth();
app.MapCart();
app.MapCheckout();
Solvelt.Marketplace.Api.Fulfilment.OrderEndpoints.MapBuyerOrders(app);
Solvelt.Marketplace.Api.Fulfilment.ProofEndpoints.MapProofs(app);
Solvelt.Marketplace.Api.Notifications.NotificationEndpoints.MapNotifications(app);
app.MapStripeWebhooks();

app.Run();

public partial class Program;
