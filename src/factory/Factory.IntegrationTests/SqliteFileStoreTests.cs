using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary><see cref="StoreContract"/> on sqlite, the database factory.db in the home.</summary>
public sealed class SqliteFileStoreTests : StoreContract
{
    protected override StateOptions Options() => new() { State = "sqlite" };
}
