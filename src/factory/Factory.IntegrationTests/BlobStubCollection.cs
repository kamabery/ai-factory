namespace Factory.IntegrationTests;

/// <summary>The test classes that share one <see cref="BlobStub"/> process.</summary>
[CollectionDefinition(Name)]
public sealed class BlobStubCollection : ICollectionFixture<BlobStub>
{
    public const string Name = "blob stub";
}
