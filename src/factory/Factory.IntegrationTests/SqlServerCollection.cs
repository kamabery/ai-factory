namespace Factory.IntegrationTests;

/// <summary>The test classes that share one <see cref="SqlServer"/> container.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServer>
{
    public const string Name = "sql server";
}
