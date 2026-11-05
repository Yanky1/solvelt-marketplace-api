using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Catalog;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>
/// How the schema comes to exist (decisions M1-03, M1-26, M1-83). Development and Testing (SQLite or PostgreSQL) build it from the model with EnsureCreated plus the search script.
/// Production on PostgreSQL applies the EF migrations at start (<c>Database:MigrateOnStart</c>, default true; EF takes a database lock, so several instances starting together are safe;
/// set it to false to apply them from a pipeline with <c>dotnet ef database update</c> or a migration bundle instead). Production never uses EnsureCreated.
/// </summary>
public static class DatabaseSetup
{
    public static void Prepare(MarketplaceDb db, bool devLike, bool migrateOnStart)
    {
        if (devLike)
        {
            db.Database.EnsureCreated();
            SearchSchema.Ensure(db);
        }
        else if (migrateOnStart && db.Database.IsNpgsql())
        {
            db.Database.Migrate();
        }
    }
}
