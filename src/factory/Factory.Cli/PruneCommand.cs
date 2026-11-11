using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>prune</c>: rolls the container back under FACTORY_STATE_CAP now, and prints its size against the cap.</summary>
public sealed class PruneCommand(IStateSettings settings, IStateSync sync, TimeProvider clock) : ICommand
{
    public string Name => "prune";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (!settings.IsAzure) throw new FactoryException("prune refused: FACTORY_STATE is not azure");
        var r = sync.Prune(session.Board.Jobs());
        if (r.Deleted > 0) session.AddEvent(Events.Create(clock.GetUtcNow(), ("kind", "note"), ("detail", $"pruned {r.Deleted} blobs, {r.Freed} bytes freed")));
        return CommandResult.Ok($"{r.Used} of {r.Cap} bytes");
    }
}
