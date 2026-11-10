namespace Factory.Store.Abstractions;

/// <summary>The state backend's settings, from the environment, else .env (contract, "State backends").</summary>
public interface IStateSettings
{
    bool IsAzure { get; }

    /// <summary>FACTORY_BLOB_URL without a trailing slash, its <c>{home}</c> replaced; null when unset.</summary>
    string? ContainerUrl { get; }

    /// <summary>FACTORY_STATE_CAP, default 1 GB.</summary>
    long Cap { get; }
}
