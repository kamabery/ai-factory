using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory runner</c> (contract, "Implementations"): the runner, on this console, with the contract's default run
/// mode, local, unless Factory:Mode is already set. FACTORY_MODE in the environment or .env still wins in the runner.
/// </summary>
public sealed class RunnerCommand(IFactoryPaths paths, IFileStore files, IProcessRunner processes, CliEnvironment host) : IStandaloneCommand
{
    public string Name => "runner";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var dll = paths.RunnerDll;
        if (files.Stat(dll) is null)
        {
            stderr.WriteLine($"factory: runner refused: the runner is not built at {dll} (dotnet build src/factory)");
            return 1;
        }
        var env = new Dictionary<string, string?>();
        if (host.Get("Factory__Mode") is null) env["Factory__Mode"] = "local";
        return processes.RunAttached("dotnet", [dll, .. args], host.WorkingDirectory, env);
    }
}
