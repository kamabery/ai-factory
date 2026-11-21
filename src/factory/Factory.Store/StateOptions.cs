namespace Factory.Store;

/// <summary>
/// The state backend's raw settings from the environment (contract, "State backends"). FACTORY_STATE,
/// FACTORY_BLOB_URL and FACTORY_STATE_CAP fall back to .env through <see cref="StateSettings"/>; the rest are
/// environment only.
/// </summary>
public sealed class StateOptions
{
    /// <summary>FACTORY_STATE: local (the default), azure, sqlite or sql.</summary>
    public string? State { get; set; }

    /// <summary>FACTORY_BLOB_URL: the container's URL; <c>{home}</c> becomes a hash of FACTORY_HOME (tests).</summary>
    public string? BlobUrl { get; set; }

    /// <summary>FACTORY_DB: the sqlite backend's database file, else &lt;FACTORY_HOME&gt;/factory.db.</summary>
    public string? Db { get; set; }

    /// <summary>FACTORY_SQL: the sql backend's connection string.</summary>
    public string? Sql { get; set; }

    /// <summary>FACTORY_STATE_CAP: the container's size cap in bytes.</summary>
    public string? Cap { get; set; }

    /// <summary>FACTORY_STATE_TOKEN: the bearer token (tests).</summary>
    public string? Token { get; set; }

    /// <summary>FACTORY_LOCAL_DIR, else &lt;local app data&gt;/ai-factory: the token cache, outside FACTORY_HOME.</summary>
    public string? LocalDir { get; set; }

    /// <summary>FACTORY_BOARD_LOCK_SECONDS: the board mutex's wait, default 30.</summary>
    public string? BoardLockSeconds { get; set; }

    /// <summary>FACTORY_STATE_LOCK_SECONDS: the board lease's wait, default the mutex's.</summary>
    public string? StateLockSeconds { get; set; }

    /// <summary>FACTORY_AZ_CMD: a program and its leading arguments that stand in for az (tests).</summary>
    public string? AzCmd { get; set; }

    /// <summary>IDENTITY_ENDPOINT and IDENTITY_HEADER: a managed identity (Container Apps, App Service).</summary>
    public string? IdentityEndpoint { get; set; }

    public string? IdentityHeader { get; set; }

    /// <summary>AZURE_CLIENT_ID: the user-assigned managed identity every token request names.</summary>
    public string? ClientId { get; set; }

    /// <summary>FACTORY_STORAGE_ACCOUNT: the state storage account allow-ip opens, else the container URL's host.</summary>
    public string? StorageAccount { get; set; }

    /// <summary>FACTORY_STATE_RG: its resource group, else rg-factory-state.</summary>
    public string? StateGroup { get; set; }

    /// <summary>FACTORY_IP_URL: where allow-ip asks for this machine's public IP (tests), else api.ipify.org.</summary>
    public string? IpUrl { get; set; }

    /// <summary>FACTORY_STATE_RENEW_SECONDS: how often a held lease (the runner's) is renewed, default 20.</summary>
    public string? RenewSeconds { get; set; }
}
