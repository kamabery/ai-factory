using Factory.Data.Abstractions;
using Microsoft.Data.Sqlite;

namespace Factory.IntegrationTests;

/// <summary><see cref="BoardRowsContract"/> on a shared-cache in-memory SQLite database, kept alive by one open connection.</summary>
public sealed class SqliteMemoryBoardRowsTests : BoardRowsContract, IDisposable
{
    private readonly SqliteConnection _keeper;

    public SqliteMemoryBoardRowsTests()
    {
        Db = new TestDb(DbProvider.Sqlite, $"Data Source=file:m{Guid.NewGuid():N}?mode=memory&cache=shared");
        Factory.Data.SqliteNative.Init();
        _keeper = new SqliteConnection(Db.ConnectionString);
        _keeper.Open();
    }

    protected override IDbConnectionInfo Db { get; }

    public void Dispose() => _keeper.Dispose();
}
