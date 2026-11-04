using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>Root EF context. Entity configuration lives in <c>IEntityTypeConfiguration</c> classes next to each entity.</summary>
public sealed class MarketplaceDb(DbContextOptions<MarketplaceDb> options) : DbContext(options)
{
    public DbSet<PortalInboundEvent> PortalInboundEvents => Set<PortalInboundEvent>();
    public DbSet<ProjectionState> ProjectionStates => Set<ProjectionState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarketplaceDb).Assembly);

        // SQLite cannot order or compare DateTimeOffset natively. All stored times are UTC (offset 0), so the binary
        // representation orders correctly. PostgreSQL keeps native timestamptz (Npgsql requires UTC offsets).
        if (Database.IsSqlite())
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
    }
}
