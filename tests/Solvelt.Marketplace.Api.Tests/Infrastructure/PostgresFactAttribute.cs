using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>
/// A test that can only run against a real PostgreSQL server (decision M8-15, owner approval O-25). It carries the trait <c>Db=Postgres</c>, so a job without a server can leave it out
/// (<c>dotnet test --filter "Db!=Postgres"</c>, which is what CI's SQLite job does: 0 skipped). When it IS run without PostgreSQL (a plain local <c>dotnet test</c>) it is reported as
/// SKIPPED with the reason, never as passed: an early <c>return</c> made these tests pass without having run. With <c>TEST_DB_PROVIDER=postgres</c> and
/// <c>TEST_POSTGRES_CONNECTION</c> set it runs like any fact.
/// </summary>
[TraitDiscoverer("Solvelt.Marketplace.Api.Tests.Infrastructure.PostgresTraitDiscoverer", "Solvelt.Marketplace.Api.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute, ITraitAttribute
{
    public const string NeedsPostgres = "needs PostgreSQL: set TEST_DB_PROVIDER=postgres and TEST_POSTGRES_CONNECTION";

    public PostgresFactAttribute()
    {
        if (!Available) Skip = NeedsPostgres;
    }

    /// <summary>True when the run is against PostgreSQL: the provider is "postgres" and a connection string is given (the condition the tests used to return early on).</summary>
    public static bool Available =>
        TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"));
}

/// <summary>The same for a theory (decision M8-15): trait <c>Db=Postgres</c>, SKIPPED with the reason when there is no PostgreSQL.</summary>
[TraitDiscoverer("Solvelt.Marketplace.Api.Tests.Infrastructure.PostgresTraitDiscoverer", "Solvelt.Marketplace.Api.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresFactAttribute.Available) Skip = PostgresFactAttribute.NeedsPostgres;
    }
}

/// <summary>Gives every <see cref="PostgresFactAttribute"/> and <see cref="PostgresTheoryAttribute"/> the trait <c>Db=Postgres</c>.</summary>
public sealed class PostgresTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Db", "Postgres");
    }
}
