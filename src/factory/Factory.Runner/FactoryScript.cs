using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Runner;

/// <summary><c>pwsh -NoProfile -File factory/scripts/factory.ps1 &lt;args&gt;</c> in a child process, as runner.ps1's F-Child ran it.</summary>
public sealed class FactoryScript(IFactoryPaths paths, IProcessRunner processes) : IFactoryCommands
{
    public FactoryResult Run(params string[] args)
    {
        var r = processes.Run("pwsh", ["-NoProfile", "-File", Path.Combine(paths.ScriptsDir, "factory.ps1"), .. args], "");
        var err = string.Join(' ', r.StdErr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return new FactoryResult(r.ExitCode, r.StdOut.Trim(), err);
    }
}
