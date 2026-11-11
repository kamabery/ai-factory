using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>idea-abandon &lt;ideaId&gt;</c>: hides a waiting, drafting or failed idea; its jobs stay, and purge clears it.</summary>
public sealed class IdeaAbandonCommand(TimeProvider clock) : ICommand
{
    public string Name => "idea-abandon";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var i = session.Board.Idea(args.Arg(1));
        if (!PsJsonReader.In(i.Status, "waiting", "drafting", "failed")) throw new FactoryException($"idea-abandon refused: {i.Id} is {i.Status}");
        i.Node["status"] = "abandoned";
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("idea", i.Id), ("kind", "idea"), ("detail", "abandoned")));
        return CommandResult.Ok();
    }
}
