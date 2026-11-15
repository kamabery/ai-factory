using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory check file &lt;path&gt; ...</c> (contract, "factory commands"): a gate's file check, from the current directory.
/// Failures go to stdout (exit 1), a usage error to stderr (exit 2). It reads no state, so it runs without the board.
/// </summary>
public sealed class CheckCommand(CliEnvironment host, IFileStore files) : IStandaloneCommand
{
    public string Name => "check";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        FileCheckRequest request;
        try { request = FileCheckRequest.Parse(args); }
        catch (FactoryException e)
        {
            stderr.WriteLine(e.Message);
            return e.ExitCode;
        }
        var failures = FileCheck.Failures(request, files.ReadText(Path.GetFullPath(request.Path, host.WorkingDirectory)));
        foreach (var f in failures) stdout.WriteLine(f);
        return failures.Count == 0 ? 0 : 1;
    }
}
