using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Solvelt.Marketplace.Api.Data;

// Nothing to run: `dotnet ef` loads the factory below.
Console.WriteLine("Use `dotnet ef`; see MigrationsTool.csproj.");

/// <summary>Builds the context for `dotnet ef` against PostgreSQL. `migrations add` never opens the connection; `database update` reads MARKETPLACE_DB.</summary>
public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<MarketplaceDb>
{
    public MarketplaceDb CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("MARKETPLACE_DB") ?? "Host=localhost;Database=marketplace_design";
        return new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(cs, o => o.MigrationsAssembly("Solvelt.Marketplace.Api")).Options);
    }
}
