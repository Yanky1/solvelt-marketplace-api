using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// Everything M4 (services and bookings) registers, in one place so <c>Program.cs</c> carries exactly two lines for it and parallel workstreams do not collide there.
/// </summary>
public static class ServicesModule
{
    /// <summary>
    /// Settings that would otherwise turn into 500s or silent 422s for every shopper: a window above the portal's 62 days makes every availability call throw, a default window of zero makes every
    /// default request invalid, a cache above the 30 s the contract allows serves stale slots, and a bucket above the portal's own 600 per minute protects nothing (review of eb27be3, L5).
    /// </summary>
    public static IReadOnlyList<string> ConfigProblems(IConfiguration config)
    {
        var problems = new List<string>();
        static bool Bad(IConfiguration c, string key, int fallback, int min, int max, out int value)
        {
            var raw = c[key];
            value = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return !int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < min || value > max;
        }
        var maxOk = !Bad(config, "Services:MaxWindowDays", 62, 1, 62, out var maxWindow);
        if (!maxOk) problems.Add("Services:MaxWindowDays must be a whole number from 1 to 62 (the portal answers at most 62 days).");
        if (Bad(config, "Services:DefaultWindowDays", 14, 1, 62, out var defWindow) || (maxOk && defWindow > maxWindow)) problems.Add("Services:DefaultWindowDays must be a whole number from 1 to Services:MaxWindowDays.");
        if (Bad(config, "Services:AvailabilityCacheSeconds", 30, 0, 30, out _)) problems.Add("Services:AvailabilityCacheSeconds must be a whole number from 0 to 30 (the contract allows at most 30 s).");
        if (Bad(config, "Services:AvailabilityPortalCallsPerMinute", 300, 1, 600, out _)) problems.Add("Services:AvailabilityPortalCallsPerMinute must be a whole number from 1 to 600 (the portal allows 600 per service per minute for all shoppers).");
        return problems;
    }

    public static IServiceCollection AddMarketplaceServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        services.Configure<ServicesOptions>(configuration.GetSection("Services"));
        services.AddSingleton<AvailabilityCache>();
        services.AddSingleton<ServiceOptionsCache>();
        services.Configure<BookingOptions>(configuration.GetSection("Bookings"));
        services.AddScoped<Solvelt.Marketplace.Api.Account.IAccountEraser, BookingsAccountEraser>();
        services.AddScoped<BookingService>();
        services.AddScoped<BookingMaintenance>();
        services.AddScoped<BookingActions>();
        services.AddScoped<BuyerQuoteService>();
        services.AddSingleton<IProjectionHandler, QuoteEventHandler>();
        if (!env.IsEnvironment("Testing")) services.AddHostedService<BookingWorker>();
        services.AddSingleton<IProjectionHandler, BookingEventHandler>();
        services.AddSingleton<BookingHooks>();
        services.AddScoped<BookingPaymentService>();
        services.AddScoped<BookingRefundService>();
        services.AddScoped<ExtraChargeService>();
        services.AddScoped<BookingPaymentEvents>();
        services.AddScoped<IOutboxHandler, BookingConfirmHandler>();
        services.AddScoped<IOutboxHandler, BookingStripeRefundHandler>();
        services.AddScoped<IOutboxHandler, BookingLateCancelHandler>();
        services.AddScoped<IOutboxHandler, BookingReconcileHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapMarketplaceServices(this IEndpointRouteBuilder app)
    {
        app.MapServices();
        app.MapBookings();
        app.MapQuotes();
        return app;
    }
}
