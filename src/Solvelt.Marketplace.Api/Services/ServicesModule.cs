using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal.Webhooks;

namespace Solvelt.Marketplace.Api.Services;

/// <summary>
/// Everything M4 (services and bookings) registers, in one place so <c>Program.cs</c> carries exactly two lines for it and parallel workstreams do not collide there.
/// </summary>
public static class ServicesModule
{
    public static IServiceCollection AddMarketplaceServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        services.Configure<ServicesOptions>(configuration.GetSection("Services"));
        services.AddSingleton<AvailabilityCache>();
        services.Configure<BookingOptions>(configuration.GetSection("Bookings"));
        services.AddScoped<BookingService>();
        services.AddScoped<BookingMaintenance>();
        if (!env.IsEnvironment("Testing")) services.AddHostedService<BookingWorker>();
        services.AddSingleton<IProjectionHandler, BookingEventHandler>();
        services.AddScoped<BookingPaymentService>();
        services.AddScoped<BookingRefundService>();
        services.AddScoped<BookingPaymentEvents>();
        services.AddScoped<IOutboxHandler, BookingConfirmHandler>();
        services.AddScoped<IOutboxHandler, BookingStripeRefundHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapMarketplaceServices(this IEndpointRouteBuilder app)
    {
        app.MapServices();
        app.MapBookings();
        return app;
    }
}
