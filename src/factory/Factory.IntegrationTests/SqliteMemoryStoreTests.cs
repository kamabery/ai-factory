using Factory.Store;
using Microsoft.Data.Sqlite;

namespace Factory.IntegrationTests;

/// <summary><see cref="StoreContract"/> on sqlite, the database a shared-cache in-memory one kept alive by an open connection.</summary>
public sealed class SqliteMemoryStoreTests : StoreContract
{
    private readonly string _db = $"file:m{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keeper;

    public SqliteMemoryStoreTests()
    {
        Factory.Data.SqliteNative.Init();
        _keeper = new SqliteConnection($"Data Source={_db}");
        _keeper.Open();
    }

    protected override StateOptions Options() => new() { State = "sqlite", Db = _db };

    public override void Dispose()
    {
        _keeper.Dispose();
        base.Dispose();
    }
}
