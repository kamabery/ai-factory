namespace Factory.Store.Abstractions;

/// <summary>
/// The operating system's secret store (contract, "Secrets, init and doctor"): Credential Manager on Windows, libsecret
/// on Linux, a folder of files under FACTORY_SECRET_DIR for tests.
/// </summary>
public interface ISecretStore
{
    /// <summary>Where the secrets are, for doctor and init to name.</summary>
    string Name { get; }

    /// <summary>The secret, or null when the store has none by that name.</summary>
    string? Get(string name);

    void Set(string name, string value);

    /// <summary>Removes the secret; a missing one is fine.</summary>
    void Delete(string name);
}
