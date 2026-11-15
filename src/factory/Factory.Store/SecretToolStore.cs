using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>libsecret through secret-tool: attributes service &lt;prefix&gt; and name &lt;NAME&gt;; the value goes on stdin.</summary>
public sealed class SecretToolStore(IProcessRunner processes, string prefix) : ISecretStore
{
    public string Name => $"libsecret ({prefix}, through secret-tool)";

    // secret-tool lookup prints the value with no newline after it, and exits 1 when there is none.
    public string? Get(string name)
    {
        var r = Run(null, "lookup", "service", prefix, "name", name);
        return r.ExitCode == 0 ? r.StdOut : null;
    }

    public void Set(string name, string value)
    {
        var r = Run(value, "store", $"--label={prefix} {name}", "service", prefix, "name", name);
        if (r.ExitCode != 0) throw new FactoryException($"secret store: secret-tool store {name} failed: {(r.StdErr + r.StdOut).Trim()}");
    }

    public void Delete(string name) => Run(null, "clear", "service", prefix, "name", name);

    private ProcessResult Run(string? stdin, params string[] args)
    {
        try { return processes.Run("secret-tool", args, "", stdin, null); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new FactoryException($"secret store: secret-tool is not installed ({e.Message}); install libsecret-tools");
        }
    }
}
