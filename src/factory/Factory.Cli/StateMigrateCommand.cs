using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>state-migrate</c>: the one-time upload of an existing FACTORY_HOME to the azure container (contract, "State
/// backends"). Its board is not refreshed first: the container has none yet.
/// </summary>
public sealed class StateMigrateCommand(IStateSettings settings, IStateSync sync) : ICommand
{
    public string Name => "state-migrate";

    public bool RefreshesBoard => false;

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (!settings.IsAzure) throw new FactoryException("state-migrate refused: FACTORY_STATE is not azure");
        return CommandResult.Ok($"migrated: {sync.Migrate()} blobs to {settings.ContainerUrl}");
    }
}
