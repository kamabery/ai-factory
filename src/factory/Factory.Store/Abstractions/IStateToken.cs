namespace Factory.Store.Abstractions;

/// <summary>The Entra bearer token for Blob storage (contract, "State backends", Auth), and for other Azure resources.</summary>
public interface IStateToken
{
    string Get();

    /// <summary>A token for <paramref name="resource"/> (Key Vault's) from the same chain, FACTORY_STATE_TOKEN aside.</summary>
    string Get(string resource);
}
