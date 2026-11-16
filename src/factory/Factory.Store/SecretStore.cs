using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// This machine's store: FACTORY_SECRET_VAULT's Key Vault when set (a deployed factory), else FACTORY_SECRET_DIR's
/// folder, else Credential Manager on Windows, else libsecret.
/// </summary>
public sealed class SecretStore(IOptions<SecretOptions> options, IProcessRunner processes, IStateToken token, HttpClient http) : ISecretStore
{
    private ISecretStore? _store;

    private ISecretStore Store => _store ??= Choose();

    public string Name => Store.Name;

    public string? Get(string name) => Store.Get(name);

    public void Set(string name, string value) => Store.Set(name, value);

    public void Delete(string name) => Store.Delete(name);

    private ISecretStore Choose()
    {
        var o = options.Value;
        if (!string.IsNullOrEmpty(o.Vault)) return new KeyVaultSecretStore(http, token, o.Vault);
        if (!string.IsNullOrEmpty(o.Dir)) return new FileSecretStore(o.Dir);
        return OperatingSystem.IsWindows() ? new WindowsCredentialStore(o.Prefix) : new SecretToolStore(processes, o.Prefix);
    }
}
