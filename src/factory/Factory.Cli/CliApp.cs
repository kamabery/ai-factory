using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// factory.ps1's body: dispatch to the command named first (case-insensitive), on the board opened under its lock,
/// and end every command, refused or not, by closing the board (in azure, the push) and releasing the lock.
/// </summary>
public sealed class CliApp(IEnumerable<ICommand> commands, IFactoryStore store)
{
    public CommandResult Run(IReadOnlyList<string> args)
    {
        try
        {
            var a = CommandArgs.Parse(args);
            var command = commands.FirstOrDefault(c => string.Equals(c.Name, a.Command, StringComparison.OrdinalIgnoreCase))
                ?? throw new FactoryException($"unknown command '{a.Command}'");
            using var session = store.Open();
            CommandResult result;
            try { result = command.Run(a, session); }
            catch (FactoryException e) { result = Refusal(e); }
            var pushed = session.Close();
            return pushed.Count == 0 ? result : result with { Err = [.. result.Err, .. pushed] };
        }
        catch (FactoryException e)
        {
            return Refusal(e);
        }
    }

    private static CommandResult Refusal(FactoryException e) => new(e.ExitCode, [], [$"factory: {e.Message}"]);
}
