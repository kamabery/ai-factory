using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>state-pull [prefix]</c>: downloads what changed in the container (under the prefix), and rebuilds events.jsonl.</summary>
public sealed class StatePullCommand(IStateSettings settings, IStateSync sync) : ICommand
{
    public string Name => "state-pull";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (!settings.SharesFiles) throw new FactoryException("state-pull refused: FACTORY_STATE shares no files (azure, or sql with FACTORY_BLOB_URL)");
        var n = sync.Pull(args.Arg(1));
        sync.SyncEvents();
        return CommandResult.Ok($"pulled: {n}");
    }
}
