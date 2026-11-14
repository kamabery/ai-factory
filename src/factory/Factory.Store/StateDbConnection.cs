using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The database a sqlite or sql backend's settings name (contract, "State backends", Database).</summary>
public sealed class StateDbConnection(IStateSettings settings) : IDbConnectionInfo
{
    public DbProvider Provider => settings.IsSql ? DbProvider.SqlServer : DbProvider.Sqlite;

    // No pooling: a pooled SQLite connection keeps the file open after the command is done with it.
    public string ConnectionString => settings.IsSql ? settings.SqlConnection! : $"Data Source={settings.DbPath};Pooling=False";

    public string TenantId => "default";
}
