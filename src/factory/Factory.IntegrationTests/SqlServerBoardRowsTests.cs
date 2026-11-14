using Factory.Data.Abstractions;

namespace Factory.IntegrationTests;

/// <summary><see cref="BoardRowsContract"/> on SQL Server, a database of its own per test on the shared container.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerBoardRowsTests(SqlServer server) : BoardRowsContract
{
    protected override IDbConnectionInfo Db { get; } = new TestDb(DbProvider.SqlServer, server.NewDatabase());
}
