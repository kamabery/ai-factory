using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary><see cref="StoreContract"/> on azure, a container of its own on the blob stub.</summary>
[Collection(BlobStubCollection.Name)]
public sealed class AzureStoreTests(BlobStub stub) : StoreContract
{
    private readonly string _container = stub.NewContainer();

    protected override StateOptions Options() => new() { State = "azure", BlobUrl = _container, Token = BlobStub.Token };
}
