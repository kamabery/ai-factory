using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>A probe secret can be written, read back and deleted.</summary>
public sealed class SecretStoreCheck(ISecretStore store) : IDoctorCheck
{
    private const string Probe = "doctor-probe";

    public string Name => "secret-store";

    public DoctorResult Check()
    {
        var value = Guid.NewGuid().ToString("N");
        try
        {
            store.Set(Probe, value);
            var back = store.Get(Probe);
            store.Delete(Probe);
            if (back != value) return DoctorResult.Fail($"{store.Name} gave back a different value", "check the store, or set FACTORY_SECRET_PREFIX to start a fresh one");
            if (store.Get(Probe) is not null) return DoctorResult.Fail($"{store.Name} kept a deleted secret", "check the store");
            return DoctorResult.Ok(store.Name);
        }
        catch (FactoryException e)
        {
            return DoctorResult.Fail(e.Message, OperatingSystem.IsWindows() ? "check Credential Manager (control keymgr.dll)" : "install libsecret-tools and run a secret service (gnome-keyring)");
        }
    }
}
