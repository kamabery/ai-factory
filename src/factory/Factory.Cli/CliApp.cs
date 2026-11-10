using Factory.Cli.Abstractions;
using Factory.Core;

namespace Factory.Cli;

/// <summary>Dispatches to the command named first, as factory.ps1's switch does (case-insensitive).</summary>
public sealed class CliApp(IEnumerable<ICommand> commands)
{
    public CommandResult Run(IReadOnlyList<string> args)
    {
        try
        {
            var a = CommandArgs.Parse(args);
            var command = commands.FirstOrDefault(c => string.Equals(c.Name, a.Command, StringComparison.OrdinalIgnoreCase))
                ?? throw new FactoryException($"unknown command '{a.Command}'");
            return command.Run(a);
        }
        catch (FactoryException e)
        {
            return new CommandResult(e.ExitCode, [], [$"factory: {e.Message}"]);
        }
    }
}
