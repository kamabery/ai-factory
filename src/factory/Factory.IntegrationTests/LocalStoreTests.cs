using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary><see cref="StoreContract"/> on local: the files are the board.</summary>
public sealed class LocalStoreTests : StoreContract
{
    protected override StateOptions Options() => new();
}
