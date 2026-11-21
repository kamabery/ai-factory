namespace Factory.Store.Abstractions;

/// <summary>The state backend's settings, from the environment, else .env (contract, "State backends").</summary>
public interface IStateSettings
{
    bool IsAzure { get; }

    /// <summary>
    /// The artifacts are shared through FACTORY_BLOB_URL's container: azure, or sql with a blob URL (a deployed factory;
    /// contract, "State backends", Database).
    /// </summary>
    bool SharesFiles { get; }

    /// <summary>sqlite or sql: the board and events live in a database, and board.json and events.jsonl are its exports.</summary>
    bool IsDatabase { get; }

    /// <summary>sql: the database is FACTORY_SQL's SQL Server (Azure SQL in cloud mode).</summary>
    bool IsSql { get; }

    /// <summary>sqlite: FACTORY_DB, else &lt;FACTORY_HOME&gt;/factory.db; null otherwise.</summary>
    string? DbPath { get; }

    /// <summary>sql: FACTORY_SQL, the connection string; null otherwise.</summary>
    string? SqlConnection { get; }

    /// <summary>FACTORY_BLOB_URL without a trailing slash, its <c>{home}</c> replaced; null when unset.</summary>
    string? ContainerUrl { get; }

    /// <summary>FACTORY_STATE_CAP, default 1 GB.</summary>
    long Cap { get; }

    /// <summary>FACTORY_STORAGE_ACCOUNT, else the container URL's first host label.</summary>
    string? Account { get; }

    /// <summary>FACTORY_STATE_RG, else rg-factory-state.</summary>
    string Group { get; }
}
