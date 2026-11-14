using System.Globalization;
using Factory.Core;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>state.ps1's Initialize-State: the environment, else .env, read once when first needed.</summary>
public sealed class StateSettings(IOptions<StateOptions> options, IFactoryPaths paths, IDotEnv dotEnv) : IStateSettings
{
    private static readonly string[] Modes = ["local", "azure", "sqlite", "sql"];

    private Resolved? _resolved;

    public bool IsAzure => Resolve().Mode == "azure";
    public string? ContainerUrl => Resolve().Url;
    public long Cap => Resolve().Cap;
    public bool IsDatabase => Resolve().Mode is "sqlite" or "sql";
    public bool IsSql => Resolve().Mode == "sql";
    public string? DbPath => Resolve().DbPath;
    public string? SqlConnection => Resolve().Sql;

    public string? Account =>
        Setting(options.Value.StorageAccount, "FACTORY_STORAGE_ACCOUNT") ?? (ContainerUrl is { } u ? new Uri(u).Host.Split('.')[0] : null);

    public string Group => Setting(options.Value.StateGroup, "FACTORY_STATE_RG") ?? "rg-factory-state";

    private string? Setting(string? fromEnvironment, string name) => !string.IsNullOrEmpty(fromEnvironment) ? fromEnvironment : dotEnv.Get(name);

    private Resolved Resolve() => _resolved ??= Read();

    private Resolved Read()
    {
        var o = options.Value;
        var mode = (Setting(o.State, "FACTORY_STATE") ?? "local").ToLowerInvariant();
        if (!Modes.Contains(mode)) throw new FactoryException($"state: FACTORY_STATE '{Setting(o.State, "FACTORY_STATE")}' must be local, azure, sqlite or sql");
        var url = Setting(o.BlobUrl, "FACTORY_BLOB_URL");
        if (mode == "azure" && url is null) throw new FactoryException("state: FACTORY_STATE=azure needs FACTORY_BLOB_URL (the container URL)");
        // Tests: {home} becomes a hash of FACTORY_HOME, so every test's home gets its own container.
        if (url is not null && url.Contains("{home}", StringComparison.Ordinal))
            url = url.Replace("{home}", "h" + StateHash.Of(paths.Home).ToLowerInvariant(), StringComparison.Ordinal);
        var cap = Setting(o.Cap, "FACTORY_STATE_CAP");
        var sql = mode == "sql" ? Setting(o.Sql, "FACTORY_SQL") ?? throw new FactoryException("state: FACTORY_STATE=sql needs FACTORY_SQL (the connection string)") : null;
        var db = mode == "sqlite" ? Setting(o.Db, "FACTORY_DB") ?? Path.Combine(paths.Home, "factory.db") : null;
        return new Resolved(mode, url?.TrimEnd('/'), cap is null ? 1L << 30 : long.Parse(cap, CultureInfo.InvariantCulture), db, sql);
    }

    private sealed record Resolved(string Mode, string? Url, long Cap, string? DbPath, string? Sql);
}
