using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

// schema "<connection string>" <postgres|sqlite>: lists the differences between the database and the model, then prints the SQL that adds what is missing. Reads only.
if (args.Length < 2 || args[1].ToLowerInvariant() is not ("postgres" or "sqlite"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SchemaTool -- \"<connection string>\" <postgres|sqlite>");
    return 2;
}
var options = new DbContextOptionsBuilder<MarketplaceDb>();
if (args[1].Equals("postgres", StringComparison.OrdinalIgnoreCase)) options.UseNpgsql(args[0]); else options.UseSqlite(args[0]);
using var db = new MarketplaceDb(options.Options);
var differences = SchemaCheck.Differences(db);
Console.WriteLine(differences.Count == 0 ? "-- The database matches the model." : $"-- {differences.Count} differences:");
foreach (var d in differences) Console.WriteLine("--   " + d);
if (differences.Count > 0) Console.WriteLine(SchemaCheck.FixScript(db));
return differences.Count == 0 ? 0 : 1;
