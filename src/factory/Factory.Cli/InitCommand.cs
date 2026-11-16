using Factory.Cli.Abstractions;
using Factory.Core;

namespace Factory.Cli;

/// <summary><c>factory init</c>: sets a machine up from nothing, one idempotent step at a time (contract, "Secrets, init and doctor").</summary>
public sealed class InitCommand(InitService init, InitCloud cloud) : IStandaloneCommand
{
    public string Name => "init";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        // init cloud connects this machine to a deployed factory (contract, "Cloud mode (deployed)").
        if (args is ["cloud", ..]) return cloud.Run([.. args.Skip(1)], stdout, stderr);
        try { return init.Run(InitOptions.Parse(args), stdout); }
        catch (FactoryException e)
        {
            stderr.WriteLine($"factory: {e.Message}");
            return 1;
        }
    }
}
