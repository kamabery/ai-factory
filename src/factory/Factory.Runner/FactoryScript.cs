using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Runner;

/// <summary><c>dotnet &lt;factory.dll&gt; &lt;args&gt;</c> in a child process, so the runner reads the refusal the CLI writes to stderr.</summary>
public sealed class FactoryScript(IFactoryPaths paths, IProcessRunner processes) : IFactoryCommands
{
    public FactoryResult Run(params string[] args)
    {
        var r = processes.Run("dotnet", [paths.CliDll, .. args], "");
        var err = string.Join(' ', r.StdErr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return new FactoryResult(r.ExitCode, r.StdOut.Trim(), err);
    }
}
