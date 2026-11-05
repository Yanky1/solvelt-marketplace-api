using Solvelt.Marketplace.Api.Data;

// schema <postgres|sqlite> [-]: lists the differences between the database and the model, then prints the SQL that adds what is missing. Reads only (SQLite is opened read-only, PostgreSQL with
// default_transaction_read_only). The connection string comes from the environment variable MARKETPLACE_DB, or from standard input when the second argument is "-": never from the command
// line, where it would stay in the shell history and the process list.
if (args.Length < 1 || args[0].ToLowerInvariant() is not ("postgres" or "sqlite") || args.Length > 2 || (args.Length == 2 && args[1] != "-"))
{
    Console.Error.WriteLine("Usage: MARKETPLACE_DB=\"<connection string>\" dotnet run --project tools/SchemaTool -- <postgres|sqlite>");
    Console.Error.WriteLine("   or: echo \"<connection string>\" | dotnet run --project tools/SchemaTool -- <postgres|sqlite> -");
    return 2;
}
var connection = args.Length == 2 ? Console.In.ReadLine() : Environment.GetEnvironmentVariable("MARKETPLACE_DB");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine(args.Length == 2 ? "Nothing was read from standard input." : "MARKETPLACE_DB is not set.");
    return 2;
}
try
{
    using var db = SchemaCheck.OpenReadOnly(args[0], connection);
    var differences = SchemaCheck.Differences(db);
    Console.WriteLine(differences.Count == 0 ? "-- The database matches the model." : $"-- {differences.Count} differences:");
    foreach (var d in differences) Console.WriteLine("--   " + d);
    if (differences.Count > 0) Console.WriteLine(SchemaCheck.FixScript(db));
    return differences.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"The database could not be read ({ex.GetType().Name}): {SchemaCheck.Sanitise(ex.Message)}");
    return 3;
}
