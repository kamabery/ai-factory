using System.Globalization;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>state.ps1's Initialize-State: the environment, else .env, read once when first needed.</summary>
public sealed class StateSettings(IOptions<StateOptions> options, IFactoryPaths paths, IFileStore files) : IStateSettings
{
    private (bool Azure, string? Url, long Cap)? _resolved;

    public bool IsAzure => Resolve().Azure;
    public string? ContainerUrl => Resolve().Url;
    public long Cap => Resolve().Cap;

    private (bool Azure, string? Url, long Cap) Resolve() => _resolved ??= Read();

    private (bool, string?, long) Read()
    {
        var o = options.Value;
        IReadOnlyDictionary<string, string>? env = null;
        string? Setting(string? fromEnvironment, string name)
        {
            if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;
            env ??= EnvFile.Parse(paths.EnvFile is { } f ? files.ReadText(f) : null);
            return env.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
        }

        var mode = Setting(o.State, "FACTORY_STATE") ?? "local";
        var azure = string.Equals(mode, "azure", StringComparison.OrdinalIgnoreCase);
        if (!azure && !string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
            throw new FactoryException($"state: FACTORY_STATE '{mode}' must be local or azure");
        var url = Setting(o.BlobUrl, "FACTORY_BLOB_URL");
        if (azure && url is null) throw new FactoryException("state: FACTORY_STATE=azure needs FACTORY_BLOB_URL (the container URL)");
        // Tests: {home} becomes a hash of FACTORY_HOME, so every test's home gets its own container.
        if (url is not null && url.Contains("{home}", StringComparison.Ordinal))
            url = url.Replace("{home}", "h" + StateHash.Of(paths.Home).ToLowerInvariant(), StringComparison.Ordinal);
        var cap = Setting(o.Cap, "FACTORY_STATE_CAP");
        return (azure, url?.TrimEnd('/'), cap is null ? 1L << 30 : long.Parse(cap, CultureInfo.InvariantCulture));
    }
}
