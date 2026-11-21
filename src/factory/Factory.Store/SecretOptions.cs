namespace Factory.Store;

/// <summary>Where secrets come from (contract, "Secrets, init and doctor"), as the composition root read them.</summary>
public sealed class SecretOptions
{
    /// <summary>FACTORY_SECRET_PREFIX, else ai-factory: the store's names are &lt;prefix&gt;/&lt;NAME&gt;.</summary>
    public string Prefix { get; set; } = "ai-factory";

    /// <summary>FACTORY_SECRET_DIR: a folder of one file per secret, for tests and the simulator only.</summary>
    public string? Dir { get; set; }

    /// <summary>FACTORY_SECRET_VAULT: a deployed factory's Key Vault URI, which wins over every other store.</summary>
    public string? Vault { get; set; }

    /// <summary>The secrets the process environment sets, by name.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
}
