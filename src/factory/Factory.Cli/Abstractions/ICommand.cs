using Factory.Store.Abstractions;

namespace Factory.Cli.Abstractions;

/// <summary>A factory.ps1 command, ported. It runs on the board <see cref="CliApp"/> opened for it, under the board lock.</summary>
public interface ICommand
{
    string Name { get; }

    /// <summary>Whether the board is refreshed from the container before the command runs (azure); state-migrate's is not.</summary>
    bool RefreshesBoard => true;
    CommandResult Run(CommandArgs args, IBoardSession session);
}
