using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>Reads .env once, when a value is first asked for (env-file.ps1's Read-EnvFile).</summary>
public sealed class DotEnv(IFactoryPaths paths, IFileStore files) : IDotEnv
{
    private IReadOnlyDictionary<string, string>? _vars;

    public string? Get(string name)
    {
        _vars ??= EnvFile.Parse(paths.EnvFile is { } f ? files.ReadText(f) : null);
        return _vars.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
    }
}
