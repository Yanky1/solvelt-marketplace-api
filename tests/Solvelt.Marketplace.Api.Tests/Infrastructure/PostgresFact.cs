using Xunit;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>
/// A test that needs PostgreSQL (<c>TEST_DB_PROVIDER=postgres</c> and <c>TEST_POSTGRES_CONNECTION</c>). Without them it is reported as SKIPPED with the reason, instead of returning early and
/// passing without having run. CI's PostgreSQL job sets both, so it runs there.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Reason = "Needs PostgreSQL: set TEST_DB_PROVIDER=postgres and TEST_POSTGRES_CONNECTION (CI's PostgreSQL job runs it).";
    public static bool Available => TestFactory.ResolveProvider(Environment.GetEnvironmentVariable("TEST_DB_PROVIDER")) == "postgres" && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"));
    public PostgresFactAttribute() { if (!Available) Skip = Reason; }
}

/// <summary>The theory twin of <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() { if (!PostgresFactAttribute.Available) Skip = PostgresFactAttribute.Reason; }
}
