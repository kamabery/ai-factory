using Testcontainers.MsSql;

namespace Factory.IntegrationTests;

/// <summary>
/// One SQL Server container for the test classes that share it (Testcontainers, not LocalDB: Docker runs the same image on
/// Windows and in WSL, and LocalDB is Windows only). Each test asks for a database of its own, which EF Core's
/// migrations create.
/// </summary>
public sealed class SqlServer : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A connection string to a new, not yet created, database on the container.</summary>
    public string NewDatabase() =>
        _container.GetConnectionString().Replace("Database=master", $"Database=t{Guid.NewGuid():N}", StringComparison.OrdinalIgnoreCase);
}
