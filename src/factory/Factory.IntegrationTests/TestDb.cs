using Factory.Data.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>A database for one test: the provider and connection string a backend would resolve.</summary>
public sealed record TestDb(DbProvider Provider, string ConnectionString) : IDbConnectionInfo
{
    public string TenantId => "default";

    /// <summary>A SQLite file in <paramref name="dir"/>, without pooling, so the test can delete it after.</summary>
    public static TestDb SqliteFile(string dir) => new(DbProvider.Sqlite, $"Data Source={Path.Combine(dir, "factory.db")};Pooling=False");
}
