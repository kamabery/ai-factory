namespace Factory.Data.Abstractions;

/// <summary>Where the database is (contract, "State backends", Database): FACTORY_DB's file, or FACTORY_SQL's server.</summary>
public interface IDbConnectionInfo
{
    DbProvider Provider { get; }

    string ConnectionString { get; }

    /// <summary>The hosted seam: always <c>default</c> for now.</summary>
    string TenantId { get; }
}
