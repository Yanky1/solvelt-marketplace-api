using Microsoft.EntityFrameworkCore;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>Root EF context. Entities are added per slice in <see cref="OnModelCreating"/> via configuration classes.</summary>
public sealed class MarketplaceDb(DbContextOptions<MarketplaceDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarketplaceDb).Assembly);
}
