using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Store;

public interface IFactoryPaths
{
    string Home { get; }
    string BoardPath { get; }
}

/// <summary>FACTORY_HOME, else &lt;main checkout&gt;/.factory-runs (factory.ps1's Get-MainRoot).</summary>
public sealed class FactoryPaths(IOptions<FactoryOptions> options, IGitClient git) : IFactoryPaths
{
    private readonly FactoryOptions _o = options.Value;
    private string? _home;

    public string Home => _home ??= Resolve();
    public string BoardPath => Path.Combine(Home, "board.json");

    private string Resolve()
    {
        if (!string.IsNullOrEmpty(_o.Home)) return _o.Home;
        return Path.Combine(MainRoot(), ".factory-runs");
    }

    private string MainRoot()
    {
        if (!string.IsNullOrEmpty(_o.Repo)) return _o.Repo;
        var common = git.CommonDir(_o.WorkingDirectory) ?? throw new FactoryException("not inside a git repository");
        return Path.GetDirectoryName(Path.GetFullPath(common.TrimEnd('/', '\\')))!;
    }
}
