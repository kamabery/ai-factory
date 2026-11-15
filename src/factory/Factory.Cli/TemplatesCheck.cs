using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>The template workspace is there: its .claude-plugin/plugin.json (contract, "Secrets, init and doctor").</summary>
public sealed class TemplatesCheck(IFactoryPaths paths, IFileStore files) : IDoctorCheck
{
    public string Name => "templates";

    public DoctorResult? Check()
    {
        var root = paths.TemplatesRoot;
        if (files.ReadText(Path.Combine(root, ".claude-plugin", "plugin.json")) is not null) return DoctorResult.Ok($"template workspace {root}");
        return DoctorResult.Warn($"no template workspace at {root}",
            paths.IsToolInstall ? "factory init" : "clone FACTORY_TEMPLATES_REPO to the template workspace");
    }
}
