using System.Text;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>FACTORY_SECRET_DIR: one file per secret, named after it. Tests and the simulator only.</summary>
public sealed class FileSecretStore(string dir) : ISecretStore
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public string Name => $"the folder {dir}";

    public string? Get(string name) => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name), Utf8) : null;

    public void Set(string name, string value)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), value, Utf8);
    }

    public void Delete(string name) => File.Delete(Path.Combine(dir, name));
}
