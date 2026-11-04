using Microsoft.Extensions.Options;

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
        return services;
    }

    public static IEndpointRouteBuilder MapMarketplaceServices(this IEndpointRouteBuilder app)
    {
        app.MapServices();
        return app;
    }
}
