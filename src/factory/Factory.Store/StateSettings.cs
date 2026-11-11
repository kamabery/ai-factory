using System.Globalization;
using Factory.Core;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>state.ps1's Initialize-State: the environment, else .env, read once when first needed.</summary>
public sealed class StateSettings(IOptions<StateOptions> options, IFactoryPaths paths, IDotEnv dotEnv) : IStateSettings
{
    private (bool Azure, string? Url, long Cap)? _resolved;

    public bool IsAzure => Resolve().Azure;
    public string? ContainerUrl => Resolve().Url;
    public long Cap => Resolve().Cap;

    public string? Account =>
        Setting(options.Value.StorageAccount, "FACTORY_STORAGE_ACCOUNT") ?? (ContainerUrl is { } u ? new Uri(u).Host.Split('.')[0] : null);

    public string Group => Setting(options.Value.StateGroup, "FACTORY_STATE_RG") ?? "rg-factory-state";

    private string? Setting(string? fromEnvironment, string name) => !string.IsNullOrEmpty(fromEnvironment) ? fromEnvironment : dotEnv.Get(name);

    private (bool Azure, string? Url, long Cap) Resolve() => _resolved ??= Read();

    private (bool, string?, long) Read()
    {
        var o = options.Value;
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
