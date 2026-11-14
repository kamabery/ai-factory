using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory serve [--port n] ...</c> (contract, "Implementations"): the dashboard server, on this console, with its
/// arguments and this process as its parent, so stopping <c>factory serve</c> stops the server too.
/// </summary>
public sealed class ServeCommand(IFactoryPaths paths, IFileStore files, IProcessRunner processes, CliEnvironment host) : IStandaloneCommand
{
    public string Name => "serve";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var dll = paths.ServerDll;
        if (files.Stat(dll) is null)
        {
            stderr.WriteLine($"factory: serve refused: the dashboard server is not built at {dll} (dotnet build src/factory)");
            return 1;
        }
        return processes.RunAttached("dotnet", [dll, .. args, "--parent-pid", $"{host.ProcessId}"], host.WorkingDirectory, null);
    }
}
