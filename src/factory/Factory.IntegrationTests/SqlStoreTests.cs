using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary><see cref="StoreContract"/> on sql: SQL Server, a database of its own per test.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlStoreTests(SqlServer server) : StoreContract
{
    private readonly string _sql = server.NewDatabase();

    protected override StateOptions Options() => new() { State = "sql", Sql = _sql };
}
