namespace Solvelt.PayConnector;

/// <summary>Entry point. An explicit <c>Main</c> (not top-level statements) so that no second <c>Program</c> type exists beside the marketplace API's when a test project references both.</summary>
public static class ConnectorProgram
{
    public static void Main(string[] args) => ConnectorApp.Build(args).Run();
}
