namespace Factory.IntegrationTests;

/// <summary><see cref="CliDatabaseContract"/> on sql: SQL Server, a database of its own per test.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class CliSqlTests(SqlServer server) : CliDatabaseContract
{
    private readonly string _sql = server.NewDatabase();

    protected override Dictionary<string, string?> Backend() => new() { ["FACTORY_STATE"] = "sql", ["FACTORY_SQL"] = _sql };
}
