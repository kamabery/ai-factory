using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>FACTORY_HOME, else &lt;main checkout&gt;/.factory-runs (factory.ps1's Get-MainRoot), and the folders around it.</summary>
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

    public string JobDir(string id) => Path.Combine(Home, "jobs", id);

    public string WorkRoot => string.IsNullOrEmpty(_o.Work) ? Path.Combine(_o.TempPath, "ai-factory") : _o.Work;

    public string WorkDir(string id) => Path.Combine(WorkRoot, "jobs", id);

    public string ScriptsDir => !string.IsNullOrEmpty(_o.Scripts) ? _o.Scripts
        : throw new FactoryException("cannot find factory/scripts above the .NET CLI; set FACTORY_SCRIPTS");

    // factory/scripts -> factory -> the repo -> its parent, where template-projects sits beside it.
    public string TemplatesRoot => !string.IsNullOrEmpty(_o.Templates) ? _o.Templates
        : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ScriptsDir))!)!)!, "template-projects");

    /// <summary>factory/scripts in the nearest folder above <paramref name="from"/> that has factory.ps1, or null.</summary>
    public static string? FindScripts(string from)
    {
        for (var d = new DirectoryInfo(from); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "factory", "scripts");
            if (File.Exists(Path.Combine(p, "factory.ps1"))) return p;
        }
        return null;
    }

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
