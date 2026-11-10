namespace Factory.IntegrationTests;

/// <summary>The test classes that share one <see cref="GitHubStub"/> process.</summary>
[CollectionDefinition(Name)]
public sealed class GitHubStubCollection : ICollectionFixture<GitHubStub>
{
    public const string Name = "github stub";
}
