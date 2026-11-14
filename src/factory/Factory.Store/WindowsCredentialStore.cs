using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// Windows Credential Manager: generic credentials named &lt;prefix&gt;/&lt;NAME&gt;, for this user. The value is stored
/// as UTF-8, so a GitHub App's private key (about 1.7 KB) fits under the 2,560-byte blob cap.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string prefix) : ISecretStore
{
    public string Name => $"Windows Credential Manager ({prefix}/...)";

    private string Target(string name) => $"{prefix}/{name}";

    public string? Get(string name)
    {
        if (!CredentialNative.CredRead(Target(name), CredentialNative.Generic, 0, out var p))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == CredentialNative.NotFound) return null;
            throw Refusal("read", name, error);
        }
        try
        {
            var c = Marshal.PtrToStructure<CredentialNative.Credential>(p);
            var bytes = new byte[c.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredentialNative.CredFree(p); }
    }

    public void Set(string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var target = Marshal.StringToHGlobalUni(Target(name));
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CredentialNative.Credential
            {
                Type = CredentialNative.Generic, TargetName = target, UserName = user, CredentialBlob = blob,
                CredentialBlobSize = bytes.Length, Persist = CredentialNative.PersistLocalMachine,
            };
            if (!CredentialNative.CredWrite(ref c, 0)) throw Refusal("write", name, Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string name)
    {
        if (CredentialNative.CredDelete(Target(name), CredentialNative.Generic, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != CredentialNative.NotFound) throw Refusal("delete", name, error);
    }

    private FactoryException Refusal(string what, string name, int error) =>
        new($"secret store: cannot {what} {Target(name)} in Credential Manager: {new Win32Exception(error).Message}");
}
