using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>FACTORY_HOME, else &lt;main checkout&gt;/.factory-runs (factory.ps1's Get-MainRoot).</summary>
public sealed class FactoryPaths(IOptions<FactoryOptions> options, IGitClient git) : IFactoryPaths
{
    private readonly FactoryOptions _o = options.Value;
    private string? _home;

    public string Home => _home ??= Resolve();
    public string BoardPath => Path.Combine(Home, "board.json");
    public string EventsPath => Path.Combine(Home, "events.jsonl");

    // factory.ps1 refuses outside a repository even with FACTORY_HOME set, because it always looks for .env;
    // here a missing main checkout only means no .env.
    public string? EnvFile => !string.IsNullOrEmpty(_o.EnvFile) ? _o.EnvFile : MainRootOrNull() is { } root ? Path.Combine(root, ".env") : null;

    private string Resolve()
    {
        if (!string.IsNullOrEmpty(_o.Home)) return _o.Home;
        return Path.Combine(MainRootOrNull() ?? throw new FactoryException("not inside a git repository"), ".factory-runs");
    }

    private string? MainRootOrNull()
    {
        if (!string.IsNullOrEmpty(_o.Repo)) return _o.Repo;
        var common = git.CommonDir(_o.WorkingDirectory);
        return common is null ? null : Path.GetDirectoryName(Path.GetFullPath(common.TrimEnd('/', '\\')))!;
    }
}
